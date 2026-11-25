/**
 * Gemini model discovery + error classification.
 * Uses Generative Language API v1beta ListModels (REST) — never hardcode a single model id.
 */

export type GeminiModelInfo = {
  id: string
  displayName: string
  description?: string
  supportsGenerateContent: boolean
  supportsVision: boolean
  supportsTools: boolean
}

export type GeminiErrorKind = 'auth' | 'model' | 'network' | 'other'

export type ClassifiedGeminiError = {
  kind: GeminiErrorKind
  /** Short user-facing Russian line for journal / TTS */
  userMessage: string
  /** Technical detail (collapsible, not spoken) */
  detail: string
  status?: number
  suggestedModel?: string
}

const LIST_URL = 'https://generativelanguage.googleapis.com/v1beta/models'

/** Known-retired defaults we migrate away from on load/save. */
export const RETIRED_GEMINI_MODELS = new Set([
  'gemini-2.0-flash',
  'gemini-2.0-flash-001',
  'gemini-2.0-flash-lite',
  'gemini-2.0-flash-lite-001',
  'models/gemini-2.0-flash',
])

export function stripModelsPrefix(name: string): string {
  return name.replace(/^models\//, '').trim()
}

export function parseSuggestedModel(message: string): string | undefined {
  if (!message) return undefined
  // "Please update your code to use models/gemini-2.5-flash-…"
  const useMatch = message.match(
    /(?:use|try|switch to|update your code to use)\s+(?:the\s+)?(?:model\s+)?(?:models\/)?(gemini-[\w.\-]+)/i,
  )
  if (useMatch?.[1]) return stripModelsPrefix(useMatch[1])

  // Collect all models/gemini-* mentions; prefer the one that isn't the failing id
  const all = [...message.matchAll(/models\/(gemini-[\w.\-]+)/gi)].map((m) => stripModelsPrefix(m[1]))
  if (all.length >= 2) {
    const failed = all[0]
    const alt = all.find((id) => id !== failed)
    if (alt) return alt
  }
  if (all.length === 1 && /no longer available|not found|is not found/i.test(message)) {
    return undefined
  }
  return all[0]
}

export function isModelUnavailableMessage(message: string, status?: number): boolean {
  if (status === 404) return true
  return /no longer available|not found|is not supported|INVALID_ARGUMENT.*model/i.test(message)
}

/** Rank flash candidates: higher = better. Prefer stable over preview/exp. */
export function scoreFlashModel(id: string): number {
  const n = stripModelsPrefix(id).toLowerCase()
  if (!n.includes('flash')) return Number.NEGATIVE_INFINITY
  if (n.includes('tts') || n.includes('embed') || n.includes('image-generation')) {
    return Number.NEGATIVE_INFINITY
  }
  let s = 0
  const ver = n.match(/gemini-(\d+)\.(\d+)/)
  if (ver) s += Number(ver[1]) * 1000 + Number(ver[2]) * 20
  if (n.includes('preview') || n.includes('-exp') || n.includes('thinking')) s -= 200
  if (n.includes('lite')) s -= 15
  if (n.includes('8b')) s -= 10
  // Prefer plain flash over dated suffixes
  if (/^gemini-\d+\.\d+-flash$/.test(n)) s += 80
  else if (/^gemini-\d+\.\d+-flash-0\d\d$/.test(n)) s += 40
  return s
}

export function pickBestFlash(models: GeminiModelInfo[]): string | undefined {
  const usable = models.filter(
    (m) => m.supportsGenerateContent && (m.supportsTools || m.supportsVision || m.id.includes('flash')),
  )
  const flash = usable.filter((m) => /flash/i.test(m.id))
  const pool = flash.length ? flash : usable
  if (!pool.length) return undefined
  return [...pool].sort((a, b) => scoreFlashModel(b.id) - scoreFlashModel(a.id))[0]?.id
}

export function isRetiredOrMissing(model: string | undefined, availableIds: string[]): boolean {
  const id = stripModelsPrefix(model || '')
  if (!id) return true
  if (RETIRED_GEMINI_MODELS.has(id) || RETIRED_GEMINI_MODELS.has(`models/${id}`)) return true
  if (availableIds.length && !availableIds.includes(id)) return true
  return false
}

type ListModelsResponse = {
  models?: Array<{
    name?: string
    displayName?: string
    description?: string
    supportedGenerationMethods?: string[]
    inputTokenLimit?: number
  }>
  error?: { code?: number; message?: string; status?: string }
}

export async function listGeminiModels(
  apiKey: string,
  fetchImpl: typeof fetch = fetch,
): Promise<GeminiModelInfo[]> {
  if (!apiKey?.trim()) throw Object.assign(new Error('Нет ключа Gemini'), { status: 401 })

  const url = `${LIST_URL}?pageSize=100&key=${encodeURIComponent(apiKey.trim())}`
  let res: Response
  try {
    res = await fetchImpl(url, { method: 'GET', headers: { Accept: 'application/json' } })
  } catch (err) {
    const detail = err instanceof Error ? err.message : String(err)
    throw Object.assign(new Error(`Сеть: не удалось связаться с Gemini (${detail})`), {
      status: 0,
      kind: 'network' as const,
    })
  }

  const body = (await res.json().catch(() => ({}))) as ListModelsResponse
  if (!res.ok) {
    const msg = body.error?.message || res.statusText || `HTTP ${res.status}`
    throw Object.assign(new Error(msg), { status: res.status, kind: res.status === 400 || res.status === 401 || res.status === 403 ? 'auth' : 'other' })
  }

  const out: GeminiModelInfo[] = []
  for (const m of body.models ?? []) {
    const id = stripModelsPrefix(m.name || '')
    if (!id) continue
    const methods = m.supportedGenerationMethods ?? []
    const supportsGenerateContent = methods.includes('generateContent')
    if (!supportsGenerateContent) continue
    // Flash / Pro multimodal models accept images; exclude pure embedding/tts
    const supportsVision = !/embed|tts|aqa/i.test(id)
    const supportsTools = supportsGenerateContent && !/embed|tts/i.test(id)
    out.push({
      id,
      displayName: m.displayName || id,
      description: m.description,
      supportsGenerateContent,
      supportsVision,
      supportsTools,
    })
  }
  return out.sort((a, b) => scoreFlashModel(b.id) - scoreFlashModel(a.id) || a.id.localeCompare(b.id))
}

export async function resolveGeminiModel(
  apiKey: string,
  currentModel: string | undefined,
  fetchImpl: typeof fetch = fetch,
): Promise<{ model: string; models: GeminiModelInfo[]; migrated: boolean }> {
  const models = await listGeminiModels(apiKey, fetchImpl)
  const ids = models.map((m) => m.id)
  const cur = stripModelsPrefix(currentModel || '')
  if (!isRetiredOrMissing(cur, ids) && ids.includes(cur)) {
    return { model: cur, models, migrated: false }
  }
  const picked = pickBestFlash(models)
  if (!picked) throw new Error('Нет доступных моделей Gemini с generateContent')
  return { model: picked, models, migrated: cur !== picked }
}

export function isOverloadMessage(message: string, status?: number): boolean {
  if (status === 429 || status === 503) return true
  return /high demand|resource.?exhausted|unavailable|rate.?limit|quota|overloaded|try again later/i.test(
    message,
  )
}

/** Ordered flash fallbacks after `current` (best → lite). */
export function nextFlashFallbacks(models: GeminiModelInfo[], current: string): string[] {
  const cur = stripModelsPrefix(current)
  const ranked = [...models]
    .filter((m) => m.supportsGenerateContent && /flash/i.test(m.id))
    .sort((a, b) => scoreFlashModel(b.id) - scoreFlashModel(a.id))
    .map((m) => m.id)
  const rest = ranked.filter((id) => id !== cur)
  // Prefer non-preview first among remaining
  const stable = rest.filter((id) => !/preview|-exp|thinking/i.test(id))
  const preview = rest.filter((id) => /preview|-exp|thinking/i.test(id))
  return [...stable, ...preview]
}

export function sleep(ms: number): Promise<void> {
  return new Promise((r) => setTimeout(r, ms))
}

export function classifyGeminiError(err: unknown): ClassifiedGeminiError {
  const detail = err instanceof Error ? err.message : String(err)
  const status =
    typeof err === 'object' && err && 'status' in err
      ? Number((err as { status?: number }).status)
      : undefined
  const suggestedModel = parseSuggestedModel(detail)

  if (status === 0 || /fetch failed|ECONNREFUSED|ENOTFOUND|network|ETIMEDOUT|сете/i.test(detail)) {
    return {
      kind: 'network',
      userMessage: 'Нет связи с Gemini. Проверьте интернет.',
      detail,
      status,
    }
  }

  if (
    status === 401 ||
    status === 403 ||
    (status === 400 && /API key|API_KEY|not valid|permission|PERMISSION/i.test(detail)) ||
    /API key not valid|API_KEY_INVALID|permission denied|PERMISSION_DENIED/i.test(detail)
  ) {
    return {
      kind: 'auth',
      userMessage: 'Ключ API отклонён. Проверьте ключ в Настройках (aistudio.google.com).',
      detail,
      status,
    }
  }

  if (isOverloadMessage(detail, status)) {
    return {
      kind: 'model',
      userMessage: 'Модель перегружена. Повторяю запрос…',
      detail,
      status: status ?? 503,
      suggestedModel,
    }
  }

  if (isModelUnavailableMessage(detail, status) || suggestedModel) {
    return {
      kind: 'model',
      userMessage: suggestedModel
        ? `Модель недоступна → ${suggestedModel}`
        : 'Модель Gemini недоступна. Выберите другую в Настройках.',
      detail,
      status: status ?? 404,
      suggestedModel,
    }
  }

  return {
    kind: 'other',
    userMessage: 'Ошибка Gemini. Подробности в журнале.',
    detail,
    status,
  }
}

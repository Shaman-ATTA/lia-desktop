/**
 * Free LLM providers:
 * - gemini (default): Google AI Studio free key → gemini-2.0-flash (vision + tools)
 * - ollama: local OpenAI-compatible (qwen2.5-vl, llama3.2-vision, qwen2.5-coder)
 * - openai-compatible: any base URL
 */
import { GoogleGenerativeAI, SchemaType, type FunctionDeclaration } from '@google/generative-ai'
import OpenAI from 'openai'
import type { LiaConfig } from '../secure/secrets'
import { TOOL_DECLARATIONS, type ToolName, runTool, type ToolContext } from './tools'

export interface ChatMessage {
  role: 'system' | 'user' | 'assistant' | 'tool'
  content: string
  name?: string
  tool_call_id?: string
}

export interface AgentEvent {
  type: 'narrate' | 'tool_start' | 'tool_end' | 'need_confirm' | 'done' | 'error' | 'abort'
  text?: string
  tool?: string
  args?: unknown
  result?: unknown
  confirmId?: string
}

const SYSTEM = `Ты — Лиа, женский голосовой ИИ-ассистент ATTA CORP (N-33). Отвечай коротко по-русски, тёплым живым тоном, женский род.
Ты управляешь ПК пользователя (личное некоммерческое использование): файлы, терминал, браузер Playwright, экран, мышь/клавиатура.
Всегда кратко озвучивай, что делаешь («Сейчас открою…», «Готово.»).
Перед удалением, перезаписью вне workspace, установкой пакетов и admin-командами запрашивай подтверждение через tool request_confirmation.
Если пользователь сказал «Лиа, стоп» — немедленно прекрати действия.
Не выдумывай пути: сначала list/search. Предпочитай workspace пользователя.`

export class LlmAgent {
  private aborted = false
  private history: ChatMessage[] = [{ role: 'system', content: SYSTEM }]

  constructor(
    private cfg: LiaConfig,
    private ctx: ToolContext,
    private onEvent: (e: AgentEvent) => void,
  ) {}

  abort() {
    this.aborted = true
    this.onEvent({ type: 'abort', text: 'Остановлено.' })
  }

  resetSession() {
    this.history = [{ role: 'system', content: SYSTEM }]
  }

  updateConfig(cfg: LiaConfig) {
    this.cfg = cfg
  }

  async turn(userText: string, imageBase64?: string): Promise<string> {
    this.aborted = false
    this.history.push({
      role: 'user',
      content: imageBase64
        ? `${userText}\n\n[К кадру экрана приложено изображение]`
        : userText,
    })

    let final = ''
    for (let step = 0; step < 12 && !this.aborted; step++) {
      if (this.cfg.provider === 'gemini') {
        const r = await this.geminiStep(imageBase64 && step === 0 ? imageBase64 : undefined)
        if (r.kind === 'text') {
          final = r.text
          this.onEvent({ type: 'narrate', text: final })
          this.history.push({ role: 'assistant', content: final })
          break
        }
        for (const call of r.calls) {
          if (this.aborted) break
          this.onEvent({ type: 'tool_start', tool: call.name, args: call.args, text: `Делаю: ${call.name}` })
          const result = await runTool(call.name as ToolName, call.args, this.ctx)
          this.onEvent({ type: 'tool_end', tool: call.name, result })
          this.history.push({
            role: 'assistant',
            content: `tool:${call.name}:${JSON.stringify(call.args)}`,
          })
          this.history.push({ role: 'tool', name: call.name, content: JSON.stringify(result) })
        }
      } else {
        const r = await this.openaiStep(imageBase64 && step === 0 ? imageBase64 : undefined)
        if (r.kind === 'text') {
          final = r.text
          this.onEvent({ type: 'narrate', text: final })
          this.history.push({ role: 'assistant', content: final })
          break
        }
        for (const call of r.calls) {
          if (this.aborted) break
          this.onEvent({ type: 'tool_start', tool: call.name, args: call.args })
          const result = await runTool(call.name as ToolName, call.args, this.ctx)
          this.onEvent({ type: 'tool_end', tool: call.name, result })
          this.history.push({ role: 'assistant', content: `tool:${call.name}` })
          this.history.push({
            role: 'tool',
            name: call.name,
            tool_call_id: call.id,
            content: JSON.stringify(result),
          })
        }
      }
    }
    this.onEvent({ type: 'done', text: final })
    return final
  }

  private geminiTools(): FunctionDeclaration[] {
    return TOOL_DECLARATIONS.map((t) => ({
      name: t.name,
      description: t.description,
      parameters: {
        type: SchemaType.OBJECT,
        properties: Object.fromEntries(
          Object.entries(t.parameters.properties).map(([k, v]) => [
            k,
            { type: SchemaType.STRING, description: v.description },
          ]),
        ),
        required: t.parameters.required ?? [],
      },
    }))
  }

  private async geminiStep(imageBase64?: string): Promise<
    | { kind: 'text'; text: string }
    | { kind: 'tools'; calls: { name: string; args: Record<string, string> }[] }
  > {
    if (!this.cfg.apiKey) throw new Error('Нет ключа Gemini. Введите бесплатный ключ в Настройках (aistudio.google.com).')
    const genAI = new GoogleGenerativeAI(this.cfg.apiKey)
    const model = genAI.getGenerativeModel({
      model: this.cfg.model || 'gemini-2.0-flash',
      systemInstruction: SYSTEM,
      tools: [{ functionDeclarations: this.geminiTools() }],
    })

    const contents = this.history
      .filter((m) => m.role !== 'system')
      .map((m) => {
        const role = m.role === 'assistant' ? 'model' : 'user'
        return { role, parts: [{ text: `[${m.role}] ${m.content}` }] }
      })

    if (imageBase64) {
      contents.push({
        role: 'user',
        parts: [
          { text: '[screen]' } as { text: string },
          // @ts-expect-error gemini inline
          { inlineData: { mimeType: 'image/png', data: imageBase64 } },
        ],
      })
    }

    const result = await model.generateContent({ contents })
    const resp = result.response
    const calls = resp.functionCalls?.() ?? []
    if (calls.length) {
      return {
        kind: 'tools',
        calls: calls.map((c) => ({
          name: c.name,
          args: Object.fromEntries(
            Object.entries((c.args ?? {}) as Record<string, unknown>).map(([k, v]) => [k, String(v)]),
          ),
        })),
      }
    }
    return { kind: 'text', text: resp.text() || 'Готово.' }
  }

  private client(): OpenAI {
    const base =
      this.cfg.provider === 'ollama'
        ? this.cfg.baseUrl || 'http://127.0.0.1:11434/v1'
        : this.cfg.baseUrl || 'http://127.0.0.1:11434/v1'
    return new OpenAI({
      apiKey: this.cfg.apiKey || 'ollama',
      baseURL: base,
    })
  }

  private async openaiStep(imageBase64?: string): Promise<
    | { kind: 'text'; text: string }
    | { kind: 'tools'; calls: { id: string; name: string; args: Record<string, string> }[] }
  > {
    const tools = TOOL_DECLARATIONS.map((t) => ({
      type: 'function' as const,
      function: {
        name: t.name,
        description: t.description,
        parameters: t.parameters,
      },
    }))

    const messages: OpenAI.Chat.ChatCompletionMessageParam[] = this.history.map((m) => {
      if (m.role === 'tool') {
        return {
          role: 'tool',
          tool_call_id: m.tool_call_id || m.name || 'tool',
          content: m.content,
        }
      }
      return { role: m.role as 'system' | 'user' | 'assistant', content: m.content }
    })

    if (imageBase64) {
      messages.push({
        role: 'user',
        content: [
          { type: 'text', text: 'Скриншот экрана:' },
          { type: 'image_url', image_url: { url: `data:image/png;base64,${imageBase64}` } },
        ],
      })
    }

    const completion = await this.client().chat.completions.create({
      model: this.cfg.model || 'qwen2.5-vl',
      messages,
      tools,
    })
    const msg = completion.choices[0]?.message
    if (msg?.tool_calls?.length) {
      return {
        kind: 'tools',
        calls: msg.tool_calls.map((c) => ({
          id: c.id,
          name: c.function.name,
          args: JSON.parse(c.function.arguments || '{}'),
        })),
      }
    }
    return { kind: 'text', text: msg?.content || 'Готово.' }
  }
}

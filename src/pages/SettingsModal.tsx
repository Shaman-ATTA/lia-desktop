import { useCallback, useEffect, useState } from 'react'

type Props = { open: boolean; onClose: () => void }

type GeminiModel = { id: string; displayName: string }

export function SettingsModal({ open, onClose }: Props) {
  const [provider, setProvider] = useState('gemini')
  const [apiKey, setApiKey] = useState('')
  const [baseUrl, setBaseUrl] = useState('https://generativelanguage.googleapis.com/v1beta')
  const [model, setModel] = useState('')
  const [models, setModels] = useState<GeminiModel[]>([])
  const [modelsMsg, setModelsMsg] = useState('')
  const [workspacePath, setWorkspace] = useState('')
  const [ttsVoice, setTtsVoice] = useState('SvetlanaNeural')
  const [saving, setSaving] = useState(false)
  const [loadingModels, setLoadingModels] = useState(false)

  const refreshModels = useCallback(async (key: string, prefer?: string) => {
    if (!key.trim() || key === '***') {
      setModelsMsg('Введите ключ API, чтобы загрузить список моделей')
      return
    }
    setLoadingModels(true)
    setModelsMsg('')
    try {
      const r = (await window.lia.listGeminiModels(key)) as {
        ok: boolean
        models?: GeminiModel[]
        error?: string
      }
      if (!r.ok) {
        setModels([])
        setModelsMsg(r.error || 'Не удалось загрузить модели')
        return
      }
      const list = r.models || []
      setModels(list)
      if (list.length) {
        const ids = list.map((m) => m.id)
        if (!prefer || !ids.includes(prefer)) {
          setModel(list[0].id)
        } else {
          setModel(prefer)
        }
        setModelsMsg(`${list.length} моделей · выбрана ${prefer && ids.includes(prefer) ? prefer : list[0].id}`)
      } else {
        setModelsMsg('Нет моделей с generateContent')
      }
    } finally {
      setLoadingModels(false)
    }
  }, [])

  useEffect(() => {
    if (!open) return
    void window.lia.getConfigSecret().then((c: Record<string, string>) => {
      setProvider(c.provider || 'gemini')
      setApiKey(c.apiKey || '')
      setBaseUrl(c.baseUrl || '')
      setModel(c.model || '')
      setWorkspace(c.workspacePath || '')
      setTtsVoice(c.ttsVoice || 'SvetlanaNeural')
      if ((c.provider || 'gemini') === 'gemini' && c.apiKey) {
        void refreshModels(c.apiKey, c.model)
      }
    })
  }, [open, refreshModels])

  if (!open) return null

  const save = async () => {
    setSaving(true)
    try {
      const partial: Record<string, unknown> = {
        provider,
        baseUrl:
          provider === 'ollama'
            ? baseUrl || 'http://127.0.0.1:11434/v1'
            : provider === 'gemini'
              ? 'https://generativelanguage.googleapis.com/v1beta'
              : baseUrl,
        model,
        visionModel: model,
        workspacePath,
        ttsVoice,
      }
      if (apiKey && apiKey !== '***') partial.apiKey = apiKey
      const saved = (await window.lia.saveConfig(partial)) as { model?: string }
      if (saved?.model) setModel(saved.model)
      onClose()
    } finally {
      setSaving(false)
    }
  }

  return (
    <div className="overlay" onClick={onClose}>
      <div className="modal" onClick={(e) => e.stopPropagation()}>
        <h2>Настройки · Лиа</h2>
        <p>
          Бесплатный ключ Gemini:{' '}
          <a
            href="#"
            style={{ color: 'var(--cyan)' }}
            onClick={(e) => {
              e.preventDefault()
              void window.lia.openExternal('https://aistudio.google.com/apikey')
            }}
          >
            aistudio.google.com/apikey
          </a>
          . Модель подбирается через ListModels (не хардкод).
        </p>

        <div className="field">
          <label>Провайдер LLM / Vision</label>
          <select
            value={provider}
            onChange={(e) => {
              const p = e.target.value
              setProvider(p)
              if (p === 'gemini') {
                setModel('')
                setBaseUrl('https://generativelanguage.googleapis.com/v1beta')
              } else if (p === 'ollama') {
                setModel('qwen2.5-vl')
                setBaseUrl('http://127.0.0.1:11434/v1')
              }
            }}
          >
            <option value="gemini">Google Gemini (бесплатный tier)</option>
            <option value="ollama">Ollama (локально, offline)</option>
            <option value="openai-compatible">OpenAI-compatible endpoint</option>
          </select>
        </div>

        <div className="field">
          <label>{provider === 'ollama' ? 'API key (можно ollama)' : 'API key'}</label>
          <input
            type="password"
            value={apiKey}
            onChange={(e) => setApiKey(e.target.value)}
            onBlur={() => {
              if (provider === 'gemini' && apiKey && apiKey !== '***') void refreshModels(apiKey, model)
            }}
            placeholder={provider === 'gemini' ? 'AIza…' : 'optional'}
            autoComplete="off"
          />
        </div>

        {(provider === 'ollama' || provider === 'openai-compatible') && (
          <div className="field">
            <label>Base URL</label>
            <input value={baseUrl} onChange={(e) => setBaseUrl(e.target.value)} />
          </div>
        )}

        <div className="field">
          <label>Модель (vision + tools)</label>
          {provider === 'gemini' ? (
            <>
              <div className="row" style={{ gap: 8, marginBottom: 6 }}>
                <select
                  value={model}
                  onChange={(e) => setModel(e.target.value)}
                  disabled={!models.length}
                  style={{ flex: 1 }}
                >
                  {!models.length && <option value={model || ''}>{model || '— загрузите список —'}</option>}
                  {models.map((m) => (
                    <option key={m.id} value={m.id}>
                      {m.id}
                      {m.displayName && m.displayName !== m.id ? ` · ${m.displayName}` : ''}
                    </option>
                  ))}
                </select>
                <button
                  type="button"
                  className="btn secondary"
                  disabled={loadingModels || !apiKey}
                  onClick={() => void refreshModels(apiKey, model)}
                >
                  {loadingModels ? '…' : 'Обновить'}
                </button>
              </div>
              {modelsMsg && <div className="hint">{modelsMsg}</div>}
            </>
          ) : (
            <input value={model} onChange={(e) => setModel(e.target.value)} list="models" />
          )}
          <datalist id="models">
            <option value="qwen2.5-vl" />
            <option value="llama3.2-vision" />
            <option value="qwen2.5-coder" />
          </datalist>
        </div>

        <div className="field">
          <label>Голос TTS (бесплатный Edge neural)</label>
          <select value={ttsVoice} onChange={(e) => setTtsVoice(e.target.value)}>
            <option value="SvetlanaNeural">ru-RU-SvetlanaNeural (рекомендуется)</option>
          </select>
        </div>

        <div className="field">
          <label>Workspace (безопасная папка)</label>
          <input value={workspacePath} onChange={(e) => setWorkspace(e.target.value)} />
        </div>

        <div className="row">
          <button className="btn secondary" onClick={onClose}>
            Отмена
          </button>
          <button className="btn" onClick={() => void save()} disabled={saving}>
            {saving ? '…' : 'Сохранить'}
          </button>
        </div>
      </div>
    </div>
  )
}

import { useEffect, useState } from 'react'

type Props = { open: boolean; onClose: () => void }

export function SettingsModal({ open, onClose }: Props) {
  const [provider, setProvider] = useState('gemini')
  const [apiKey, setApiKey] = useState('')
  const [baseUrl, setBaseUrl] = useState('https://generativelanguage.googleapis.com/v1beta/openai/')
  const [model, setModel] = useState('gemini-2.0-flash')
  const [workspacePath, setWorkspace] = useState('')
  const [ttsVoice, setTtsVoice] = useState('SvetlanaNeural')
  const [saving, setSaving] = useState(false)

  useEffect(() => {
    if (!open) return
    void window.lia.getConfigSecret().then((c: Record<string, string>) => {
      setProvider(c.provider || 'gemini')
      setApiKey(c.apiKey || '')
      setBaseUrl(c.baseUrl || '')
      setModel(c.model || 'gemini-2.0-flash')
      setWorkspace(c.workspacePath || '')
      setTtsVoice(c.ttsVoice || 'SvetlanaNeural')
    })
  }, [open])

  if (!open) return null

  const save = async () => {
    setSaving(true)
    try {
      await window.lia.saveConfig({
        provider,
        apiKey,
        baseUrl:
          provider === 'ollama'
            ? baseUrl || 'http://127.0.0.1:11434/v1'
            : provider === 'gemini'
              ? 'https://generativelanguage.googleapis.com/v1beta/openai/'
              : baseUrl,
        model,
        visionModel: model,
        workspacePath,
        ttsVoice,
      })
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
          Бесплатный ключ Gemini: откройте{' '}
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
          , создайте API key и вставьте ниже. Либо выберите Ollama (локально).
        </p>

        <div className="field">
          <label>Провайдер LLM / Vision</label>
          <select
            value={provider}
            onChange={(e) => {
              const p = e.target.value
              setProvider(p)
              if (p === 'gemini') {
                setModel('gemini-2.0-flash')
                setBaseUrl('https://generativelanguage.googleapis.com/v1beta/openai/')
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
          <input value={model} onChange={(e) => setModel(e.target.value)} list="models" />
          <datalist id="models">
            <option value="gemini-2.0-flash" />
            <option value="gemini-2.5-flash" />
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

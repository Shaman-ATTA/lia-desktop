import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { HologramView } from './face/HologramView'
import { SettingsModal } from './pages/SettingsModal'
import { useSpeechRecognition } from './hooks/useSpeech'
import './styles/app.css'

type Tab = 'home' | 'dialog' | 'agent' | 'more'
type VoiceState = 'IDLE' | 'LISTENING' | 'THINKING' | 'SPEAKING'

const GREETING_TITLE = 'Доброй ночи, сэр.'
const GREETING_SUB = 'Я готова к диалогу и локальным навыкам.'

const STOP_RE = /лиа[,\s]+стоп/i

export default function App() {
  const [tab, setTab] = useState<Tab>('home')
  const [settingsOpen, setSettingsOpen] = useState(false)
  const [voice, setVoice] = useState<VoiceState>('IDLE')
  const [statusSub, setStatusSub] = useState('Скажите «Лиа» или нажмите микрофон')
  const [transcript, setTranscript] = useState('')
  const [log, setLog] = useState<string[]>([])
  const [controlling, setControlling] = useState(false)
  const [confirmPrompt, setConfirmPrompt] = useState<string | null>(null)
  const [mouth, setMouth] = useState({ jaw: 0, funnel: 0, wide: 0 })
  const [textInput, setTextInput] = useState('')
  const audioRef = useRef<HTMLAudioElement | null>(null)
  const queueRef = useRef<string[]>([])
  const speakTextRef = useRef('')
  const boundariesRef = useRef<{ offsetMs: number; durationMs: number; charStart: number; charEnd: number }[]>([])
  const speakStartedAt = useRef(0)

  const pushLog = useCallback((line: string) => {
    setLog((prev) => [...prev.slice(-120), line])
  }, [])

  const playNext = useCallback(() => {
    const next = queueRef.current.shift()
    if (!next) {
      setVoice('IDLE')
      setMouth({ jaw: 0, funnel: 0, wide: 0 })
      return
    }
    if (!audioRef.current) audioRef.current = new Audio()
    const a = audioRef.current
    a.src = next
    void a.play().catch((err) => console.warn('audio play', err))
    a.onended = () => playNext()
  }, [])

  useEffect(() => {
    const offs = [
      window.lia.on('voice:state', (p) => {
        const s = p as { state: VoiceState; text?: string }
        setVoice(s.state)
        if (s.text) setStatusSub(s.text.slice(0, 100))
      }),
      window.lia.on('voice:audio-chunk', (p) => {
        const c = p as { path: string; index: number; dataUrl?: string }
        if (c.dataUrl) queueRef.current.push(c.dataUrl)
        if (c.index === 0) {
          setVoice('SPEAKING')
          speakStartedAt.current = performance.now()
          playNext()
        }
      }),
      window.lia.on('voice:speak-result', (p) => {
        const r = p as {
          text: string
          boundaries: typeof boundariesRef.current
          dataUrl?: string
        }
        speakTextRef.current = r.text
        boundariesRef.current = r.boundaries || []
        if (!queueRef.current.length && r.dataUrl) {
          queueRef.current.push(r.dataUrl)
          speakStartedAt.current = performance.now()
          setVoice('SPEAKING')
          playNext()
        }
      }),
      window.lia.on('agent:log', (line) => pushLog(String(line))),
      window.lia.on('agent:event', (ev) => {
        const e = ev as { type: string; text?: string; tool?: string; args?: unknown; result?: unknown }
        if (e.type === 'tool_start') setControlling(true)
        if (e.type === 'done' || e.type === 'abort' || e.type === 'error') setControlling(false)
        if (e.type === 'tool_start' && e.tool) {
          pushLog(`→ ${e.tool}${e.args ? ' ' + JSON.stringify(e.args) : ''}`)
        } else if (e.type === 'tool_end' && e.tool) {
          const summary =
            e.result && typeof e.result === 'object'
              ? JSON.stringify(e.result).slice(0, 180)
              : String(e.result ?? 'ok')
          pushLog(`✓ ${e.tool}: ${summary}`)
        } else if (e.text) {
          pushLog(`${e.type}: ${e.text}`)
        }
      }),
      window.lia.on('agent:confirm', (p) => {
        setConfirmPrompt((p as { prompt: string }).prompt)
      }),
      window.lia.on('agent:aborted', () => {
        setControlling(false)
        setConfirmPrompt(null)
        setVoice('IDLE')
        setStatusSub('Остановлено')
        audioRef.current?.pause()
        queueRef.current = []
      }),
      // Capture harness: force mouth open without audio
      window.lia.on('debug:mouth', (p) => {
        const m = p as { jaw: number; funnel?: number; wide?: number; speaking?: boolean }
        if (m.speaking) setVoice('SPEAKING')
        else if (m.jaw <= 0.05) setVoice('IDLE')
        setMouth({
          jaw: m.jaw,
          funnel: m.funnel ?? 0,
          wide: m.wide ?? 0,
        })
        if (m.speaking) setStatusSub('Демо анимации рта')
      }),
    ]
    return () => offs.forEach((off) => off())
  }, [playNext, pushLog])

  // Drive visemes from word boundaries while speaking
  useEffect(() => {
    if (voice !== 'SPEAKING') return
    let raf = 0
    const loop = () => {
      raf = requestAnimationFrame(loop)
      const t = performance.now() - speakStartedAt.current
      const text = speakTextRef.current
      if (!text) {
        // Forced open for capture / silent demo
        const amp = 0.72 + 0.12 * Math.sin(t / 160)
        setMouth({ jaw: Math.min(0.58, amp * 0.55), funnel: 0, wide: 0.15 })
        return
      }
      const b = boundariesRef.current.find(
        (x) => t >= x.offsetMs && t <= x.offsetMs + Math.max(90, x.durationMs),
      )
      const amp = 0.62 + 0.28 * Math.abs(Math.sin(t / 130))
      if (b) {
        void window.lia.visemeShape(text, b.charStart, b.charEnd, amp).then((m) => {
          setMouth({ jaw: Math.max(0.35, m.jaw), funnel: m.funnel, wide: m.wide })
        })
      } else {
        setMouth((prev) => ({
          jaw: Math.max(0.28, prev.jaw * 0.92),
          funnel: prev.funnel * 0.9,
          wide: prev.wide * 0.9,
        }))
      }
    }
    loop()
    return () => cancelAnimationFrame(raf)
  }, [voice])

  const handleUserText = async (text: string) => {
    const t = text.trim()
    if (!t) return
    setVoice('THINKING')
    setStatusSub('Думаю…')
    setControlling(true)
    try {
      await window.lia.runAgent(t)
    } finally {
      setControlling(false)
    }
  }

  const onSpeech = useCallback(
    (r: { text: string; final: boolean }) => {
      setTranscript(r.text)
      if (!r.final) return
      if (STOP_RE.test(r.text)) {
        void window.lia.abortAgent()
        void window.lia.stopSpeak()
        setStatusSub('Остановлено по команде')
        return
      }
      if (voice === 'SPEAKING') {
        void window.lia.stopSpeak()
        audioRef.current?.pause()
        queueRef.current = []
      }
      void handleUserText(r.text)
    },
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [voice],
  )

  const { supported, listening, start, stop } = useSpeechRecognition(onSpeech)

  const toggleMic = () => {
    if (listening) stop()
    else {
      setTranscript('')
      setVoice('LISTENING')
      setStatusSub('Слушаю')
      start()
    }
  }

  const statusLine = useMemo(() => {
    switch (voice) {
      case 'SPEAKING':
        return 'Говорю'
      case 'LISTENING':
        return 'Слушаю'
      case 'THINKING':
        return 'Думаю'
      default:
        return listening ? 'Слушаю' : 'В режиме ожидания'
    }
  }, [voice, listening])

  return (
    <div className="app">
      {controlling && <div className="banner-controlling">Лиа управляет · Ctrl+Alt+S / «Лиа, стоп»</div>}

      <header className="topbar">
        <div className="brand">
          ЛИА<span>ATTA CORP · N-33</span>
        </div>
        <div className="top-actions">
          <button className="ghost-btn" onClick={() => setSettingsOpen(true)}>
            Настройки
          </button>
        </div>
      </header>

      <div className="greeting-block">
        <div className="greeting-title">{GREETING_TITLE}</div>
        <div className="greeting-sub">{GREETING_SUB}</div>
      </div>

      <div className="main">
        <div className="stage-wrap">
          <HologramView mouth={mouth} speaking={voice === 'SPEAKING'} />
          <div className={`status ${voice === 'SPEAKING' ? 'speaking' : ''}`}>
            {statusLine.toUpperCase()}
            <span className="status-sub">{statusSub}</span>
          </div>
        </div>

        <aside className="side">
          <div className="panel">
            <h3>Диалог</h3>
            <div className="transcript">{transcript || 'Скажите задачу или нажмите микрофон…'}</div>
            <div className="chat-input">
              <input
                value={textInput}
                onChange={(e) => setTextInput(e.target.value)}
                placeholder="Текст…"
                onKeyDown={(e) => {
                  if (e.key === 'Enter') {
                    void handleUserText(textInput)
                    setTextInput('')
                  }
                }}
              />
              <button
                className="btn send"
                onClick={() => {
                  void handleUserText(textInput)
                  setTextInput('')
                }}
                aria-label="Отправить"
              >
                →
              </button>
            </div>
            {!supported && (
              <p className="hint">Web Speech недоступен — используйте текстовый ввод.</p>
            )}
          </div>
          <div className="panel log-panel">
            <h3>Журнал действий</h3>
            <div className="log" data-testid="action-log">
              {log.length === 0 && <div>Пока пусто</div>}
              {log.map((l, i) => (
                <div key={i}>{l}</div>
              ))}
            </div>
          </div>
        </aside>
      </div>

      <footer className="bottom-bar">
        <nav className="nav-glass">
          <button className={`nav-item ${tab === 'home' ? 'active' : ''}`} onClick={() => setTab('home')}>
            <span className="nav-ico" aria-hidden>
              ▦
            </span>
            <span>Главная</span>
          </button>
          <button className={`nav-item ${tab === 'dialog' ? 'active' : ''}`} onClick={() => setTab('dialog')}>
            <span className="nav-ico" aria-hidden>
              ◢
            </span>
            <span>Диалог</span>
          </button>

          <div className="mic-slot">
            <button className={`mic ${listening ? 'active' : ''}`} onClick={toggleMic} aria-label="Микрофон" />
            <span className="mic-label">{listening ? 'Слушаю…' : 'Микрофон'}</span>
          </div>

          <button className={`nav-item ${tab === 'agent' ? 'active' : ''}`} onClick={() => setTab('agent')}>
            <span className="nav-ico" aria-hidden>
              △
            </span>
            <span>Агент</span>
          </button>
          <button
            className={`nav-item ${tab === 'more' ? 'active' : ''}`}
            onClick={() => {
              setTab('more')
              setSettingsOpen(true)
            }}
          >
            <span className="nav-ico" aria-hidden>
              ···
            </span>
            <span>Ещё</span>
          </button>
        </nav>
      </footer>

      {confirmPrompt && (
        <div className="overlay">
          <div className="modal">
            <h2>Подтверждение</h2>
            <p>{confirmPrompt}</p>
            <div className="row">
              <button
                className="btn secondary"
                onClick={() => {
                  void window.lia.confirm(false)
                  setConfirmPrompt(null)
                }}
              >
                Нет
              </button>
              <button
                className="btn"
                onClick={() => {
                  void window.lia.confirm(true)
                  setConfirmPrompt(null)
                }}
              >
                Да
              </button>
            </div>
          </div>
        </div>
      )}

      <SettingsModal open={settingsOpen} onClose={() => setSettingsOpen(false)} />
    </div>
  )
}

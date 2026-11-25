import { useCallback, useEffect, useMemo, useRef, useState, type CSSProperties } from 'react'
import { HologramView } from './face/HologramView'
import { SettingsModal } from './pages/SettingsModal'
import { useSpeechRecognition } from './hooks/useSpeech'
import { useSysReadout } from './hooks/useSysReadout'
import { BootOverlay } from './components/BootOverlay'
import { ScrambleText } from './components/ScrambleText'
import './styles/app.css'

type Tab = 'home' | 'dialog' | 'agent' | 'more'
type VoiceState = 'IDLE' | 'LISTENING' | 'THINKING' | 'SPEAKING'

const GREETING = 'Доброй ночи, сэр.'
const GREETING_SUB = 'Я готова к диалогу и локальным навыкам.'
const STOP_RE = /лиа[,\s]+стоп/i

function MicIcon({ listening }: { listening: boolean }) {
  return (
    <svg className="mic-svg" viewBox="0 0 24 24" aria-hidden>
      <rect x="9" y="2" width="6" height="11" rx="3" fill="currentColor" />
      <path
        d="M5 11a7 7 0 0 0 14 0"
        fill="none"
        stroke="currentColor"
        strokeWidth="1.8"
        strokeLinecap="round"
      />
      <path d="M12 18v3M9 21h6" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" />
      {listening && <circle className="mic-pulse-ring" cx="12" cy="8" r="9" />}
    </svg>
  )
}

export default function App() {
  const [booted, setBooted] = useState(() => new URLSearchParams(location.search).has('noboot'))
  const [tab, setTab] = useState<Tab>('home')
  const [settingsOpen, setSettingsOpen] = useState(false)
  const [voice, setVoice] = useState<VoiceState>('IDLE')
  const [statusSub, setStatusSub] = useState('Скажите «Лиа» или нажмите микрофон')
  const [subtitle, setSubtitle] = useState(GREETING_SUB)
  const [transcript, setTranscript] = useState('')
  const [log, setLog] = useState<string[]>([])
  const [controlling, setControlling] = useState(false)
  const [confirmPrompt, setConfirmPrompt] = useState<string | null>(null)
  const [mouth, setMouth] = useState({ jaw: 0, funnel: 0, wide: 0 })
  const [textInput, setTextInput] = useState('')
  const [poseYaw, setPoseYaw] = useState(0)
  const [motionBoost, setMotionBoost] = useState(1)
  const [parallax, setParallax] = useState({ x: 0, y: 0 })
  const audioRef = useRef<HTMLAudioElement | null>(null)
  const queueRef = useRef<string[]>([])
  const speakTextRef = useRef('')
  const boundariesRef = useRef<{ offsetMs: number; durationMs: number; charStart: number; charEnd: number }[]>([])
  const speakStartedAt = useRef(0)
  const sys = useSysReadout(booted)

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
    const onMove = (e: PointerEvent) => {
      const x = (e.clientX / window.innerWidth) * 2 - 1
      const y = (e.clientY / window.innerHeight) * 2 - 1
      setParallax({ x, y })
    }
    window.addEventListener('pointermove', onMove)
    return () => window.removeEventListener('pointermove', onMove)
  }, [])

  useEffect(() => {
    const offs = [
      window.lia.on('voice:state', (p) => {
        const s = p as { state: VoiceState; text?: string }
        setVoice(s.state)
        if (s.text) {
          setStatusSub(s.text.slice(0, 100))
          setSubtitle(s.text.slice(0, 140))
        }
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
        setSubtitle(r.text.slice(0, 160))
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
          if (e.type === 'done') setSubtitle(e.text.slice(0, 160))
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
        setSubtitle('Остановлено · Ctrl+Alt+S')
        audioRef.current?.pause()
        queueRef.current = []
      }),
      window.lia.on('debug:mouth', (p) => {
        const m = p as {
          jaw: number
          funnel?: number
          wide?: number
          speaking?: boolean
          poseYaw?: number
          motionBoost?: number
        }
        if (m.speaking) setVoice('SPEAKING')
        else if (m.jaw <= 0.05) setVoice('IDLE')
        setMouth({
          jaw: m.jaw,
          funnel: m.funnel ?? 0,
          wide: m.wide ?? 0,
        })
        if (typeof m.poseYaw === 'number') setPoseYaw(m.poseYaw)
        if (typeof m.motionBoost === 'number') setMotionBoost(m.motionBoost)
        if (m.speaking) setStatusSub('Речь · анимация рта')
      }),
    ]
    return () => offs.forEach((off) => off())
  }, [playNext, pushLog])

  useEffect(() => {
    if (voice !== 'SPEAKING') return
    let raf = 0
    const loop = () => {
      raf = requestAnimationFrame(loop)
      const t = performance.now() - speakStartedAt.current
      const text = speakTextRef.current
      if (!text) {
        const amp = 0.78 + 0.14 * Math.sin(t / 150)
        setMouth({ jaw: Math.min(0.72, amp * 0.62), funnel: 0.05, wide: 0.12 })
        return
      }
      const b = boundariesRef.current.find(
        (x) => t >= x.offsetMs && t <= x.offsetMs + Math.max(90, x.durationMs),
      )
      const amp = 0.68 + 0.3 * Math.abs(Math.sin(t / 120))
      if (b) {
        void window.lia.visemeShape(text, b.charStart, b.charEnd, amp).then((m) => {
          setMouth({ jaw: Math.max(0.38, m.jaw), funnel: m.funnel, wide: m.wide })
        })
      } else {
        setMouth((prev) => ({
          jaw: Math.max(0.3, prev.jaw * 0.9),
          funnel: prev.funnel * 0.88,
          wide: prev.wide * 0.88,
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
    setSubtitle(t)
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
      setSubtitle('Слушаю…')
      start()
    }
  }

  const statusLine = useMemo(() => {
    switch (voice) {
      case 'SPEAKING':
        return 'ГОВОРЮ'
      case 'LISTENING':
        return 'СЛУШАЮ'
      case 'THINKING':
        return 'АНАЛИЗ'
      default:
        return listening ? 'СЛУШАЮ' : 'ОЖИДАНИЕ'
    }
  }, [voice, listening])

  const panelStyle = (side: 'left' | 'right'): CSSProperties => {
    const sx = side === 'left' ? 1 : -1
    return {
      transform: `perspective(900px) rotateY(${parallax.x * 4 * sx}deg) translate3d(${parallax.x * 6 * sx}px, ${parallax.y * -4}px, 0)`,
    }
  }

  return (
    <div className={`app ${booted ? 'booted' : ''}`}>
      {!booted && <BootOverlay onDone={() => setBooted(true)} />}

      <div className="hud-bg" aria-hidden>
        <div className="hex-grid" />
        <div className="scan-sweep" />
        <div className="vignette" />
      </div>

      {controlling && (
        <div className="banner-controlling">
          Лиа управляет · Ctrl+Alt+S / «Лиа, стоп»
        </div>
      )}

      <header className="topbar hud-chrome">
        <div className="brand">
          <ScrambleText text="ЛИА" active={booted} />
          <span>ATTA CORP · N-33</span>
        </div>
        <div className="sys-strip">
          <span>{sys.date}</span>
          <span className="sys-time">{sys.time}</span>
          <span>CPU {sys.cpu}%</span>
          <span>RAM {sys.ram}%</span>
          <span>{sys.net}</span>
        </div>
        <div className="top-actions">
          <button className="ghost-btn" onClick={() => setSettingsOpen(true)}>
            Настройки
          </button>
        </div>
      </header>

      <div className="greeting-block">
        <div className="greeting-title">
          <ScrambleText text={GREETING} active={booted} />
        </div>
      </div>

      <div className="main">
        <aside className="side left hud-panel" style={panelStyle('left')}>
          <div className="panel-bracket">
            <h3>СТАТУС</h3>
            <div className="readout">
              <div>
                <em>VOICE</em> {statusLine}
              </div>
              <div>
                <em>AGENT</em> {controlling ? 'ACTIVE' : 'STANDBY'}
              </div>
              <div>
                <em>MESH</em> GLB · 3D
              </div>
              <div>
                <em>LINK</em> {sys.net}
              </div>
            </div>
          </div>
          <div className="panel-bracket grow">
            <h3>ЖУРНАЛ</h3>
            <div className="log" data-testid="action-log">
              {log.length === 0 && <div className="log-empty">Нет задач</div>}
              {log.map((l, i) => (
                <div key={i}>{l}</div>
              ))}
            </div>
          </div>
        </aside>

        <div className="stage-wrap">
          <div className="stage-frame">
            <span className="corner tl" />
            <span className="corner tr" />
            <span className="corner bl" />
            <span className="corner br" />
            <HologramView
              mouth={mouth}
              speaking={voice === 'SPEAKING' || listening}
              poseYaw={poseYaw}
              motionBoost={motionBoost}
            />
            <div className={`status ${voice === 'SPEAKING' ? 'speaking' : ''}`}>
              <ScrambleText text={statusLine} active={voice !== 'IDLE'} />
              <span className="status-sub">{statusSub}</span>
            </div>
          </div>
          <div className="hud-subtitle" data-testid="hud-subtitle">
            {subtitle}
          </div>
        </div>

        <aside className="side right hud-panel" style={panelStyle('right')}>
          <div className="panel-bracket">
            <h3>ДИАЛОГ</h3>
            <div className="transcript">{transcript || 'Скажите задачу или нажмите микрофон…'}</div>
            <div className="chat-input">
              <input
                value={textInput}
                onChange={(e) => setTextInput(e.target.value)}
                placeholder="Команда…"
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
                ›
              </button>
            </div>
            {!supported && <p className="hint">Web Speech недоступен — текст.</p>}
          </div>
          <div className="panel-bracket grow">
            <h3>СИСТЕМА</h3>
            <div className="readout">
              <div>
                <em>TIME</em> {sys.time}
              </div>
              <div>
                <em>DATE</em> {sys.date}
              </div>
              <div>
                <em>CPU</em> {sys.cpu}%
              </div>
              <div>
                <em>MEM</em> {sys.ram}%
              </div>
            </div>
          </div>
        </aside>
      </div>

      <footer className="bottom-bar">
        <nav className="nav-glass">
          <button className={`nav-item ${tab === 'home' ? 'active' : ''}`} onClick={() => setTab('home')}>
            <span className="nav-ico" aria-hidden>
              ⌂
            </span>
            <span>Главная</span>
          </button>
          <button className={`nav-item ${tab === 'dialog' ? 'active' : ''}`} onClick={() => setTab('dialog')}>
            <span className="nav-ico" aria-hidden>
              ◈
            </span>
            <span>Диалог</span>
          </button>

          <div className="mic-slot">
            <button
              className={`mic ${listening ? 'active' : ''}`}
              onClick={toggleMic}
              aria-label="Микрофон"
            >
              <MicIcon listening={listening} />
            </button>
            <span className="mic-label">{listening ? 'Слушаю…' : 'Микрофон'}</span>
          </div>

          <button className={`nav-item ${tab === 'agent' ? 'active' : ''}`} onClick={() => setTab('agent')}>
            <span className="nav-ico" aria-hidden>
              ⬡
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
              ☰
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

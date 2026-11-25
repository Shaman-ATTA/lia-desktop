import { useEffect, useState } from 'react'
import { ScrambleText } from './ScrambleText'

type Props = { onDone: () => void }

const LINES = [
  'ЛИА · ATTA CORP · N-33',
  'ИНИЦИАЛИЗАЦИЯ ГОЛОГРАФИЧЕСКОГО ЯДРА…',
  'СЕТЬ · ЛОКАЛЬНЫЙ АГЕНТ · ГОЛОС',
  'СИСТЕМА ГОТОВА',
]

export function BootOverlay({ onDone }: Props) {
  const [step, setStep] = useState(0)
  const [fade, setFade] = useState(false)

  useEffect(() => {
    const timers: number[] = []
    LINES.forEach((_, i) => {
      timers.push(window.setTimeout(() => setStep(i + 1), 380 + i * 420))
    })
    timers.push(
      window.setTimeout(() => {
        setFade(true)
      }, 380 + LINES.length * 420 + 280),
    )
    timers.push(
      window.setTimeout(() => {
        onDone()
      }, 380 + LINES.length * 420 + 900),
    )
    return () => timers.forEach((t) => window.clearTimeout(t))
  }, [onDone])

  return (
    <div className={`boot-overlay ${fade ? 'boot-out' : ''}`}>
      <div className="boot-frame">
        <div className="boot-scan" />
        <div className="boot-brand">
          <ScrambleText text="ЛИА" active />
        </div>
        <ul className="boot-lines">
          {LINES.slice(0, step).map((l) => (
            <li key={l}>
              <ScrambleText text={l} active />
            </li>
          ))}
        </ul>
        <div className="boot-bar">
          <span style={{ width: `${Math.min(100, (step / LINES.length) * 100)}%` }} />
        </div>
      </div>
    </div>
  )
}

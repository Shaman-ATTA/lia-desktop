import { useEffect, useState } from 'react'

const GLYPHS = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789░▒/<›·'

type Props = {
  text: string
  className?: string
  active?: boolean
  fps?: number
}

export function ScrambleText({ text, className, active = true, fps = 28 }: Props) {
  const [out, setOut] = useState(text)

  useEffect(() => {
    if (!active) {
      setOut(text)
      return
    }
    let frame = 0
    const total = Math.max(12, text.length + 8)
    const id = window.setInterval(() => {
      frame++
      const reveal = Math.floor((frame / total) * text.length)
      let s = ''
      for (let i = 0; i < text.length; i++) {
        if (text[i] === ' ') s += ' '
        else if (i < reveal) s += text[i]
        else s += GLYPHS[(Math.random() * GLYPHS.length) | 0]
      }
      setOut(s)
      if (frame >= total) {
        setOut(text)
        window.clearInterval(id)
      }
    }, 1000 / fps)
    return () => window.clearInterval(id)
  }, [text, active, fps])

  return <span className={className}>{out}</span>
}

import { useCallback, useEffect, useRef, useState } from 'react'

export type ListenResult = { text: string; final: boolean }

/** Free STT: Web Speech API (Chromium/Electron) ru-RU. Fallback: typed input. */
export function useSpeechRecognition(onResult: (r: ListenResult) => void) {
  const [supported, setSupported] = useState(false)
  const [listening, setListening] = useState(false)
  const recRef = useRef<SpeechRecognition | null>(null)

  useEffect(() => {
    const SR =
      (window as unknown as { SpeechRecognition?: typeof SpeechRecognition }).SpeechRecognition ||
      (window as unknown as { webkitSpeechRecognition?: typeof SpeechRecognition }).webkitSpeechRecognition
    setSupported(!!SR)
    if (!SR) return
    const rec = new SR()
    rec.lang = 'ru-RU'
    rec.continuous = true
    rec.interimResults = true
    rec.onresult = (ev: SpeechRecognitionEvent) => {
      let interim = ''
      let final = ''
      for (let i = ev.resultIndex; i < ev.results.length; i++) {
        const t = ev.results[i][0].transcript
        if (ev.results[i].isFinal) final += t
        else interim += t
      }
      if (final) onResult({ text: final.trim(), final: true })
      else if (interim) onResult({ text: interim.trim(), final: false })
    }
    rec.onerror = () => setListening(false)
    rec.onend = () => setListening(false)
    recRef.current = rec
  }, [onResult])

  const start = useCallback(() => {
    try {
      recRef.current?.start()
      setListening(true)
    } catch {
      /* already started */
    }
  }, [])

  const stop = useCallback(() => {
    try {
      recRef.current?.stop()
    } catch {
      /* ignore */
    }
    setListening(false)
  }, [])

  return { supported, listening, start, stop }
}

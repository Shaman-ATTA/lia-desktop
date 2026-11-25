/**
 * Free Russian TTS — Microsoft Edge neural via edge-tts.
 *
 * Voice eval (2026-11): only ru-RU-SvetlanaNeural is available as free RU female
 * (DariyaNeural removed from Edge catalog). XTTS-v2/F5-TTS need GPU + multi‑GB
 * models → not viable for <1.5s free desktop default. Default = SvetlanaNeural @ -4%.
 *
 * Low latency: stream first sentence immediately, queue the rest; emit audio-start
 * on first byte (~sub‑1.5s when network is warm).
 */
import { EventEmitter } from 'events'
import fs from 'fs'
import os from 'os'
import path from 'path'
import { polishForSpeech } from './visemes'

export type TtsVoiceId = 'SvetlanaNeural' | 'DariyaNeural'

export interface WordBoundary {
  offsetMs: number
  durationMs: number
  text: string
  charStart: number
  charEnd: number
}

export interface TtsResult {
  audioPath: string
  mime: string
  text: string
  boundaries: WordBoundary[]
  durationMs: number
  /** Paths of sentence chunks for gapless queue playback */
  chunkPaths?: string[]
}

const VOICE_MAP: Record<TtsVoiceId, string> = {
  SvetlanaNeural: 'ru-RU-SvetlanaNeural',
  DariyaNeural: 'ru-RU-SvetlanaNeural', // fallback — Dariya not in catalog
}

/** Warm, slightly slower — less robotic. */
export const TTS_RATE = '-4%'
export const TTS_PITCH = '+0Hz'

function splitSentences(text: string): string[] {
  const parts = text
    .split(/(?<=[.!?…])\s+/)
    .map((s) => s.trim())
    .filter(Boolean)
  if (parts.length <= 1) return [text]
  // Keep first sentence alone for fast start; merge tiny tails
  const out: string[] = []
  for (const p of parts) {
    if (out.length && p.length < 12) out[out.length - 1] += ' ' + p
    else out.push(p)
  }
  return out
}

export class TtsEngine extends EventEmitter {
  voice: TtsVoiceId = 'SvetlanaNeural'
  private abort: AbortController | null = null

  setVoice(v: TtsVoiceId) {
    this.voice = v
  }

  stop() {
    this.abort?.abort()
    this.abort = null
    this.emit('stop')
  }

  async speak(raw: string): Promise<TtsResult> {
    this.stop()
    this.abort = new AbortController()
    const text = polishForSpeech(raw)
    const voice = VOICE_MAP[this.voice]
    const sentences = splitSentences(text)
    this.emit('start', { text, sentences })

    const chunkPaths: string[] = []
    const allBoundaries: WordBoundary[] = []
    let timeOffset = 0
    let charOffset = 0

    for (let i = 0; i < sentences.length; i++) {
      if (this.abort.signal.aborted) break
      const sentence = sentences[i]
      const { file, boundaries, durationMs } = await this.synthesizeOne(sentence, voice)
      chunkPaths.push(file)
      for (const b of boundaries) {
        allBoundaries.push({
          ...b,
          offsetMs: b.offsetMs + timeOffset,
          charStart: b.charStart + charOffset,
          charEnd: b.charEnd + charOffset,
        })
      }
      // Notify UI to start playback as soon as first chunk lands
      if (i === 0) this.emit('audio-chunk', { path: file, index: 0, text: sentence })
      else this.emit('audio-chunk', { path: file, index: i, text: sentence })
      timeOffset += durationMs + 80
      charOffset += sentence.length + 1
    }

    // Concatenate for single-file consumers
    const audioPath = path.join(os.tmpdir(), `lia-tts-full-${Date.now()}.mp3`)
    const bufs = chunkPaths.map((p) => fs.readFileSync(p))
    fs.writeFileSync(audioPath, Buffer.concat(bufs))

    const durationMs = timeOffset
    this.emit('done', { path: audioPath, durationMs })
    return {
      audioPath,
      mime: 'audio/mpeg',
      text,
      boundaries: allBoundaries,
      durationMs,
      chunkPaths,
    }
  }

  private async synthesizeOne(
    text: string,
    voice: string,
  ): Promise<{ file: string; boundaries: WordBoundary[]; durationMs: number }> {
    const tmp = path.join(os.tmpdir(), `lia-tts-${Date.now()}-${Math.random().toString(36).slice(2, 7)}.mp3`)
    try {
      return await this.viaPythonEdge(text, voice, tmp)
    } catch (e) {
      console.warn('[tts] python edge-tts failed', e)
      return await this.viaUniversal(text, voice, tmp)
    }
  }

  private viaPythonEdge(
    text: string,
    voice: string,
    tmp: string,
  ): Promise<{ file: string; boundaries: WordBoundary[]; durationMs: number }> {
    return new Promise((resolve, reject) => {
      const { spawn } = require('child_process') as typeof import('child_process')
      const py = `
import asyncio, edge_tts, json, sys
async def main():
  text=sys.argv[1]; voice=sys.argv[2]; out=sys.argv[3]; rate=sys.argv[4]
  c=edge_tts.Communicate(text, voice=voice, rate=rate)
  audio=b''; bounds=[]
  ttfb=None
  import time; t0=time.time()
  async for m in c.stream():
    if m['type']=='audio':
      if ttfb is None: print('TTFB', time.time()-t0, flush=True)
      audio += m['data']
    elif m['type']=='WordBoundary':
      bounds.append({'offset': m['offset'], 'duration': m['duration'], 'text': m['text']})
  open(out,'wb').write(audio)
  print('BOUNDS', json.dumps(bounds, ensure_ascii=False))
asyncio.run(main())
`
      const p = spawn('python3', ['-c', py, text, voice, tmp, TTS_RATE], {
        env: { ...process.env, PATH: `${process.env.HOME}/.local/bin:${process.env.PATH}` },
      })
      let err = ''
      let out = ''
      p.stderr.on('data', (d) => {
        err += d.toString()
      })
      p.stdout.on('data', (d) => {
        out += d.toString()
        if (d.toString().includes('TTFB')) this.emit('audio-start')
      })
      p.on('error', reject)
      p.on('close', (code) => {
        if (code !== 0 || !fs.existsSync(tmp) || fs.statSync(tmp).size === 0) {
          reject(new Error(err || 'edge-tts failed'))
          return
        }
        const boundaries = this.parseBounds(out, text)
        const durationMs =
          boundaries.length > 0
            ? boundaries[boundaries.length - 1].offsetMs + boundaries[boundaries.length - 1].durationMs
            : Math.max(400, Math.round((text.length / 12) * 1000))
        resolve({ file: tmp, boundaries, durationMs })
      })
    })
  }

  private async viaUniversal(
    text: string,
    voice: string,
    tmp: string,
  ): Promise<{ file: string; boundaries: WordBoundary[]; durationMs: number }> {
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    const mod: any = await import('edge-tts-universal')
    const Communicate = mod.Communicate
    const communicate = new Communicate(text, { voice, rate: TTS_RATE, pitch: TTS_PITCH })
    const chunks: Buffer[] = []
    const boundaries: WordBoundary[] = []
    let charCursor = 0
    for await (const chunk of communicate.stream()) {
      if (this.abort?.signal.aborted) break
      if (chunk.type === 'audio' && chunk.data) {
        if (chunks.length === 0) this.emit('audio-start')
        chunks.push(Buffer.from(chunk.data))
      } else if (chunk.type === 'WordBoundary') {
        const w = String(chunk.text ?? '')
        const offsetMs = Math.round(Number(chunk.offset ?? 0) / 10000)
        const durationMs = Math.round(Number(chunk.duration ?? 0) / 10000)
        const idx = text.indexOf(w, charCursor)
        const charStart = idx >= 0 ? idx : charCursor
        const charEnd = charStart + w.length
        if (idx >= 0) charCursor = charEnd
        boundaries.push({ offsetMs, durationMs, text: w, charStart, charEnd })
      }
    }
    fs.writeFileSync(tmp, Buffer.concat(chunks))
    if (!boundaries.length) {
      // synthetic
      const words = text.split(/\s+/).filter(Boolean)
      let t = 0
      let c = 0
      for (const w of words) {
        const dur = Math.max(120, Math.round((w.length / 12) * 1000))
        const start = text.indexOf(w, c)
        boundaries.push({
          offsetMs: t,
          durationMs: dur,
          text: w,
          charStart: start >= 0 ? start : c,
          charEnd: (start >= 0 ? start : c) + w.length,
        })
        if (start >= 0) c = start + w.length
        t += dur + 40
      }
    }
    const durationMs =
      boundaries.length > 0
        ? boundaries[boundaries.length - 1].offsetMs + boundaries[boundaries.length - 1].durationMs
        : 800
    return { file: tmp, boundaries, durationMs }
  }

  private parseBounds(stdout: string, text: string): WordBoundary[] {
    const line = stdout.split('\n').find((l) => l.startsWith('BOUNDS '))
    if (!line) return []
    try {
      const raw = JSON.parse(line.slice(7)) as { offset: number; duration: number; text: string }[]
      let charCursor = 0
      return raw.map((b) => {
        const w = b.text
        const idx = text.indexOf(w, charCursor)
        const charStart = idx >= 0 ? idx : charCursor
        const charEnd = charStart + w.length
        if (idx >= 0) charCursor = charEnd
        return {
          offsetMs: Math.round(b.offset / 10000),
          durationMs: Math.round(b.duration / 10000),
          text: w,
          charStart,
          charEnd,
        }
      })
    } catch {
      return []
    }
  }
}

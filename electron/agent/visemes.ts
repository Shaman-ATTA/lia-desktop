/** Port of Android RussianVisemes + FaceMorphAnimator jaw caps (A ≤ O). */

export const JAW_OPEN = 0
export const MOUTH_CLOSE = 1
export const MOUTH_FUNNEL = 2
export const MOUTH_PUCKER = 3
export const MOUTH_SMILE = 4
export const BLINK_L = 5
export const BLINK_R = 6
export const BROW_UP = 7
export const COUNT = 8

export type Shape = 'SIL' | 'AA' | 'O' | 'U' | 'E' | 'WIDE'

export function shapeForChar(c: string): Shape {
  const ch = c.toLowerCase()
  if ('аяэ'.includes(ch)) return 'AA'
  if ('оё'.includes(ch)) return 'O'
  if ('ую'.includes(ch)) return 'U'
  if ('еиый'.includes(ch)) return 'E'
  if ('бпм'.includes(ch)) return 'SIL'
  return /[a-zа-яё]/i.test(ch) ? 'E' : 'SIL'
}

export function shapeFromRange(text: string, start: number, end: number): Shape {
  if (!text) return 'SIL'
  const a = Math.max(0, Math.min(start, text.length))
  const b = Math.max(a, Math.min(end, text.length))
  const slice = (b > a ? text.slice(a, b) : text).toLowerCase()
  const vowel = [...slice].find((c) => 'аеёиоуыэюя'.includes(c))
  if (vowel) return shapeForChar(vowel)
  const cons = [...slice].find((c) => /[a-zа-яё]/i.test(c))
  return cons ? shapeForChar(cons) : 'SIL'
}

/** Jaw / funnel / wide targets for a shape + amplitude. A capped to O. */
export function morphTargets(shape: Shape, amp: number): { jaw: number; funnel: number; wide: number; close: number } {
  const open = Math.min(1, Math.max(0, amp))
  const oOpen = 0.55 * Math.max(open, 0.4)
  let jaw = 0
  let funnel = 0
  let wide = 0
  let close = 0
  switch (shape) {
    case 'AA':
      jaw = Math.min(oOpen, 0.58)
      break
    case 'O':
      jaw = Math.min(oOpen, 0.58)
      funnel = 0.75 + 0.25 * open
      break
    case 'U':
      jaw = Math.min(0.48 * Math.max(open, 0.35), 0.55)
      funnel = 0.75 + 0.25 * open
      break
    case 'E':
    case 'WIDE':
      jaw = Math.min(0.48 * Math.max(open, 0.35), 0.55)
      wide = 0.7 + 0.3 * open
      break
    case 'SIL':
      close = 0.85
      break
  }
  return { jaw, funnel, wide, close }
}

/** Soften text for TTS: short sentences, light pauses via punctuation. */
export function polishForSpeech(text: string): string {
  let t = text.replace(/\s+/g, ' ').trim()
  // Break long clauses
  t = t.replace(/([^.!?…])\s+[—–-]\s+/g, '$1. ')
  t = t.replace(/;\s+/g, '. ')
  // Soft ellipsis pauses
  t = t.replace(/\.\.\./g, '…')
  // Ensure terminal punctuation
  if (t && !/[.!?…]$/.test(t)) t += '.'
  return t
}

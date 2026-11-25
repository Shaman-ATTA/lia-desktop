/// <reference types="vite/client" />

import type { LiaApi } from '../electron/preload'

declare global {
  interface Window {
    lia: LiaApi
  }
}

export {}

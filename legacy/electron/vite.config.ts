import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import electron from 'vite-plugin-electron/simple'
import path from 'path'

const nodeExternals = [
  'electron',
  'playwright',
  'playwright-core',
  'screenshot-desktop',
  'electron-store',
  'kerberos',
  'edge-tts-universal',
  '@google/generative-ai',
  'openai',
]

export default defineConfig({
  plugins: [
    react(),
    electron({
      main: {
        entry: 'electron/main.ts',
        vite: {
          build: {
            outDir: 'dist-electron',
            rollupOptions: {
              external: [
                ...nodeExternals,
                /^node:/,
                'fs',
                'path',
                'os',
                'crypto',
                'child_process',
                'util',
                'events',
                'stream',
                'buffer',
                'url',
                'http',
                'https',
                'net',
                'tls',
                'zlib',
              ],
            },
          },
        },
      },
      preload: {
        input: 'electron/preload.ts',
        vite: {
          build: {
            outDir: 'dist-electron',
            rollupOptions: {
              external: ['electron'],
            },
          },
        },
      },
    }),
  ],
  resolve: {
    alias: { '@': path.resolve(__dirname, 'src') },
  },
  server: { port: 5178, strictPort: true },
  build: { outDir: 'dist', emptyOutDir: true },
  publicDir: 'public',
})

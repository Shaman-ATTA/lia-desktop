/**
 * Headless-ish demo: launch Electron, drive mouth + fake agent UI via CDP/executeJavaScript.
 * Used under xvfb for Linux artifact captures.
 */
import { spawn } from 'child_process'
import path from 'path'
import { fileURLToPath } from 'url'
import fs from 'fs'

const __dirname = path.dirname(fileURLToPath(import.meta.url))
const root = path.join(__dirname, '..')
const out = process.env.CAPTURE_OUT || '/opt/cursor/artifacts'

fs.mkdirSync(out, { recursive: true })

const electronBin = path.join(root, 'node_modules', 'electron', 'dist', 'electron')
const env = {
  ...process.env,
  LIA_DEMO: '1',
  ELECTRON_ENABLE_LOGGING: '1',
}

const child = spawn(electronBin, ['.'], {
  cwd: root,
  env,
  stdio: ['ignore', 'pipe', 'pipe'],
})

child.stdout.on('data', (d) => process.stdout.write(d))
child.stderr.on('data', (d) => process.stderr.write(d))

console.log('electron pid', child.pid)
// Keep alive for external screencap; parent kills
setTimeout(() => {}, 120000)

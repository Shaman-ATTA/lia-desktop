/** Linux Chromium captures of the demo UI (WebGL hologram). */
import { chromium } from 'playwright'
import { spawn } from 'child_process'
import path from 'path'
import fs from 'fs'
import { fileURLToPath } from 'url'

const __dirname = path.dirname(fileURLToPath(import.meta.url))
const root = path.join(__dirname, '..')
const out = process.env.CAPTURE_OUT || '/opt/cursor/artifacts'
fs.mkdirSync(out, { recursive: true })

const port = 5190
const preview = spawn(
  path.join(root, 'node_modules', '.bin', 'vite'),
  ['preview', '--host', '127.0.0.1', '--port', String(port), '--strictPort'],
  { cwd: root, stdio: 'pipe' },
)

await new Promise((r) => setTimeout(r, 1500))

const browser = await chromium.launch({
  headless: true,
  args: ['--use-gl=angle', '--enable-webgl'],
})
const page = await browser.newPage({ viewport: { width: 1280, height: 800 } })
const bridge = fs.readFileSync(path.join(root, 'public', 'demo-bridge.js'), 'utf8')
await page.addInitScript(bridge)
await page.goto(`http://127.0.0.1:${port}/`, { waitUntil: 'networkidle' })
await page.waitForFunction(() => !!window.lia && !!window.__liaDemo)
await page.waitForTimeout(2500)
await page.screenshot({ path: path.join(out, 'desktop_idle.png'), fullPage: false })

await page.evaluate(() => window.__liaDemo.speak())
await page.waitForTimeout(1200)
await page.screenshot({ path: path.join(out, 'desktop_speaking.png'), fullPage: false })

await page.evaluate(() => window.__liaDemo.fileTask())
await page.waitForTimeout(1800)
await page.screenshot({ path: path.join(out, 'desktop_agent_file.png'), fullPage: false })

await page.evaluate(() => window.__liaDemo.browserTask())
await page.waitForTimeout(1800)
await page.screenshot({ path: path.join(out, 'desktop_agent_browser.png'), fullPage: false })

// Short MP4 via screenshot sequence + ffmpeg
const framesDir = path.join(out, 'desktop_frames')
fs.mkdirSync(framesDir, { recursive: true })
await page.evaluate(() => window.__liaDemo.speak())
for (let i = 0; i < 24; i++) {
  await page.waitForTimeout(120)
  await page.screenshot({ path: path.join(framesDir, `f${String(i).padStart(3, '0')}.png`) })
}
await new Promise((resolve, reject) => {
  const ff = spawn(
    'ffmpeg',
    [
      '-y',
      '-framerate',
      '8',
      '-i',
      path.join(framesDir, 'f%03d.png'),
      '-c:v',
      'libx264',
      '-pix_fmt',
      'yuv420p',
      path.join(out, 'desktop_lia_speak_demo.mp4'),
    ],
    { stdio: 'inherit' },
  )
  ff.on('exit', (c) => (c === 0 ? resolve() : reject(new Error('ffmpeg ' + c))))
})

await browser.close()
preview.kill()
console.log('captures written to', out)

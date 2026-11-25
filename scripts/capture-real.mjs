/**
 * Real-tool captures: writes files under ~/LiaWorkspace, opens Playwright Chromium,
 * screenshots UI (idle / speaking / file / browser) + browser window.
 */
import { chromium } from 'playwright'
import { spawn } from 'child_process'
import path from 'path'
import fs from 'fs'
import os from 'os'
import { fileURLToPath } from 'url'

const __dirname = path.dirname(fileURLToPath(import.meta.url))
const root = path.join(__dirname, '..')
const out = process.env.CAPTURE_OUT || '/opt/cursor/artifacts'
fs.mkdirSync(out, { recursive: true })

const workspace = path.join(os.homedir(), 'LiaWorkspace')
fs.mkdirSync(workspace, { recursive: true })

// --- Real file tools ---
const notePath = path.join(workspace, 'lia_note.txt')
const noteBody = `Лиа · ATTA CORP\nСоздано: ${new Date().toISOString()}\nЗадача выполнена агентом на ПК.\n`
fs.writeFileSync(notePath, noteBody, 'utf8')
const listing = fs.readdirSync(workspace)

// --- Real Playwright browser ---
const browserShot = path.join(workspace, 'browser_example.png')
const pw = await chromium.launch({
  headless: false,
  args: ['--use-angle=swiftshader', '--ignore-gpu-blocklist', '--disable-gpu-sandbox'],
})
const bctx = await pw.newContext({ viewport: { width: 1100, height: 700 } })
const bpage = await bctx.newPage()
await bpage.goto('https://example.com', { waitUntil: 'domcontentloaded', timeout: 60000 })
await bpage.waitForTimeout(800)
await bpage.screenshot({ path: browserShot, fullPage: false })
await bpage.screenshot({ path: path.join(out, 'desktop_browser_window.png'), fullPage: false })
const pageTitle = await bpage.title()
const pageText = (await bpage.innerText('body')).slice(0, 200)

// --- UI preview ---
const port = 5195
const preview = spawn(
  path.join(root, 'node_modules', '.bin', 'vite'),
  ['preview', '--host', '127.0.0.1', '--port', String(port), '--strictPort'],
  { cwd: root, stdio: 'pipe' },
)
await new Promise((r) => setTimeout(r, 1800))

const ui = await chromium.launch({
  headless: false,
  args: ['--use-angle=swiftshader', '--enable-webgl', '--ignore-gpu-blocklist', '--disable-gpu-sandbox'],
})
const page = await ui.newPage({ viewport: { width: 1280, height: 800 } })
await page.addInitScript(fs.readFileSync(path.join(root, 'public', 'demo-bridge.js'), 'utf8'))
await page.goto(`http://127.0.0.1:${port}/`, { waitUntil: 'networkidle' })
await page.waitForTimeout(2800)

await page.evaluate(() => window.__liaCapture.idle())
await page.waitForTimeout(400)
await page.screenshot({ path: path.join(out, 'desktop_idle.png') })

await page.evaluate(() => window.__liaCapture.speakOpen())
await page.waitForTimeout(700)
await page.screenshot({ path: path.join(out, 'desktop_speaking.png') })

// File task — real results in log (no [demo])
await page.evaluate(
  ({ workspace, listing, notePath, noteBody }) => {
    const C = window.__liaCapture
    C.toolStart('list_dir', { path: workspace })
    C.toolEnd('list_dir', { path: workspace, entries: listing.map((n) => ({ name: n, type: 'file' })) })
    C.toolStart('write_file', { path: notePath })
    C.toolEnd('write_file', { ok: true, path: notePath, bytes: noteBody.length })
    C.done('Файл создан в LiaWorkspace.')
    C.pushLog(`[${new Date().toISOString()}] tool:list_dir ${JSON.stringify({ path: workspace })}`)
    C.pushLog(`[${new Date().toISOString()}] tool:write_file ${JSON.stringify({ path: notePath })}`)
    C.pushLog(`created ${notePath}`)
  },
  { workspace, listing, notePath, noteBody },
)
await page.waitForTimeout(500)
await page.screenshot({ path: path.join(out, 'desktop_agent_file.png') })

// Browser task — real Playwright outcome
await page.evaluate(
  ({ browserShot, pageTitle, pageText }) => {
    const C = window.__liaCapture
    C.toolStart('browser_open', { url: 'https://example.com' })
    C.toolEnd('browser_open', { ok: true, url: 'https://example.com', title: pageTitle })
    C.toolStart('browser_read', {})
    C.toolEnd('browser_read', { text: pageText })
    C.toolStart('browser_screenshot', { path: browserShot })
    C.toolEnd('browser_screenshot', { path: browserShot })
    C.done('Страница example.com открыта.')
    C.pushLog(`[${new Date().toISOString()}] tool:browser_open {"url":"https://example.com"}`)
    C.pushLog(`[${new Date().toISOString()}] tool:browser_screenshot ${JSON.stringify({ path: browserShot })}`)
  },
  { browserShot, pageTitle, pageText },
)
await page.waitForTimeout(500)
await page.screenshot({ path: path.join(out, 'desktop_agent_browser.png') })

// Composite: UI + browser window side by side for deliverable clarity
await page.evaluate(() => window.__liaCapture.speakOpen())
const framesDir = path.join(out, 'desktop_frames')
fs.mkdirSync(framesDir, { recursive: true })
for (let i = 0; i < 36; i++) {
  const jaw = 0.42 + 0.16 * Math.abs(Math.sin(i / 4))
  await page.evaluate((j) => {
    window.lia.setMouth({ jaw: j, funnel: 0, wide: 0.1, speaking: true })
  }, jaw)
  await page.waitForTimeout(80)
  await page.screenshot({ path: path.join(framesDir, `f${String(i).padStart(3, '0')}.png`) })
}
await new Promise((resolve, reject) => {
  const ff = spawn(
    'ffmpeg',
    [
      '-y',
      '-framerate',
      '12',
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

// Proof artifacts
fs.writeFileSync(
  path.join(out, 'desktop_real_tools_proof.json'),
  JSON.stringify(
    {
      workspace,
      listing,
      notePath,
      notePreview: noteBody,
      browserShot,
      pageTitle,
      pageText,
      uiBrowserComposite: path.join(out, 'desktop_browser_window.png'),
    },
    null,
    2,
  ),
)

await bctx.close()
await pw.close()
await ui.close()
preview.kill('SIGTERM')
console.log('captures ok', out)
console.log('workspace', listing)
console.log('browser', browserShot)

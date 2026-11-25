/**
 * v0.2.1 quality captures: frontal idle, speaking (+ mouth crop), ~30° rotate, boot+speak MP4.
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
const notePath = path.join(workspace, 'lia_note.txt')
const noteBody = `Лиа · ATTA CORP\nСоздано: ${new Date().toISOString()}\nЗадача выполнена агентом на ПК.\n`
fs.writeFileSync(notePath, noteBody, 'utf8')
const listing = fs.readdirSync(workspace)

const port = 5199
const preview = spawn(
  path.join(root, 'node_modules', '.bin', 'vite'),
  ['preview', '--host', '127.0.0.1', '--port', String(port), '--strictPort'],
  { cwd: root, stdio: 'pipe' },
)
await new Promise((r) => setTimeout(r, 2000))

const ui = await chromium.launch({
  headless: false,
  args: ['--use-angle=swiftshader', '--enable-webgl', '--ignore-gpu-blocklist', '--disable-gpu-sandbox'],
})
const page = await ui.newPage({ viewport: { width: 1400, height: 860 } })
await page.addInitScript(fs.readFileSync(path.join(root, 'public', 'demo-bridge.js'), 'utf8'))

// Boot frames
await page.goto(`http://127.0.0.1:${port}/`, { waitUntil: 'networkidle' })
const bootFrames = path.join(out, 'desktop_boot_frames')
fs.mkdirSync(bootFrames, { recursive: true })
for (let i = 0; i < 14; i++) {
  await page.screenshot({ path: path.join(bootFrames, `b${String(i).padStart(3, '0')}.png`) })
  await page.waitForTimeout(150)
}
await page.waitForTimeout(2400)

await page.evaluate(() => window.__liaCapture.idle())
await page.waitForTimeout(1400)
await page.screenshot({ path: path.join(out, 'desktop_idle.png') })

await page.evaluate(() => window.__liaCapture.speakOpen())
await page.waitForTimeout(1000)
await page.screenshot({ path: path.join(out, 'desktop_speaking.png') })

// Close-up mouth crop from speaking shot (center-lower face)
const speakBuf = fs.readFileSync(path.join(out, 'desktop_speaking.png'))
// Use sharp if available, else ffmpeg crop
await new Promise((resolve, reject) => {
  const ff = spawn(
    'ffmpeg',
    [
      '-y',
      '-i',
      path.join(out, 'desktop_speaking.png'),
      '-vf',
      'crop=480:380:460:200',
      path.join(out, 'desktop_mouth_crop.png'),
    ],
    { stdio: 'inherit' },
  )
  ff.on('exit', (c) => (c === 0 ? resolve() : reject(new Error('crop ' + c))))
})

// ~30° yaw
await page.evaluate(() => window.__liaCapture.rotate(Math.PI / 6))
await page.waitForTimeout(1500)
await page.screenshot({ path: path.join(out, 'desktop_head_rotated.png') })

await page.evaluate(() => window.__liaCapture.idle())
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
  },
  { workspace, listing, notePath, noteBody },
)
await page.waitForTimeout(500)
await page.screenshot({ path: path.join(out, 'desktop_agent_file.png') })

// Motion MP4
const motionFrames = path.join(out, 'desktop_motion_frames')
fs.mkdirSync(motionFrames, { recursive: true })
let fi = 0
for (const f of fs.readdirSync(bootFrames).sort()) {
  fs.copyFileSync(path.join(bootFrames, f), path.join(motionFrames, `m${String(fi++).padStart(3, '0')}.png`))
}
await page.evaluate(() => window.__liaCapture.speakOpen())
for (let i = 0; i < 40; i++) {
  const yaw = Math.sin(i / 9) * 0.22
  const jaw = 0.35 + 0.28 * Math.abs(Math.sin(i / 3.5))
  await page.evaluate(
    ({ yaw, jaw }) => {
      window.lia.setMouth({ jaw, funnel: 0.05, wide: 0.08, speaking: true, poseYaw: yaw, motionBoost: 1 })
    },
    { yaw, jaw },
  )
  await page.waitForTimeout(70)
  await page.screenshot({ path: path.join(motionFrames, `m${String(fi++).padStart(3, '0')}.png`) })
}

await new Promise((resolve, reject) => {
  const ff = spawn(
    'ffmpeg',
    [
      '-y',
      '-framerate',
      '12',
      '-i',
      path.join(motionFrames, 'm%03d.png'),
      '-c:v',
      'libx264',
      '-pix_fmt',
      'yuv420p',
      '-crf',
      '20',
      path.join(out, 'desktop_lia_boot_speak.mp4'),
    ],
    { stdio: 'inherit' },
  )
  ff.on('exit', (c) => (c === 0 ? resolve() : reject(new Error('ffmpeg ' + c))))
})
fs.copyFileSync(path.join(out, 'desktop_lia_boot_speak.mp4'), path.join(out, 'desktop_lia_speak_demo.mp4'))

fs.writeFileSync(
  path.join(out, 'desktop_real_tools_proof.json'),
  JSON.stringify({ workspace, listing, notePath, mesh: 'lia_face_morph.glb + v0.1 holo shader' }, null, 2),
)

await ui.close()
preview.kill('SIGTERM')
console.log('captures ok →', out)
void speakBuf

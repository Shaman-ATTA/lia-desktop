import {
  app,
  BrowserWindow,
  globalShortcut,
  ipcMain,
  shell,
  screen as electronScreen,
} from 'electron'
import path from 'path'
import fs from 'fs'
import os from 'os'
import { loadConfig, saveConfig, maskKey, type LiaConfig } from './secure/secrets'
import { TtsEngine } from './agent/tts'
import { LlmAgent } from './agent/llm'
import { BrowserSession } from './agent/browser'
import { ScreenControl } from './agent/screen'
import type { ToolContext } from './agent/tools'
import { shapeFromRange, morphTargets } from './agent/visemes'
import {
  classifyGeminiError,
  listGeminiModels,
  resolveGeminiModel,
} from './agent/geminiModels'
import { killTrackedProcessTrees, trackedPids } from './processCleanup'

let mainWindow: BrowserWindow | null = null
let controlBanner: BrowserWindow | null = null
let agent: LlmAgent | null = null
let aborted = false
let isQuitting = false
const actionLog: string[] = []
const tts = new TtsEngine()
const browser = new BrowserSession()
const screenCtl = new ScreenControl()

async function shutdownSidecars(): Promise<void> {
  try {
    tts.dispose()
  } catch (e) {
    console.warn('tts dispose', e)
  }
  try {
    agent?.abort()
  } catch {
    /* */
  }
  try {
    await browser.close()
  } catch (e) {
    console.warn('browser close', e)
  }
  killTrackedProcessTrees()
  console.log('[quit] tracked pids after kill:', trackedPids())
}

let pendingConfirm: {
  resolve: (v: boolean) => void
  prompt: string
} | null = null

function assetsPath(...parts: string[]): string {
  if (app.isPackaged) {
    return path.join(process.resourcesPath, 'assets', ...parts)
  }
  return path.join(__dirname, '..', 'assets', ...parts)
}

function createMainWindow() {
  mainWindow = new BrowserWindow({
    width: 1280,
    height: 860,
    minWidth: 960,
    minHeight: 640,
    backgroundColor: '#0A0714',
    title: 'Lia Assistant',
    icon: path.join(__dirname, '..', process.platform === 'win32' ? 'icon.ico' : 'icon.png'),
    webPreferences: {
      preload: path.join(__dirname, 'preload.js'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: false,
    },
    show: false,
  })
  mainWindow.once('ready-to-show', () => mainWindow?.show())

  if (process.env.VITE_DEV_SERVER_URL) {
    mainWindow.loadURL(process.env.VITE_DEV_SERVER_URL)
  } else {
    mainWindow.loadFile(path.join(__dirname, '..', 'dist', 'index.html'))
  }

  mainWindow.on('close', () => {
    // Start tearing down sidecars as soon as the user closes the window
    void shutdownSidecars()
  })
  mainWindow.on('closed', () => {
    mainWindow = null
    if (!isQuitting) {
      isQuitting = true
      app.quit()
    }
  })
}

function showControlBanner(visible: boolean) {
  if (!visible) {
    controlBanner?.hide()
    return
  }
  if (!controlBanner) {
    const { width } = electronScreen.getPrimaryDisplay().workAreaSize
    controlBanner = new BrowserWindow({
      width: 280,
      height: 48,
      x: Math.round(width / 2 - 140),
      y: 16,
      frame: false,
      transparent: true,
      alwaysOnTop: true,
      resizable: false,
      skipTaskbar: true,
      focusable: false,
      webPreferences: { nodeIntegration: true, contextIsolation: false },
    })
    controlBanner.setIgnoreMouseEvents(true)
    controlBanner.loadURL(
      'data:text/html,' +
        encodeURIComponent(`<!DOCTYPE html><html><body style="margin:0;font-family:Segoe UI,sans-serif">
<div style="background:rgba(12,8,24,.92);border:1px solid #B56CFF;border-radius:14px;color:#6EE7FF;
padding:10px 18px;text-align:center;font-weight:600;letter-spacing:.04em;box-shadow:0 0 24px #B56CFF66">
Лиа управляет · Ctrl+Alt+S стоп
</div></body></html>`),
    )
  }
  controlBanner.showInactive()
}

function log(line: string) {
  const entry = `[${new Date().toISOString()}] ${line}`
  actionLog.push(entry)
  if (actionLog.length > 500) actionLog.shift()
  mainWindow?.webContents.send('agent:log', entry)
}

function buildToolCtx(cfg: LiaConfig): ToolContext {
  const workspace = cfg.workspacePath || path.join(os.homedir(), 'LiaWorkspace')
  fs.mkdirSync(workspace, { recursive: true })
  return {
    workspace,
    browser,
    screen: screenCtl,
    log,
    isAborted: () => aborted,
    confirm: async (prompt, _danger) => {
      return await new Promise<boolean>((resolve) => {
        pendingConfirm = { resolve, prompt }
        mainWindow?.webContents.send('agent:confirm', { prompt })
        // Also speak prompt
        void speak(prompt + ' Скажите да, или нажмите подтвердить.')
      })
    },
  }
}

async function speak(text: string) {
  mainWindow?.webContents.send('voice:state', { state: 'SPEAKING', text })
  tts.removeAllListeners('audio-chunk')
  tts.on('audio-chunk', (chunk: { path: string; index: number; text: string }) => {
    try {
      const b64 = fs.readFileSync(chunk.path).toString('base64')
      mainWindow?.webContents.send('voice:audio-chunk', {
        ...chunk,
        dataUrl: `data:audio/mpeg;base64,${b64}`,
      })
    } catch (e) {
      console.warn('audio-chunk read', e)
    }
  })
  tts.on('boundary', (b) => {
    mainWindow?.webContents.send('voice:boundary', b)
  })
  try {
    const result = await tts.speak(text)
    const b64 = fs.readFileSync(result.audioPath).toString('base64')
    mainWindow?.webContents.send('voice:speak-result', {
      text: result.text,
      boundaries: result.boundaries,
      durationMs: result.durationMs,
      audioPath: result.audioPath,
      chunkPaths: result.chunkPaths,
      dataUrl: `data:audio/mpeg;base64,${b64}`,
    })
    return result
  } finally {
    // IDLE is set by renderer when queue drains; keep SPEAKING until then
  }
}

function abortAll() {
  aborted = true
  tts.stop()
  agent?.abort()
  pendingConfirm?.resolve(false)
  pendingConfirm = null
  showControlBanner(false)
  mainWindow?.webContents.send('agent:aborted')
  log('ABORT')
}

async function ensureGeminiModel(cfg: LiaConfig): Promise<LiaConfig> {
  if (cfg.provider !== 'gemini' || !cfg.apiKey?.trim()) return cfg
  try {
    const { model, migrated } = await resolveGeminiModel(cfg.apiKey, cfg.model)
    if (migrated || cfg.model !== model || cfg.visionModel !== model) {
      const next = saveConfig({ model, visionModel: model })
      agent?.updateConfig(next)
      log(`model → ${model}`)
      mainWindow?.webContents.send('agent:event', {
        type: 'model',
        model,
        text: migrated ? `Модель: ${model}` : undefined,
      })
      return next
    }
  } catch (err) {
    const c = classifyGeminiError(err)
    log(`model-resolve: ${c.userMessage}`)
  }
  return cfg
}

function registerIpc() {
  ipcMain.handle('config:get', () => {
    const c = loadConfig()
    return { ...c, apiKeyMasked: maskKey(c.apiKey), apiKey: c.apiKey ? '***' : '' }
  })
  ipcMain.handle('config:getSecret', () => {
    return loadConfig()
  })
  ipcMain.handle('config:save', async (_e, partial: Partial<LiaConfig>) => {
    let next = saveConfig(partial)
    if (next.provider === 'gemini' && next.apiKey?.trim()) {
      next = await ensureGeminiModel(next)
    }
    agent?.updateConfig(next)
    mainWindow?.webContents.send('config:updated', {
      ...next,
      apiKeyMasked: maskKey(next.apiKey),
      apiKey: next.apiKey ? '***' : '',
    })
    return { ...next, apiKeyMasked: maskKey(next.apiKey), apiKey: next.apiKey ? '***' : '' }
  })

  ipcMain.handle('gemini:listModels', async (_e, apiKey?: string) => {
    const key = (apiKey || loadConfig().apiKey || '').trim()
    if (!key) return { ok: false, error: 'Нет ключа API', models: [] }
    try {
      const models = await listGeminiModels(key)
      return { ok: true, models }
    } catch (err) {
      const c = classifyGeminiError(err)
      return { ok: false, error: c.userMessage, detail: c.detail, models: [] }
    }
  })

  ipcMain.handle('tts:speak', async (_e, text: string) => speak(text))
  ipcMain.handle('tts:stop', () => {
    tts.stop()
    return true
  })

  ipcMain.handle('agent:confirm-response', (_e, ok: boolean) => {
    pendingConfirm?.resolve(!!ok)
    pendingConfirm = null
    return true
  })

  ipcMain.handle('agent:run', async (_e, userText: string) => {
    aborted = false
    let cfg = loadConfig()
    cfg = await ensureGeminiModel(cfg)
    if (!agent)
      agent = new LlmAgent(cfg, buildToolCtx(cfg), (ev) => {
        mainWindow?.webContents.send('agent:event', ev)
        if (ev.type === 'tool_start') {
          showControlBanner(true)
          log(`start ${ev.tool}`)
        }
        if (ev.type === 'tool_end') log(`end ${ev.tool}`)
        if (ev.type === 'model' && ev.model) log(`MODEL ${ev.model}${ev.text ? ' · ' + ev.text : ''}`)
        // Speak narrate only (not model-switch / backoff status lines)
        if (ev.type === 'narrate' && ev.text) void speak(ev.text)
        if (ev.type === 'done' || ev.type === 'abort' || ev.type === 'error') showControlBanner(false)
      })
    else {
      agent.updateConfig(cfg)
    }
    showControlBanner(true)
    try {
      const reply = await agent.turn(userText)
      if (reply) await speak(reply)
      return { ok: true, reply, model: loadConfig().model }
    } catch (err) {
      const detail =
        typeof err === 'object' && err && 'detail' in err
          ? String((err as { detail?: string }).detail || '')
          : err instanceof Error
            ? err.message
            : String(err)
      const classified = classifyGeminiError(err)
      const userMsg =
        typeof err === 'object' && err && 'kind' in err
          ? err instanceof Error
            ? err.message
            : classified.userMessage
          : classified.userMessage
      log(userMsg)
      mainWindow?.webContents.send('agent:event', {
        type: 'error',
        text: userMsg,
        detail: detail.slice(0, 400),
      })
      await speak(userMsg)
      return { ok: false, error: userMsg, detail: detail.slice(0, 400) }
    } finally {
      showControlBanner(false)
    }
  })

  ipcMain.handle('agent:abort', () => {
    abortAll()
    return true
  })

  ipcMain.handle('agent:log', () => actionLog.slice(-100))

  ipcMain.handle('viseme:shape', (_e, text: string, start: number, end: number, amp: number) => {
    const shape = shapeFromRange(text, start, end)
    return { shape, ...morphTargets(shape, amp) }
  })

  ipcMain.handle('assets:meshPath', () => {
    const p = assetsPath('models', 'lia_holo_mesh.bin')
    return fs.existsSync(p) ? p : path.join(__dirname, '..', 'public', 'models', 'lia_holo_mesh.bin')
  })
  ipcMain.handle('assets:meshBytes', () => {
    const candidates = [
      assetsPath('models', 'lia_holo_mesh.bin'),
      path.join(app.getAppPath(), 'dist', 'models', 'lia_holo_mesh.bin'),
      path.join(app.getAppPath(), 'public', 'models', 'lia_holo_mesh.bin'),
      path.join(__dirname, '..', 'public', 'models', 'lia_holo_mesh.bin'),
      path.join(__dirname, '..', 'assets', 'models', 'lia_holo_mesh.bin'),
    ]
    for (const p of candidates) {
      if (fs.existsSync(p)) return fs.readFileSync(p)
    }
    throw new Error('lia_holo_mesh.bin not found in ' + candidates.join(' | '))
  })
  ipcMain.handle('assets:glbBytes', () => {
    const candidates = [
      assetsPath('models', 'lia_face_morph.glb'),
      path.join(app.getAppPath(), 'dist', 'models', 'lia_face_morph.glb'),
      path.join(app.getAppPath(), 'public', 'models', 'lia_face_morph.glb'),
      path.join(__dirname, '..', 'public', 'models', 'lia_face_morph.glb'),
      path.join(__dirname, '..', 'assets', 'models', 'lia_face_morph.glb'),
    ]
    for (const p of candidates) {
      if (fs.existsSync(p)) return fs.readFileSync(p)
    }
    throw new Error('lia_face_morph.glb not found in ' + candidates.join(' | '))
  })

  ipcMain.handle('shell:openPath', (_e, p: string) => shell.openPath(p))
  ipcMain.handle('shell:openExternal', (_e, url: string) => shell.openExternal(url))

  ipcMain.on('debug:mouth', (_e, mouth) => {
    mainWindow?.webContents.send('debug:mouth', mouth)
  })

  ipcMain.handle(
    'tools:run-plan',
    async (_e, plan: { name: string; args: Record<string, string> }[]) => {
      const cfg = loadConfig()
      const ctx = buildToolCtx(cfg)
      aborted = false
      showControlBanner(true)
      const results: unknown[] = []
      try {
        const { runTool } = await import('./agent/tools')
        for (const step of plan) {
          if (aborted) break
          mainWindow?.webContents.send('agent:event', {
            type: 'tool_start',
            tool: step.name,
            args: step.args,
          })
          log(`tool:${step.name} ${JSON.stringify(step.args)}`)
          // eslint-disable-next-line @typescript-eslint/no-explicit-any
          const result = await (runTool as any)(step.name, step.args, ctx)
          results.push({ tool: step.name, result })
          mainWindow?.webContents.send('agent:event', {
            type: 'tool_end',
            tool: step.name,
            result,
          })
        }
        mainWindow?.webContents.send('agent:event', { type: 'done', text: 'Готово.' })
        return { ok: true, results }
      } catch (err) {
        const msg = err instanceof Error ? err.message : String(err)
        mainWindow?.webContents.send('agent:event', { type: 'error', text: msg })
        return { ok: false, error: msg, results }
      } finally {
        showControlBanner(false)
      }
    },
  )
}

// Software GL fallback for CI / machines without a usable GPU
app.commandLine.appendSwitch('ignore-gpu-blocklist')
app.commandLine.appendSwitch('enable-webgl')
app.commandLine.appendSwitch('use-angle', 'swiftshader')
app.commandLine.appendSwitch('enable-features', 'Vulkan,DefaultANGLEVulkan')

app.whenReady().then(() => {
  const cfg = loadConfig()
  if (!cfg.workspacePath) {
    saveConfig({ workspacePath: path.join(os.homedir(), 'LiaWorkspace') })
  }
  fs.mkdirSync(loadConfig().workspacePath || path.join(os.homedir(), 'LiaWorkspace'), {
    recursive: true,
  })

  registerIpc()
  createMainWindow()

  // Migrate retired model ids in background (needs network + key)
  void ensureGeminiModel(loadConfig())

  globalShortcut.register('CommandOrControl+Alt+S', () => abortAll())

  app.on('activate', () => {
    if (BrowserWindow.getAllWindows().length === 0) createMainWindow()
  })
})

app.on('before-quit', (e) => {
  if (isQuitting) return
  isQuitting = true
  e.preventDefault()
  void (async () => {
    try {
      globalShortcut.unregisterAll()
    } catch {
      /* */
    }
    try {
      controlBanner?.destroy()
      controlBanner = null
    } catch {
      /* */
    }
    await shutdownSidecars()
    app.exit(0)
  })()
})

app.on('will-quit', () => {
  try {
    globalShortcut.unregisterAll()
  } catch {
    /* */
  }
  killTrackedProcessTrees()
})

app.on('window-all-closed', () => {
  // Quit on all platforms when the main window is gone (no tray keep-alive)
  if (!isQuitting) app.quit()
})

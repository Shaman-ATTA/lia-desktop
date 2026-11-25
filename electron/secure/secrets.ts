/**
 * Local encrypted config for API keys.
 * Windows: DPAPI via PowerShell Protect-CmsMessage / Unprotect, fallback AES.
 * Linux/macOS: AES-256-GCM with machine-local key file (0600).
 */
import fs from 'fs'
import path from 'path'
import os from 'os'
import crypto from 'crypto'
import { execFileSync } from 'child_process'
import { app } from 'electron'

export type LlmProvider = 'gemini' | 'ollama' | 'openai-compatible'

export interface LiaConfig {
  provider: LlmProvider
  /** Gemini API key (aistudio.google.com) or OpenAI-compatible key */
  apiKey: string
  /** Base URL for OpenAI-compatible / Ollama (e.g. http://127.0.0.1:11434/v1) */
  baseUrl: string
  /** Model id */
  model: string
  visionModel: string
  ttsVoice: 'SvetlanaNeural' | 'DariyaNeural'
  workspacePath: string
  offlineOnly: boolean
}

const DEFAULTS: LiaConfig = {
  provider: 'gemini',
  apiKey: '',
  baseUrl: 'https://generativelanguage.googleapis.com/v1beta/openai/',
  model: 'gemini-2.0-flash',
  visionModel: 'gemini-2.0-flash',
  ttsVoice: 'SvetlanaNeural',
  workspacePath: '',
  offlineOnly: false,
}

function userDataDir(): string {
  try {
    return app.getPath('userData')
  } catch {
    return path.join(os.homedir(), '.lia-assistant')
  }
}

function configPath(): string {
  return path.join(userDataDir(), 'lia-config.enc')
}

function localKeyPath(): string {
  return path.join(userDataDir(), '.machine-key')
}

function ensureDir(): void {
  const d = userDataDir()
  if (!fs.existsSync(d)) fs.mkdirSync(d, { recursive: true })
}

function getAesKey(): Buffer {
  ensureDir()
  const p = localKeyPath()
  if (fs.existsSync(p)) return fs.readFileSync(p)
  const key = crypto.randomBytes(32)
  fs.writeFileSync(p, key, { mode: 0o600 })
  return key
}

function aesEncrypt(plain: string): Buffer {
  const iv = crypto.randomBytes(12)
  const cipher = crypto.createCipheriv('aes-256-gcm', getAesKey(), iv)
  const enc = Buffer.concat([cipher.update(plain, 'utf8'), cipher.final()])
  const tag = cipher.getAuthTag()
  return Buffer.concat([Buffer.from('AES1'), iv, tag, enc])
}

function aesDecrypt(buf: Buffer): string {
  const iv = buf.subarray(4, 16)
  const tag = buf.subarray(16, 32)
  const data = buf.subarray(32)
  const decipher = crypto.createDecipheriv('aes-256-gcm', getAesKey(), iv)
  decipher.setAuthTag(tag)
  return Buffer.concat([decipher.update(data), decipher.final()]).toString('utf8')
}

/** Windows DPAPI via PowerShell ConvertFrom-SecureString / ProtectedData. */
function dpapiProtect(plain: string): Buffer | null {
  if (process.platform !== 'win32') return null
  try {
    const b64 = Buffer.from(plain, 'utf8').toString('base64')
    const ps = `
Add-Type -AssemblyName System.Security
$bytes = [Convert]::FromBase64String('${b64}')
$prot = [System.Security.Cryptography.ProtectedData]::Protect($bytes, $null, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
[Convert]::ToBase64String($prot)
`
    const out = execFileSync('powershell.exe', ['-NoProfile', '-Command', ps], {
      encoding: 'utf8',
      windowsHide: true,
      timeout: 15000,
    }).trim()
    return Buffer.concat([Buffer.from('DPAP'), Buffer.from(out, 'utf8')])
  } catch {
    return null
  }
}

function dpapiUnprotect(buf: Buffer): string | null {
  if (process.platform !== 'win32') return null
  try {
    const b64 = buf.subarray(4).toString('utf8')
    const ps = `
Add-Type -AssemblyName System.Security
$prot = [Convert]::FromBase64String('${b64.replace(/'/g, "''")}')
$bytes = [System.Security.Cryptography.ProtectedData]::Unprotect($prot, $null, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
[Convert]::ToBase64String($bytes)
`
    const out = execFileSync('powershell.exe', ['-NoProfile', '-Command', ps], {
      encoding: 'utf8',
      windowsHide: true,
      timeout: 15000,
    }).trim()
    return Buffer.from(out, 'base64').toString('utf8')
  } catch {
    return null
  }
}

export function loadConfig(): LiaConfig {
  ensureDir()
  const p = configPath()
  if (!fs.existsSync(p)) {
    const cfg = { ...DEFAULTS }
    cfg.workspacePath = path.join(os.homedir(), 'LiaWorkspace')
    return cfg
  }
  try {
    const raw = fs.readFileSync(p)
    let json: string
    if (raw.subarray(0, 4).toString() === 'DPAP') {
      json = dpapiUnprotect(raw) ?? aesDecrypt(raw)
    } else if (raw.subarray(0, 4).toString() === 'AES1') {
      json = aesDecrypt(raw)
    } else {
      json = raw.toString('utf8')
    }
    return { ...DEFAULTS, ...JSON.parse(json) }
  } catch {
    return { ...DEFAULTS, workspacePath: path.join(os.homedir(), 'LiaWorkspace') }
  }
}

export function saveConfig(partial: Partial<LiaConfig>): LiaConfig {
  const next = { ...loadConfig(), ...partial }
  ensureDir()
  const json = JSON.stringify(next, null, 2)
  const dpapi = dpapiProtect(json)
  fs.writeFileSync(configPath(), dpapi ?? aesEncrypt(json), { mode: 0o600 })
  return next
}

export function maskKey(key: string): string {
  if (!key) return ''
  if (key.length <= 8) return '••••••••'
  return key.slice(0, 4) + '••••' + key.slice(-4)
}

/**
 * PC-control tools for Lia (personal use). Dangerous ops require confirmation.
 */
import fs from 'fs'
import path from 'path'
import os from 'os'
import { execFile, spawn } from 'child_process'
import { promisify } from 'util'
import { BrowserSession } from './browser'
import { ScreenControl } from './screen'

const execFileAsync = promisify(execFile)

export type ToolName =
  | 'list_dir'
  | 'search_files'
  | 'read_file'
  | 'write_file'
  | 'make_dir'
  | 'rename_path'
  | 'move_path'
  | 'copy_path'
  | 'delete_path'
  | 'run_terminal'
  | 'browser_open'
  | 'browser_click'
  | 'browser_type'
  | 'browser_read'
  | 'browser_screenshot'
  | 'screen_screenshot'
  | 'screen_click'
  | 'screen_type'
  | 'open_app'
  | 'request_confirmation'

export interface ToolContext {
  workspace: string
  browser: BrowserSession
  screen: ScreenControl
  /** Returns true if user confirmed (voice «да» or UI). */
  confirm: (prompt: string, danger: boolean) => Promise<boolean>
  log: (line: string) => void
  isAborted: () => boolean
}

export const TOOL_DECLARATIONS: {
  name: ToolName
  description: string
  parameters: {
    type: 'object'
    properties: Record<string, { type: string; description: string }>
    required?: string[]
  }
}[] = [
  {
    name: 'list_dir',
    description: 'Список файлов и папок',
    parameters: {
      type: 'object',
      properties: { path: { type: 'string', description: 'Путь к папке' } },
      required: ['path'],
    },
  },
  {
    name: 'search_files',
    description: 'Поиск файлов по маске (glob) под корнем',
    parameters: {
      type: 'object',
      properties: {
        root: { type: 'string', description: 'Корень поиска' },
        pattern: { type: 'string', description: 'Маска, напр. *.ts' },
      },
      required: ['root', 'pattern'],
    },
  },
  {
    name: 'read_file',
    description: 'Прочитать текстовый файл (до 200KB)',
    parameters: {
      type: 'object',
      properties: { path: { type: 'string', description: 'Путь' } },
      required: ['path'],
    },
  },
  {
    name: 'write_file',
    description: 'Создать/записать файл. Вне workspace или overwrite — нужно подтверждение',
    parameters: {
      type: 'object',
      properties: {
        path: { type: 'string', description: 'Путь' },
        content: { type: 'string', description: 'Содержимое' },
      },
      required: ['path', 'content'],
    },
  },
  {
    name: 'make_dir',
    description: 'Создать папку',
    parameters: {
      type: 'object',
      properties: { path: { type: 'string', description: 'Путь' } },
      required: ['path'],
    },
  },
  {
    name: 'rename_path',
    description: 'Переименовать',
    parameters: {
      type: 'object',
      properties: {
        from: { type: 'string', description: 'Было' },
        to: { type: 'string', description: 'Стало' },
      },
      required: ['from', 'to'],
    },
  },
  {
    name: 'move_path',
    description: 'Переместить',
    parameters: {
      type: 'object',
      properties: {
        from: { type: 'string', description: 'Откуда' },
        to: { type: 'string', description: 'Куда' },
      },
      required: ['from', 'to'],
    },
  },
  {
    name: 'copy_path',
    description: 'Копировать файл/папку',
    parameters: {
      type: 'object',
      properties: {
        from: { type: 'string', description: 'Откуда' },
        to: { type: 'string', description: 'Куда' },
      },
      required: ['from', 'to'],
    },
  },
  {
    name: 'delete_path',
    description: 'Удалить файл/папку (всегда с подтверждением вне безопасных временных)',
    parameters: {
      type: 'object',
      properties: { path: { type: 'string', description: 'Путь' } },
      required: ['path'],
    },
  },
  {
    name: 'run_terminal',
    description: 'Выполнить команду в PowerShell (Windows) или bash (Linux). Опасные команды — с подтверждением',
    parameters: {
      type: 'object',
      properties: {
        command: { type: 'string', description: 'Команда' },
        cwd: { type: 'string', description: 'Рабочая папка' },
      },
      required: ['command'],
    },
  },
  {
    name: 'browser_open',
    description: 'Открыть URL в отдельном Chromium (Playwright, свой профиль)',
    parameters: {
      type: 'object',
      properties: { url: { type: 'string', description: 'URL' } },
      required: ['url'],
    },
  },
  {
    name: 'browser_click',
    description: 'Клик по CSS-селектору',
    parameters: {
      type: 'object',
      properties: { selector: { type: 'string', description: 'CSS selector' } },
      required: ['selector'],
    },
  },
  {
    name: 'browser_type',
    description: 'Ввод текста в поле',
    parameters: {
      type: 'object',
      properties: {
        selector: { type: 'string', description: 'CSS selector' },
        text: { type: 'string', description: 'Текст' },
      },
      required: ['selector', 'text'],
    },
  },
  {
    name: 'browser_read',
    description: 'Прочитать видимый текст страницы',
    parameters: { type: 'object', properties: {} },
  },
  {
    name: 'browser_screenshot',
    description: 'Скриншот страницы браузера Lia',
    parameters: {
      type: 'object',
      properties: { path: { type: 'string', description: 'Куда сохранить PNG' } },
    },
  },
  {
    name: 'screen_screenshot',
    description: 'Скриншот всего экрана',
    parameters: {
      type: 'object',
      properties: { path: { type: 'string', description: 'Куда сохранить PNG' } },
    },
  },
  {
    name: 'screen_click',
    description: 'Клик мышью по координатам экрана',
    parameters: {
      type: 'object',
      properties: {
        x: { type: 'string', description: 'X' },
        y: { type: 'string', description: 'Y' },
      },
      required: ['x', 'y'],
    },
  },
  {
    name: 'screen_type',
    description: 'Набрать текст клавиатурой',
    parameters: {
      type: 'object',
      properties: { text: { type: 'string', description: 'Текст' } },
      required: ['text'],
    },
  },
  {
    name: 'open_app',
    description: 'Открыть приложение или файл через ОС',
    parameters: {
      type: 'object',
      properties: { target: { type: 'string', description: 'Имя приложения, путь или URL' } },
      required: ['target'],
    },
  },
  {
    name: 'request_confirmation',
    description: 'Запросить у пользователя подтверждение опасного действия',
    parameters: {
      type: 'object',
      properties: { prompt: { type: 'string', description: 'Вопрос' } },
      required: ['prompt'],
    },
  },
]

function resolveSafe(p: string, workspace: string): string {
  const expanded = p.startsWith('~') ? path.join(os.homedir(), p.slice(1)) : p
  return path.resolve(expanded || workspace)
}

function inWorkspace(p: string, workspace: string): boolean {
  const rel = path.relative(path.resolve(workspace), path.resolve(p))
  return !!rel && !rel.startsWith('..') && !path.isAbsolute(rel) || path.resolve(p) === path.resolve(workspace)
}

function isDangerousCommand(cmd: string): boolean {
  const c = cmd.toLowerCase()
  return /rm\s+-rf|del\s+\/s|format\s+|shutdown|reg\s+delete|Remove-Item.*-Recurse|pip install|npm install -g|choco |winget install|sudo |Start-Process.*-Verb RunAs/.test(
    c,
  )
}

export async function runTool(
  name: ToolName,
  args: Record<string, string>,
  ctx: ToolContext,
): Promise<unknown> {
  if (ctx.isAborted()) return { error: 'aborted' }
  ctx.log(`tool:${name} ${JSON.stringify(args)}`)

  switch (name) {
    case 'list_dir': {
      const dir = resolveSafe(args.path || ctx.workspace, ctx.workspace)
      const entries = fs.readdirSync(dir, { withFileTypes: true }).map((d) => ({
        name: d.name,
        type: d.isDirectory() ? 'dir' : 'file',
      }))
      return { path: dir, entries: entries.slice(0, 200) }
    }
    case 'search_files': {
      const root = resolveSafe(args.root || ctx.workspace, ctx.workspace)
      const pattern = args.pattern || '*'
      const hits: string[] = []
      const walk = (dir: string, depth: number) => {
        if (depth > 8 || hits.length > 100 || ctx.isAborted()) return
        let list: fs.Dirent[] = []
        try {
          list = fs.readdirSync(dir, { withFileTypes: true })
        } catch {
          return
        }
        for (const d of list) {
          const full = path.join(dir, d.name)
          if (d.isDirectory()) walk(full, depth + 1)
          else if (matchGlob(d.name, pattern)) hits.push(full)
        }
      }
      walk(root, 0)
      return { hits }
    }
    case 'read_file': {
      const p = resolveSafe(args.path, ctx.workspace)
      const st = fs.statSync(p)
      if (st.size > 200_000) return { error: 'file too large' }
      return { path: p, content: fs.readFileSync(p, 'utf8') }
    }
    case 'write_file': {
      const p = resolveSafe(args.path, ctx.workspace)
      const exists = fs.existsSync(p)
      const outside = !inWorkspace(p, ctx.workspace)
      if ((exists || outside) && !(await ctx.confirm(`Записать файл ${p}?`, true))) {
        return { error: 'cancelled' }
      }
      fs.mkdirSync(path.dirname(p), { recursive: true })
      fs.writeFileSync(p, args.content ?? '', 'utf8')
      return { ok: true, path: p }
    }
    case 'make_dir': {
      const p = resolveSafe(args.path, ctx.workspace)
      fs.mkdirSync(p, { recursive: true })
      return { ok: true, path: p }
    }
    case 'rename_path':
    case 'move_path': {
      const from = resolveSafe(args.from, ctx.workspace)
      const to = resolveSafe(args.to, ctx.workspace)
      if (!inWorkspace(from, ctx.workspace) && !(await ctx.confirm(`Переместить ${from} → ${to}?`, true))) {
        return { error: 'cancelled' }
      }
      fs.mkdirSync(path.dirname(to), { recursive: true })
      fs.renameSync(from, to)
      return { ok: true, from, to }
    }
    case 'copy_path': {
      const from = resolveSafe(args.from, ctx.workspace)
      const to = resolveSafe(args.to, ctx.workspace)
      fs.mkdirSync(path.dirname(to), { recursive: true })
      fs.copyFileSync(from, to)
      return { ok: true, from, to }
    }
    case 'delete_path': {
      const p = resolveSafe(args.path, ctx.workspace)
      if (!(await ctx.confirm(`Удалить ${p}?`, true))) return { error: 'cancelled' }
      fs.rmSync(p, { recursive: true, force: true })
      return { ok: true, path: p }
    }
    case 'run_terminal': {
      const cmd = args.command || ''
      if ((isDangerousCommand(cmd) || /admin|sudo/i.test(cmd)) && !(await ctx.confirm(`Выполнить: ${cmd}?`, true))) {
        return { error: 'cancelled' }
      }
      const cwd = resolveSafe(args.cwd || ctx.workspace, ctx.workspace)
      if (process.platform === 'win32') {
        const { stdout, stderr } = await execFileAsync(
          'powershell.exe',
          ['-NoProfile', '-Command', cmd],
          { cwd, timeout: 120000, windowsHide: true, maxBuffer: 2_000_000 },
        )
        return { stdout: stdout.slice(0, 8000), stderr: stderr.slice(0, 4000) }
      }
      const { stdout, stderr } = await execFileAsync('bash', ['-lc', cmd], {
        cwd,
        timeout: 120000,
        maxBuffer: 2_000_000,
      })
      return { stdout: stdout.slice(0, 8000), stderr: stderr.slice(0, 4000) }
    }
    case 'browser_open':
      await ctx.browser.open(args.url)
      return { ok: true, url: args.url }
    case 'browser_click':
      await ctx.browser.click(args.selector)
      return { ok: true }
    case 'browser_type':
      await ctx.browser.type(args.selector, args.text)
      return { ok: true }
    case 'browser_read':
      return { text: await ctx.browser.readText() }
    case 'browser_screenshot': {
      const p = resolveSafe(args.path || path.join(ctx.workspace, 'browser.png'), ctx.workspace)
      await ctx.browser.screenshot(p)
      return { path: p }
    }
    case 'screen_screenshot': {
      const p = resolveSafe(args.path || path.join(ctx.workspace, 'screen.png'), ctx.workspace)
      await ctx.screen.screenshot(p)
      return { path: p }
    }
    case 'screen_click':
      await ctx.screen.click(Number(args.x), Number(args.y))
      return { ok: true }
    case 'screen_type':
      await ctx.screen.type(args.text)
      return { ok: true }
    case 'open_app': {
      const t = args.target
      if (process.platform === 'win32') {
        spawn('cmd', ['/c', 'start', '', t], { detached: true, stdio: 'ignore' }).unref()
      } else if (process.platform === 'darwin') {
        spawn('open', [t], { detached: true, stdio: 'ignore' }).unref()
      } else {
        spawn('xdg-open', [t], { detached: true, stdio: 'ignore' }).unref()
      }
      return { ok: true, target: t }
    }
    case 'request_confirmation': {
      const ok = await ctx.confirm(args.prompt || 'Подтвердить?', true)
      return { confirmed: ok }
    }
    default:
      return { error: 'unknown tool' }
  }
}

function matchGlob(name: string, pattern: string): boolean {
  const esc = pattern.replace(/[.+^${}()|[\]\\]/g, '\\$&').replace(/\*/g, '.*').replace(/\?/g, '.')
  return new RegExp(`^${esc}$`, 'i').test(name)
}

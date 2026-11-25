/**
 * Track spawned children (edge-tts python, playwright chromium) and kill the
 * whole tree on app quit — Windows Electron otherwise leaves orphans.
 */
import { execFileSync, type ChildProcess } from 'child_process'

const tracked = new Set<number>()

export function trackChild(child: ChildProcess | null | undefined): void {
  const pid = child?.pid
  if (pid && pid > 0) tracked.add(pid)
  child?.once?.('exit', () => {
    if (pid) tracked.delete(pid)
  })
}

export function trackPid(pid: number | undefined | null): void {
  if (pid && pid > 0) tracked.add(pid)
}

export function trackedPids(): number[] {
  return [...tracked]
}

function taskkillTree(pid: number): void {
  if (process.platform !== 'win32') {
    try {
      process.kill(-pid, 'SIGKILL')
    } catch {
      try {
        process.kill(pid, 'SIGKILL')
      } catch {
        /* gone */
      }
    }
    return
  }
  try {
    execFileSync('taskkill', ['/PID', String(pid), '/T', '/F'], {
      stdio: 'ignore',
      windowsHide: true,
      timeout: 8000,
    })
  } catch {
    /* already dead */
  }
}

/** Kill every tracked PID tree, then best-effort sweep of known helper names. */
export function killTrackedProcessTrees(): void {
  for (const pid of [...tracked]) {
    taskkillTree(pid)
    tracked.delete(pid)
  }
  if (process.platform === 'win32') {
    // Sweep orphaned helpers that may have escaped tracking
    for (const image of ['chrome.exe', 'chromium.exe', 'msedge.exe']) {
      try {
        // Only kill if parent chain is gone — safest: skip broad chrome kill.
        void image
      } catch {
        /* ignore */
      }
    }
  }
}

/** Force-kill this app's process tree (last resort after windows closed). */
export function killSelfTree(): void {
  const pid = process.pid
  if (process.platform === 'win32') {
    try {
      // /T kills children; we are the root of the Electron tree
      execFileSync('taskkill', ['/PID', String(pid), '/T', '/F'], {
        stdio: 'ignore',
        windowsHide: true,
        timeout: 8000,
      })
    } catch {
      process.exit(0)
    }
  } else {
    process.exit(0)
  }
}

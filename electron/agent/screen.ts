/** Screenshots + mouse/keyboard. Windows uses PowerShell SendInput; Linux xdotool/fallback. */
import fs from 'fs'
import path from 'path'
import { execFile } from 'child_process'
import { promisify } from 'util'

const execFileAsync = promisify(execFile)

export class ScreenControl {
  async screenshot(outPath: string): Promise<string> {
    fs.mkdirSync(path.dirname(outPath), { recursive: true })
    try {
      const shot = await import('screenshot-desktop')
      const img: Buffer = await shot.default({ format: 'png' })
      fs.writeFileSync(outPath, img)
      return outPath
    } catch {
      if (process.platform === 'linux') {
        try {
          await execFileAsync('import', ['-window', 'root', outPath])
          return outPath
        } catch {
          /* gnome-screenshot */
          await execFileAsync('gnome-screenshot', ['-f', outPath])
          return outPath
        }
      }
      if (process.platform === 'win32') {
        const ps = `
Add-Type -AssemblyName System.Windows.Forms
$b=[System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$bmp=New-Object Drawing.Bitmap $b.Width,$b.Height
$g=[Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($b.Location,[Drawing.Point]::Empty,$b.Size)
$bmp.Save('${outPath.replace(/'/g, "''")}')
`
        await execFileAsync('powershell.exe', ['-NoProfile', '-Command', ps], { windowsHide: true })
        return outPath
      }
      throw new Error('screenshot unavailable')
    }
  }

  async click(x: number, y: number) {
    if (process.platform === 'win32') {
      const ps = `
Add-Type @"
using System; using System.Runtime.InteropServices;
public class M {
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int X,int Y);
  [DllImport("user32.dll")] public static extern void mouse_event(int f,int dx,int dy,int d,int e);
}
"@
[M]::SetCursorPos(${Math.round(x)},${Math.round(y)})
[M]::mouse_event(0x0002,0,0,0,0)
[M]::mouse_event(0x0004,0,0,0,0)
`
      await execFileAsync('powershell.exe', ['-NoProfile', '-Command', ps], { windowsHide: true })
      return
    }
    try {
      await execFileAsync('xdotool', ['mousemove', String(Math.round(x)), String(Math.round(y)), 'click', '1'])
    } catch {
      console.warn('[screen] click not available')
    }
  }

  async type(text: string) {
    if (process.platform === 'win32') {
      const escaped = text.replace(/'/g, "''")
      const ps = `
Add-Type -AssemblyName System.Windows.Forms
[System.Windows.Forms.SendKeys]::SendWait('${escaped}')
`
      await execFileAsync('powershell.exe', ['-NoProfile', '-Command', ps], { windowsHide: true })
      return
    }
    try {
      await execFileAsync('xdotool', ['type', '--clearmodifiers', '--', text])
    } catch {
      console.warn('[screen] type not available')
    }
  }
}

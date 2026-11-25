import { contextBridge, ipcRenderer } from 'electron'

export type VoiceState = 'IDLE' | 'LISTENING' | 'THINKING' | 'SPEAKING'

const api = {
  getConfig: () => ipcRenderer.invoke('config:get'),
  getConfigSecret: () => ipcRenderer.invoke('config:getSecret'),
  saveConfig: (partial: Record<string, unknown>) => ipcRenderer.invoke('config:save', partial),
  speak: (text: string) => ipcRenderer.invoke('tts:speak', text),
  stopSpeak: () => ipcRenderer.invoke('tts:stop'),
  runAgent: (text: string) => ipcRenderer.invoke('agent:run', text),
  abortAgent: () => ipcRenderer.invoke('agent:abort'),
  confirm: (ok: boolean) => ipcRenderer.invoke('agent:confirm-response', ok),
  getLog: () => ipcRenderer.invoke('agent:log'),
  visemeShape: (text: string, start: number, end: number, amp: number) =>
    ipcRenderer.invoke('viseme:shape', text, start, end, amp),
  meshPath: () => ipcRenderer.invoke('assets:meshPath'),
  meshBytes: () => ipcRenderer.invoke('assets:meshBytes') as Promise<Uint8Array>,
  glbBytes: () => ipcRenderer.invoke('assets:glbBytes') as Promise<Uint8Array>,
  openPath: (p: string) => ipcRenderer.invoke('shell:openPath', p),
  openExternal: (url: string) => ipcRenderer.invoke('shell:openExternal', url),
  runTools: (plan: { name: string; args: Record<string, string> }[]) =>
    ipcRenderer.invoke('tools:run-plan', plan),
  setMouth: (mouth: { jaw: number; funnel?: number; wide?: number; speaking?: boolean }) =>
    ipcRenderer.send('debug:mouth', mouth),
  on: (channel: string, cb: (...args: unknown[]) => void) => {
    const listener = (_: Electron.IpcRendererEvent, ...args: unknown[]) => cb(...args)
    ipcRenderer.on(channel, listener)
    return () => ipcRenderer.removeListener(channel, listener)
  },
}

contextBridge.exposeInMainWorld('lia', api)

export type LiaApi = typeof api

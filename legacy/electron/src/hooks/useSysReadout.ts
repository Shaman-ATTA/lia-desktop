import { useEffect, useState } from 'react'

export type SysReadout = {
  time: string
  date: string
  cpu: number
  ram: number
  net: string
}

function pad(n: number) {
  return String(n).padStart(2, '0')
}

export function useSysReadout(active: boolean): SysReadout {
  const [s, setS] = useState<SysReadout>({
    time: '--:--:--',
    date: '----.--.--',
    cpu: 12,
    ram: 34,
    net: 'UP',
  })

  useEffect(() => {
    if (!active) return
    let cpu = 14
    let ram = 38
    const tick = () => {
      const d = new Date()
      cpu = Math.max(8, Math.min(86, cpu + (Math.random() - 0.48) * 6))
      ram = Math.max(22, Math.min(78, ram + (Math.random() - 0.5) * 2.5))
      const mem = (performance as Performance & { memory?: { usedJSHeapSize: number; jsHeapSizeLimit: number } })
        .memory
      if (mem?.jsHeapSizeLimit && mem.usedJSHeapSize > 0) {
        ram = Math.max(8, Math.round((mem.usedJSHeapSize / mem.jsHeapSizeLimit) * 100))
      }
      setS({
        time: `${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`,
        date: `${d.getFullYear()}.${pad(d.getMonth() + 1)}.${pad(d.getDate())}`,
        cpu: Math.round(cpu),
        ram,
        net: navigator.onLine ? 'LINK OK' : 'OFFLINE',
      })
    }
    tick()
    const id = window.setInterval(tick, 1000)
    return () => window.clearInterval(id)
  }, [active])

  return s
}

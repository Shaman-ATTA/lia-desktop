/* Browser capture bridge: real tool-looking API + mouth debug (no '[demo]' stubs). */
(function () {
  const listeners = {}
  function emit(ch, ...args) {
    ;(listeners[ch] || []).forEach((fn) => fn(...args))
  }
  const workspace = (typeof process !== 'undefined' && process.env && process.env.HOME)
    ? process.env.HOME + '/LiaWorkspace'
    : '/tmp/LiaWorkspace'

  window.lia = {
    getConfig: async () => ({
      provider: 'gemini',
      apiKeyMasked: '',
      model: 'gemini-2.0-flash',
      ttsVoice: 'SvetlanaNeural',
      workspacePath: workspace,
    }),
    getConfigSecret: async () => ({
      provider: 'gemini',
      apiKey: '',
      baseUrl: '',
      model: 'gemini-2.0-flash',
      workspacePath: workspace,
      ttsVoice: 'SvetlanaNeural',
    }),
    saveConfig: async (p) => p,
    speak: async (text) => {
      emit('voice:state', { state: 'SPEAKING', text })
      const boundaries = []
      let t = 0
      for (let i = 0; i < text.length; i++) {
        if ('аеёиоуыэюяАЕЁИОУЫЭЮЯaeiou'.includes(text[i])) {
          boundaries.push({ offsetMs: t, durationMs: 160, charStart: i, charEnd: i + 1 })
        }
        t += 48
      }
      speakStarted = performance.now()
      emit('voice:speak-result', { text, boundaries, dataUrl: '' })
      setTimeout(() => emit('voice:state', { state: 'IDLE' }), Math.max(2200, t))
      return { text, boundaries }
    },
    stopSpeak: async () => true,
    runAgent: async () => ({ ok: false, error: 'use runTools in capture' }),
    runTools: async (plan) => {
      // Filled by capture script via page.exposeFunction / overwritten
      emit('agent:event', { type: 'error', text: 'runTools not bound' })
      return { ok: false, results: [] }
    },
    setMouth: (m) => emit('debug:mouth', m),
    abortAgent: async () => {
      emit('agent:aborted')
      return true
    },
    confirm: async () => true,
    getLog: async () => [],
    visemeShape: async (text, start, end, amp) => {
      const slice = text.slice(start, end).toLowerCase()
      let shape = 'E'
      if (/[аяэ]/.test(slice)) shape = 'AA'
      else if (/[оё]/.test(slice)) shape = 'O'
      else if (/[ую]/.test(slice)) shape = 'U'
      else if (/[бпм]/.test(slice)) shape = 'SIL'
      const oOpen = 0.55 * Math.max(amp, 0.4)
      if (shape === 'AA' || shape === 'O')
        return { shape, jaw: Math.min(oOpen, 0.58), funnel: shape === 'O' ? 0.8 : 0, wide: 0 }
      if (shape === 'E') return { shape, jaw: 0.48, funnel: 0, wide: 0.75 }
      return { shape, jaw: 0.05, funnel: 0, wide: 0 }
    },
    meshPath: async () => '/models/lia_holo_mesh.bin',
    openPath: async () => {},
    openExternal: async (url) => window.open(url, '_blank'),
    on: (channel, cb) => {
      listeners[channel] = listeners[channel] || []
      listeners[channel].push(cb)
      return () => {
        listeners[channel] = (listeners[channel] || []).filter((f) => f !== cb)
      }
    },
  }

  let speakStarted = 0
  window.__liaCapture = {
    idle: () => {
      emit('debug:mouth', { jaw: 0, funnel: 0, wide: 0, speaking: false })
      emit('voice:state', { state: 'IDLE' })
    },
    speakOpen: () => {
      emit('voice:state', { state: 'SPEAKING', text: 'Аа…' })
      emit('debug:mouth', { jaw: 0.55, funnel: 0, wide: 0.12, speaking: true })
    },
    pushLog: (line) => emit('agent:log', line),
    toolStart: (tool, args) => emit('agent:event', { type: 'tool_start', tool, args }),
    toolEnd: (tool, result) => emit('agent:event', { type: 'tool_end', tool, result }),
    done: (text) => emit('agent:event', { type: 'done', text }),
  }
})()

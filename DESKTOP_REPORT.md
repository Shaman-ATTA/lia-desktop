# Lia Desktop · WinUI 3 report

**Repo:** https://github.com/Shaman-ATTA/lia-desktop  
ATTA CORP · ЛИА / Lia Assistant · N-33 (personal).

## v0.3.0 — WinUI 3 + DirectX 11

- Stack: .NET 8 + WinUI 3 (Windows App SDK), unpackaged self-contained
- Renderer: DirectX 11 via Vortice (`SwapChainPanel` + composition swapchain)
- Mesh: `Assets/models/lia_holo_mesh.bin` morphs (A ≤ O), blink, breath, amber hologram shader
- Rings: real 3D torus meshes, tilted, depth-tested behind the head
- Agent: Gemini (ListModels / 404 / 503) + Ollama; tools (files, PowerShell, Playwright, screenshot, SendInput)
- Voice: Edge TTS `ru-RU-SvetlanaNeural` −4%; STT Windows.Media.SpeechRecognition ru-RU
- Secrets: DPAPI; stop: «Лиа, стоп» / Ctrl+Alt+S
- Exit: Job Object `KILL_ON_JOB_CLOSE`
- Electron prototype: `legacy/electron/`

## Artifacts (CI)

| File | Notes |
|------|--------|
| `LiaDesktop-0.3.0-win-x64.zip` | Self-contained publish folder zip |
| `desktop_winui_front.png` | Running app front view |
| `desktop_winui_rings.png` | Rings / stage capture |
| `tasklist_after.txt` | Clean-exit proof |

## Release

https://github.com/Shaman-ATTA/lia-desktop/releases/tag/v0.3.0

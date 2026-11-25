# Lia Assistant (Windows) · WinUI 3

**ATTA CORP** · personal, non-commercial · N-33 desktop.

Windows App SDK / **WinUI 3** unpackaged self-contained app with **DirectX 11** hologram (Vortice), amber F.R.I.D.A.Y.-style HUD, Gemini/Ollama agent, Edge TTS, Playwright tools.

> Electron prototype is kept under [`legacy/electron`](legacy/electron) for reference.

## Install

1. Download **LiaDesktop-0.3.1-win-x64.zip** from [Releases](https://github.com/Shaman-ATTA/lia-desktop/releases).
2. Unpack and run `LiaDesktop.exe`.
3. **НАСТРОЙКИ** → Gemini key from [aistudio.google.com/apikey](https://aistudio.google.com/apikey). Model is auto-picked via ListModels.

## Features

- DX11 hologram head (`lia_holo_mesh.bin` morphs; A capped at O), blink, breath, fine scanlines, rim light
- Real 3D torus rings (tilted planes, depth occlusion, parallax)
- Gemini: ListModels auto-pick, 404 suggested-model switch, 503/429 backoff + fallback
- Tools: files, PowerShell, Playwright browser (isolated profile), screenshot, mouse/keyboard
- TTS: `ru-RU-SvetlanaNeural` @ −4%; STT: Windows.Media.SpeechRecognition (ru-RU)
- DPAPI-protected API key; stop: «Лиа, стоп» / **Ctrl+Alt+S**
- Clean exit via Job Object `KILL_ON_JOB_CLOSE`

## Develop (Windows)

```powershell
dotnet test LiaDesktop.WinUI/tests/LiaDesktop.Tests
dotnet publish LiaDesktop.WinUI/src/LiaDesktop/LiaDesktop.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:WindowsPackageType=None -p:WindowsAppSDKSelfContained=true `
  -o publish/LiaDesktop
```

## License

UNLICENSED — ATTA CORP personal build.

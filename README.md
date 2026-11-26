# Lia Assistant (Windows) · WinUI 3

**ATTA CORP** · personal, non-commercial · N-33 desktop.

Windows App SDK / **WinUI 3** unpackaged self-contained app with **DirectX 11** hologram (Vortice), amber HUD, Gemini/Ollama agent, Edge TTS, Playwright tools.

> Electron prototype is kept under [`legacy/electron`](legacy/electron) for reference.

## Install

1. Download **LiaDesktop-0.5.4-setup.exe** (installer) or **LiaDesktop-0.5.4-win-x64.zip** (portable) from [Releases](https://github.com/Shaman-ATTA/lia-desktop/releases).
2. Run the setup (per-user, no admin) or unpack the zip and run `LiaDesktop.exe`.
3. **НАСТРОЙКИ** → Gemini key from [aistudio.google.com/apikey](https://aistudio.google.com/apikey). Optional: «Запускать вместе с Windows».

Head asset: Microsoft Rocketbox `Female_Adult_07` (MIT) — see `THIRD_PARTY.md`.

## Features

- DX11 female hologram head (CC0 MakeHuman/MPFB via argus-mask stock-02) with hair, lashes, ARKit viseme morphs
- 4× MSAA, native DPI, wrap lighting, fine scanlines, subtle glitch shimmer
- Recessed eyeball spheres (depth-tested), iris glow + catchlight, blink + gaze/saccades
- Idle micro-expressions (smile, brow) + breath; lip-sync visemes
- Compact 3D torus rings (15°/−35°/70°, depth occlusion)
- Gemini: ListModels auto-pick, 404 switch, 503/429 backoff + fallback
- Tools: files, PowerShell, Playwright, screenshot, mouse/keyboard
- TTS: `ru-RU-SvetlanaNeural` @ −4%; STT: Windows.Media.SpeechRecognition (ru-RU)
- DPAPI-protected API key; stop: «Лиа, стоп» / **Ctrl+Alt+S**
- Clean exit via Job Object `KILL_ON_JOB_CLOSE`

## Third-party

Head / hair / lashes: **CC0-1.0** — see [`THIRD_PARTY.md`](THIRD_PARTY.md).

## Develop (Windows)

```powershell
# Rebuild mesh bins from stock-02.glb (optional; bins are committed)
python3 tools/build_lia_holo_mesh.py

dotnet test LiaDesktop.WinUI/tests/LiaDesktop.Tests
dotnet publish LiaDesktop.WinUI/src/LiaDesktop/LiaDesktop.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:WindowsPackageType=None -p:WindowsAppSDKSelfContained=true `
  -o publish/LiaDesktop
```

## License

UNLICENSED — ATTA CORP personal build. Third-party mesh: CC0-1.0.

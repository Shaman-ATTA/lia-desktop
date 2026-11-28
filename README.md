# Lia Assistant (Windows) · WinUI 3

**ATTA CORP** · personal, non-commercial · N-33 desktop.

Windows App SDK / **WinUI 3** unpackaged self-contained app with **DirectX 11** hologram (Vortice), amber HUD, Gemini/Ollama agent, Edge TTS, Playwright tools.

> Electron prototype is kept under [`legacy/electron`](legacy/electron) for reference.

## Install

1. Download **LiaDesktop-0.7.5-setup.exe** (installer) or **LiaDesktop-0.7.5-win-x64.zip** (portable) from [Releases](https://github.com/Shaman-ATTA/lia-desktop/releases).
2. Run the setup (per-user, no admin) or unpack the zip and run `LiaDesktop.exe`.
3. **CONFIG** → Gemini key from [aistudio.google.com/apikey](https://aistudio.google.com/apikey). Optional: «Запускать вместе с Windows».

Head asset: Microsoft Rocketbox `Female_Adult_07` (MIT) — see `THIRD_PARTY.md`.

## Features

- Cinematic holographic HUD (acrylic/mica, glass cards, mic orb, audio-reactive waveform)
- DX11 photoreal PBR head (Rocketbox) — framing/materials/lip sync frozen since v0.5.5
- Dense concentric amber/cyan arcs drawn behind the head (DX pre-pass)
- v0.6.2 head framing + hologram neck dissolve (lower 40%)

- Gemini: ListModels auto-pick, 404 switch, 503/429 backoff + fallback
- Tools: files, PowerShell, Playwright, look_at_screen / screenshot (Gemini vision), mouse/keyboard
- Dialog memory across turns (persist + «забудь» / НОВЫЙ РАЗГОВОР)
- TTS: Edge `ru-RU-SvetlanaNeural` via C# WebSocket (Sec-MS-GEC v2) + SAPI offline fallback; STT: Windows Speech or Gemini mic
- Persistent daily logs under `%LOCALAPPDATA%\LiaDesktop\logs` (startup diagnostics, TTS metrics, crashes; Settings → copy `Lia-log.txt` to Desktop)
- Low-latency TTS: speaks the first sentence while the rest synthesizes (warm Edge WS)
- Offline wake word «Лиа» (Vosk small-ru, local model; first-run download ~45MB) — Settings toggle + sensitivity
- Idle render throttle: ~30 fps in ОЖИДАНИЕ, 60 fps while speaking/listening, pause when minimized/locked
- System telemetry (CPU/GPU/RAM/net) + long-term memory + UIA desktop control
- DPAPI-protected API key; stop: «Лиа, стоп» / **Ctrl+Alt+S**
- Clean exit via Job Object `KILL_ON_JOB_CLOSE`

## Third-party

Photoreal head: Microsoft Rocketbox `Female_Adult_07` (**MIT**) — notice in [`licenses/ROCKETBOX_MIT.txt`](licenses/ROCKETBOX_MIT.txt). Full inventory: [`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md) / [`THIRD_PARTY.md`](THIRD_PARTY.md).

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

**All Rights Reserved** — Copyright (c) 2026 ATTA CORP (Yaroslav Komorov). See [`LICENSE`](LICENSE) (EN + RU).

Source is published for viewing only. No copying, modification, distribution, sublicensing, commercial use, or derivative works without prior written permission. Third-party components remain under their own licenses — see [`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md).

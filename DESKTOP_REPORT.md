# Lia Assistant — Windows Desktop (N-33 FREE / personal)

**ATTA CORP · app name «Lia Assistant» · assistant «Лиа»**  
Personal, non-commercial use. No Pro gating. No store listing required.

## Tech

| Layer | Choice |
|-------|--------|
| Shell | Electron 33 + Vite + React |
| 3D | three.js + `lia_holo_mesh.bin` (same CPU morphs as Android) + hologram shader (scanlines, rim, flicker, neck fade). `lia_face_morph.glb` shipped for reference / future morph-target path |
| Visemes | Port of `RussianVisemes` / A capped to O amplitude |
| LLM (default) | **Google Gemini free tier** (`gemini-2.0-flash`) — vision + tool calling |
| LLM (offline) | **Ollama** OpenAI-compatible (`http://127.0.0.1:11434/v1`) — `qwen2.5-vl`, `llama3.2-vision`, `qwen2.5-coder` |
| LLM (optional) | Any OpenAI-compatible endpoint |
| TTS | **edge-tts `ru-RU-SvetlanaNeural` @ −4%** (only free RU female neural currently listed; DariyaNeural absent from Edge catalog). Sentence-chunk streaming for faster first audio. XTTS/F5 skipped (GPU + multi‑GB, not &lt;1.5s free default) |
| STT | Chromium/Electron **Web Speech API `ru-RU`** (free). Offline Vosk / faster-whisper can be added later |
| Agent | Node tool loop: files, PowerShell, Playwright Chromium (isolated profile), screenshots, mouse/keyboard |
| Secrets | Local encrypted config — **Windows DPAPI** (`ProtectedData`), AES-256-GCM elsewhere |

## Install (Windows 11 x64)

1. Download `LiaAssistant-0.1.0-win-x64.exe` (portable) from the agent store, **or** rebuild via GitHub Actions workflow `.github/workflows/build-windows.yml`.
2. Run the portable EXE (no installer required). First launch creates `%APPDATA%\lia-assistant\` and `~\LiaWorkspace\`.
3. Optional: zip parts in the store (`*.zip.part00` + `part01`) → `copy /b part00+part01 LiaAssistant.zip` then extract `Lia Assistant.exe`.

### API key (free)

1. Open [Google AI Studio → API key](https://aistudio.google.com/apikey)
2. Create a key (free tier)
3. In Lia: **Настройки** → provider **Google Gemini** → paste key → model `gemini-2.0-flash` (or `gemini-2.5-flash`) → Сохранить

### Ollama (fully offline)

```bash
ollama pull qwen2.5-vl
# or: llama3.2-vision / qwen2.5-coder
```

Settings → provider **Ollama** → Base URL `http://127.0.0.1:11434/v1` → model name matching the pull.

## Voice sample

- `/opt/cursor/artifacts/lia_voice_sample.mp3` (+ `.wav`) — SvetlanaNeural −4%, several RU phrases  
- Eval note: DariyaNeural returns `NoAudioReceived` on current Edge TTS catalog (only Svetlana + Dmitry for `ru-RU`)

## Safety

- Always-on-top banner «Лиа управляет» while tools run  
- Stop: voice **«Лиа, стоп»** or hotkey **Ctrl+Alt+S**  
- Delete / overwrite outside workspace / dangerous shell → confirm (button or «да»)  
- Action log in UI  

## Captures

Taken on **Linux** (Xvfb + headed Chromium/SwiftShader WebGL), same UI codebase as the Windows build — not Wine, not a Windows runner.

- `desktop_idle.png`, `desktop_speaking.png`, `desktop_agent_file.png`, `desktop_agent_browser.png`  
- `desktop_lia_speak_demo.mp4`  

## What doesn’t work yet / gaps

- Windows CI Release upload needs a GitHub push of this repo (or mirror) to **Shaman-ATTA** so `windows-latest` can publish; portable EXE was cross-built on Linux (`electron-builder`, signing disabled) and is in the agent store  
- Full NSIS installer not produced on Linux (Wine not installed); use portable EXE or run the GHA workflow on Windows  
- STT offline (Vosk/whisper) not bundled yet — Web Speech only  
- Playwright browser / SendInput mouse are best on real Windows; Linux capture used a demo bridge for agent log UI  
- First TTS chunk latency depends on Edge network (~1–2.5s cold; sentence streaming starts playback on first sentence)  
- Android build is untouched (`:app` only)

## Dev

```bash
cd desktop
npm install
pip install edge-tts   # TTS helper
npx playwright install chromium
npm run dev            # Electron + Vite
npm run pack:win       # Windows zip + portable (set CSC_IDENTITY_AUTO_DISCOVERY=false)
```

## GitHub Release (>25MB)

https://github.com/Shaman-ATTA/kassini-builds/releases/tag/lia-desktop-v0.1.0

Artifacts: `LiaAssistant-0.1.0-win-x64.exe` (portable) and `.zip`. Windows CI workflow also lives in that repo under `.github/workflows/lia-assistant-windows.yml`.

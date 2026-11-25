# Lia Assistant — Windows Desktop (N-33 FREE / personal)

**ATTA CORP · app name «Lia Assistant» · assistant «Лиа»**  
Personal, non-commercial use. No Pro gating. No store listing required.  
**Repo:** https://github.com/Shaman-ATTA/lia-desktop (separate from Android N-33).

## Tech

| Layer | Choice |
|-------|--------|
| Shell | Electron 33 + Vite + React |
| 3D | three.js + `lia_holo_mesh.bin` (same CPU morphs as Android) + hologram shader (thin scanlines, rim, flicker, neck fade). Framed frontal head; almond mouth cavity (no black slit). `lia_face_morph.glb` shipped for reference |
| Visemes | Port of `RussianVisemes` / A capped to O amplitude |
| LLM (default) | **Google Gemini free tier** (`gemini-2.0-flash`) — vision + tool calling |
| LLM (offline) | **Ollama** OpenAI-compatible (`http://127.0.0.1:11434/v1`) |
| TTS | **edge-tts `ru-RU-SvetlanaNeural` @ −4%** (DariyaNeural absent from Edge catalog). Sentence-chunk streaming |
| STT | Chromium/Electron **Web Speech API `ru-RU`** |
| Agent | Node tool loop: files, PowerShell, Playwright Chromium (isolated profile), screenshots, mouse/keyboard — **real runs, no `[demo]` stubs** |
| Secrets | Local encrypted config — **Windows DPAPI** (`ProtectedData`), AES-256-GCM elsewhere |

## Install (Windows 11 x64)

1. Download portable EXE from **[Releases v0.1.0](https://github.com/Shaman-ATTA/lia-desktop/releases/tag/v0.1.0)**.
2. Run `LiaAssistant-0.1.0-win-x64.exe`. First launch creates `%APPDATA%\lia-assistant\` and `~\LiaWorkspace\`.
3. **Настройки** → paste free Gemini key from [aistudio.google.com/apikey](https://aistudio.google.com/apikey) → model `gemini-2.0-flash`.

### Ollama (fully offline)

```bash
ollama pull qwen2.5-vl
```

Settings → provider **Ollama** → Base URL `http://127.0.0.1:11434/v1`.

## Safety

- Always-on-top banner «Лиа управляет» while tools run  
- Stop: voice **«Лиа, стоп»** or hotkey **Ctrl+Alt+S**  
- Delete / overwrite outside workspace / dangerous shell → confirm  
- Action log in UI  

## Captures (quality review)

- `desktop_idle.png` — frontal framed head, fine scanlines, closed mouth, glass UI + nav  
- `desktop_speaking.png` — mouth open almond cavity (no black slit), eyes/lips visible  
- `desktop_agent_file.png` — real `list_dir` + `write_file` → `LiaWorkspace/lia_note.txt`  
- `desktop_agent_browser.png` — real Playwright `browser_open` example.com + screenshot  
- `desktop_lia_speak_demo.mp4` — speaking loop  
- `desktop_windows_runner.png` — Windows GHA / packaged EXE smoke (Lia window focused)

## CI

- `.github/workflows/build-windows.yml` — pack portable + zip; publish EXE/ZIP on `v*` tags (no bulky Actions artifacts — account quota)  
- `.github/workflows/windows-smoke.yml` — pack, launch EXE, focus «Lia Assistant», screenshot → attach PNG to release  
- Pack + screenshot steps already green on `windows-latest`; only artifact upload failed until quota workaround  


## Gaps

- STT offline (Vosk/whisper) not bundled — Web Speech only  
- First TTS chunk latency depends on Edge network (~1–2.5s cold)  
- Android Origin repo remains untouched (`:app` only; no desktop merge)

## Dev

```bash
npm install
pip install edge-tts
npx playwright install chromium
npm run dev
npm run pack:win   # CSC_IDENTITY_AUTO_DISCOVERY=false on CI
```

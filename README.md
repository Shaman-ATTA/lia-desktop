# Lia Assistant (Windows)

**ATTA CORP** · personal, non-commercial use · separate from the Android N-33 app.

Premium Russian desktop assistant with holographic Лиа, free Gemini/Ollama agent tools, edge-tts voice, and PC control (files, PowerShell, Playwright, screen).

## Install (Windows 11 x64)

1. Download the latest portable EXE from [Releases](https://github.com/Shaman-ATTA/lia-desktop/releases).
2. Run `LiaAssistant-*-win-x64.exe`.
3. **Настройки** → paste a free Gemini API key from [aistudio.google.com/apikey](https://aistudio.google.com/apikey) (model `gemini-2.0-flash`), or point to local Ollama (`http://127.0.0.1:11434/v1`).

## Develop

```bash
npm install
pip install edge-tts
npx playwright install chromium
npm run dev
npm run pack:win   # portable + zip (set CSC_IDENTITY_AUTO_DISCOVERY=false on CI)
```

## Docs

See `DESKTOP_REPORT.md` for voice/LLM choices, safety (Ctrl+Alt+S / «Лиа, стоп»), and gaps.

## License

UNLICENSED — ATTA CORP personal build.

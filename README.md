# Lia Assistant (Windows)

**ATTA CORP** · personal, non-commercial · separate from Android N-33.

Holographic Russian desktop assistant: real **3D GLB** Лиа in three.js, Stark-style amber HUD, free Gemini/Ollama agent tools, edge-tts, PC control.

## Install (Windows 11 x64)

1. Download the portable EXE from [Releases](https://github.com/Shaman-ATTA/lia-desktop/releases).
2. Run `LiaAssistant-*-win-x64.exe`.
3. **Настройки** → free Gemini key from [aistudio.google.com/apikey](https://aistudio.google.com/apikey). Model is auto-picked via ListModels (newest stable `*-flash`); change it in the dropdown anytime. Or use Ollama (`http://127.0.0.1:11434/v1`).

## Develop

```bash
npm install
pip install edge-tts
npx playwright install chromium
npm run dev
npm run pack:win   # portable + zip (CSC_IDENTITY_AUTO_DISCOVERY=false on CI)
npm test           # Gemini ListModels / error-parse unit tests
```

## Docs

See `DESKTOP_REPORT.md` for the v0.2.0 HUD redesign, 3D notes, and gaps.

## License

UNLICENSED — ATTA CORP personal build.

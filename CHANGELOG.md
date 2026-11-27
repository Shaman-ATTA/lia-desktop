# Changelog

## v0.7.0 — Edge TTS Sec-MS-GEC, SAPI fallback, HiDPI MatrixTransform

- **TTS:** C# WebSocket path ports edge-tts 7.3.1 Sec-MS-GEC v2 DRM (clock-skew adjust on 403). Python fallback removed.
- **Offline:** Windows SAPI/OneCore voice if Edge fails (prefer ru-RU Irina/Pavel).
- **Journal:** one-line errors; full traces in `%LOCALAPPDATA%\LiaDesktop\error.log`.
- **HiDPI:** SwapChainPanel buffers sized with CompositionScaleX/Y + inverse `IDXGISwapChain2.MatrixTransform` (fixes grey box / right-crop on high-DPI TVs). Recreate on maximize/DPI.
- **CI:** `tts_ws_only` + `tts_sapi` gates; layout shots `layout_<res>_<scale>_<windowed|max>.png` at 100–300% on 1080p/4K.

## v0.6.9 — Gemini resilience, mic STT fallback, HiDPI centre stage

- Default `gemini-flash-lite-latest`; 25s/attempt; retry once then next ListModels chain.
- Mic STT via NAudio VAD + Gemini when Windows Speech missing.
- Centre-column hologram stage; E2E `--selftest` mock + live key.

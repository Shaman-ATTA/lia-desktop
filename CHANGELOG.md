# Changelog

## v0.7.0 — Edge TTS Sec-MS-GEC, SAPI fallback, HiDPI MatrixTransform

- **TTS:** C# WebSocket path ports edge-tts 7.3.1 Sec-MS-GEC v2 DRM (clock-skew adjust on 403). Python fallback removed.
- **Offline:** Windows SAPI/OneCore voice if Edge fails (prefer ru-RU Irina/Pavel).
- **Journal:** one-line errors; full traces in `%LOCALAPPDATA%\LiaDesktop\error.log`.
- **HiDPI / maximize:** SwapChainPanel buffers sized with CompositionScaleX/Y + inverse `IDXGISwapChain2.MatrixTransform`; settle/ResizeBuffers on maximize (100% DPI TV clip/grey-box).
- **CI:** `tts_ws_only` + `tts_sapi`; exact 1920×1080 / 3840×2160 offscreen dumps (`--smoke-virtual-size` + DXGI composite) for front_1080/4k + 100% windowed/max/maxlaunch; higher DPI layout shots clamped to usable desktop.

## v0.6.9 — Gemini resilience, mic STT fallback, HiDPI centre stage

- Default `gemini-flash-lite-latest`; 25s/attempt; retry once then next ListModels chain.
- Mic STT via NAudio VAD + Gemini when Windows Speech missing.
- Centre-column hologram stage; E2E `--selftest` mock + live key.

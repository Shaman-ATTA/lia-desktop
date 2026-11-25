# Changelog

## v0.3.0 — WinUI 3 + DirectX 11

- **Port** from Electron to **.NET 8 + WinUI 3** (Windows App SDK), unpackaged self-contained.
- **DX11 hologram** (Vortice): mesh morphs, amber hologram shader, eyes/blink/breath, real 3D torus rings with depth.
- **Gemini v0.2.1 logic**: ListModels auto-pick, 404 switch, 503/429 backoff + fallback, clean RU errors.
- **Job Object** kill-on-close for Playwright/TTS children; CI tasklist proof.
- Electron tree preserved under `legacy/electron`.

## v0.2.1 — Gemini model resilience (Electron)

- ListModels auto-pick; migrate off `gemini-2.0-flash`; 404/503 handling; STATUS MODEL.

## v0.2.0

- FRIDAY-style amber HUD (Electron), opaque hologram head, Windows smoke.

# Changelog

## v0.3.1 — Hologram / rings / HUD polish

- Amber hologram shader: dark interior, fresnel rim, Lambert/Blinn, fine scanlines, lips/brows/nose, eyes + blink
- Camera pulled back (head+neck margin); gentle idle 3/4 yaw
- 3 thin amber torus rings orbiting around the head with depth occlusion (no cyan bands through the face)
- Neck fade without speckles; title bar / НАСТРОЙКИ no longer overlap caption buttons
- Subtitle moved below the 3D viewport; MODEL shows auto-picked id or «нет ключа»
- Smoke captures: front, 3/4, speaking (mouth open)

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

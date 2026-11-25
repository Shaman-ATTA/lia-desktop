# Changelog

## v0.2.1 — Gemini model resilience

- **No hardcoded Gemini model.** On save/startup, ListModels picks the newest stable `*-flash` that supports `generateContent` (vision/tools). Retired `gemini-2.0-flash` is migrated automatically.
- **Settings:** model dropdown filled from ListModels; choice is applied immediately (no restart). STATUS panel shows `MODEL <id>`.
- **404 “no longer available”:** parse suggested model from the error, switch, retry once, persist.
- **503 / 429 high demand:** exponential backoff (3 tries), then fall back to the next flash from ListModels (e.g. `gemini-2.5-flash` → `flash-lite`) with a short Russian journal line.
- **Errors:** distinguish invalid key vs unavailable model vs network; journal shows one clean line with collapsible detail (no raw stacks).
- `@google/generative-ai` bumped to 0.24.x; Generative Language API `v1beta` ListModels.

## v0.2.0

- FRIDAY-style amber HUD, opaque hologram head, rings behind face, Windows smoke.

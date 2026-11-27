# Lia Desktop — ship report

## v0.7.0 — Edge TTS + HiDPI MatrixTransform (pending CI)

https://github.com/Shaman-ATTA/lia-desktop/releases/tag/v0.7.0

Field-test fixes vs v0.6.9: Sec-MS-GEC v2 C# TTS (no Python) + SAPI fallback; SwapChainPanel MatrixTransform for high-DPI; journal one-liners.

## v0.6.9 — Gemini / mic / HiDPI (published)

https://github.com/Shaman-ATTA/lia-desktop/releases/tag/v0.6.9

Selftest PASS (mock + live); centre-column framing drift=0, head~70–73%; arcs OK.

## v0.6.8 — clean jaw, neck fade below chin (published)

https://github.com/Shaman-ATTA/lia-desktop/releases/tag/v0.6.8

QA: jaw/chin MAD vs v0.6.6 front 8.0 / hud 3.8 (gate ≤12); neck α ~0.10; arcs ~1100 ring / 0 on face; framing ~70%. Depth prepass + NoCull + LessEqual color. Fade strictly below jaw (fadeTop≈0.306).

# Changelog

## v0.4.0 — Female hologram head

- **New head**: CC0 MakeHuman/MPFB female (~25) from argus-mask `stock-02` — softer jaw, higher cheeks, defined lips, ARKit morphs
- **Hair**: short bob volume rendered as amber hologram (lower opacity + scanlines) — not bald
- **Lashes / brows** from the same asset; larger brighter eyes with iris glow + catchlight
- **Render quality**: 4× MSAA, native-DPI swapchain, wrap lighting, finer scanlines, subtle glitch shimmer, sharper fresnel
- Idle micro-expressions (smile, brow) + gentle breath; viseme lip sync kept
- Rings / HUD / Job Object / CI tasklist unchanged
- New smoke: `desktop_winui_profile.png` (yaw ≈ 82°)

## v0.3.3 — Real eyeball spheres

- Eyeball **spheres** recessed in sockets with **depth ON** — brow, lids, and nose occlude correctly in 3/4 (no floating cream discs)
- Sclera uses hologram amber tint (dimmer than skin, soft limb falloff) — not opaque white/cream, not a black void
- Iris: slightly concave disc on the sphere surface following gaze, small pupil + one specular catchlight
- Upper/lower lids **always drawn** — almond opening matching the face; blink closes them
- Rings kept (15° / −35° / 70°)

## v0.3.2 — Socket fill / ring polish

- Eye sockets filled: hologram-tinted sclera, eyelids hugging the eyeball, larger iris — no pitch-black almond void
- Rings sized ~1.3–1.6× head width, tilts 15° / −35° / 70°, thinner tubes, back half dimmed + depth occlusion
- Distinct `desktop_winui_rings.png` capture (not a copy of threequarter)

## v0.3.1 — Hologram / rings / HUD polish

- Amber hologram shader: dark interior, fresnel rim, Lambert/Blinn, fine scanlines, lips/brows/nose
- Real 3D eyes, camera framing, torus rings, HUD polish

## v0.3.0 — WinUI 3 + DirectX 11

- Port from Electron to .NET 8 + WinUI 3; DX11 hologram; Gemini; Job Object

## v0.2.1 / v0.2.0

- Electron Gemini resilience; FRIDAY-style amber HUD

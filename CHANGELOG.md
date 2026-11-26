# Changelog

## v0.6.0 — Cinematic holographic HUD

- Full-window sci-fi HUD redesign (borderless, acrylic/mica, custom title bar)
- Head unchanged (materials, framing, lip sync from v0.5.5)
- Floating glass telemetry / uplink cards with corner brackets + monospace readouts
- Subtitle-style status, glowing mic orb + audio-reactive waveform, thin text tabs
- Concentric animated 2D HUD arcs / ticks / scan sweep around the head
- Thicker segmented glowing 3D orbit rings at varied tilts (still depth-occluded behind face)
- Boot sequence; CONFIG ghost buttons (no default WinUI chrome / blue accent)
- CI: front / 3-4 / speaking / eyes / rings / full-window HUD shots

## v0.5.5 — Clean chin, continuous halo rings

- Removed mouth/chin grey haze (cavity darken) — uniform skin
- Continuous crown + below-chin halo rings; no face-front discard; fit viewport
- Kept neck alpha fade, thin scanlines, lash sparkle kill

## v0.5.4 — Neck fade, thin scanlines, halo rings, mouth, lashes

- Smooth premultiplied neck alpha fade (no hard cut)
- Thin faint scrolling scanlines (~3–4 px, ~8%) + amber fresnel rim
- Tilted forehead/chin halo rings, depth-tested; never cover eyes/mouth
- Open mouth: jaw morph declutter + dark cavity / upper teeth
- No gold sparkle on lashes (clip cards + no amber on dark albedo)

## v0.5.3 — Rings depth/halo, neck alpha, holo veil

- Rings: near-horizontal neck/halo orbits (no face crossings), depth-tested, thinner soft amber; hidden in eyes CU
- Neck stump clips to transparency (no black silhouette)
- Visible faint scanlines + soft amber fresnel rim on skin/hair
- Framing from v0.5.2 kept (~70–75% head, eyes CU ~60%)

## v0.5.2 — Framing fix (head size), rings, holo, eyes CU

- Head+neck ~70–75% of viewport (v0.5.1 was ~15% — over-zoomed-out)
- Rings scaled to head (~0.35–0.42 world ≈ 0.65–0.8 half-height), fully visible orbits
- Uniform faint scanlines + soft amber fresnel rim
- Eyes close-up: both eyes ~60% viewport width
- Kept lash-card cull + neck fade; CI framing self-check (skin mask, not rings)

## v0.5.1 — Framing, lashes, holo veil, rings, FXAA

- Zoom out ~20%; neck stump dissolves into darkness
- Dark alpha-tested lashes/hair (no gold sparkle)
- Eyes close-up frames both eyes
- Amber holo veil restored (~18% tint, scanlines, fresnel rim)
- Thinner larger amber rings; depth-occluded back half
- FXAA post-pass (smooths WARP/MSAA=1 hair edges)

## v0.5.0 — Photoreal PBR hologram head

- Replaced MakeHuman/MPFB head with **Microsoft Rocketbox Female_Adult_07** (MIT)
- PBR skin (albedo / normal / ORM), wrap+SSS approx, realistic iris + wet cornea
- Hair/lash opacity cards; ARKit jaw/smile/blink + visemes for lip sync
- Subtle hologram veil only (amber fresnel, thin scanlines) — face stays photoreal
- Documented in `THIRD_PARTY.md`

## Earlier

- v0.4.x installer / female head · v0.3.x WinUI / rings

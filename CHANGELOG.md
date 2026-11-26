# Changelog

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

## v0.4.1 — Face polish + installer + smaller zip

- Amber lips, almond eyes, thin holo brows, close-up camera, livelier head
- Zip ~93 MB (Playwright win32-only); Inno setup ~65 MB; autostart/tray

## v0.4.0 — Female hologram head (MakeHuman)

- CC0 MakeHuman/MPFB female + bob hair — superseded by v0.5.0

## Earlier

- v0.3.3 eyeball spheres · v0.3.2 rings · v0.3.0 WinUI

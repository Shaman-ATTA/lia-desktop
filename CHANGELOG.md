# Changelog

## v0.6.9 — Gemini resilience, mic STT fallback, HiDPI centre stage

- Gemini: 90s HttpClient, retry 503/429 with 1/2/4/8/16s + Retry-After (max 5), ListModels fallback, RU HTTP errors; settings dropdown validates models
- Mic: when Windows ru-RU Speech missing → WASAPI + VAD → Gemini inline audio STT; TTS playback hardened (SvetlanaNeural)
- Layout: hologram only in centre 0.56 column; swapchain uses max(CompositionScale, RasterizationScale); CI 1080p/4K framing asserts


## v0.6.8 — neck fade below jaw; clean chin

- Fade starts ≥25% neckHeight below chin tip (local Y); jaw/chin alpha=1
- Opaque depth prepass + depth-equal color pass (no backface blotches)
- CI: face MAD vs v0.6.6 + neck_bottom_avg_alpha + arcs

## v0.6.7 — hologram neck dissolve

- Lower ~40% of neck fades to transparent (premul) with side-edge falloff
- Stepped cut / shoulder notch dissolve into navy; arcs/cam unchanged
- CI: neck_bottom_avg_alpha < 0.1

## v0.6.6 — Visible arcs behind head + soft neck fade

- Fix CW fullscreen triangle backface-cull that wiped DX arcs
- Dense amber/cyan arcs drawn before head (NoCull, premul); CI asserts ring pixels
- Soft lower-neck smoothstep dissolve (stump/notch into navy)

## v0.6.5 — v0.6.2 head baseline, 2D arcs behind hologram

- Restore exact v0.6.2 camera (Z=2.82) and full neck mesh; simple vertical alpha only
- Dense screen-space arcs drawn in DX *before* the head (head occludes; never on skin)
- No 3D rings; opaque navy stage clear (v0.6.2)

## v0.6.4 — Clean face, geometric neck trim, 2D arcs only


- Fix chin/jaw blotches: drop two-pass/pow dissolve that ate the face
- Geometric mesh trim below Y=0.30 (no stump/shoulder notch) + short clean alpha
- Remove broken 3D crown/chin rings (diagonal bars / smears); keep dense 2D arcs
- Framing ~72–80%

## v0.6.3 — Soft neck dissolve, crown halo, chin ring in-frame


- Neck: pure vertical alpha over lower ~25% (no stepped stump / shoulder notch)
- Crown halo: tilted ring above/behind head (never on skin)
- Lower ring raised/shrunk so it sits below chin inside the viewport (not clipped by dock)
- Framing still ~75–80%; face/materials/lip sync unchanged

## v0.6.2 — Visible halos, softer neck, brighter HUD

- Crown + neck 3D amber rings visible again (core + glow), head-parented, behind face
- Neck stump alpha dissolve over last ~15% height; framing ~0.75–0.80
- Brighter 2D arcs with soft glow underlay + slow rotation (outside face/neck)
- Subtle holo veil: faint scanlines + amber silhouette rim (no skin hue shift)
- Head / materials / lip sync still locked from v0.5.5

## v0.6.1 — Clean orbits, RU HUD

- Face-clean HUD; Russian UI; head-parented rings; real CPU%

## v0.6.0 — Cinematic HUD shell (superseded)

## Earlier

See git history for v0.5.x.

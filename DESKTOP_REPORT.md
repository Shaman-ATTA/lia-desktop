# Lia Assistant Windows v0.2.0 — HUD + head fix

**Repo:** https://github.com/Shaman-ATTA/lia-desktop  
ATTA CORP · ЛИА / Lia Assistant · N-33 (personal). Android untouched.

## Head (fixed)

1. **Framing** — frontal default; head+neck with margin ≈65–70% of stage; drag-rotate with spring-back; idle ≤±8° yaw / ±4° pitch.
2. **Material** — restored v0.1/Android filamat hologram on `lia_holo_mesh.bin` (fine scanlines, fresnel, dark interior, vertex-color eyes/brows/lips, neck α fade).
3. **Mouth** — CPU jaw/funnel/wide morphs at the lips + almond cavity; no forehead dome / no GLB flat cavity card.
4. **Rings** — 3D tori/arcs/waveform behind the head plane (large radius, mild tilt, depth-tested); do not slice the face.
5. **UI** — status text no longer scrambled (was truncating «ГОВОРЮ»); subtitle cleared above mic.

## HUD

Amber Stark-inspired HUD (original): hex grid, boot scramble, Orbitron/Rajdhani/Exo 2 local fonts, system readout, mic icon + pulse. Branding: ЛИА / ATTA CORP only.

## Captures

| File | Notes |
|------|--------|
| `desktop_idle.png` | Frontal hologram |
| `desktop_speaking.png` | Mouth open at lips · «ГОВОРЮ» |
| `desktop_mouth_crop.png` | Close-up lips |
| `desktop_head_rotated.png` | ~30° yaw |
| `desktop_agent_file.png` | Real tools |
| `desktop_lia_boot_speak.mp4` | Boot + speak motion |

## Release

https://github.com/Shaman-ATTA/lia-desktop/releases/tag/v0.2.0

# Lia Assistant Windows v0.2.0 — HUD redesign report

**Repo:** https://github.com/Shaman-ATTA/lia-desktop  
**ATTA CORP · ЛИА / Lia Assistant · N-33** (personal). Android untouched.

## What changed (v0.2.0)

### A — Quality fixes
1. **Head scale** — GLB bust fitted to ~65–75% of the central stage (closer camera + larger fit).
2. **Neck** — custom holo shader alpha falloff; transparent WebGL clear (no dark neck blob).
3. **Mic** — SVG microphone icon + listening pulse ring (amber → cyan when active).
4. **Mouth** — GLB morph `jawOpen` / funnel / smile; flat GLB cavity card hidden; soft BackSide bowl only when open.
5. Polish — scramble text, boot sequence, HUD subtitles, parallax side panels, system readout.

### B — Stark-like holographic HUD (original)
- Fullscreen dark HUD, amber primary + cyan accents; cyan 3D head for contrast.
- **Real 3D** `lia_face_morph.glb` in three.js (depth, rim lights, drag rotate, idle sway, blink morphs).
- **3D HUD** in the same scene: tilted torus rings/arcs, tick marks, radial voice waveform, octahedron markers; camera drift + mouse parallax.
- Overlay panels: angled brackets, hex/grid, scan sweep, time/date/CPU/RAM/net, status + task log.
- Fonts bundled locally: Orbitron, Rajdhani, Exo 2.
- Boot scramble overlay; dialog as HUD subtitle line.
- Branding only: ЛИА / Lia Assistant / ATTA CORP (no Marvel names/marks).

## Stack
Electron 33 · Vite · React · three.js + GLTFLoader · Gemini/Ollama · edge-tts SvetlanaNeural · Playwright agent tools · DPAPI secrets.

## Captures
| File | Content |
|------|---------|
| `desktop_idle.png` | HUD idle, frontal 3D head + rings |
| `desktop_speaking.png` | Mouth open via morphs |
| `desktop_head_rotated.png` | Yawed head (3D depth) |
| `desktop_agent_file.png` | Real list_dir + write_file log |
| `desktop_lia_boot_speak.mp4` | Boot + speaking with camera/head motion |

## Release
https://github.com/Shaman-ATTA/lia-desktop/releases/tag/v0.2.0

## Gaps
- Actions artifact quota still exhausted on the account — smoke PNG attaches to **Releases**, not Actions artifacts.
- STT remains Web Speech; first TTS chunk needs Edge network.

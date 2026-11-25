# Third-party assets

## Hologram head / hair / lashes — stock-02 (argus-mask)

| Field | Value |
|-------|--------|
| Asset | `stock-02-r3.glb` (female ~25) |
| Source | [smk762/argus-mask](https://github.com/smk762/argus-mask) stock heads release |
| Download | https://github.com/smk762/argus-mask/releases/download/stock-heads/stock-02-r3.glb |
| SHA-256 | `aafedb7eb871dc697fa484de6c3c700652640f2f24dfa8031b9290aef2a08729` |
| License | **CC0-1.0** (public domain dedication) |
| Provenance | Built with Blender + MPFB 2 from MakeHuman system assets (`makehuman_system_assets`, `faceunits01`): base mesh + ARKit face units by Mika Suominen (CC0); hair `short02`, eyes high-poly, eyebrow002, eyelashes02 (CC0). Generated character — not a real person. |

Processed into:

- `LiaDesktop.WinUI/src/LiaDesktop/Assets/models/lia_holo_mesh.bin` — head + jaw/funnel/wide/smile/brow morphs
- `lia_holo_hair.bin` — short bob hair volume
- `lia_holo_lashes.bin` — eyelashes + eyebrows

Rebuild: `python3 tools/build_lia_holo_mesh.py` (requires `pygltflib`, `numpy`; GLB at `/tmp/stock-02.glb`).

No Marvel / F.R.I.D.A.Y. / proprietary character assets are used.

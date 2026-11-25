# Third-party assets

## Photoreal hologram head — Microsoft Rocketbox Female_Adult_07

| Field | Value |
|-------|-------|
| Asset | `Female_Adult_07` (head mesh, 2K PBR maps, ARKit + Oculus viseme blendshapes) |
| Source | [microsoft/Microsoft-Rocketbox](https://github.com/microsoft/Microsoft-Rocketbox) |
| License | **MIT** (Copyright (c) Microsoft Corporation) |
| Use | Redistributable; personal & commercial OK with MIT notice |
| Pipeline | `tools/build_lia_pbr_head.py` (Blender bake → `lia_pbr_*.bin/png`) |

Runtime files: `lia_pbr_head.bin`, `lia_pbr_hair.bin`, `lia_pbr_albedo.png`, `lia_pbr_normal.png`, `lia_pbr_orm.png`, `lia_pbr_opacity.png`, `lia_pbr_meta.json`.

Rendering: PBR skin (albedo/normal/ORM + wrap/SSS approx) with a **subtle** amber hologram veil (fresnel rim, thin scanlines). Separate realistic iris + wet cornea shells. Hair/lash cards from Rocketbox opacity mesh.

## Previously used (removed from runtime in v0.5.0)

- MakeHuman / MPFB `stock-02` (argus-mask) — CC0-1.0 — kept only as historical reference in git history; **not** shipped.

## Other

- **Vortice** Direct3D 11 bindings — MIT
- **Microsoft.WindowsAppSDK** / WinUI 3 — Microsoft license
- **Microsoft.Playwright** — Apache-2.0 (uses installed Edge via `channel=msedge`; win32 Playwright node only)

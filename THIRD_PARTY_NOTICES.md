# Third-party notices

This repository includes third-party components that remain under their own
licenses. The proprietary LICENSE at the repo root does **not** relicense them.

## Microsoft Rocketbox — Female_Adult_07

| Field | Value |
|-------|-------|
| Asset | Head mesh, 2K PBR maps, ARKit + Oculus viseme blendshapes (baked to `lia_pbr_*`) |
| Paths | `LiaDesktop.WinUI/src/LiaDesktop/Assets/models/lia_pbr_*.{bin,png,json}` |
| Source | [microsoft/Microsoft-Rocketbox](https://github.com/microsoft/Microsoft-Rocketbox) |
| License | **MIT** — Copyright (c) 2020 Microsoft |
| Notice | Full text: [`licenses/ROCKETBOX_MIT.txt`](licenses/ROCKETBOX_MIT.txt) |
| Redistribution | Allowed under MIT (notice retained). **OK for a public repo.** |

Pipeline: `tools/build_lia_pbr_head.py` (Blender bake → runtime bins/PNGs).

## Historical MakeHuman / MPFB (CC0) — not the current head

| Field | Value |
|-------|-------|
| Asset | Derived hologram bins from argus-mask `stock-02` (MakeHuman/MPFB) used in v0.4.x |
| Paths (still tracked) | `lia_holo_mesh.bin`, `lia_holo_hair.bin`, `lia_holo_lashes.bin`, `lia_holo_brows.bin`, `lia_face_morph.glb` under WinUI/legacy/assets |
| Source mesh | Never committed as `stock-02.glb`; only derived bins/glb |
| License | **CC0-1.0** (MakeHuman/MPFB stock assets) |
| Status | Replaced at runtime by Rocketbox in v0.5.0+; remnants kept for reference |
| Redistribution | CC0 allows public redistribution. **OK for a public repo.** |

## Fonts (legacy Electron UI)

| Font | Paths | License | Source |
|------|-------|---------|--------|
| Exo 2 | `legacy/electron/public/fonts/Exo2-*.ttf` | **SIL OFL 1.1** | [googlefonts/Exo-2.0](https://github.com/googlefonts/Exo-2.0) |
| Orbitron | `legacy/electron/public/fonts/Orbitron-*.ttf` | **SIL OFL 1.1** (Reserved Font Name: "Orbitron") | [theleagueof/orbitron](https://github.com/theleagueof/orbitron) |
| Rajdhani | `legacy/electron/public/fonts/Rajdhani-*.ttf` | **SIL OFL 1.1** | Indian Type Foundry / Google Fonts |

OFL permits redistribution in a public repo; Reserved Font Name rules apply to
modified fonts under a different name. **OK for a public repo** (bundled as-is).

## Voice samples

| Field | Value |
|-------|-------|
| Paths | `assets/voice/lia_voice_sample.{wav,mp3}` |
| Likely origin | Synthetic speech via Microsoft Edge TTS (`ru-RU-SvetlanaNeural`) |
| License | Not a third-party redistributable asset with a clear OSS grant; treat as ATTA CORP demo media. Edge TTS / Microsoft Online Services terms govern generation — **flag**: if you need a hard redistribution guarantee, regenerate from a clearly licensed voice or remove before going public. |

## Icons

| Paths | Notes |
|-------|-------|
| `icon.png`, `icon.ico`, `legacy/electron/build/icon.*`, `LiaDesktop.WinUI/.../Assets/app.ico` | ATTA CORP original branding — covered by the proprietary LICENSE |

## NuGet / runtime libraries (not vendored in-tree)

Fetched at build time; not committed as binaries:

| Package | License |
|---------|---------|
| Vortice.Direct3D11 / DXGI / D3DCompiler / Mathematics | MIT |
| Microsoft.WindowsAppSDK / Windows.SDK.BuildTools | Microsoft proprietary / Windows SDK EULA |
| Microsoft.Playwright | Apache-2.0 (runtime uses installed Edge via `channel=msedge`; **no Playwright browser binaries committed**) |
| System.Security.Cryptography.ProtectedData / System.Text.Json / System.Drawing.Common | MIT (dotnet) |

## Playwright / Edge binaries

**Not present** in the repository. CI/dev installs Playwright separately; the app targets system Edge.

## See also

- [`THIRD_PARTY.md`](THIRD_PARTY.md) — short runtime-oriented summary
- [`licenses/ROCKETBOX_MIT.txt`](licenses/ROCKETBOX_MIT.txt) — Rocketbox MIT full text

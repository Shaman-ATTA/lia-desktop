# Lia Desktop — ship report

## v0.7.6 — обновление на месте — **Latest** (pending CI green)

In-place installer upgrade + GitHub auto-update + voice «обнови себя». Hard gates: `update_upgrade_exists`, `update_check`, `update_sha_mismatch`, `update_offline`, `update_e2e`. SHA256SUMS on release. Prior 49 gates stay hard. Head/materials/lip sync untouched.

## v0.7.5 — System telemetry · UIA · LTM · idle throttle

https://github.com/Shaman-ATTA/lia-desktop/releases/tag/v0.7.5  
CI: https://github.com/Shaman-ATTA/lia-desktop/actions/runs/46110017785 (`552fc35`)

Real system CPU/GPU/RAM/net/ping + ЛИА ЦП·ОЗУ; friendly mic journal; UIA agent tools (act-observe + destructive confirm); LTM (`memory.json` + Settings Память); Idle ~20fps economy that keeps Active visuals.

### Product

- **Idle visual parity**: same arcs/rings/hair/head/materials as Active. CPU only via ~20fps, PresentInterval 3, skip-unchanged Present, 1× RT blit (no MSAA resolve / FXAA). Smoke stays real default Idle pace.
- **`type_text` cascade** (product `ToolHost`, not the test): SendInput → clipboard paste (save/restore CF_UNICODETEXT) → `WM_SETTEXT` Edit; returns `method=sendinput|clipboard|settext` + verified readback.

### Hard gates (CI selftest on WARP)

| Gate | Result |
|------|--------|
| `app_idle_cpu` | PASS drop_pct=**81.0%** (fps60 38.54% → idle 7.34%; minimized 0.00%; hard ≥40%, min&lt;3%) |
| `idle_visual_parity` | PASS **mean_abs_diff=0.06** (max 16; framing OK on Idle + Active window composites) |
| `agent_notepad` | PASS **method=clipboard** text_len=16 (tool path only) |
| `mic_error_friendly` | PASS |
| `telemetry_system` | PASS |
| `agent_confirm` / `agent_uia_click` / `agent_retry` | PASS |
| `ltm_save` / `ltm_recall_new_session` / `ltm_forget` | PASS |
| Smoke framing (Idle pace) | PASS |
| Installer silent install/uninstall | PASS |

Assets: `LiaDesktop-0.7.5-setup.exe`, `LiaDesktop-0.7.5-win-x64.zip`, smoke/layout PNGs, `selftest.txt`.

## v0.7.4 — Offline wake word «Лиа»

https://github.com/Shaman-ATTA/lia-desktop/releases/tag/v0.7.4

## v0.7.2 — Vision + memory + Multimedia TTS + logs

https://github.com/Shaman-ATTA/lia-desktop/releases/tag/v0.7.2

## v0.7.1 — Barge-in + real SAPI + layout_window_max

https://github.com/Shaman-ATTA/lia-desktop/releases/tag/v0.7.1

## v0.7.0 — superseded

https://github.com/Shaman-ATTA/lia-desktop/releases/tag/v0.7.0

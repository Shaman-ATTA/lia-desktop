# Lia Desktop — ship report

## v0.7.6 — обновление на месте — **Latest**

https://github.com/Shaman-ATTA/lia-desktop/releases/tag/v0.7.6  
CI: https://github.com/Shaman-ATTA/lia-desktop/actions/runs/46132256560 (`3cb6be8`)

In-place installer upgrade + GitHub auto-update + voice «обнови себя» / «проверь обновления». Release ships `SHA256SUMS` + `update_check.png`.

### Product

- **Installer:** same AppId; CloseApplications on `{app}`; keeps settings / `memory.json` / Vosk / logs; same-version reinstall + downgrade OK; `/LAUNCHAFTER=1` after silent upgrade.
- **Auto-update:** GitHub Releases check on startup + every 6h; HUD «Доступно обновление vX.Y.Z»; Settings toggle + «Проверить сейчас»; download → SHA256 verify → silent Inno (breakaway from KillOnClose job) → relaunch.
- Head / materials / lip sync untouched.

### Hard gates (CI)

| Gate | Result |
|------|--------|
| `update_offline` | PASS |
| `update_sha_mismatch` | PASS |
| `update_check` | PASS (+ screenshot) |
| `update_upgrade_exists` | PASS (v0.7.5→0.7.6, marker survived, 1 Programs entry) |
| `update_e2e` | PASS (fake server → 0.7.7 bits) |
| `app_idle_cpu` / `idle_visual_parity` | PASS (prior gates kept) |
| Smoke / installer / live Gemini | PASS |

Assets: `LiaDesktop-0.7.6-setup.exe`, `LiaDesktop-0.7.6-win-x64.zip`, `SHA256SUMS`, smoke/layout PNGs, `selftest.txt`, `update_check.png`.

## v0.7.5 — System telemetry · UIA · LTM · idle throttle

https://github.com/Shaman-ATTA/lia-desktop/releases/tag/v0.7.5

## v0.7.4 — Offline wake word «Лиа»

https://github.com/Shaman-ATTA/lia-desktop/releases/tag/v0.7.4

## v0.7.2 — Vision + memory + Multimedia TTS + logs

https://github.com/Shaman-ATTA/lia-desktop/releases/tag/v0.7.2

## v0.7.1 — Barge-in + real SAPI + layout_window_max

https://github.com/Shaman-ATTA/lia-desktop/releases/tag/v0.7.1

## v0.7.0 — superseded

https://github.com/Shaman-ATTA/lia-desktop/releases/tag/v0.7.0

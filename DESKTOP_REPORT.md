# Lia Desktop — ship report

## v0.7.5 — Idle render throttle (Latest)

https://github.com/Shaman-ATTA/lia-desktop/releases/tag/v0.7.5

Published non-draft Latest. ОЖИДАНИЕ ~30 fps; speaking/listening 60 fps; minimized/occluded/locked pause Present. Selftest hard `app_idle_cpu` (Idle drop ≥40% vs 60fps; minimized &lt;3%; prints fps60/fps30/drop/minimized + gpu). Wake «Лиа» + wake_* absolute CPU labels from v0.7.4 kept.

## v0.7.4 — Offline wake word «Лиа»

https://github.com/Shaman-ATTA/lia-desktop/releases/tag/v0.7.4

Offline Vosk wake «Лиа»/«Лия» + inline commands; echo-guard vs own TTS; MicGemini 8s no-speech → ОЖИДАНИЕ; Settings toggle/sensitivity. Streaming TTS from v0.7.3 kept. Selftest: `wake_*` incl. `wake_e2e`/`wake_timeout`/`wake_cpu` absolute baseline/active/delta + gpu + all prior hard gates. Assets: setup/zip, `selftest.txt`, `sample_log.txt` (with `wake: DETECT`), vision_test, layout/hud/speaking.

## v0.7.2 — Vision + memory + Multimedia TTS + logs

https://github.com/Shaman-ATTA/lia-desktop/releases/tag/v0.7.2

Selftest PASS incl. `playback_rms` on default Multimedia (VB-CABLE), `render_role` / `render_nondefault_comm`, `log_file`, vision_live, memory_*.

## v0.7.1 — Barge-in + real SAPI + layout_window_max

https://github.com/Shaman-ATTA/lia-desktop/releases/tag/v0.7.1

Published non-draft Latest. Selftest PASS: `tts_ws_only`, `tts_sapi`, barge_*. Live maximized `layout_window_max.png` + exact 1920/4K dumps.

## v0.7.0 — superseded by later Latest

https://github.com/Shaman-ATTA/lia-desktop/releases/tag/v0.7.0

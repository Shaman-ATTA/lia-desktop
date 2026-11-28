# Changelog

## v0.7.6 — обновление на месте

- **Installer upgrade:** same AppId — upgrades over an existing install (closes LiaDesktop via CloseApplications, replaces binaries, keeps settings / `memory.json` / Vosk / logs under `%LOCALAPPDATA%`). Same-version reinstall and downgrade allowed.
- **In-app auto-update:** GitHub Releases `latest` check on startup + every 6h (unauthenticated; quiet on offline / rate limit). HUD «Доступно обновление vX.Y.Z» with «Обновить» / «Позже»; Settings toggle «Проверять обновления автоматически» + «Проверить сейчас» (shows current version).
- **Install path:** download setup → verify SHA256 against release `SHA256SUMS` → silent Inno upgrade (`/LAUNCHAFTER=1`) → app exits → installer relaunches Lia.
- **Voice:** «Лиа, обнови себя» / «проверь обновления» → same flow; confirmation before install.
- **Hard gates:** `update_upgrade_exists`, `update_check` (+ screenshot), `update_sha_mismatch`, `update_offline`, `update_e2e` — prior gates unchanged (idle_cpu, idle_visual_parity, live Gemini, wake, layout, …). Head / materials / lip sync untouched.

## v0.7.5 — System telemetry · UIA agent · LTM · idle throttle

- **ЖУРНАЛ / mic:** capture failures show one RU line («Микрофон недоступен — проверьте устройство в Настройках»); raw WaveIn/WASAPI walls go to the log file only; ГОЛОС → `МИК НЕДОСТ.`. Gate `mic_error_friendly`.
- **Telemetry:** system CPU (GetSystemTimes), GPU % (PDH best-effort), RAM used/total ГБ, net ↓↑ Мбит/с, ping, plus `ЛИА ЦП · ОЗУ`. Gate `telemetry_system` (±10 vs typeperf under load).
- **Idle visual parity:** Idle (ОЖИДАНИЕ) keeps arcs/hair/rings/head/materials identical to Active; CPU saved only via ~20fps, skip-unchanged Present, cheaper AA (1× RT). Gate `idle_visual_parity` (check_framing on both + `mean_abs_diff=`). Smoke stays on real Idle pace.
- **Agent PC control:** UIA tools `list_windows` / `focus_window` / `wait_for_window` / `find_element` / `click_element`; `type_text` cascades SendInput → clipboard paste (restore user clipboard) → WM_SETTEXT with verified readback (`method=`); act-observe after click/type/ps/browser; destructive confirm dialog (Settings toggle). Gates `agent_notepad` (tool path only + independent read), `agent_uia_click`, `agent_confirm`, `agent_retry`.
- **Long-term memory:** `%LOCALAPPDATA%\LiaDesktop\memory.json`, tools `remember_fact` / `recall` / `forget_fact`, auto-capture name/prefs, Settings «Память», «забудь всё обо мне». Gates `ltm_save`, `ltm_recall_new_session`, `ltm_forget`.
- **Render pace:** ОЖИДАНИЕ ~20 fps economy (1× RT/no MSAA resolve, no FXAA/hair/arcs, eased rings, skip Present when unchanged; HUD/telemetry throttled); speaking/listening 60 fps; minimized/occluded/locked pause Present. Gate `app_idle_cpu` hard ≥40% Idle vs Active on all adapters; minimized &lt;3%; prints `fps60_avg`/`fps_idle_avg`/`drop`/`minimized`/`gpu`.
- Wake «Лиа» from v0.7.4 kept (`wake_cpu` absolute baseline/active/delta + gpu).

## v0.7.4 — Offline wake word «Лиа»

- **Wake word:** always-listening low-CPU Vosk `vosk-model-small-ru-0.22` with grammar `["лиа","лия","[unk]"]` (UTF-8 PInvoke); no cloud for detection.
- **Behaviour:** ОЖИДАНИЕ → «Лиа» pulses HUD + optional soft chime → capture follow-up; one-breath «Лиа, открой блокнот» runs the command; mic button unchanged.
- **Echo guard:** own TTS (barge PlaybackStarted/Stopped) must not self-wake.
- **Settings:** toggle «Пробуждение по слову „Лиа“» (default ON) + sensitivity slider; model downloads on first run (~45MB) into `%LOCALAPPDATA%\LiaDesktop\vosk` (installer stays lean).
- **Follow-up safety:** MicGemini no-speech hard timeout (8s) → ОЖИДАНИЕ (never hang).
- **Selftest hard gates:** `wake_positive` ≥90%, `wake_negative` ≤1 false wake (`speech_s`/`noise_s`/`false_wakes`), `wake_own_tts`, `wake_command_inline`, `wake_e2e` (injectable mic → mock Gemini → TTS ≤60s), `wake_timeout` (silence → ОЖИДАНИЕ), `wake_offline`, `wake_cpu` listen overhead &lt;5% with absolute `baseline_avg`/`active_avg`/`delta` + `gpu` (`mode=wavein|wasapi|loopback`); all prior gates unchanged. `sample_log.txt` includes `wake: DETECT` lines.

## v0.7.3 — Speak from the first sentence (low TTS latency)

- **Streaming TTS:** split reply on RU `.!?…` / newlines (+ soft ~200-char comma cuts); synthesize sentence 1 immediately and play as soon as ready while prefetching 2+ (ordered).
- **Edge warm WS:** pre-connect when the user sends; first-audio-byte flush; abrupt close without handshake still OK if audio received.
- **Barge-in:** abort cancels current playback **and** the rest of the sentence queue; lip sync follows each chunk.
- **Timings logged:** `t_text_ready`, `t_first_audio_byte`, `t_playback_start` (ms from turn origin).
- **Selftest:** `tts_first_sentence_latency`, `tts_order`, `barge_queue_cancel`, live latency lines — prior gates remain hard.

## v0.7.2 — Vision + dialog memory + audible Multimedia TTS + logs

- **Field fix (inaudible TTS):** playback uses default **Multimedia/Console** render (never Communications); follow default-device changes; WasapiOut → WaveOutEvent fallback; log `TTS → <device>`. Settings: output device picker + **ПРОВЕРИТЬ ГОЛОС**.
- **Persistent logs:** `%LOCALAPPDATA%\LiaDesktop\logs\lia-YYYY-MM-DD.log` (UTF-8, rotate 7d / ~5MB); startup diagnostics (version, displays, all audio endpoints + defaults, mic, model, key yes/no); TTS device/bytes/dur/RMS; crash handler; HUD **ЛОГИ** + Settings copy to Desktop as `Lia-log.txt`.
- **Edge TTS:** abrupt WebSocket close without handshake is SUCCESS if audio bytes (or turn.end) already received.
- **Vision:** `look_at_screen` / `screenshot` → Gemini `inline_data`; DPI click map.
- **Dialog memory:** session persist, «забудь» / НОВЫЙ РАЗГОВОР, barge truncated spoken.
- **Selftest:** `log_file`, `render_role`, `render_nondefault_comm`, `playback_rms` (hard loopback on VB-CABLE), `ws_close_handshake`, `vision_live`, `click_map`, `memory_live` (hard Слава+58) + prior gates. Release asset `sample_log.txt`.

## v0.7.1 — Barge-in / natural interruption + real SAPI

See prior release notes on GitHub.

## v0.7.0 — superseded

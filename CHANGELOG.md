# Changelog

## v0.7.5 — System telemetry · UIA agent · LTM · idle throttle

- **ЖУРНАЛ / mic:** capture failures show one RU line («Микрофон недоступен — проверьте устройство в Настройках»); raw WaveIn/WASAPI walls go to the log file only; ГОЛОС → `МИК НЕДОСТ.`. Gate `mic_error_friendly`.
- **Telemetry:** system CPU (GetSystemTimes), GPU % (PDH best-effort), RAM used/total ГБ, net ↓↑ Мбит/с, ping, plus `ЛИА ЦП · ОЗУ`. Gate `telemetry_system` (±10 vs typeperf under load).
- **Agent PC control:** UIA tools `list_windows` / `focus_window` / `wait_for_window` / `find_element` / `click_element`; act-observe screenshot after click/type/ps/browser; destructive confirm dialog (Settings toggle). Gates `agent_notepad`, `agent_uia_click`, `agent_confirm`, `agent_retry`.
- **Long-term memory:** `%LOCALAPPDATA%\LiaDesktop\memory.json`, tools `remember_fact` / `recall` / `forget_fact`, auto-capture name/prefs, Settings «Память», «забудь всё обо мне». Gates `ltm_save`, `ltm_recall_new_session`, `ltm_forget`.
- **Render pace:** ОЖИДАНИЕ ~30 fps; speaking/listening 60 fps; minimized/occluded/locked pause Present. Gate `app_idle_cpu` (Idle drop ≥40%; minimized &lt;3%).
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

- **Mic open while speaking:** WASAPI communications capture (OS AEC) + software echo-cancel VAD; ≥300ms user speech barges in.
- **Interrupt policy:** backchannels (`угу`/`ага`/`да`/`продолжай`/`дальше`/`так`) resume remainder; `стоп`/`хватит`/`замолчи`/`тихо` stop silent; else new request with spoken-so-far context.
- **HUD:** `СЛУШАЮ` on barge-in; Edge TTS SvetlanaNeural unchanged (no Gemini TTS).
- **SAPI:** System.Speech wave output (ru-RU Irina/Pavel if installed, else any voice); reject header-only/silent files. Selftest requires ≥10KB + RMS for `tts_sapi` and `tts_ws_only`.
- **CI:** live maximized `layout_window_max.png`; barge_* gates in `selftest.txt`.

## v0.7.0 — Edge TTS Sec-MS-GEC, SAPI fallback, HiDPI MatrixTransform

- **TTS:** C# WebSocket path ports edge-tts 7.3.1 Sec-MS-GEC v2 DRM (clock-skew adjust on 403). Python fallback removed.
- **Offline:** Windows SAPI/OneCore voice if Edge fails (prefer ru-RU Irina/Pavel).
- **Journal:** one-line errors; full traces in `%LOCALAPPDATA%\LiaDesktop\error.log`.
- **HiDPI / maximize:** SwapChainPanel buffers sized with CompositionScaleX/Y + inverse `IDXGISwapChain2.MatrixTransform`; settle/ResizeBuffers on maximize (100% DPI TV clip/grey-box).
- **CI:** `tts_ws_only` + `tts_sapi`; exact 1920×1080 / 3840×2160 offscreen dumps (`--smoke-virtual-size` + DXGI composite) for front_1080/4k + 100% windowed/max/maxlaunch; higher DPI layout shots clamped to usable desktop.

## v0.6.9 — Gemini resilience, mic STT fallback, HiDPI centre stage

- Default `gemini-flash-lite-latest`; 25s/attempt; retry once then next ListModels chain.
- Mic STT via NAudio VAD + Gemini when Windows Speech missing.
- Centre-column hologram stage; E2E `--selftest` mock + live key.

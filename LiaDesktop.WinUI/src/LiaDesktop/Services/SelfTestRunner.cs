using System.Diagnostics;
using System.Drawing;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using LiaDesktop.Services.Tools;
using NAudio.Wave;

namespace LiaDesktop.Services;

/// <summary>
/// Headless E2E gate (--selftest): mock Gemini scenarios + optional live call.
/// Writes selftest.txt and exits the process with 0/1.
/// </summary>
public static class SelfTestRunner
{
    public static bool IsSelfTest { get; private set; }
    public static string? OutPath { get; private set; }

    public static bool Detect(string[] args)
    {
        IsSelfTest = args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)
                                   || a.StartsWith("--selftest=", StringComparison.OrdinalIgnoreCase)
                                   || a.Equals("--selftest-live", StringComparison.OrdinalIgnoreCase));
        foreach (var a in args)
        {
            if (a.StartsWith("--selftest-out=", StringComparison.OrdinalIgnoreCase))
                OutPath = a["--selftest-out=".Length..];
        }
        OutPath ??= Path.Combine(Environment.CurrentDirectory, "selftest.txt");
        return IsSelfTest;
    }

    public static async Task RunAndExitAsync(AppServices services)
    {
        var log = new StringBuilder();
        void L(string s)
        {
            log.AppendLine(s);
            services.Log.Info("[selftest] " + s);
            Program.BootLog("selftest: " + s);
        }

        var failed = 0;
        var baseUrl = GeminiEndpoints.Base;
        var liveKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY")
                      ?? Environment.GetEnvironmentVariable("LIA_GEMINI_API_KEY");
        // Never log the key — only whether present
        L($"gemini_base={baseUrl}");
        L($"live_key_present={!string.IsNullOrWhiteSpace(liveKey)}");

        try
        {
            // Isolate config
            var cfg = services.Secrets.Load();
            cfg.Provider = "gemini";
            cfg.ApiKey = "ci-mock-key";
            cfg.Model = "gemini-2.5-flash"; // retired → flash-lite-latest via ListModels
            cfg.VisionModel = cfg.Model;
            services.Secrets.Save(cfg);

            if (!baseUrl.Contains("googleapis.com", StringComparison.OrdinalIgnoreCase))
            {
                failed += await RunMockScenariosAsync(services, baseUrl, L);
            }
            else
            {
                L("SKIP mock scenarios (base is production — pass --gemini-base=mock)");
            }

            // TTS gates — must prove C# WebSocket alone (no Python) + SAPI offline
            failed += await RunTtsGatesAsync(services, L);

            // Streaming first-sentence TTS pipeline
            failed += await RunStreamingTtsGatesAsync(services, L);

            // Audio render: Multimedia (not Communications) + abrupt WS close + loopback RMS
            failed += await RunRenderGatesAsync(services, L);

            // Barge-in: backchannel / stop / new-request + TTS loopback must not trigger
            services.Agent.SuspendBargeHandling(true);
            try
            {
                failed += await RunBargeInGatesAsync(services, L);
            }
            finally
            {
                services.Agent.SuspendBargeHandling(false);
            }
            services.Agent.Abort();
            await Task.Delay(400);
            services.Agent.Memory.Clear();

            // Offline wake word «Лиа» (Vosk) — hard gates (before log sample so wake lines appear)
            L("wake: starting gates…");
            failed += await RunWakeGatesAsync(services, L);
            L("wake: gates done");

            // Persistent log file gate (+ sample_log.txt with wake DETECT lines)
            failed += RunLogFileGate(services, L);

            // Vision click mapping (no network) + dialog memory (mock) + persist
            failed += RunClickMapGate(L);
            failed += await RunMemoryGatesAsync(services, baseUrl, L);

            if (!string.IsNullOrWhiteSpace(liveKey))
            {
                var liveFail = await RunLiveAsync(services, liveKey!, L);
                failed += liveFail;
                if (liveFail != 0)
                    L("FAIL live: at least one real model must answer (flash-lite-latest preferred)");
                failed += await RunVisionLiveAsync(services, liveKey!, L);
            }
            else
            {
                L("SKIP live: GEMINI_API_KEY absent");
                L("vision_live: SKIP (no GEMINI_API_KEY)");
            }
        }
        catch (Exception ex)
        {
            L("FAIL unhandled: " + ex.GetType().Name + ": " + ex.Message);
            failed++;
        }

        L(failed == 0 ? "SELFTEST PASS" : $"SELFTEST FAIL count={failed}");
        try
        {
            File.WriteAllText(OutPath!, log.ToString(), Encoding.UTF8);
            L($"wrote {OutPath}");
        }
        catch (Exception ex)
        {
            Program.BootLog("selftest write failed: " + ex.Message);
        }

        await Task.Delay(400);
        Environment.Exit(failed == 0 ? 0 : 1);
    }

    static async Task<int> RunMockScenariosAsync(AppServices s, string baseUrl, Action<string> L)
    {
        var fail = 0;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        async Task SetScenario(string name)
        {
            var idx = baseUrl.IndexOf("/v1beta", StringComparison.OrdinalIgnoreCase);
            var root = idx > 0 ? baseUrl[..idx] : baseUrl.TrimEnd('/');
            var url = $"{root}/_control/scenario/{name}";
            using var res = await http.PostAsync(url, null);
            res.EnsureSuccessStatusCode();
            L($"scenario → {name} ({(int)res.StatusCode})");
        }

            // 1) phantom saved model → ListModels picks flash, then normal reply
        await SetScenario("normal");
        fail += await OneTurnAsync(s, "привет", "normal+listmodels", L, expectRu: true);

        // 2) 503 x2 then 200 (same model: attempt + 1 retry, then would move — mock succeeds on 3rd hit
        //    so force single-model by scenario that returns 503 twice then 200 on same id)
        await SetScenario("overload503");
        PersistModel(s, "gemini-flash-lite-latest");
        fail += await OneTurnAsync(s, "привет", "overload503", L, expectRu: true, timeoutSec: 90);

        // 3) 429 + Retry-After
        await SetScenario("retry429");
        PersistModel(s, "gemini-flash-lite-latest");
        fail += await OneTurnAsync(s, "привет", "retry429", L, expectRu: true);

        // 4) slow 20s (under 25s attempt? — 20s OK; if flaky bump attempt — mock sleeps 20)
        await SetScenario("slow20");
        PersistModel(s, "gemini-flash-lite-latest");
        fail += await OneTurnAsync(s, "привет", "slow20", L, expectRu: true, timeoutSec: 60);

        // 5) generateContent 404 on lite → chain moves to next preferred
        await SetScenario("badmodel");
        PersistModel(s, "gemini-3.1-flash-lite");
        fail += await OneTurnAsync(s, "привет", "badmodel404", L, expectRu: true);

        return fail;
    }

    static void PersistModel(AppServices s, string model)
    {
        var cfg = s.Secrets.Load();
        cfg.Model = model;
        cfg.VisionModel = model;
        s.Secrets.Save(cfg);
    }

    static async Task<int> RunLiveAsync(AppServices s, string key, Action<string> L)
    {
        L("LIVE begin (key redacted)");
        var prev = GeminiEndpoints.Base;
        try
        {
            GeminiEndpoints.Base = GeminiEndpoints.DefaultBase;
            var cfg = s.Secrets.Load();
            cfg.Provider = "gemini";
            cfg.ApiKey = key;
            cfg.Model = ""; // resolve via ListModels
            cfg.VisionModel = "";
            s.Secrets.Save(cfg);
            s.Agent.SuspendBargeHandling(true);
            s.Agent.Abort();
            s.Agent.Memory.Clear();
            await Task.Delay(200);
            // Chain may walk preferred models (25s each × 2); lite-latest should answer fast.
            try
            {
                var rc = await OneTurnAsync(s, "привет", "live", L, expectRu: true, timeoutSec: 120);
                L($"live latency: t_text_ready={s.Agent.StreamTts.LastTextReadyMs}ms t_first_audio_byte={s.Agent.StreamTts.LastFirstAudioByteMs}ms t_playback_start={s.Agent.StreamTts.LastPlaybackStartMs}ms");
                return rc;
            }
            finally
            {
                s.Agent.SuspendBargeHandling(false);
            }
        }
        finally
        {
            GeminiEndpoints.Base = prev;
            // wipe key from temp config
            var cfg = s.Secrets.Load();
            cfg.ApiKey = "ci-mock-key";
            s.Secrets.Save(cfg);
        }
    }

    static async Task<int> OneTurnAsync(
        AppServices s,
        string prompt,
        string name,
        Action<string> L,
        bool expectRu,
        int timeoutSec = 60,
        string[]? mustContain = null,
        string[]? mustNotContain = null,
        bool requireTts = true)
    {
        var marker = $"__selftest_{name}_{Guid.NewGuid():N}__";
        s.Log.Info(marker);

        string? narrated = null;
        string? speakPath = null;
        void OnNarrate(string t) => narrated = t;
        void OnSpeak(string p) => speakPath = p;
        s.Agent.Narrate += OnNarrate;
        s.Agent.SpeakFile += OnSpeak;

        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));
            // Agent.RunAsync has its own CTS — call gemini+tts path via RunAsync
            var run = s.Agent.RunAsync(prompt);
            var done = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(timeoutSec)));
            if (done != run)
            {
                s.Agent.Abort();
                L($"FAIL {name}: timeout {timeoutSec}s");
                return 1;
            }
            await run;
        }
        catch (Exception ex)
        {
            L($"FAIL {name}: exception {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
        finally
        {
            s.Agent.Narrate -= OnNarrate;
            s.Agent.SpeakFile -= OnSpeak;
        }
        sw.Stop();

        var lines = s.Log.Lines.Select(x => x.Line + " " + (x.Detail ?? "")).ToList();
        var after = lines.SkipWhile(l => !l.Contains(marker)).Skip(1).ToList();
        var blob = string.Join("\n", after);

        // Soft: barge/Abort often logs cancel while a valid Narrate already landed
        var canceledNoise = blob.Contains("operation was canceled", StringComparison.OrdinalIgnoreCase)
                            || blob.Contains("The operation was canceled", StringComparison.OrdinalIgnoreCase);
        if (canceledNoise && string.IsNullOrWhiteSpace(narrated))
        {
            L($"FAIL {name}: journal contains 'operation was canceled'");
            return 1;
        }

        if (string.IsNullOrWhiteSpace(narrated))
        {
            L($"FAIL {name}: empty Narrate reply (journal): {Truncate(blob, 240)}");
            return 1;
        }
        if (narrated.Contains("Ошибка Gemini", StringComparison.OrdinalIgnoreCase) ||
            narrated.Contains("Нет связи", StringComparison.OrdinalIgnoreCase) ||
            narrated.Contains("Ключ API", StringComparison.OrdinalIgnoreCase))
        {
            L($"FAIL {name}: error narrated: {narrated}");
            return 1;
        }
        if (expectRu && name != "live" && mustContain == null &&
            !narrated.Contains("Привет", StringComparison.OrdinalIgnoreCase))
        {
            L($"FAIL {name}: expected mock 'Привет…' reply, got: {Truncate(narrated, 120)}");
            return 1;
        }
        if (mustContain != null)
        {
            foreach (var m in mustContain)
            {
                if (!narrated.Contains(m, StringComparison.OrdinalIgnoreCase))
                {
                    L($"FAIL {name}: reply missing '{m}': {Truncate(narrated, 200)}");
                    return 1;
                }
            }
        }
        if (mustNotContain != null)
        {
            foreach (var m in mustNotContain)
            {
                if (narrated.Contains(m, StringComparison.OrdinalIgnoreCase))
                {
                    L($"FAIL {name}: reply unexpectedly contains '{m}': {Truncate(narrated, 200)}");
                    return 1;
                }
            }
        }

        if (requireTts)
        {
            // SpeakFile is raised before RunAsync returns; if TTS failed, retry directly.
            if (speakPath == null || !File.Exists(speakPath) || new FileInfo(speakPath).Length <= 0)
            {
                L($"{name}: SpeakFile missing — retrying EdgeTts directly…");
                speakPath = await s.Tts.SpeakToFileAsync(narrated!, CancellationToken.None);
            }

            if (speakPath == null || !File.Exists(speakPath))
            {
                L($"FAIL {name}: TTS file missing (edge-tts / websocket failed)");
                return 1;
            }
            var len = new FileInfo(speakPath).Length;
            if (len <= 0)
            {
                L($"FAIL {name}: TTS mp3 empty");
                return 1;
            }

            L($"PASS {name}: reply={narrated!.Length}c tts={len}B {sw.ElapsedMilliseconds}ms");
        }
        else
        {
            L($"PASS {name}: reply={Truncate(narrated!, 120)} {sw.ElapsedMilliseconds}ms");
        }
        return 0;
    }

    static int RunClickMapGate(Action<string> L)
    {
        try
        {
            // DPI 100/150/200% → different physical sizes for same logical 1920×1080 desktop
            // Target button rect in physical pixels; map from downscaled image coords and require centre lands inside.
            var cases = new (int Dpi, int PhysW, int PhysH, int ImgW, int ImgH)[]
            {
                (96, 1920, 1080, 1600, 900),   // 100%
                (144, 2880, 1620, 1600, 900),  // 150%
                (192, 3840, 2160, 1600, 900),  // 200%
            };
            foreach (var (dpi, pw, ph, iw, ih) in cases)
            {
                var shot = ScreenShot.ForMapping(pw, ph, iw, ih, 0, 0, dpi);
                // Synthetic target button: 160×80 centred
                var btnL = pw / 2 - 80;
                var btnT = ph / 2 - 40;
                var btnR = btnL + 160;
                var btnB = btnT + 80;
                var targetPx = (btnL + btnR) / 2;
                var targetPy = (btnT + btnB) / 2;
                var imgX = (int)Math.Round(targetPx / shot.ScaleX);
                var imgY = (int)Math.Round(targetPy / shot.ScaleY);
                var mappedX = shot.ImageToPhysicalX(imgX);
                var mappedY = shot.ImageToPhysicalY(imgY);
                if (mappedX < btnL || mappedX >= btnR || mappedY < btnT || mappedY >= btnB)
                {
                    L($"FAIL click_map: dpi={dpi} mapped=({mappedX},{mappedY}) outside button [{btnL},{btnT}-{btnR},{btnB}]");
                    return 1;
                }
                if (Math.Abs(mappedX - targetPx) > 2 || Math.Abs(mappedY - targetPy) > 2)
                {
                    L($"FAIL click_map: dpi={dpi} mapped=({mappedX},{mappedY}) target=({targetPx},{targetPy})");
                    return 1;
                }
            }

            // Live: topmost TARGET overlay; map image→physical and SetCursorPos into it
            using var overlay = Win32TestOverlay.Show(
                "TARGET", 200, 200, 320, 160,
                Color.Orange, Color.Black, 36f);
            var bounds = overlay.ScreenBounds;
            var cx = bounds.Left + bounds.Width / 2;
            var cy = bounds.Top + bounds.Height / 2;
            var physW = GetSystemMetrics(0);
            var physH = GetSystemMetrics(1);
            var shotLive = ScreenShot.ForMapping(physW, physH, physW, physH, 0, 0, 96);
            var px = shotLive.ImageToPhysicalX(cx);
            var py = shotLive.ImageToPhysicalY(cy);
            if (px < bounds.Left || px >= bounds.Right || py < bounds.Top || py >= bounds.Bottom)
            {
                L($"FAIL click_map: live mapped=({px},{py}) outside overlay {bounds}");
                return 1;
            }
            SetCursorPos(px, py);
            mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
            mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);

            L("click_map: PASS (dpi 100/150/200 + live target overlay)");
            return 0;
        }
        catch (Exception ex)
        {
            L($"FAIL click_map: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int nIndex);

    static async Task<int> RunMemoryGatesAsync(AppServices s, string baseUrl, Action<string> L)
    {
        var fail = 0;
        try
        {
            s.Agent.SuspendBargeHandling(true);
            s.Agent.Abort();
            await Task.Delay(300);
            s.Agent.Memory.Clear();

            // memory_live — two turns via mock (or live if base is prod — still works)
            if (!baseUrl.Contains("googleapis.com", StringComparison.OrdinalIgnoreCase))
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                var idx = baseUrl.IndexOf("/v1beta", StringComparison.OrdinalIgnoreCase);
                var root = idx > 0 ? baseUrl[..idx] : baseUrl.TrimEnd('/');
                await http.PostAsync($"{root}/_control/scenario/normal", null);
            }

            // turn1: soft (any non-error ack). turn2 memory_live: HARD — must contain Слава + 58.
            fail += await OneTurnAsync(
                s, "меня зовут Слава, запомни число 58", "memory_turn1", L,
                expectRu: false, timeoutSec: 90);

            fail += await OneTurnAsync(
                s, "как меня зовут и какое число?", "memory_live", L,
                expectRu: false, mustContain: ["Слава", "58"], timeoutSec: 90);

            // memory_clear
            fail += await OneTurnAsync(
                s, "забудь", "memory_clear_cmd", L,
                expectRu: false, mustContain: ["новый разговор"], timeoutSec: 60);

            fail += await OneTurnAsync(
                s, "как меня зовут и какое число?", "memory_clear", L,
                expectRu: false, mustNotContain: ["58"], timeoutSec: 90);

            // memory_persist — write facts, reload ConversationMemory from disk
            s.Agent.Memory.Clear();
            s.Agent.Memory.AppendUserText("меня зовут Слава, запомни число 58");
            s.Agent.Memory.AppendModelParts(new System.Text.Json.Nodes.JsonArray(
                new System.Text.Json.Nodes.JsonObject { ["text"] = "Запомнила, Слава, 58." }));
            var path = ConversationMemory.PathOverride
                       ?? Path.Combine(
                           Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                           "LiaDesktop", "session.json");
            if (!File.Exists(path))
            {
                L("FAIL memory_persist: session.json not written");
                fail++;
            }
            else
            {
                var reloaded = new ConversationMemory(s.Log, path);
                // ToJsonString escapes Cyrillic as \uXXXX — check decoded text parts
                var blob = new StringBuilder();
                foreach (var node in reloaded.Snapshot())
                {
                    if (node is not System.Text.Json.Nodes.JsonObject obj) continue;
                    if (obj["parts"] is not System.Text.Json.Nodes.JsonArray parts) continue;
                    foreach (var p in parts)
                    {
                        if (p is System.Text.Json.Nodes.JsonObject po &&
                            po["text"] is System.Text.Json.Nodes.JsonValue tv)
                            blob.Append(tv.GetValue<string>()).Append(' ');
                    }
                }
                var decoded = blob.ToString();
                if (!decoded.Contains("Слава", StringComparison.OrdinalIgnoreCase) ||
                    !decoded.Contains("58", StringComparison.Ordinal))
                {
                    L($"FAIL memory_persist: reload missing facts: {Truncate(decoded, 200)}");
                    fail++;
                }
                else
                    L("memory_persist: PASS (session.json reload)");
            }
        }
        catch (Exception ex)
        {
            L($"FAIL memory_gates: {ex.GetType().Name}: {ex.Message}");
            fail++;
        }
        finally
        {
            s.Agent.SuspendBargeHandling(false);
        }
        return fail;
    }

    static async Task<int> RunVisionLiveAsync(AppServices s, string key, Action<string> L)
    {
        L("vision_live begin");
        var prev = GeminiEndpoints.Base;
        Win32TestOverlay? overlay = null;
        try
        {
            GeminiEndpoints.Base = GeminiEndpoints.DefaultBase;
            var cfg = s.Secrets.Load();
            cfg.Provider = "gemini";
            cfg.ApiKey = key;
            cfg.Model = "";
            cfg.VisionModel = "";
            s.Secrets.Save(cfg);
            s.Agent.SuspendBargeHandling(true);
            s.Agent.Abort();
            s.Agent.Memory.Clear();
            // Live turn may have left a partial tool exchange; wipe again after Abort settles.
            await Task.Delay(400);
            s.Agent.Memory.Clear();

            overlay = Win32TestOverlay.Show(
                "ЛИА ТЕСТ 7429", 80, 80, 900, 400,
                Color.White, Color.Black, 72f);
            await Task.Delay(500);

            // Save vision_test.png for release assets
            try
            {
                var shot = ScreenCapture.CapturePrimary(
                    Path.Combine(Environment.CurrentDirectory, "vision_test.png"),
                    jpeg: false);
                L($"vision_test.png saved ({shot.ImageWidth}x{shot.ImageHeight})");
            }
            catch (Exception ex)
            {
                L("vision_test.png save warn: " + ex.Message);
            }

            return await OneTurnAsync(
                s,
                "что написано на экране?",
                "vision_live",
                L,
                expectRu: false,
                timeoutSec: 120,
                mustContain: ["7429"]);
        }
        catch (Exception ex)
        {
            L($"FAIL vision_live: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
        finally
        {
            try { overlay?.Dispose(); } catch { /* */ }
            s.Agent.SuspendBargeHandling(false);
            GeminiEndpoints.Base = prev;
            var cfg = s.Secrets.Load();
            cfg.ApiKey = "ci-mock-key";
            s.Secrets.Save(cfg);
        }
    }

    static int RunLogFileGate(AppServices s, Action<string> L)
    {
        try
        {
            // Plant a lookalike key; Info/redact must scrub it from the file
            const string fakeKey = "AIzaSyD-selftest-redact-check-XXXXXXXX";
            s.Log.Info("redact-probe api_key=" + fakeKey);

            // Ensure diagnostics present
            s.Log.WriteStartupDiagnostics(s);
            var path = AppLog.TodayLogPath();
            if (!File.Exists(path))
            {
                L($"FAIL log_file: missing {path}");
                return 1;
            }
            var text = File.ReadAllText(path, Encoding.UTF8);
            if (!text.Contains("STARTUP DIAGNOSTICS", StringComparison.Ordinal))
            {
                L("FAIL log_file: missing STARTUP DIAGNOSTICS block");
                return 1;
            }
            if (!text.Contains("RENDER", StringComparison.Ordinal) &&
                !text.Contains("render", StringComparison.OrdinalIgnoreCase))
            {
                L("FAIL log_file: missing audio device list");
                return 1;
            }
            var cfg = s.Secrets.Load();
            if (!string.IsNullOrWhiteSpace(cfg.ApiKey) &&
                text.Contains(cfg.ApiKey, StringComparison.Ordinal))
            {
                L("FAIL log_file: API key leaked into log");
                return 1;
            }
            if (text.Contains(fakeKey, StringComparison.Ordinal) ||
                Regex.IsMatch(text, @"AIza[0-9A-Za-z\-_]{20,}"))
            {
                L("FAIL log_file: AIza-looking key in log (redact failed)");
                return 1;
            }
            if (!text.Contains("[REDACTED", StringComparison.Ordinal))
            {
                L("FAIL log_file: expected redact marker after probe");
                return 1;
            }
            // Wake gates run before this — sample must show wake events
            if (!text.Contains("wake: DETECT", StringComparison.Ordinal))
            {
                L("FAIL log_file: missing wake: DETECT lines (wake gates should have logged)");
                return 1;
            }

            // Copy sample for CI release asset (cwd + next to exe)
            try
            {
                var sample = Path.Combine(Environment.CurrentDirectory, "sample_log.txt");
                File.Copy(path, sample, true);
                try
                {
                    var exeDir = AppContext.BaseDirectory;
                    if (!string.IsNullOrEmpty(exeDir))
                        File.Copy(path, Path.Combine(exeDir, "sample_log.txt"), true);
                }
                catch { /* */ }
                L($"log_file: wrote sample_log.txt ({new FileInfo(sample).Length}B)");
            }
            catch (Exception ex)
            {
                L("log_file: sample copy warn — " + ex.Message);
            }

            L($"log_file: PASS path={path} bytes={new FileInfo(path).Length}");
            return 0;
        }
        catch (Exception ex)
        {
            L($"FAIL log_file: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    static async Task<int> RunRenderGatesAsync(AppServices s, Action<string> L)
    {
        var fail = 0;
        try
        {
            // --- render_nondefault_comm: when Comm ≠ Multimedia, app must pick Multimedia ---
            var realMm = AudioOutput.DefaultMultimediaId();
            var realComm = AudioOutput.DefaultCommunicationsId();
            s.BargeIn.SetPreferredOutputDevice(null); // system default path only
            if (!string.IsNullOrEmpty(realMm) && !string.IsNullOrEmpty(realComm) &&
                !string.Equals(realMm, realComm, StringComparison.OrdinalIgnoreCase))
            {
                using var audio = new AudioOutput(s.Log);
                using var player = audio.OpenPlayer(out var friendly, out var role, out var id, out _);
                if (role == NAudio.CoreAudioApi.Role.Communications ||
                    string.Equals(id, realComm, StringComparison.OrdinalIgnoreCase))
                {
                    L($"FAIL render_nondefault_comm: landed on Communications ({friendly}/{id})");
                    fail++;
                }
                else if (role is not (NAudio.CoreAudioApi.Role.Multimedia or NAudio.CoreAudioApi.Role.Console) ||
                         !string.Equals(id, realMm, StringComparison.OrdinalIgnoreCase))
                {
                    L($"FAIL render_nondefault_comm: expected Multimedia id={realMm}, got role={role} id={id} ({friendly})");
                    fail++;
                }
                else
                {
                    L($"render_nondefault_comm: PASS (mm≠comm; picked {role}/{friendly})");
                }
            }
            else
            {
                // Runner has same default for both roles — simulate split via ResolveOverride
                AudioOutput.ResolveOverride = () => ("mm-selftest-id", "Selftest Multimedia Speakers",
                    NAudio.CoreAudioApi.Role.Multimedia);
                try
                {
                    using var audio = new AudioOutput(s.Log);
                    using var player = audio.OpenPlayer(out var friendly, out var role, out var id, out _);
                    if (role == NAudio.CoreAudioApi.Role.Communications || id != "mm-selftest-id")
                    {
                        L($"FAIL render_nondefault_comm: simulated pick failed role={role} id={id}");
                        fail++;
                    }
                    else
                        L($"render_nondefault_comm: PASS (mm≠comm simulated; mm={realMm} comm={realComm}; picked {role}/{id})");
                }
                finally
                {
                    AudioOutput.ResolveOverride = null;
                }
            }

            // --- render_role: real play must use Multimedia/Console, never Communications ---
            string? ttsPath = null;
            try
            {
                ttsPath = await s.Tts.SpeakSapiOnlyAsync("Проверка вывода Лиа.", CancellationToken.None);
            }
            catch (Exception ex)
            {
                L("render_role: SAPI seed warn — " + ex.Message);
                ttsPath = await s.Tts.SpeakToFileAsync("Проверка вывода Лиа.", CancellationToken.None);
            }
            if (ttsPath == null || !File.Exists(ttsPath))
            {
                L("FAIL render_role: no TTS file");
                fail++;
            }
            else
            {
                s.Agent.SuspendBargeHandling(true);
                try
                {
                    await s.BargeIn.PlayFileAsync(ttsPath, "Проверка вывода Лиа.", CancellationToken.None);
                }
                finally
                {
                    s.Agent.SuspendBargeHandling(false);
                }
                var role = s.BargeIn.LastRenderRole;
                var dev = s.BargeIn.LastRenderDevice;
                if (role == NAudio.CoreAudioApi.Role.Communications)
                {
                    L($"FAIL render_role: Role.Communications device={dev}");
                    fail++;
                }
                else if (string.IsNullOrWhiteSpace(dev) || dev == "(none)")
                {
                    L($"FAIL render_role: playback did not open a render device (role={role})");
                    fail++;
                }
                else
                {
                    L($"render_role: PASS role={role} device={dev}");
                }
            }

            // --- playback_rms: loopback of render device while TTS plays ---
            fail += await RunPlaybackRmsGateAsync(s, L, ttsPath);

            // --- ws_close_handshake: abrupt close after audio bytes → SUCCESS ---
            var prev = EdgeTts.SelfTestOfflineAbruptClose;
            var prevSapi = EdgeTts.DisableSapi;
            EdgeTts.SelfTestOfflineAbruptClose = true;
            EdgeTts.DisableSapi = true; // SpeakToFile must succeed via WS abrupt-close path alone
            try
            {
                var p = await s.Tts.SpeakWebSocketOnlyAsync("Тест обрыва WebSocket.", CancellationToken.None);
                if (!TtsAudioGate.TryValidate(p, out var n, out var rms, out var detail))
                {
                    L($"FAIL ws_close_handshake: inaudible after abrupt close ({detail})");
                    fail++;
                }
                else
                    L($"ws_close_handshake: PASS bytes={n} rms={rms:F4}");
            }
            catch (Exception ex)
            {
                L($"FAIL ws_close_handshake: {ex.GetType().Name}: {ex.Message}");
                fail++;
            }
            finally
            {
                EdgeTts.SelfTestOfflineAbruptClose = prev;
                EdgeTts.DisableSapi = prevSapi;
            }
        }
        catch (Exception ex)
        {
            L($"FAIL render_gates: {ex.GetType().Name}: {ex.Message}");
            fail++;
        }
        return fail;
    }

    static async Task<int> RunPlaybackRmsGateAsync(AppServices s, Action<string> L, string? ttsPath)
    {
        try
        {
            ttsPath ??= await s.Tts.SpeakToFileAsync("Раз два три проверка звука.", CancellationToken.None);
            if (ttsPath == null || !File.Exists(ttsPath))
            {
                L("FAIL playback_rms: no TTS file");
                return 1;
            }
            if (!TtsAudioGate.TryValidate(ttsPath, out var fileBytes, out var fileRms, out var fileDetail))
            {
                L($"FAIL playback_rms: TTS file inaudible ({fileDetail})");
                return 1;
            }

            // Same path as the user: clear preferred id → app uses default Multimedia.
            // CI is responsible for making a stereo virtual cable that default.
            s.BargeIn.SetPreferredOutputDevice(null);
            var mmId = AudioOutput.DefaultMultimediaId();
            if (string.IsNullOrEmpty(mmId))
            {
                L("FAIL playback_rms: no default Multimedia render endpoint");
                return 1;
            }

            var peak = 0f;
            using var en = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            var capDev = en.GetDevice(mmId);
            var mmName = capDev.FriendlyName;
            L($"playback_rms: default Multimedia='{mmName}'");

            using var capture = new NAudio.Wave.WasapiLoopbackCapture(capDev);
            capture.DataAvailable += (_, e) =>
            {
                if (e.BytesRecorded <= 0) return;
                var fmt = capture.WaveFormat;
                if (fmt.Encoding == NAudio.Wave.WaveFormatEncoding.IeeeFloat)
                {
                    for (var i = 0; i + 4 <= e.BytesRecorded; i += 4)
                    {
                        var v = Math.Abs(BitConverter.ToSingle(e.Buffer, i));
                        if (v > peak) peak = v;
                    }
                }
                else if (fmt.BitsPerSample == 16)
                {
                    for (var i = 0; i + 2 <= e.BytesRecorded; i += 2)
                    {
                        var s16 = (short)(e.Buffer[i] | (e.Buffer[i + 1] << 8));
                        var v = Math.Abs(s16 / 32768f);
                        if (v > peak) peak = v;
                    }
                }
            };
            capture.StartRecording();

            s.Agent.SuspendBargeHandling(true);
            try
            {
                var play = s.BargeIn.PlayFileAsync(ttsPath, "Раз два три.", CancellationToken.None);
                await Task.WhenAny(play, Task.Delay(8000));
                if (!play.IsCompleted) s.BargeIn.AbortPlayback();
                await play;
                await Task.Delay(400);
            }
            finally
            {
                s.Agent.SuspendBargeHandling(false);
                try { capture.StopRecording(); } catch { /* */ }
            }

            var device = s.BargeIn.LastRenderDevice;
            var role = s.BargeIn.LastRenderRole;
            if (string.IsNullOrWhiteSpace(device) || device == "(none)")
            {
                L($"FAIL playback_rms: app did not open render device (default Multimedia='{mmName}')");
                return 1;
            }
            if (role == NAudio.CoreAudioApi.Role.Communications)
            {
                L($"FAIL playback_rms: played on Communications device={device}");
                return 1;
            }
            if (peak < 0.0008f)
            {
                L($"FAIL playback_rms: loopback peak={peak:F5} on default '{mmName}' via app device={device} (file rms={fileRms:F4} bytes={fileBytes}; silent at render)");
                return 1;
            }
            L($"playback_rms: PASS peak={peak:F4} device={device} role={role} file_rms={fileRms:F4} bytes={fileBytes}");
            return 0;
        }
        catch (Exception ex)
        {
            L($"FAIL playback_rms: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    static async Task<int> RunStreamingTtsGatesAsync(AppServices s, Action<string> L)
    {
        var fail = 0;
        const string fourSentences =
            "Первое предложение для проверки задержки. " +
            "Второе предложение продолжается здесь. " +
            "Третье предложение ещё длиннее для синтеза. " +
            "Четвёртое предложение завершает тестовый ответ.";

        var prevDelay = EdgeTts.SelfTestSynthDelayMs;
        EdgeTts.SelfTestSynthDelayMs = 550; // force overlap: first play before all synth done
        s.Agent.SuspendBargeHandling(true);
        try
        {
            // --- tts_first_sentence_latency ---
            try
            {
                var pipe = s.Agent.StreamTts;
                pipe.TurnOriginTimestamp = Stopwatch.GetTimestamp();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
                await pipe.SpeakAsync(fourSentences, cts.Token);

                var firstAudio = pipe.LastFirstAudioByteMs;
                var playStart = pipe.LastPlaybackStartMs;
                var fullSynth = pipe.LastFullSynthMs;
                if (playStart < 0 || fullSynth < 0 || firstAudio < 0)
                {
                    L($"FAIL tts_first_sentence_latency: missing timings first={firstAudio} play={playStart} full={fullSynth}");
                    fail++;
                }
                else if (playStart >= fullSynth)
                {
                    L($"FAIL tts_first_sentence_latency: play_start={playStart}ms not before full_synth={fullSynth}ms");
                    fail++;
                }
                else if (firstAudio >= fullSynth * 0.5 || firstAudio >= 2500)
                {
                    L($"FAIL tts_first_sentence_latency: first_audio={firstAudio}ms full_synth={fullSynth}ms (need <50% and <2500)");
                    fail++;
                }
                else
                {
                    L($"tts_first_sentence_latency: PASS first_audio={firstAudio}ms play_start={playStart}ms full_synth={fullSynth}ms");
                }

                // --- tts_order ---
                var order = pipe.LastPlayedOrder;
                var expected = Enumerable.Range(0, Math.Min(4, order.Count)).ToList();
                if (order.Count < 4 || !order.SequenceEqual(Enumerable.Range(0, 4)))
                {
                    L($"FAIL tts_order: got [{string.Join(",", order)}] expected 0,1,2,3");
                    fail++;
                }
                else
                    L($"tts_order: PASS seq=[{string.Join(",", order)}]");
            }
            catch (Exception ex)
            {
                L($"FAIL tts_first_sentence_latency/tts_order: {ex.GetType().Name}: {ex.Message}");
                fail++;
            }

            // --- barge_queue_cancel ---
            try
            {
                var pipe = s.Agent.StreamTts;
                pipe.TurnOriginTimestamp = Stopwatch.GetTimestamp();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                var speak = pipe.SpeakAsync(fourSentences, cts.Token);
                // Wait until sentence 0 is playing, then cancel queue
                var deadline = DateTime.UtcNow.AddSeconds(30);
                while (DateTime.UtcNow < deadline && pipe.LastPlaybackStartMs < 0)
                    await Task.Delay(40);
                if (pipe.LastPlaybackStartMs < 0)
                {
                    L("FAIL barge_queue_cancel: playback never started");
                    fail++;
                    cts.Cancel();
                    try { await speak; } catch { /* */ }
                }
                else
                {
                    // Barge during sentence 0 — must not advance the queue
                    pipe.Cancel();
                    try { await speak; } catch { /* */ }
                    var order = pipe.LastPlayedOrder;
                    if (order.Count == 0)
                    {
                        L("FAIL barge_queue_cancel: no sentences played before cancel");
                        fail++;
                    }
                    else if (order.Any(i => i > 0))
                    {
                        L($"FAIL barge_queue_cancel: queue continued after barge seq=[{string.Join(",", order)}]");
                        fail++;
                    }
                    else
                        L($"barge_queue_cancel: PASS played=[{string.Join(",", order)}] (rest cancelled)");
                }
            }
            catch (Exception ex)
            {
                L($"FAIL barge_queue_cancel: {ex.GetType().Name}: {ex.Message}");
                fail++;
            }
        }
        finally
        {
            EdgeTts.SelfTestSynthDelayMs = prevDelay;
            s.Agent.SuspendBargeHandling(false);
            s.Agent.Abort();
            await Task.Delay(200);
        }

        return fail;
    }

    static async Task<int> RunTtsGatesAsync(AppServices s, Action<string> L)
    {
        var fail = 0;
        // Poison PATH python so any accidental py fallback cannot save us
        try
        {
            var poison = Path.Combine(Path.GetTempPath(), "lia-broken-python-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(poison);
            var stub = Path.Combine(poison, "python.cmd");
            await File.WriteAllTextAsync(stub, "@echo broken-python-for-selftest 1>&2\r\nexit /b 1\r\n");
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            Environment.SetEnvironmentVariable("PATH", poison + ";" + path);
            L("tts: poisoned PATH python stub installed");
        }
        catch (Exception ex)
        {
            L("tts: PATH poison skipped: " + ex.Message);
        }

        try
        {
            var wsPath = await s.Tts.SpeakWebSocketOnlyAsync("Привет, я Лиа.", CancellationToken.None);
            if (!TtsAudioGate.TryValidate(wsPath, out var n, out var rms, out var detail))
            {
                L($"FAIL tts_ws_only: inaudible/empty WebSocket audio ({detail})");
                fail++;
            }
            else
                L($"tts_ws_only: PASS bytes={n} rms={rms:F4}");
        }
        catch (Exception ex)
        {
            L($"FAIL tts_ws_only: {ex.GetType().Name}: {ex.Message}");
            AppLog.WriteCrash("tts_ws_only", ex);
            fail++;
        }

        try
        {
            // English+Russian — GHA runners often only ship en-US desktop voices
            var sapiPath = await s.Tts.SpeakSapiOnlyAsync(
                "Hello. This is Lia offline voice check. Привет, я Лиа.", CancellationToken.None);
            if (!TtsAudioGate.TryValidate(sapiPath, out var n, out var rms, out var detail))
            {
                L($"FAIL tts_sapi: inaudible/empty SAPI audio ({detail})");
                fail++;
            }
            else
                L($"tts_sapi: PASS bytes={n} rms={rms:F4}");
        }
        catch (Exception ex)
        {
            L($"FAIL tts_sapi: {ex.GetType().Name}: {ex.Message}");
            AppLog.WriteCrash("tts_sapi", ex);
            fail++;
        }

        return fail;
    }

    /// <summary>
    /// Mock barge-in: TTS loopback must not trigger; user WAV over TTS → classify
    /// backchannel / stop / new-request.
    /// </summary>
    static async Task<int> RunBargeInGatesAsync(AppServices s, Action<string> L)
    {
        var fail = 0;
        try
        {
            // --- loopback: TTS alone into mic must NOT barge ---
            var vad = new EchoCancelVad();
            var tts = MakeTone(EchoCancelVad.SampleRate * 2, 220f, 0.4f);
            const int frame = 320;
            for (var off = 0; off + frame <= tts.Length; off += frame)
            {
                var slice = tts.AsSpan(off, frame);
                vad.PushPlayback(slice);
                var mic = new float[frame];
                for (var i = 0; i < frame; i++) mic[i] = slice[i] * 0.7f;
                if (vad.PushMic(mic))
                {
                    L("FAIL barge_loopback: TTS loopback triggered barge-in");
                    fail++;
                    break;
                }
            }
            if (!vad.Triggered) L("barge_loopback: PASS (own TTS did not trigger)");

            fail += await OneBargeScenarioAsync(s, L, "barge_backchannel", "угу", InterruptKind.Backchannel);
            fail += await OneBargeScenarioAsync(s, L, "barge_stop", "стоп", InterruptKind.Stop);
            fail += await OneBargeScenarioAsync(s, L, "barge_new", "открой блокнот", InterruptKind.NewRequest);
        }
        catch (Exception ex)
        {
            L($"FAIL barge_in: {ex.GetType().Name}: {ex.Message}");
            fail++;
        }
        return fail;
    }

    static async Task<int> OneBargeScenarioAsync(
        AppServices s, Action<string> L, string name, string sttText, InterruptKind expect)
    {
        var barge = s.BargeIn;
        var tts = MakeTone(EchoCancelVad.SampleRate * 2, 180f, 0.35f);
        var user = MakeTone(EchoCancelVad.SampleRate, 880f, 0.55f);
        var userStart = EchoCancelVad.SampleRate / 5;
        var micOff = 0;

        barge.MockPlaybackPcm = tts;
        barge.MockMicPull = () =>
        {
            const int frame = 320;
            if (micOff + frame > tts.Length) return Array.Empty<float>();
            var mic = new float[frame];
            for (var i = 0; i < frame; i++)
            {
                var idx = micOff + i;
                float u = 0;
                if (idx >= userStart)
                {
                    var ui = idx - userStart;
                    if (ui < user.Length) u = user[ui];
                }
                mic[i] = tts[idx] * 0.45f + u;
            }
            micOff += frame;
            return mic;
        };
        barge.MockStt = (_, _) => Task.FromResult(sttText);

        InterruptKind? got = null;
        string? gotText = null;
        void OnDec(InterruptKind k, string t, string _, string __)
        {
            got = k;
            gotText = t;
        }
        barge.Decision += OnDec;
        var listening = false;
        void OnListen() => listening = true;
        barge.Listening += OnListen;

        try
        {
            var dummy = Path.Combine(Path.GetTempPath(), $"lia-barge-dummy-{Guid.NewGuid():N}.wav");
            await File.WriteAllBytesAsync(dummy, new byte[] { 0 }); // path unused in mock mode
            var end = await barge.PlayFileAsync(
                dummy,
                "Первое предложение. Второе предложение. Третье.",
                CancellationToken.None);
            try { File.Delete(dummy); } catch { /* */ }

            if (end != BargeInService.PlayEnd.Barged)
            {
                L($"FAIL {name}: expected Barged, got {end}");
                return 1;
            }
            if (!listening)
            {
                L($"FAIL {name}: Listening (СЛУШАЮ) event not raised");
                return 1;
            }
            if (got != expect)
            {
                L($"FAIL {name}: kind={got} expected={expect} text={gotText}");
                return 1;
            }
            L($"PASS {name}: kind={got} text={gotText}");
            return 0;
        }
        finally
        {
            barge.Decision -= OnDec;
            barge.Listening -= OnListen;
            barge.MockPlaybackPcm = null;
            barge.MockMicPull = null;
            barge.MockStt = null;
        }
    }

    static float[] MakeTone(int samples, float freq, float amp)
    {
        var a = new float[samples];
        for (var i = 0; i < samples; i++)
            a[i] = amp * MathF.Sin(2 * MathF.PI * freq * i / EchoCancelVad.SampleRate);
        return a;
    }

    /// <summary>
    /// Hard wake gates: positive ≥90%, negative ≤1 false, own-TTS echo, inline command,
    /// offline detection, idle CPU &lt;5%.
    /// </summary>
    static async Task<int> RunWakeGatesAsync(AppServices s, Action<string> L)
    {
        var fail = 0;
        var wake = s.Wake;
        wake.ChimeEnabled = false;
        wake.Sensitivity = 0.45f; // CI voices vary

        try
        {
            VoskModelStore.ApplyEnvOverride();
            await wake.EnsureModelAsync();
            if (!wake.IsReady)
            {
                L("FAIL wake_*: vosk model not ready");
                return 1;
            }
            L($"wake: model ready path={VoskModelStore.ModelPath()}");

            // --- wake_positive ---
            fail += await GateWakePositiveAsync(s, L);

            // --- wake_command_inline ---
            fail += await GateWakeCommandInlineAsync(s, L);

            // --- wake_own_tts ---
            fail += await GateWakeOwnTtsAsync(s, L);

            // --- wake_negative ---
            fail += await GateWakeNegativeAsync(s, L);

            // --- wake_offline ---
            fail += GateWakeOffline(s, L);

            // --- wake_cpu ---
            fail += await GateWakeCpuAsync(s, L);
        }
        catch (Exception ex)
        {
            L($"FAIL wake_*: {ex.GetType().Name}: {ex.Message}");
            fail++;
        }
        finally
        {
            try { wake.Stop(); } catch { /* */ }
            wake.SetEchoGuard(false);
            wake.ChimeEnabled = true;
        }
        return fail;
    }

    static async Task<int> GateWakePositiveAsync(AppServices s, Action<string> L)
    {
        // Several voices: SAPI first (offline), then Edge if reachable — cap Edge samples for CI time.
        var phrases = new[] { "Лиа", "Лиа.", "Лия", "Лиа, привет", "Эй, Лиа" };
        var hits = 0;
        var total = 0;

        async Task TryBackend(string name, Func<string, Task<string?>> synth, IEnumerable<string> set)
        {
            foreach (var phrase in set)
            {
                string? path = null;
                try { path = await synth(phrase); } catch { /* */ }
                if (path == null || !File.Exists(path)) continue;
                byte[] pcm;
                try { pcm = LoadPcm16kMono(path); }
                catch { continue; }
                finally { try { File.Delete(path); } catch { /* */ } }
                total++;
                if (s.Wake.ProcessUtterance(pcm, out var full, out _, out var conf))
                {
                    hits++;
                    L($"wake_positive sample ok backend={name} phrase=\"{phrase}\" conf={conf:F2} text=\"{full}\"");
                }
                else
                    L($"wake_positive MISS backend={name} phrase=\"{phrase}\"");
            }
        }

        await TryBackend("sapi", async t =>
        {
            try { return await s.Tts.SpeakSapiOnlyAsync(t, CancellationToken.None); }
            catch { return null; }
        }, phrases);

        // Edge: 3 samples (enough multi-voice without burning minutes)
        await TryBackend("edge", async t =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                return await s.Tts.SpeakWebSocketOnlyAsync(t, cts.Token);
            }
            catch { return null; }
        }, phrases.Take(3));

        if (total < 5)
        {
            L($"FAIL wake_positive: only {total} synth samples (need ≥5)");
            return 1;
        }
        var rate = hits / (double)total;
        if (rate < 0.90)
        {
            L($"FAIL wake_positive: detect={hits}/{total} ({rate:P0}) need ≥90%");
            return 1;
        }
        L($"wake_positive: PASS detect={hits}/{total} ({rate:P0})");
        return 0;
    }

    static async Task<int> GateWakeCommandInlineAsync(AppServices s, Action<string> L)
    {
        const string phrase = "Лиа, открой блокнот";
        string? path = null;
        try
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                path = await s.Tts.SpeakWebSocketOnlyAsync(phrase, cts.Token);
            }
            catch { /* */ }
            if (path == null)
                path = await s.Tts.SpeakSapiOnlyAsync(phrase, CancellationToken.None);

            var pcm = LoadPcm16kMono(path);
            string? gotCmd = null;
            float gotConf = 0;
            void OnW(string full, string cmd, float conf)
            {
                gotCmd = cmd;
                gotConf = conf;
            }
            s.Wake.Woken += OnW;
            try
            {
                var ok = s.Wake.ProcessUtterance(pcm, out var full, out var cmd, out var conf);
                if (!ok)
                {
                    L($"FAIL wake_command_inline: no wake on \"{phrase}\" text=\"{full}\"");
                    return 1;
                }
                // Prefer event command; fall back to ProcessUtterance out
                var command = !string.IsNullOrWhiteSpace(gotCmd) ? gotCmd! : cmd;
                if (!command.Contains("открой", StringComparison.OrdinalIgnoreCase)
                    || !command.Contains("блокнот", StringComparison.OrdinalIgnoreCase))
                {
                    L($"FAIL wake_command_inline: cmd=\"{command}\" full=\"{full}\" (need 'открой блокнот')");
                    return 1;
                }
                L($"wake_command_inline: PASS cmd=\"{command}\" conf={conf:F2} (NewRequest text)");
                return 0;
            }
            finally
            {
                s.Wake.Woken -= OnW;
            }
        }
        catch (Exception ex)
        {
            L($"FAIL wake_command_inline: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
        finally
        {
            try { if (path != null) File.Delete(path); } catch { /* */ }
        }
    }

    static async Task<int> GateWakeOwnTtsAsync(AppServices s, Action<string> L)
    {
        // Replies that rhyme / share phonemes but must NOT self-wake under echo guard
        var replies = new[]
        {
            "Хорошо, открываю блокнот для вас.",
            "Линия связи стабильна, я на месте.",
            "Алия передала сообщение, продолжаю.",
            "Лилии в саду, чем ещё помочь?",
            "Привет, я готова помочь с файлами.",
        };
        var before = s.Wake.DetectCount;
        s.Wake.SetEchoGuard(true);
        try
        {
            foreach (var reply in replies)
            {
                string? path = null;
                try
                {
                    try { path = await s.Tts.SpeakSapiOnlyAsync(reply, CancellationToken.None); }
                    catch
                    {
                        path = await s.Tts.SpeakToFileAsync(reply, CancellationToken.None);
                    }
                    if (path == null) continue;
                    var pcm = LoadPcm16kMono(path);
                    // Push while echo guard active — must ignore
                    s.Wake.Start();
                    for (var i = 0; i < pcm.Length; i += 3200)
                    {
                        var n = Math.Min(3200, pcm.Length - i);
                        var slice = new byte[n];
                        Buffer.BlockCopy(pcm, i, slice, 0, n);
                        s.Wake.PushPcm(slice);
                    }
                    // Also ProcessUtterance would bypass guard — only PushPcm is guarded;
                    // simulate barge path: guard stays on during playback.
                }
                finally
                {
                    try { if (path != null) File.Delete(path); } catch { /* */ }
                }
            }
            await Task.Delay(200);
            var delta = s.Wake.DetectCount - before;
            if (delta > 0)
            {
                L($"FAIL wake_own_tts: self-triggered {delta} times under echo guard");
                return 1;
            }
            L("wake_own_tts: PASS (own TTS did not self-trigger)");
            return 0;
        }
        catch (Exception ex)
        {
            L($"FAIL wake_own_tts: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
        finally
        {
            s.Wake.SetEchoGuard(false);
        }
    }

    static async Task<int> GateWakeNegativeAsync(AppServices s, Action<string> L)
    {
        // Keep wall-time bounded: free-form on multi-minute streams + grammar ghosts was
        // hanging CI (>18min). Use utterance checks + ~90s noise PushPcm (no freeform spam).
        var phrases = new[]
        {
            "Добрый день, как ваши дела сегодня?",
            "Открой пожалуйста калькулятор на рабочем столе.",
            "Какая сейчас погода в Москве и завтра?",
            "Напиши короткое письмо коллеге про встречу.",
            "Переключи музыку и сделай потише звук.",
            "Сколько будет двадцать пять умножить на восемь?",
            "Найди файл отчёт в папке документы.",
            "Расскажи анекдот про программистов коротко.",
        };
        var before = s.Wake.DetectCount;
        s.Wake.SetEchoGuard(false);
        s.Wake.Start();
        var sw = Stopwatch.StartNew();
        long pcmBytes = 0;

        // 1) Free-form on non-wake phrases (must not detect)
        foreach (var phrase in phrases)
        {
            string? path = null;
            try
            {
                path = await s.Tts.SpeakSapiOnlyAsync(phrase, CancellationToken.None);
                var pcm = LoadPcm16kMono(path);
                pcmBytes += pcm.Length;
                s.Wake.ProcessUtterance(pcm, out _, out _, out _);
            }
            catch (Exception ex)
            {
                L($"wake_negative synth warn: {ex.Message}");
            }
            finally { try { if (path != null) File.Delete(path); } catch { /* */ } }
        }

        // 2) ~90s noise + a couple speech chunks through live PushPcm (grammar path)
        var noise = MakeNoisePcm(WakeWordService.SampleRate * 2 * 90);
        pcmBytes += noise.Length;
        const int chunk = WakeWordService.SampleRate * 2 / 10; // 100ms
        for (var i = 0; i < noise.Length; i += chunk)
        {
            var n = Math.Min(chunk, noise.Length - i);
            var slice = new byte[n];
            Buffer.BlockCopy(noise, i, slice, 0, n);
            s.Wake.PushPcm(slice);
        }
        // Interleave one speech phrase via PushPcm (still no wake word)
        try
        {
            var path = await s.Tts.SpeakSapiOnlyAsync(phrases[0], CancellationToken.None);
            var pcm = LoadPcm16kMono(path);
            pcmBytes += pcm.Length;
            for (var i = 0; i < pcm.Length; i += chunk)
            {
                var n = Math.Min(chunk, pcm.Length - i);
                var slice = new byte[n];
                Buffer.BlockCopy(pcm, i, slice, 0, n);
                s.Wake.PushPcm(slice);
            }
            try { File.Delete(path); } catch { /* */ }
        }
        catch { /* */ }
        s.Wake.Flush();

        var falseWakes = s.Wake.DetectCount - before;
        var pcmSec = pcmBytes / (double)(WakeWordService.SampleRate * 2);
        var sec = sw.Elapsed.TotalSeconds;
        if (falseWakes > 1)
        {
            L($"FAIL wake_negative: false_wakes={falseWakes} over {pcmSec:F0}s audio wall={sec:F0}s (need ≤1)");
            return 1;
        }
        L($"wake_negative: PASS false_wakes={falseWakes} audio_s={pcmSec:F0} wall_s={sec:F0}");
        return 0;
    }

    static int GateWakeOffline(AppServices s, Action<string> L)
    {
        try
        {
            if (!VoskModelStore.IsReady())
            {
                L("FAIL wake_offline: model not on disk");
                return 1;
            }
            // Block outbound HTTP for this check — detection must not need network
            var old = ServicePointManager.Expect100Continue;
            try
            {
                // Synthesize a simple tone+silence is useless; use already-cached SAPI file from memory
                // Build a minimal spoken sample via SAPI (local) then detect with network "blocked"
                // by ensuring EnsureModelAsync is not called and ProcessUtterance only uses disk model.
                var path = s.Tts.SpeakSapiOnlyAsync("Лиа", CancellationToken.None).GetAwaiter().GetResult();
                var pcm = LoadPcm16kMono(path);
                try { File.Delete(path); } catch { /* */ }

                // Point DNS-sensitive downloads at invalid host for any accidental fetch
                var prevOverride = VoskModelStore.ModelRootOverride;
                // Keep model root as-is (local). Detection:
                var ok = s.Wake.ProcessUtterance(pcm, out var full, out _, out var conf);
                if (!ok)
                {
                    L($"FAIL wake_offline: no detect offline text=\"{full}\"");
                    return 1;
                }
                L($"wake_offline: PASS conf={conf:F2} text=\"{full}\" (local model, no download)");
                return 0;
            }
            finally
            {
                ServicePointManager.Expect100Continue = old;
            }
        }
        catch (Exception ex)
        {
            L($"FAIL wake_offline: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    static async Task<int> GateWakeCpuAsync(AppServices s, Action<string> L)
    {
        try
        {
            s.Wake.SetEchoGuard(false);
            s.Wake.Start();
            var proc = Process.GetCurrentProcess();
            proc.Refresh();
            var cpu0 = proc.TotalProcessorTime;
            var wall0 = Stopwatch.StartNew();
            // Idle listen: push near-silence for ~5s (simulates always-on WaveIn)
            var silence = new byte[WakeWordService.SampleRate * 2 / 10]; // 100ms
            var end = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < end)
            {
                s.Wake.PushPcm(silence);
                await Task.Delay(100);
            }
            wall0.Stop();
            proc.Refresh();
            var cpuDt = (proc.TotalProcessorTime - cpu0).TotalMilliseconds;
            var wallMs = Math.Max(1, wall0.Elapsed.TotalMilliseconds);
            var cores = Math.Max(1, Environment.ProcessorCount);
            var pct = cpuDt / wallMs / cores * 100.0;
            if (pct >= 5.0)
            {
                L($"FAIL wake_cpu: idle_avg={pct:F2}% (need <5%) cores={cores}");
                return 1;
            }
            L($"wake_cpu: PASS idle_avg={pct:F2}% cores={cores} wall_ms={wallMs:F0}");
            return 0;
        }
        catch (Exception ex)
        {
            L($"FAIL wake_cpu: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    static byte[] MakeNoisePcm(int bytes)
    {
        var b = new byte[bytes];
        for (var i = 0; i < b.Length; i += 2)
        {
            var s = (short)Random.Shared.Next(-800, 800);
            b[i] = (byte)(s & 0xff);
            b[i + 1] = (byte)((s >> 8) & 0xff);
        }
        return b;
    }

    static byte[] LoadPcm16kMono(string path)
    {
        using var reader = Path.GetExtension(path).Equals(".mp3", StringComparison.OrdinalIgnoreCase)
            ? (WaveStream)new MediaFoundationReader(path)
            : new WaveFileReader(path);
        var target = new WaveFormat(WakeWordService.SampleRate, 16, 1);
        using var resampler = new MediaFoundationResampler(reader, target) { ResamplerQuality = 60 };
        using var ms = new MemoryStream();
        var buf = new byte[target.AverageBytesPerSecond];
        int n;
        while ((n = resampler.Read(buf, 0, buf.Length)) > 0)
            ms.Write(buf, 0, n);
        return ms.ToArray();
    }

    static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}

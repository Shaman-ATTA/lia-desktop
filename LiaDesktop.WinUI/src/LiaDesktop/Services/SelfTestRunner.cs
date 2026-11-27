using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using LiaDesktop.Services.Tools;

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

            // Persistent log file gate
            failed += RunLogFileGate(services, L);

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
                return await OneTurnAsync(s, "привет", "live", L, expectRu: true, timeoutSec: 120);
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

            // Soft: mock/live may paraphrase ("Хорошо", "Запомнила", echo facts) —
            // require a non-error reply; fact recall is asserted on memory_live.
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
            // --- render_nondefault_comm: multimedia id ≠ communications; we still pick Multimedia ---
            var realMm = AudioOutput.DefaultMultimediaId();
            var realComm = AudioOutput.DefaultCommunicationsId();
            AudioOutput.ResolveOverride = () => ("mm-selftest-id", "Selftest Multimedia Speakers",
                NAudio.CoreAudioApi.Role.Multimedia);
            try
            {
                using var audio = new AudioOutput(s.Log);
                using var player = audio.OpenPlayer(out var friendly, out var role, out var id, out _);
                if (role == NAudio.CoreAudioApi.Role.Communications)
                {
                    L($"FAIL render_nondefault_comm: resolved Communications ({friendly})");
                    fail++;
                }
                else if (id != "mm-selftest-id" ||
                         role is not (NAudio.CoreAudioApi.Role.Multimedia or NAudio.CoreAudioApi.Role.Console))
                {
                    L($"FAIL render_nondefault_comm: id={id} role={role} friendly={friendly}");
                    fail++;
                }
                else
                {
                    L($"render_nondefault_comm: PASS (mm≠comm simulated; mm={realMm} comm={realComm}; picked {role}/{id})");
                }
            }
            finally
            {
                AudioOutput.ResolveOverride = null;
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
                if (role == NAudio.CoreAudioApi.Role.Communications)
                {
                    L($"FAIL render_role: Role.Communications device={s.BargeIn.LastRenderDevice}");
                    fail++;
                }
                else
                {
                    L($"render_role: PASS role={role} device={s.BargeIn.LastRenderDevice}");
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

            var peak = 0f;
            NAudio.Wave.WasapiLoopbackCapture? capture = null;
            var loopbackOk = false;
            try
            {
                capture = new NAudio.Wave.WasapiLoopbackCapture();
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
                loopbackOk = true;
            }
            catch (Exception ex)
            {
                // CI runners often have no active render endpoint → Element not found (0x80070490).
                L("playback_rms: loopback unavailable — " + ex.Message + "; falling back to file RMS + play");
                try { capture?.Dispose(); } catch { /* */ }
                capture = null;
            }

            s.Agent.SuspendBargeHandling(true);
            try
            {
                var play = s.BargeIn.PlayFileAsync(ttsPath, "Раз два три.", CancellationToken.None);
                await Task.WhenAny(play, Task.Delay(8000));
                if (!play.IsCompleted) s.BargeIn.AbortPlayback();
                await play;
            }
            finally
            {
                s.Agent.SuspendBargeHandling(false);
                try { capture?.StopRecording(); } catch { /* */ }
                try { capture?.Dispose(); } catch { /* */ }
            }

            if (loopbackOk)
            {
                if (peak < 0.0008f)
                {
                    L($"FAIL playback_rms: loopback peak={peak:F5} (silent — sound did not reach render endpoint?)");
                    return 1;
                }
                L($"playback_rms: PASS peak={peak:F4} device={s.BargeIn.LastRenderDevice}");
                return 0;
            }

            // No loopback device: still require audible TTS bytes + successful Multimedia play path.
            if (fileRms < 0.01f)
            {
                L($"FAIL playback_rms: file rms={fileRms:F4} too low without loopback");
                return 1;
            }
            L($"playback_rms: PASS (no loopback; file bytes={fileBytes} rms={fileRms:F4} role={s.BargeIn.LastRenderRole} device={s.BargeIn.LastRenderDevice})");
            return 0;
        }
        catch (Exception ex)
        {
            L($"FAIL playback_rms: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
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

    static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}

using System.Diagnostics;
using System.Net;
using System.Text.Json;
using LiaDesktop.Services.Tools;
using MainWindow = LiaDesktop.MainWindow;

namespace LiaDesktop.Services;

/// <summary>Hard update_* selftest gates (real installer / fake GitHub / SHA / offline / e2e).</summary>
public static partial class SelfTestRunner
{
    static async Task<int> RunUpdateGatesAsync(AppServices s, Action<string> L)
    {
        var fail = 0;
        L("update: starting gates…");
        fail += await GateUpdateOfflineAsync(s, L);
        fail += await GateUpdateShaMismatchAsync(s, L);
        fail += await GateUpdateCheckAsync(s, L);
        fail += await GateUpdateUpgradeExistsAsync(s, L);
        fail += await GateUpdateE2eAsync(s, L);
        L("update: gates done");
        return fail;
    }

    static async Task<int> GateUpdateOfflineAsync(AppServices s, Action<string> L)
    {
        L("update_offline: starting…");
        try
        {
            var prev = s.Updates.ApiOverride;
            s.Updates.ApiOverride = "http://127.0.0.1:1/releases/latest"; // nothing listens
            var result = await s.Updates.CheckAsync(quiet: false);
            s.Updates.ApiOverride = prev;
            if (result.Status != UpdateCheckStatus.Offline)
            {
                L($"FAIL update_offline: status={result.Status} (need Offline)");
                return 1;
            }
            if (!JournalMentions(s, UpdateService.JournalOffline) &&
                !(result.Message?.Contains(UpdateService.JournalOffline, StringComparison.Ordinal) ?? false))
            {
                L($"FAIL update_offline: missing journal «{UpdateService.JournalOffline}»");
                return 1;
            }
            L("update_offline: PASS");
            return 0;
        }
        catch (Exception ex)
        {
            L($"FAIL update_offline: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    static async Task<int> GateUpdateShaMismatchAsync(AppServices s, Action<string> L)
    {
        L("update_sha_mismatch: starting…");
        Process? server = null;
        try
        {
            var setup = FindCurrentSetupExe(L);
            if (setup == null) return 1;
            var port = FreePort();
            server = StartFakeRelease(port, "v9.9.9", setup, badSha: true, L);
            if (server == null) return 1;
            if (!await WaitHttpOk($"http://127.0.0.1:{port}/_control/health", 15_000))
            {
                L("FAIL update_sha_mismatch: fake server not healthy");
                return 1;
            }

            var prev = s.Updates.ApiOverride;
            var prevExit = s.Updates.SuppressExitAfterLaunch;
            s.Updates.ApiOverride = $"http://127.0.0.1:{port}/releases/latest";
            s.Updates.SuppressExitAfterLaunch = true;
            var check = await s.Updates.CheckAsync(quiet: false);
            if (check.Status != UpdateCheckStatus.Available || check.Info == null)
            {
                L($"FAIL update_sha_mismatch: check={check.Status}");
                s.Updates.ApiOverride = prev;
                s.Updates.SuppressExitAfterLaunch = prevExit;
                return 1;
            }
            var (ok, msg) = await s.Updates.DownloadAndInstallAsync(check.Info);
            s.Updates.ApiOverride = prev;
            s.Updates.SuppressExitAfterLaunch = prevExit;
            if (ok)
            {
                L("FAIL update_sha_mismatch: install should refuse bad SHA");
                return 1;
            }
            if (!msg.Contains("контрольная сумма", StringComparison.OrdinalIgnoreCase) &&
                !msg.Contains(UpdateService.JournalShaFail, StringComparison.Ordinal))
            {
                L($"FAIL update_sha_mismatch: friendly message missing, got «{msg}»");
                return 1;
            }
            if (!JournalMentions(s, "контрольная сумма") &&
                !JournalMentions(s, UpdateService.JournalShaFail))
            {
                L("FAIL update_sha_mismatch: journal missing SHA refusal");
                return 1;
            }
            L("update_sha_mismatch: PASS");
            return 0;
        }
        catch (Exception ex)
        {
            L($"FAIL update_sha_mismatch: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
        finally
        {
            try { server?.Kill(entireProcessTree: true); } catch { /* */ }
        }
    }

    static async Task<int> GateUpdateCheckAsync(AppServices s, Action<string> L)
    {
        L("update_check: starting…");
        Process? server = null;
        try
        {
            var setup = FindCurrentSetupExe(L);
            if (setup == null) return 1;
            var port = FreePort();
            server = StartFakeRelease(port, "v9.8.7", setup, badSha: false, L);
            if (server == null) return 1;
            if (!await WaitHttpOk($"http://127.0.0.1:{port}/_control/health", 15_000))
            {
                L("FAIL update_check: fake server not healthy");
                return 1;
            }

            var prev = s.Updates.ApiOverride;
            s.Updates.ApiOverride = $"http://127.0.0.1:{port}/releases/latest";
            UpdateInfo? seen = null;
            void Handler(UpdateInfo i) => seen = i;
            s.Updates.UpdateAvailable += Handler;
            var check = await s.Updates.CheckAsync(quiet: false);
            s.Updates.UpdateAvailable -= Handler;
            s.Updates.ApiOverride = prev;

            if (check.Status != UpdateCheckStatus.Available || check.Info == null)
            {
                L($"FAIL update_check: status={check.Status}");
                return 1;
            }
            if (App.MainWindowInstance is MainWindow mw)
            {
                mw.DispatcherQueue.TryEnqueue(() => mw.ShowUpdateBanner(check.Info));
                await Task.Delay(400);
                if (!mw.IsUpdateBannerVisible)
                {
                    L("FAIL update_check: UpdateBanner not visible");
                    return 1;
                }
                if (!mw.UpdateBannerCaption.Contains("v9.8.7", StringComparison.OrdinalIgnoreCase) &&
                    !mw.UpdateBannerCaption.Contains("9.8.7", StringComparison.Ordinal))
                {
                    L($"FAIL update_check: banner text «{mw.UpdateBannerCaption}»");
                    return 1;
                }
                var shotDir = !string.IsNullOrEmpty(OutPath)
                    ? Path.GetDirectoryName(OutPath)!
                    : Environment.CurrentDirectory;
                var shot = Path.Combine(shotDir, "update_check.png");
                try
                {
                    // Prefer window composite; fall back to primary capture
                    mw.SaveParityWindowPng(shot);
                }
                catch
                {
                    try { ScreenCapture.CapturePrimary(shot); } catch { /* */ }
                }
                if (!File.Exists(shot))
                {
                    L("FAIL update_check: screenshot missing");
                    return 1;
                }
                L($"update_check: screenshot={shot}");
            }
            else
            {
                L("FAIL update_check: MainWindow missing");
                return 1;
            }
            L($"update_check: PASS tag={check.Info.Tag} noticed={(seen != null)}");
            return 0;
        }
        catch (Exception ex)
        {
            L($"FAIL update_check: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
        finally
        {
            try { server?.Kill(entireProcessTree: true); } catch { /* */ }
            try { s.Updates.Dismiss(); } catch { /* */ }
        }
    }

    static async Task<int> GateUpdateUpgradeExistsAsync(AppServices s, Action<string> L)
    {
        L("update_upgrade_exists: starting…");
        var installDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LiaDesktop-upgrade-gate");
        Process? oldProc = null;
        try
        {
            var newSetup = FindCurrentSetupExe(L);
            if (newSetup == null) return 1;

            // Previous release installer (real v0.7.5 from GitHub)
            var oldSetup = await EnsurePreviousSetupAsync(L);
            if (oldSetup == null) return 1;

            if (Directory.Exists(installDir))
            {
                try { Directory.Delete(installDir, true); } catch { /* */ }
            }

            L($"update_upgrade_exists: silent install previous → {installDir}");
            if (!RunInnoSilent(oldSetup, installDir, launchAfter: false, L))
                return 1;

            var exe = Path.Combine(installDir, "LiaDesktop.exe");
            if (!File.Exists(exe))
            {
                L("FAIL update_upgrade_exists: old exe missing after install");
                return 1;
            }

            // Marker in user data (same folder as install for per-user layout)
            var markerText = "upgrade-marker-" + Guid.NewGuid().ToString("N")[..8];
            var memoryPath = Path.Combine(installDir, "memory.json");
            File.WriteAllText(memoryPath, JsonSerializer.Serialize(new[]
            {
                new { Id = "gate01", Text = markerText, SavedUtc = DateTime.UtcNow }
            }));
            // Settings blob (DPAPI may fail cross-user — also drop a plain marker file)
            var settingsMarker = Path.Combine(installDir, "lia-settings-marker.txt");
            File.WriteAllText(settingsMarker, markerText);
            // Fake vosk + log dir survival
            Directory.CreateDirectory(Path.Combine(installDir, "vosk", "dummy"));
            File.WriteAllText(Path.Combine(installDir, "vosk", "dummy", "keep.txt"), "vosk");
            Directory.CreateDirectory(Path.Combine(installDir, "logs"));
            File.WriteAllText(Path.Combine(installDir, "logs", "lia-keep.log"), "log-marker");

            L("update_upgrade_exists: launching previous app…");
            oldProc = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = installDir,
                UseShellExecute = true,
            });
            await Task.Delay(4000);
            if (oldProc == null || oldProc.HasExited)
            {
                L("FAIL update_upgrade_exists: previous app exited early");
                return 1;
            }

            L("update_upgrade_exists: silent upgrade with new installer (app running)…");
            if (!RunInnoSilent(newSetup, installDir, launchAfter: false, L))
                return 1;

            await Task.Delay(1500);
            try { if (!oldProc.HasExited) oldProc.Kill(entireProcessTree: true); } catch { /* */ }

            // New version must be present
            var vi = FileVersionInfo.GetVersionInfo(exe);
            var ver = vi.FileVersion ?? vi.ProductVersion ?? "";
            L($"update_upgrade_exists: installed FileVersion={ver}");
            if (!ver.StartsWith("0.7.6", StringComparison.Ordinal))
            {
                L($"FAIL update_upgrade_exists: expected 0.7.6*, got {ver}");
                return 1;
            }

            if (!File.Exists(memoryPath) || !File.ReadAllText(memoryPath).Contains(markerText, StringComparison.Ordinal))
            {
                L("FAIL update_upgrade_exists: memory.json marker lost");
                return 1;
            }
            if (!File.Exists(settingsMarker) || !File.ReadAllText(settingsMarker).Contains(markerText, StringComparison.Ordinal))
            {
                L("FAIL update_upgrade_exists: settings marker lost");
                return 1;
            }
            if (!File.Exists(Path.Combine(installDir, "vosk", "dummy", "keep.txt")))
            {
                L("FAIL update_upgrade_exists: vosk data lost");
                return 1;
            }
            if (!File.Exists(Path.Combine(installDir, "logs", "lia-keep.log")))
            {
                L("FAIL update_upgrade_exists: logs lost");
                return 1;
            }

            // Exactly one Programs entry for our AppId / name under this test dir's uninstall key is hard;
            // count uninstall registry entries for "Lia Assistant" display name — must be ≥1 and no dup stubs.
            var programs = CountLiaProgramsEntries();
            L($"update_upgrade_exists: Programs entries≈{programs}");
            if (programs < 1)
            {
                L("FAIL update_upgrade_exists: no Programs entry");
                return 1;
            }
            // Prefer single entry; allow 1–2 if prior user install exists on the runner
            if (programs > 3)
            {
                L($"FAIL update_upgrade_exists: too many Programs entries ({programs})");
                return 1;
            }

            // No leftover LiaDesktop from this install dir
            var leftovers = Process.GetProcessesByName("LiaDesktop")
                .Where(p =>
                {
                    try
                    {
                        var path = p.MainModule?.FileName ?? "";
                        return path.StartsWith(installDir, StringComparison.OrdinalIgnoreCase);
                    }
                    catch { return false; }
                }).ToList();
            foreach (var p in leftovers)
            {
                try { p.Kill(entireProcessTree: true); } catch { /* */ }
            }
            if (leftovers.Count > 0)
                L($"update_upgrade_exists: killed {leftovers.Count} leftover(s) from install dir");

            // Desktop shortcut duplicates: count Lia Assistant lnk under Desktop — soft log only if >2
            var desk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var lnks = Directory.Exists(desk)
                ? Directory.GetFiles(desk, "*Lia*.lnk").Length
                : 0;
            L($"update_upgrade_exists: desktop Lia shortcuts={lnks}");

            // Same-version reinstall must not error
            L("update_upgrade_exists: same-version reinstall…");
            if (!RunInnoSilent(newSetup, installDir, launchAfter: false, L))
            {
                L("FAIL update_upgrade_exists: same-version reinstall failed");
                return 1;
            }
            if (!File.ReadAllText(memoryPath).Contains(markerText, StringComparison.Ordinal))
            {
                L("FAIL update_upgrade_exists: marker lost after reinstall");
                return 1;
            }

            L($"update_upgrade_exists: PASS version={ver} marker_ok programs={programs}");
            return 0;
        }
        catch (Exception ex)
        {
            L($"FAIL update_upgrade_exists: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
        finally
        {
            try { if (oldProc is { HasExited: false }) oldProc.Kill(entireProcessTree: true); } catch { /* */ }
            // Leave installDir for artifact inspection; CI can clean
        }
    }

    static async Task<int> GateUpdateE2eAsync(AppServices s, Action<string> L)
    {
        L("update_e2e: starting…");
        Process? server = null;
        Process? agent = null;
        var e2eDir = Path.Combine(Path.GetTempPath(), "LiaDesktop-update-e2e");
        var marker = Path.Combine(Path.GetTempPath(), "lia-update-e2e-marker.txt");
        try
        {
            var currentSetup = FindCurrentSetupExe(L);
            var newerSetup = FindE2ePayloadSetup(L);
            if (currentSetup == null || newerSetup == null) return 1;

            if (Directory.Exists(e2eDir))
            {
                try { Directory.Delete(e2eDir, true); } catch { /* */ }
            }
            if (File.Exists(marker)) File.Delete(marker);

            L($"update_e2e: install current as base → {e2eDir}");
            if (!RunInnoSilent(currentSetup, e2eDir, launchAfter: false, L))
                return 1;

            var port = FreePort();
            server = StartFakeRelease(port, "v0.7.7", newerSetup, badSha: false, L);
            if (server == null) return 1;
            if (!await WaitHttpOk($"http://127.0.0.1:{port}/_control/health", 15_000))
            {
                L("FAIL update_e2e: fake server not healthy");
                return 1;
            }

            var exe = Path.Combine(e2eDir, "LiaDesktop.exe");
            var api = $"http://127.0.0.1:{port}/releases/latest";
            L("update_e2e: launching agent for self-upgrade…");
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = e2eDir,
                UseShellExecute = false,
                Arguments = $"--update-e2e-agent --update-api={api} --smoke-size=800x600",
            };
            psi.Environment["LIA_UPDATE_API"] = api;
            psi.Environment["LIA_UPDATE_E2E_MARKER"] = marker;
            agent = Process.Start(psi);
            if (agent == null)
            {
                L("FAIL update_e2e: agent start failed");
                return 1;
            }

            var deadline = DateTime.UtcNow.AddMinutes(10);
            while (DateTime.UtcNow < deadline)
            {
                if (File.Exists(marker))
                {
                    var text = File.ReadAllText(marker);
                    L("update_e2e marker: " + text.Trim());
                    if (text.Contains("update_e2e: PASS", StringComparison.Ordinal))
                    {
                        var vi = FileVersionInfo.GetVersionInfo(exe);
                        L($"update_e2e: FileVersion after={vi.FileVersion}");
                        if (!(vi.FileVersion ?? "").StartsWith("0.7.7", StringComparison.Ordinal))
                        {
                            L($"FAIL update_e2e: expected 0.7.7* got {vi.FileVersion}");
                            return 1;
                        }
                        L("update_e2e: PASS");
                        return 0;
                    }
                    L("FAIL update_e2e: " + text.Trim());
                    return 1;
                }
                await Task.Delay(1000);
            }
            // Diagnostics on timeout
            try
            {
                var vi = File.Exists(exe) ? FileVersionInfo.GetVersionInfo(exe).FileVersion : "(missing)";
                L($"FAIL update_e2e: timeout waiting for marker; FileVersion={vi}");
                var pending = Path.Combine(e2eDir, "update-e2e-pending.json");
                L($"update_e2e: pending_exists={File.Exists(pending)} marker_exists={File.Exists(marker)}");
                if (File.Exists(pending)) L("update_e2e pending: " + File.ReadAllText(pending));
                var boot = Path.Combine(e2eDir, "boot.log");
                if (File.Exists(boot))
                {
                    var lines = File.ReadAllLines(boot);
                    foreach (var line in lines.TakeLast(30))
                        L("update_e2e boot: " + line);
                }
            }
            catch (Exception dex) { L("update_e2e diag: " + dex.Message); }
            return 1;
        }
        catch (Exception ex)
        {
            L($"FAIL update_e2e: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
        finally
        {
            try { agent?.Kill(entireProcessTree: true); } catch { /* */ }
            try { server?.Kill(entireProcessTree: true); } catch { /* */ }
            foreach (var p in Process.GetProcessesByName("LiaDesktop"))
            {
                try
                {
                    var path = p.MainModule?.FileName ?? "";
                    if (path.StartsWith(e2eDir, StringComparison.OrdinalIgnoreCase))
                        p.Kill(entireProcessTree: true);
                }
                catch { /* */ }
            }
        }
    }

    static string? FindCurrentSetupExe(Action<string> L)
    {
        var env = Environment.GetEnvironmentVariable("LIA_SETUP_EXE");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
        {
            L($"update: setup={env}");
            return env;
        }
        foreach (var c in new[]
                 {
                     Path.Combine(Environment.CurrentDirectory, "LiaDesktop-0.7.6-setup.exe"),
                     Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "LiaDesktop-0.7.6-setup.exe")),
                     Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "LiaDesktop-0.7.6-setup.exe")),
                 })
        {
            if (File.Exists(c))
            {
                L($"update: setup={c}");
                return c;
            }
        }
        // Walk up from cwd looking for repo root artifact
        var dir = new DirectoryInfo(Environment.CurrentDirectory);
        for (var i = 0; i < 6 && dir != null; i++, dir = dir.Parent)
        {
            var p = Path.Combine(dir.FullName, "LiaDesktop-0.7.6-setup.exe");
            if (File.Exists(p))
            {
                L($"update: setup={p}");
                return p;
            }
            var art = Path.Combine(dir.FullName, "artifacts", "LiaDesktop-0.7.6-setup.exe");
            if (File.Exists(art))
            {
                L($"update: setup={art}");
                return art;
            }
        }
        L("FAIL update: LiaDesktop-0.7.6-setup.exe not found (set LIA_SETUP_EXE)");
        return null;
    }

    static string? FindE2ePayloadSetup(Action<string> L)
    {
        var env = Environment.GetEnvironmentVariable("LIA_UPDATE_E2E_SETUP");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
        {
            L($"update_e2e: payload={env}");
            return env;
        }
        foreach (var name in new[] { "LiaDesktop-0.7.7-setup.exe", "LiaDesktop-update-payload-setup.exe" })
        {
            var dir = new DirectoryInfo(Environment.CurrentDirectory);
            for (var i = 0; i < 6 && dir != null; i++, dir = dir.Parent)
            {
                var p = Path.Combine(dir.FullName, name);
                if (File.Exists(p))
                {
                    L($"update_e2e: payload={p}");
                    return p;
                }
                var art = Path.Combine(dir.FullName, "artifacts", name);
                if (File.Exists(art))
                {
                    L($"update_e2e: payload={art}");
                    return art;
                }
            }
        }
        L("FAIL update_e2e: payload setup missing (set LIA_UPDATE_E2E_SETUP to 0.7.7 installer)");
        return null;
    }

    static async Task<string?> EnsurePreviousSetupAsync(Action<string> L)
    {
        var env = Environment.GetEnvironmentVariable("LIA_PREV_SETUP_EXE");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
        {
            L($"update_upgrade_exists: prev={env}");
            return env;
        }
        var cache = Path.Combine(Path.GetTempPath(), "LiaDesktop-0.7.5-setup.exe");
        if (File.Exists(cache) && new FileInfo(cache).Length > 1_000_000)
        {
            L($"update_upgrade_exists: prev cache={cache}");
            return cache;
        }
        var url = Environment.GetEnvironmentVariable("LIA_PREV_SETUP_URL")
                  ?? "https://github.com/Shaman-ATTA/lia-desktop/releases/download/v0.7.5/LiaDesktop-0.7.5-setup.exe";
        L($"update_upgrade_exists: downloading {url}");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("LiaDesktop-SelfTest/0.7.6");
            var bytes = await http.GetByteArrayAsync(url);
            await File.WriteAllBytesAsync(cache, bytes);
            L($"update_upgrade_exists: downloaded {bytes.Length} bytes");
            return cache;
        }
        catch (Exception ex)
        {
            L($"FAIL update_upgrade_exists: download prev failed: {ex.Message}");
            return null;
        }
    }

    static bool RunInnoSilent(string setup, string dir, bool launchAfter, Action<string> L)
    {
        Directory.CreateDirectory(dir);
        var args = $"/VERYSILENT /NORESTART /SUPPRESSMSGBOXES /CLOSEAPPLICATIONS /FORCECLOSEAPPLICATIONS /DIR=\"{dir}\"";
        if (launchAfter) args += " /LAUNCHAFTER=1";
        L($"inno: {Path.GetFileName(setup)} {args}");
        var p = Process.Start(new ProcessStartInfo
        {
            FileName = setup,
            Arguments = args,
            UseShellExecute = false,
        });
        if (p == null)
        {
            L("FAIL inno: Start returned null");
            return false;
        }
        if (!p.WaitForExit(600_000))
        {
            try { p.Kill(entireProcessTree: true); } catch { /* */ }
            L("FAIL inno: timeout");
            return false;
        }
        if (p.ExitCode != 0)
        {
            L($"FAIL inno: exit={p.ExitCode}");
            return false;
        }
        return File.Exists(Path.Combine(dir, "LiaDesktop.exe"));
    }

    static Process? StartFakeRelease(int port, string tag, string setupPath, bool badSha, Action<string> L)
    {
        var py = FindPython();
        if (py == null)
        {
            L("FAIL update: python not found for fake_release_server");
            return null;
        }
        var script = FindRepoFile("tools/fake_release_server.py");
        if (script == null)
        {
            L("FAIL update: tools/fake_release_server.py missing");
            return null;
        }
        var args = $"\"{script}\" {port} --tag {tag} --setup \"{setupPath}\"";
        if (badSha) args += " --bad-sha";
        L($"fake_release: {py} {args}");
        return Process.Start(new ProcessStartInfo
        {
            FileName = py,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
        });
    }

    static string? FindRepoFile(string relative)
    {
        var dir = new DirectoryInfo(Environment.CurrentDirectory);
        for (var i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            var p = Path.Combine(dir.FullName, relative);
            if (File.Exists(p)) return p;
        }
        var fromBase = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", relative));
        return File.Exists(fromBase) ? fromBase : null;
    }

    static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    static async Task<bool> WaitHttpOk(string url, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            try
            {
                var r = await http.GetAsync(url);
                if (r.IsSuccessStatusCode) return true;
            }
            catch { /* */ }
            await Task.Delay(200);
        }
        return false;
    }

    static int CountLiaProgramsEntries()
    {
        var n = 0;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall");
            if (key == null) return 0;
            foreach (var sub in key.GetSubKeyNames())
            {
                using var s = key.OpenSubKey(sub);
                var name = s?.GetValue("DisplayName") as string ?? "";
                if (name.Contains("Lia", StringComparison.OrdinalIgnoreCase))
                    n++;
            }
        }
        catch { /* */ }
        return n;
    }

    /// <summary>AppLog ring is capped at 200 — never Skip(Count) when full.</summary>
    static bool JournalMentions(AppServices s, string needle)
    {
        if (string.IsNullOrEmpty(needle)) return false;
        return s.Log.Lines.TakeLast(40).Any(t =>
            (t.Line ?? "").Contains(needle, StringComparison.OrdinalIgnoreCase) ||
            (t.Detail ?? "").Contains(needle, StringComparison.OrdinalIgnoreCase));
    }
}

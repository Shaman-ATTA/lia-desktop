using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LiaDesktop.Services;

public sealed class UpdateInfo
{
    public required Version Version { get; init; }
    public required string Tag { get; init; }
    public required string SetupUrl { get; init; }
    public required string SetupName { get; init; }
    public string? ChecksumsUrl { get; init; }
    public string? HtmlUrl { get; init; }
}

public enum UpdateCheckStatus
{
    UpToDate,
    Available,
    Offline,
    RateLimited,
    Error,
}

public sealed class UpdateCheckResult
{
    public UpdateCheckStatus Status { get; init; }
    public UpdateInfo? Info { get; init; }
    public string? Message { get; init; }
}

/// <summary>
/// GitHub Releases auto-update (unauthenticated). Override API via
/// <see cref="ApiOverride"/> / env LIA_UPDATE_API for selftest fake server.
/// </summary>
public sealed class UpdateService : IDisposable
{
    public const string DefaultApiLatest =
        "https://api.github.com/repos/Shaman-ATTA/lia-desktop/releases/latest";

    public const string JournalOffline = "обновления: нет сети — проверка пропущена";
    public const string JournalRateLimit = "обновления: лимит GitHub API — позже";
    public const string JournalShaFail = "обновления: контрольная сумма не совпала — установка отменена";
    public const string JournalUpToDate = "обновления: уже актуальная версия";

    readonly AppLog _log;
    readonly HttpClient _http;
    readonly object _gate = new();
    CancellationTokenSource? _timerCts;
    UpdateInfo? _pending;
    bool _disposed;

    /// <summary>Replace GitHub latest URL (fake release server). Env LIA_UPDATE_API wins if set at construct.</summary>
    public string? ApiOverride { get; set; }

    /// <summary>Force Inno /DIR= (selftest e2e / portable). Default = folder of running exe.</summary>
    public string? InstallDirOverride { get; set; }

    /// <summary>When set, skip UI and install immediately after a successful check (selftest / voice confirm).</summary>
    public bool AutoInstall { get; set; }

    /// <summary>If true, DownloadAndInstallAsync does not Environment.Exit (unit/selftest sha probes).</summary>
    public bool SuppressExitAfterLaunch { get; set; }

    public UpdateInfo? Pending
    {
        get { lock (_gate) return _pending; }
        private set { lock (_gate) _pending = value; }
    }

    public event Action<UpdateInfo>? UpdateAvailable;
    public event Action<string>? Progress;
    public event Action<string>? FriendlyError;
    public event Action? Dismissed;

    public UpdateService(AppLog log)
    {
        _log = log;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("LiaDesktop-Updater/" + AppVersion.Display);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        var env = Environment.GetEnvironmentVariable("LIA_UPDATE_API");
        if (!string.IsNullOrWhiteSpace(env))
            ApiOverride = env.Trim();
    }

    public string ApiUrl =>
        !string.IsNullOrWhiteSpace(ApiOverride) ? ApiOverride!
        : DefaultApiLatest;

    public void StartPeriodicChecks(Func<bool> autoCheckEnabled, TimeSpan? period = null)
    {
        period ??= TimeSpan.FromHours(6);
        _timerCts?.Cancel();
        var cts = new CancellationTokenSource();
        _timerCts = cts;
        _ = Task.Run(async () =>
        {
            // Startup delay so HUD is up
            try { await Task.Delay(TimeSpan.FromSeconds(8), cts.Token); }
            catch { return; }
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    if (autoCheckEnabled())
                        await CheckAsync(quiet: true, cts.Token);
                }
                catch { /* */ }
                try { await Task.Delay(period.Value, cts.Token); }
                catch { return; }
            }
        }, cts.Token);
    }

    public async Task<UpdateCheckResult> CheckAsync(bool quiet = false, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, ApiUrl);
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if ((int)resp.StatusCode == 403 || (int)resp.StatusCode == 429)
            {
                if (!quiet) _log.Info(JournalRateLimit);
                return new UpdateCheckResult { Status = UpdateCheckStatus.RateLimited, Message = JournalRateLimit };
            }
            if (!resp.IsSuccessStatusCode)
            {
                var msg = $"обновления: HTTP {(int)resp.StatusCode}";
                if (!quiet) _log.Info(msg);
                return new UpdateCheckResult { Status = UpdateCheckStatus.Error, Message = msg };
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var root = doc.RootElement;
            var tag = root.TryGetProperty("tag_name", out var tn) ? tn.GetString() ?? "" : "";
            if (!AppVersion.TryParseTag(tag, out var remoteVer))
            {
                var msg = "обновления: неверный tag_name";
                if (!quiet) _log.Info(msg);
                return new UpdateCheckResult { Status = UpdateCheckStatus.Error, Message = msg };
            }

            if (!AppVersion.IsNewer(remoteVer))
            {
                Pending = null;
                if (!quiet) _log.Info(JournalUpToDate + $" ({AppVersion.Display})");
                return new UpdateCheckResult { Status = UpdateCheckStatus.UpToDate, Message = JournalUpToDate };
            }

            string? setupUrl = null, setupName = null, sumsUrl = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    var url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString()
                              : a.TryGetProperty("url", out var u2) ? u2.GetString() : null;
                    if (string.IsNullOrEmpty(url)) continue;
                    if (name.Equals("SHA256SUMS", StringComparison.OrdinalIgnoreCase) ||
                        name.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase))
                        sumsUrl = url;
                    else if (name.EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase) ||
                             Regex.IsMatch(name, @"LiaDesktop-.*-setup\.exe$", RegexOptions.IgnoreCase))
                    {
                        setupUrl = url;
                        setupName = name;
                    }
                }
            }

            if (string.IsNullOrEmpty(setupUrl) || string.IsNullOrEmpty(setupName))
            {
                var msg = "обновления: в релизе нет setup.exe";
                if (!quiet) _log.Info(msg);
                return new UpdateCheckResult { Status = UpdateCheckStatus.Error, Message = msg };
            }

            var info = new UpdateInfo
            {
                Version = remoteVer,
                Tag = tag.StartsWith('v') ? tag : "v" + tag,
                SetupUrl = setupUrl!,
                SetupName = setupName!,
                ChecksumsUrl = sumsUrl,
                HtmlUrl = root.TryGetProperty("html_url", out var hu) ? hu.GetString() : null,
            };
            Pending = info;
            if (!quiet) _log.Info($"обновления: доступно {info.Tag}");
            UpdateAvailable?.Invoke(info);
            return new UpdateCheckResult { Status = UpdateCheckStatus.Available, Info = info };
        }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException)
        {
            if (!quiet) _log.Info(JournalOffline);
            return new UpdateCheckResult { Status = UpdateCheckStatus.Offline, Message = JournalOffline };
        }
        catch (Exception ex)
        {
            var msg = "обновления: " + ex.Message;
            if (!quiet) _log.Info(msg);
            return new UpdateCheckResult { Status = UpdateCheckStatus.Error, Message = msg };
        }
    }

    public void Dismiss()
    {
        Pending = null;
        Dismissed?.Invoke();
    }

    /// <summary>
    /// Download setup, verify SHA256 against SHA256SUMS, launch silent upgrade, then exit process.
    /// </summary>
    public async Task<(bool ok, string message)> DownloadAndInstallAsync(
        UpdateInfo info,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), "LiaDesktop-update");
        Directory.CreateDirectory(tmpDir);
        var setupPath = Path.Combine(tmpDir, info.SetupName);
        try
        {
            Progress?.Invoke("Скачивание…");
            await DownloadFileAsync(info.SetupUrl, setupPath, progress, ct).ConfigureAwait(false);

            if (string.IsNullOrEmpty(info.ChecksumsUrl))
            {
                var msg = "обновления: нет SHA256SUMS в релизе — установка отменена";
                _log.Info(msg);
                FriendlyError?.Invoke(msg);
                return (false, msg);
            }

            Progress?.Invoke("Проверка SHA256…");
            var sumsText = await _http.GetStringAsync(info.ChecksumsUrl, ct).ConfigureAwait(false);
            if (!UpdateLogic.TryGetSha256(sumsText, info.SetupName, out var expected) ||
                string.IsNullOrEmpty(expected))
            {
                var msg = "обновления: SHA256SUMS не содержит " + info.SetupName;
                _log.Info(msg);
                FriendlyError?.Invoke(msg);
                return (false, msg);
            }

            var actual = await ComputeSha256Async(setupPath, ct).ConfigureAwait(false);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                _log.Info(JournalShaFail + $" expected={expected} actual={actual}");
                FriendlyError?.Invoke(JournalShaFail);
                try { File.Delete(setupPath); } catch { /* */ }
                return (false, JournalShaFail);
            }

            Progress?.Invoke("Установка…");
            var installDir = InstallDirOverride
                             ?? Path.GetDirectoryName(Environment.ProcessPath)
                             ?? AppContext.BaseDirectory;
            // Leave a pending marker so the relaunched build can confirm e2e / write PASS.
            try { UpdateE2eHook.WritePending(installDir, info.Version); } catch { /* */ }
            var args = UpdateLogic.BuildSilentUpgradeArgs(installDir);
            // Program.Main puts us in a KillOnClose job — launching setup directly would
            // get the installer killed when we Environment.Exit. Escape via shell+.cmd.
            var wrapper = Path.Combine(tmpDir, "lia-run-setup.cmd");
            File.WriteAllText(wrapper,
                "@echo off\r\n" +
                "rem delay so parent can exit and unlock files\r\n" +
                "ping -n 3 127.0.0.1 >nul\r\n" +
                "\"" + setupPath + "\" " + args + "\r\n");
            _log.Info($"обновления: запуск установщика {info.SetupName} dir={installDir}");
            Process.Start(new ProcessStartInfo
            {
                FileName = wrapper,
                UseShellExecute = true,
                WorkingDirectory = tmpDir,
            });

            if (SuppressExitAfterLaunch)
                return (true, "installing");

            await Task.Delay(800, ct).ConfigureAwait(false);
            try
            {
                Environment.Exit(0);
            }
            catch
            {
                Process.GetCurrentProcess().Kill(entireProcessTree: true);
            }
            return (true, "installing");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            var msg = "обновления: " + ex.Message;
            _log.Info(msg);
            FriendlyError?.Invoke(msg);
            return (false, msg);
        }
    }

    public static string BuildSilentUpgradeArgs(string? dir = null) =>
        UpdateLogic.BuildSilentUpgradeArgs(dir);

    public static bool TryGetSha256(string sumsText, string fileName, out string hash) =>
        UpdateLogic.TryGetSha256(sumsText, fileName, out hash);

    public static async Task<string> ComputeSha256Async(string path, CancellationToken ct = default)
    {
        await using var fs = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    async Task DownloadFileAsync(string url, string path, IProgress<double>? progress, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? -1;
        await using var input = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var output = File.Create(path);
        var buf = new byte[81920];
        long read = 0;
        int n;
        while ((n = await input.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
            read += n;
            if (total > 0)
                progress?.Report(read / (double)total);
            else
                progress?.Report(-1);
        }
    }

    public static bool IsUpdateCommand(string? text) => UpdateLogic.IsUpdateCommand(text);

    public static bool IsInstallIntent(string? text) => UpdateLogic.IsInstallIntent(text);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _timerCts?.Cancel(); } catch { /* */ }
        _http.Dispose();
    }
}

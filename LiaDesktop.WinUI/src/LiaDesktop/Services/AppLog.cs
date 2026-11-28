using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace LiaDesktop.Services;

public sealed class AppLog
{
    readonly List<(string Line, string? Detail)> _lines = [];
    public event Action<(string Line, string? Detail)>? Added;

    static readonly object FileLock = new();
    static string? _currentFile;
    static long _currentLen;

    /// <summary>Selftest / CI: redirect logs under a temp tree.</summary>
    public static string? LogRootOverride { get; set; }

    public IReadOnlyList<(string Line, string? Detail)> Lines
    {
        get { lock (_lines) return _lines.ToList(); }
    }

    public void Info(string line) => Push(Collapse(line), null);

    public void Error(string line, string? detail = null)
    {
        var shortLine = Collapse(line);
        var shortDetail = string.IsNullOrWhiteSpace(detail) ? null : Collapse(detail, 160);
        var journal = shortDetail == null ? shortLine : $"{shortLine}: {shortDetail}";
        if (!string.IsNullOrWhiteSpace(detail) && detail.Length > 160)
            WriteCrash("journal", detail);
        Push(journal, null);
    }

    /// <summary>Write to daily log / error.log only — never the ЖУРНАЛ HUD ring.</summary>
    public void FileOnly(string line, string? detail = null)
    {
        line = RedactSecrets(Collapse(line));
        detail = detail == null ? null : RedactSecrets(detail);
        AppendFile("DEBUG", line, detail);
        if (!string.IsNullOrWhiteSpace(detail) && detail.Length > 200)
            WriteCrash("file-only", detail);
    }

    /// <summary>One friendly Russian HUD line; full technical detail stays in the log file.</summary>
    public void MicUnavailable(string technicalDetail)
    {
        FileOnly("mic capture failed", technicalDetail);
        Error(MicUnavailableJournalLine);
    }

    public const string MicUnavailableJournalLine =
        "Микрофон недоступен - проверьте устройство в Настройках";

    void Push(string line, string? detail)
    {
        line = RedactSecrets(line);
        detail = detail == null ? null : RedactSecrets(detail);
        lock (_lines)
        {
            _lines.Add((line, detail));
            if (_lines.Count > 200) _lines.RemoveAt(0);
        }
        Added?.Invoke((line, detail));
        AppendFile("INFO", line, detail);
    }

    public static string LogDir()
    {
        var root = LogRootOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LiaDesktop");
        var dir = Path.Combine(root, "logs");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string LogRoot()
    {
        var root = LogRootOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LiaDesktop");
        Directory.CreateDirectory(root);
        return root;
    }

    public static string TodayLogPath() =>
        Path.Combine(LogDir(), $"lia-{DateTime.Now:yyyy-MM-dd}.log");

    public static string? LatestLogPath()
    {
        try
        {
            var files = Directory.GetFiles(LogDir(), "lia-*.log");
            if (files.Length == 0) return null;
            return files.OrderByDescending(f => f).First();
        }
        catch { return null; }
    }

    public static void OpenLogFolder()
    {
        try
        {
            var dir = LogDir();
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = dir,
                UseShellExecute = true,
            });
        }
        catch { /* */ }
    }

    public static string? CopyLatestToDesktop(string fileName = "Lia-log.txt")
    {
        try
        {
            var src = LatestLogPath() ?? TodayLogPath();
            if (!File.Exists(src)) return null;
            var desk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var dest = Path.Combine(desk, fileName);
            File.Copy(src, dest, true);
            return dest;
        }
        catch { return null; }
    }

    public static void WriteBoot(string msg) => AppendFile("BOOT", RedactSecrets(msg), null);

    public static void WriteCrash(string tag, Exception ex) =>
        WriteCrash(tag, ex.ToString());

    public static void WriteCrash(string tag, string text)
    {
        text = RedactSecrets(text);
        try
        {
            // Legacy error.log (kept) + daily rolling log
            var legacy = Path.Combine(LogRoot(), "error.log");
            var block = new StringBuilder();
            block.AppendLine($"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{tag}] ====");
            block.AppendLine(text);
            block.AppendLine();
            lock (FileLock)
            {
                File.AppendAllText(legacy, block.ToString(), Encoding.UTF8);
            }
            AppendFile("CRASH", $"[{tag}]", text);
        }
        catch { /* never throw from logger */ }
    }

    public void WriteStartupDiagnostics(AppServices services)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("======== STARTUP DIAGNOSTICS ========");
            var ver = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.7.6";
            sb.AppendLine($"app_version={ver}");
            sb.AppendLine($"os={Environment.OSVersion}");
            try
            {
                sb.AppendLine($"windows_build={Environment.OSVersion.Version}");
            }
            catch { /* */ }
            sb.AppendLine($"machine={Environment.MachineName} user={Environment.UserName}");
            sb.AppendLine($"pid={Environment.ProcessId} cwd={Environment.CurrentDirectory}");

            // Displays
            try
            {
                using var en = new MMDeviceEnumerator(); // just to ensure COM ok
                _ = en;
            }
            catch { /* */ }
            try
            {
                // GDI monitors via System.Windows.Forms Screen is unavailable (no WinForms).
                // Use GetSystemMetrics + EnumDisplayMonitors via ScreenCapture path / SM_*
                var cx = GetSystemMetrics(0);
                var cy = GetSystemMetrics(1);
                sb.AppendLine($"primary_screen_physical={cx}x{cy}");
                var dpi = GetDpiForSystemSafe();
                sb.AppendLine($"system_dpi={dpi} scale≈{dpi / 96.0:F2}");
                AppendMonitorList(sb);
            }
            catch (Exception ex)
            {
                sb.AppendLine("display_enum_error=" + Collapse(ex.Message));
            }

            // Audio endpoints
            try
            {
                var mm = AudioOutput.DefaultMultimediaId();
                var comm = AudioOutput.DefaultCommunicationsId();
                sb.AppendLine($"default_multimedia_id={mm ?? "(none)"}");
                sb.AppendLine($"default_communications_id={comm ?? "(none)"}");
                sb.AppendLine($"lia_preferred_output={services.Secrets.Load().AudioOutputDeviceId}");
                sb.AppendLine($"lia_active_output={services.Audio.LastDeviceFriendlyName} role={services.Audio.LastRole}");
                using var en = new MMDeviceEnumerator();
                sb.AppendLine("-- render endpoints --");
                foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active | DeviceState.Disabled | DeviceState.Unplugged))
                {
                    var flags = "";
                    if (d.ID == mm) flags += " [DEFAULT_MULTIMEDIA]";
                    if (d.ID == comm) flags += " [DEFAULT_COMMUNICATIONS]";
                    sb.AppendLine($"  RENDER {d.State} {d.FriendlyName} id={ShortId(d.ID)}{flags}");
                }
                sb.AppendLine("-- capture endpoints --");
                string? defCapMm = null, defCapComm = null;
                try { defCapMm = en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia).ID; } catch { /* */ }
                try { defCapComm = en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications).ID; } catch { /* */ }
                sb.AppendLine($"default_capture_multimedia_id={defCapMm ?? "(none)"}");
                sb.AppendLine($"default_capture_communications_id={defCapComm ?? "(none)"}");
                sb.AppendLine($"lia_mic_device={defCapComm ?? defCapMm ?? "(none)"}");
                foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active | DeviceState.Disabled | DeviceState.Unplugged))
                {
                    var flags = "";
                    if (d.ID == defCapMm) flags += " [DEFAULT_MULTIMEDIA]";
                    if (d.ID == defCapComm) flags += " [DEFAULT_COMMUNICATIONS]";
                    sb.AppendLine($"  CAPTURE {d.State} {d.FriendlyName} id={ShortId(d.ID)}{flags}");
                }
                try
                {
                    var micCount = WaveIn.DeviceCount;
                    sb.AppendLine($"wavein_devices={micCount}");
                    for (var i = 0; i < micCount; i++)
                    {
                        var caps = WaveIn.GetCapabilities(i);
                        sb.AppendLine($"  WAVEIN[{i}] {caps.ProductName} ch={caps.Channels}");
                    }
                }
                catch (Exception ex)
                {
                    sb.AppendLine("wavein_enum_error=" + Collapse(ex.Message));
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("audio_enum_error=" + Collapse(ex.Message));
            }

            // Gemini / TTS — never log the key
            var cfg = services.Secrets.Load();
            var keyPresent = !string.IsNullOrWhiteSpace(cfg.ApiKey);
            sb.AppendLine($"gemini_provider={cfg.Provider}");
            sb.AppendLine($"gemini_model={cfg.Model}");
            sb.AppendLine($"gemini_key_present={(keyPresent ? "yes" : "no")}");
            sb.AppendLine($"gemini_active={services.Gemini.ActiveModel}");
            sb.AppendLine($"tts_voice={EdgeTts.Voice} rate={EdgeTts.Rate}");
            sb.AppendLine($"tts_ws_disabled={EdgeTts.DisableWebSocket} tts_sapi_disabled={EdgeTts.DisableSapi}");
            sb.AppendLine($"tts_path_chosen={services.Tts.LastPathChosen}");
            sb.AppendLine("======== END DIAGNOSTICS ========");

            var block = sb.ToString();
            Info("startup diagnostics written");
            AppendFile("DIAG", "startup", block);
        }
        catch (Exception ex)
        {
            WriteCrash("startup-diag", ex);
        }
    }

    public void LogTtsPlayback(string device, long bytes, double durationSec, float? rms, float? peak, string? error)
    {
        var parts = new List<string>
        {
            $"TTS → {device}",
            $"bytes={bytes}",
            $"dur={durationSec:F2}s",
        };
        if (rms.HasValue) parts.Add($"rms={rms.Value:F4}");
        if (peak.HasValue) parts.Add($"peak={peak.Value:F4}");
        if (!string.IsNullOrWhiteSpace(error)) parts.Add("err=" + Collapse(error!));
        Info(string.Join(" | ", parts));
    }

    static void AppendMonitorList(StringBuilder sb)
    {
        try
        {
            var list = new List<string>();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hMon, _, _, _) =>
            {
                var mi = new MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>() };
                if (GetMonitorInfo(hMon, ref mi))
                {
                    var w = mi.rcMonitor.Right - mi.rcMonitor.Left;
                    var h = mi.rcMonitor.Bottom - mi.rcMonitor.Top;
                    uint dpiX = 96, dpiY = 96;
                    try { GetDpiForMonitor(hMon, 0, out dpiX, out dpiY); } catch { /* */ }
                    list.Add($"monitor={mi.szDevice} {w}x{h}@{dpiX}dpi primary={(mi.dwFlags & 1) != 0}");
                }
                return true;
            }, IntPtr.Zero);
            foreach (var l in list) sb.AppendLine(l);
            if (list.Count == 0) sb.AppendLine("monitors=(none enumerated)");
        }
        catch (Exception ex)
        {
            sb.AppendLine("monitor_enum_error=" + Collapse(ex.Message));
        }
    }

    static void AppendFile(string level, string line, string? detail)
    {
        try
        {
            RotateIfNeeded();
            var path = TodayLogPath();
            var sb = new StringBuilder();
            sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            sb.Append(' ');
            sb.Append(level);
            sb.Append(' ');
            sb.Append(line);
            if (!string.IsNullOrWhiteSpace(detail))
            {
                sb.AppendLine();
                sb.Append(detail);
            }
            sb.AppendLine();
            var bytes = Encoding.UTF8.GetBytes(sb.ToString());
            lock (FileLock)
            {
                using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                fs.Write(bytes);
                _currentFile = path;
                _currentLen = fs.Length;
            }
        }
        catch { /* never throw */ }
    }

    static void RotateIfNeeded()
    {
        try
        {
            var dir = LogDir();
            // Drop files older than 7 days
            foreach (var f in Directory.GetFiles(dir, "lia-*.log"))
            {
                try
                {
                    if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-7))
                        File.Delete(f);
                }
                catch { /* */ }
            }

            var today = TodayLogPath();
            if (File.Exists(today))
            {
                var len = new FileInfo(today).Length;
                if (len > 5L * 1024 * 1024)
                {
                    var rotated = Path.Combine(dir, $"lia-{DateTime.Now:yyyy-MM-dd}-part{DateTime.Now:HHmmss}.log");
                    File.Move(today, rotated, true);
                    _currentLen = 0;
                }
            }
        }
        catch { /* */ }
    }

    /// <summary>Strip API keys / bearer tokens from anything we write.</summary>
    public static string RedactSecrets(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        // Google AI keys often AIza...
        s = Regex.Replace(s, @"AIza[0-9A-Za-z\-_]{20,}", "[REDACTED_KEY]");
        s = Regex.Replace(s, @"(?i)(api[_-]?key|apikey|bearer)\s*[:=]\s*['\""]?([^\s'""&]+)", "$1=[REDACTED]");
        s = Regex.Replace(s, @"key=[^&\s]+", "key=[REDACTED]");
        return s;
    }

    static string ShortId(string id)
    {
        if (string.IsNullOrEmpty(id)) return "";
        return id.Length <= 24 ? id : id[..12] + "…" + id[^8..];
    }

    static string Collapse(string s, int max = 200)
    {
        var t = Regex.Replace(s ?? "", @"\s+", " ").Trim();
        var idx = t.IndexOf(" at ", StringComparison.Ordinal);
        if (idx > 40) t = t[..idx].Trim();
        idx = t.IndexOf(" ---> ", StringComparison.Ordinal);
        if (idx > 40) t = t[..idx].Trim();
        if (t.Length > max) t = t[..max] + "…";
        return t;
    }

    static int GetDpiForSystemSafe()
    {
        try { return (int)GetDpiForSystem(); }
        catch { return 96; }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern int GetSystemMetrics(int nIndex);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern uint GetDpiForSystem();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, IntPtr lprcMonitor, IntPtr dwData);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [System.Runtime.InteropServices.DllImport("Shcore.dll")]
    static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }
}

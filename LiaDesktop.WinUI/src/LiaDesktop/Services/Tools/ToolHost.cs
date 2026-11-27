using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Playwright;

namespace LiaDesktop.Services.Tools;

public sealed class ToolHost : IAsyncDisposable
{
    readonly AppLog _log;
    readonly Func<string> _workspace;
    IPlaywright? _pw;
    IBrowserContext? _browser;

    /// <summary>Last look_at_screen / screenshot — used to map image coords → physical.</summary>
    public ScreenShot? LastShot { get; private set; }

    public ToolHost(AppLog log, Func<string> workspace)
    {
        _log = log;
        _workspace = workspace;
    }

    public async Task<object> RunAsync(string name, Dictionary<string, string> args, CancellationToken ct)
    {
        return name switch
        {
            "list_dir" => ListDir(args.GetValueOrDefault("path") ?? _workspace()),
            "read_file" => ReadFile(args.GetValueOrDefault("path") ?? ""),
            "write_file" => WriteFile(args.GetValueOrDefault("path") ?? "", args.GetValueOrDefault("content") ?? ""),
            "run_powershell" => await RunPsAsync(args.GetValueOrDefault("command") ?? "", ct),
            "screenshot" => CaptureScreen(args.GetValueOrDefault("path")),
            "look_at_screen" => CaptureScreen(args.GetValueOrDefault("path")),
            "browser_open" => await BrowserOpenAsync(args.GetValueOrDefault("url") ?? "about:blank"),
            "browser_close" => await BrowserCloseAsync(),
            "type_text" => TypeText(args.GetValueOrDefault("text") ?? ""),
            "mouse_click" => MouseClick(
                int.TryParse(args.GetValueOrDefault("x"), out var x) ? x : 0,
                int.TryParse(args.GetValueOrDefault("y"), out var y) ? y : 0,
                args.GetValueOrDefault("physical")),
            _ => new { error = "unknown tool" },
        };
    }

    public async ValueTask DisposeAsync()
    {
        await BrowserCloseAsync();
        _pw?.Dispose();
        _pw = null;
    }

    object ListDir(string path)
    {
        path = Resolve(path);
        if (!Directory.Exists(path)) return new { error = "not found", path };
        var entries = Directory.EnumerateFileSystemEntries(path)
            .Take(200)
            .Select(p => new { name = Path.GetFileName(p), type = Directory.Exists(p) ? "dir" : "file" })
            .ToList();
        return new { path, entries };
    }

    object ReadFile(string path)
    {
        path = Resolve(path);
        if (!File.Exists(path)) return new { error = "not found" };
        var fi = new FileInfo(path);
        if (fi.Length > 200_000) return new { error = "file too large" };
        return new { path, content = File.ReadAllText(path, Encoding.UTF8) };
    }

    object WriteFile(string path, string content)
    {
        path = Resolve(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, Encoding.UTF8);
        return new { ok = true, path, bytes = Encoding.UTF8.GetByteCount(content) };
    }

    async Task<object> RunPsAsync(string command, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -Command \"{command.Replace("\"", "\\\"")}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("ps");
        var stdout = await p.StandardOutput.ReadToEndAsync(ct);
        var stderr = await p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return new
        {
            exit = p.ExitCode,
            stdout = Trunc(stdout, 4000),
            stderr = Trunc(stderr, 1000),
        };
    }

    ScreenShot CaptureScreen(string? path)
    {
        path ??= Path.Combine(_workspace(), $"shot-{DateTime.Now:yyyyMMdd-HHmmss}.jpg");
        path = Resolve(path);
        // Prefer JPEG for Gemini size; keep .png if caller asked for png
        var jpeg = !path.EndsWith(".png", StringComparison.OrdinalIgnoreCase);
        var shot = ScreenCapture.CapturePrimary(path, jpeg);
        LastShot = shot;
        _log.Info($"screen {shot.PhysicalWidth}x{shot.PhysicalHeight}@{shot.Dpi}dpi → {shot.ImageWidth}x{shot.ImageHeight}");
        return shot;
    }

    async Task<object> BrowserOpenAsync(string url)
    {
        _pw ??= await Playwright.CreateAsync();
        if (_browser == null)
        {
            var profile = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LiaDesktop", "playwright-profile");
            Directory.CreateDirectory(profile);
            // Use installed Edge — do not ship Chromium (~150MB+) in the zip
            _browser = await _pw.Chromium.LaunchPersistentContextAsync(profile, new()
            {
                Channel = "msedge",
                Headless = false,
                ViewportSize = new() { Width = 1280, Height = 800 },
            });
        }
        var page = _browser.Pages.FirstOrDefault() ?? await _browser.NewPageAsync();
        await page.GotoAsync(url, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 });
        return new { ok = true, url };
    }

    async Task<object> BrowserCloseAsync()
    {
        if (_browser != null)
        {
            await _browser.CloseAsync();
            _browser = null;
        }
        return new { ok = true };
    }

    object TypeText(string text)
    {
        foreach (var ch in text) SendChar(ch);
        return new { ok = true, len = text.Length };
    }

    object MouseClick(int x, int y, string? physicalFlag)
    {
        var forcePhysical = string.Equals(physicalFlag, "1", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(physicalFlag, "true", StringComparison.OrdinalIgnoreCase);
        int px = x, py = y;
        if (!forcePhysical && LastShot != null)
        {
            px = LastShot.ImageToPhysicalX(x);
            py = LastShot.ImageToPhysicalY(y);
        }
        SetCursorPos(px, py);
        mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
        mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
        return new { ok = true, image_x = x, image_y = y, physical_x = px, physical_y = py };
    }

    string Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) path = _workspace();
        if (!Path.IsPathRooted(path)) path = Path.Combine(_workspace(), path);
        return Path.GetFullPath(path);
    }

    static string Trunc(string s, int n) => s.Length <= n ? s : s[..n];

    static void SendChar(char c)
    {
        var inputs = new INPUT[2];
        inputs[0].type = 1;
        inputs[0].U.ki.wScan = c;
        inputs[0].U.ki.dwFlags = 0x0004;
        inputs[1] = inputs[0];
        inputs[1].U.ki.dwFlags = 0x0004 | 0x0002;
        SendInput(2, inputs, Marshal.SizeOf<INPUT>());
    }

    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);
    [DllImport("user32.dll")] static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public InputUnion U; }
    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public UIntPtr dwExtraInfo;
    }
}

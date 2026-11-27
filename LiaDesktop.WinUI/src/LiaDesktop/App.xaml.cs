using LiaDesktop.Services;
using Microsoft.UI.Xaml;

namespace LiaDesktop;

public partial class App : Application
{
    public static Window? MainWindowInstance { get; private set; }
    public static AppServices Services { get; } = new();

    public App()
    {
        Program.BootLog("App ctor begin");
        InitializeComponent();
        Program.BootLog("App InitializeComponent ok");
        UnhandledException += (_, e) =>
        {
            Program.BootLog("UnhandledException: " + e.Message);
            Services.Log.Error($"UI: {e.Message}");
            e.Handled = true;
        };
    }

    /// <summary>Parse --smoke-size=1920x1080 or LIA_SMOKE_SIZE. Default 1280x800.</summary>
    public static (int W, int H) ParseSmokeSize()
    {
        static bool TryParse(string? s, out int w, out int h)
        {
            w = h = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;
            var parts = s.Split('x', 'X', '×');
            return parts.Length == 2
                   && int.TryParse(parts[0], out w) && int.TryParse(parts[1], out h)
                   && w >= 640 && h >= 480;
        }

        foreach (var a in Environment.GetCommandLineArgs())
        {
            if (a.StartsWith("--smoke-size=", StringComparison.OrdinalIgnoreCase) &&
                TryParse(a["--smoke-size=".Length..], out var w, out var h))
                return (w, h);
        }
        if (TryParse(Environment.GetEnvironmentVariable("LIA_SMOKE_SIZE"), out var ew, out var eh))
            return (ew, eh);
        return (1280, 800);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Program.BootLog("OnLaunched");
        try
        {
            Services.Bootstrap();
            Program.BootLog("Bootstrap ok");
            MainWindowInstance = new MainWindow();
            Program.BootLog("MainWindow created");
            MainWindowInstance.Activate();
            Program.BootLog("MainWindow.Activate");
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(MainWindowInstance);
            Program.BootLog($"hwnd=0x{hwnd.ToInt64():X}");
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
            var (w, h) = SelfTestRunner.IsSelfTest ? (1024, 720) : ParseSmokeSize();
            appWindow.Resize(new Windows.Graphics.SizeInt32(w, h));
            // Maximize / DPI changes must recreate the DX swapchain with MatrixTransform
            appWindow.Changed += (_, e) =>
            {
                if (e.DidSizeChange || e.DidPresenterChange)
                {
                    if (MainWindowInstance is MainWindow mw)
                        mw.DispatcherQueue.TryEnqueue(() => mw.NotifyStageResized());
                }
            };
            appWindow.Show();
            Program.BootLog($"AppWindow.Show done size={w}x{h}");
        }
        catch (Exception ex)
        {
            Program.BootLog("OnLaunched FATAL: " + ex);
            throw;
        }
    }
}

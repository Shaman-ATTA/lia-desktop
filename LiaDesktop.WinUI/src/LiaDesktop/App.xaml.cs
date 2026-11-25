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
            appWindow.Resize(new Windows.Graphics.SizeInt32(1280, 800));
            appWindow.Show();
            Program.BootLog("AppWindow.Show done");
        }
        catch (Exception ex)
        {
            Program.BootLog("OnLaunched FATAL: " + ex);
            throw;
        }
    }
}

using LiaDesktop.Services;
using Microsoft.UI.Xaml;

namespace LiaDesktop;

public partial class App : Application
{
    public static Window? MainWindowInstance { get; private set; }
    public static AppServices Services { get; } = new();

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            Services.Log.Error($"UI: {e.Message}");
            e.Handled = true;
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Services.Bootstrap();
        MainWindowInstance = new MainWindow();
        MainWindowInstance.Activate();
        // Ensure a visible footprint even if DX init is slow (helps CI capture)
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(MainWindowInstance);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
            appWindow.Resize(new Windows.Graphics.SizeInt32(1280, 800));
            appWindow.Show();
        }
        catch { /* */ }
    }
}

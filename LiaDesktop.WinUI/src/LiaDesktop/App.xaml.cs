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
    }
}

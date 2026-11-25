using System.Runtime.InteropServices;
using LiaDesktop.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.ApplicationModel.DynamicDependency;
using WinRT;

namespace LiaDesktop;

public static class Program
{
    [DllImport("Microsoft.ui.xaml.dll")]
    private static extern void XamlCheckProcessRequirements();

    static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LiaDesktop", "boot.log");

    public static void BootLog(string msg)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
        }
        catch { /* */ }
    }

    [STAThread]
    public static void Main(string[] args)
    {
        try { File.WriteAllText(LogPath, $"==== boot {DateTime.Now:o} pid={Environment.ProcessId} ====\n"); } catch { /* */ }

        try
        {
            using var job = JobObject.CreateKillOnClose($"LiaDesktopJob-{Environment.ProcessId}");
            job.AssignCurrentProcess();
            BootLog("job ok");

            // Explicit bootstrap for unpackaged + custom Main (DISABLE_XAML_GENERATED_MAIN)
            try
            {
                Bootstrap.Initialize(0x00010006);
                BootLog("WindowsAppRuntime Bootstrap ok");
            }
            catch (Exception bex)
            {
                BootLog("Bootstrap warn: " + bex.Message);
            }

            XamlCheckProcessRequirements();
            BootLog("xaml check ok");
            ComWrappersSupport.InitializeComWrappers();
            BootLog("com wrappers ok");

            Application.Start(_ =>
            {
                try
                {
                    BootLog("Start callback enter");
                    var ctx = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                    SynchronizationContext.SetSynchronizationContext(ctx);
                    BootLog("sync context ok");
                    new App();
                    BootLog("App ctor returned");
                }
                catch (Exception ex)
                {
                    BootLog("Start callback FATAL: " + ex);
                    throw;
                }
            });
            BootLog("app exited cleanly");
        }
        catch (Exception ex)
        {
            BootLog("FATAL: " + ex);
            throw;
        }
    }
}

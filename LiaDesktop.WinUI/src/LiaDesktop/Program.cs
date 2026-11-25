using System.Runtime.InteropServices;
using LiaDesktop.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinRT;

namespace LiaDesktop;

public static class Program
{
    [DllImport("Microsoft.ui.xaml.dll")]
    private static extern void XamlCheckProcessRequirements();

    [STAThread]
    public static void Main(string[] args)
    {
        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LiaDesktop", "boot.log");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.AppendAllText(logPath, $"\n==== boot {DateTime.Now:o} ====\n");
        }
        catch { /* */ }

        void BootLog(string msg)
        {
            try { File.AppendAllText(logPath, msg + "\n"); } catch { /* */ }
            System.Diagnostics.Debug.WriteLine(msg);
        }

        try
        {
            // Kill-on-close job wraps this process + all children (Playwright, TTS, …)
            using var job = JobObject.CreateKillOnClose("LiaDesktopJob");
            job.AssignCurrentProcess();
            BootLog("job ok");

            XamlCheckProcessRequirements();
            BootLog("xaml check ok");
            ComWrappersSupport.InitializeComWrappers();
            Application.Start(_ =>
            {
                var ctx = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(ctx);
                new App();
                BootLog("app started");
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

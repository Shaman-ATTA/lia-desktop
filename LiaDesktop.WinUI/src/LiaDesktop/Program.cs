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
            GeminiEndpoints.ApplyFromArgs(args);
            var selftest = SelfTestRunner.Detect(args);
            if (selftest)
            {
                // Isolate DPAPI config so CI never touches the user's key store
                var dir = Path.Combine(Path.GetTempPath(), "LiaDesktop-selftest-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                SecretsStore.ConfigDirOverride = dir;
                ConversationMemory.PathOverride = Path.Combine(dir, "session.json");
                BootLog($"selftest config dir={dir}");
                BootLog($"gemini base={GeminiEndpoints.Base}");
            }

            // Self-contained WinAppSDK: do NOT call Bootstrap.Initialize — that prompts for
            // the framework MSIX. Local Microsoft.WindowsAppRuntime*.dll next to the exe is enough.
            using var job = JobObject.CreateKillOnClose($"LiaDesktopJob-{Environment.ProcessId}");
            job.AssignCurrentProcess();
            BootLog("job ok");

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

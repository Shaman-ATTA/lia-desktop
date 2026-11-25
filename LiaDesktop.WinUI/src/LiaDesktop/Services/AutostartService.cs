using Microsoft.Win32;

namespace LiaDesktop.Services;

/// <summary>HKCU Run entry — per-user, no admin. Starts minimized waiting for wake word.</summary>
public static class AutostartService
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "LiaDesktop";

    public static bool IsEnabled()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, false);
            return k?.GetValue(ValueName) is string;
        }
        catch { return false; }
    }

    public static void SetEnabled(bool enabled)
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey, true)
                      ?? Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            var exe = Environment.ProcessPath
                       ?? Path.Combine(AppContext.BaseDirectory, "LiaDesktop.exe");
            k.SetValue(ValueName, $"\"{exe}\" --autostart");
        }
        else
        {
            try { k.DeleteValue(ValueName, false); } catch { /* */ }
        }
    }

    public static bool WantsAutostart(string[] args) =>
        args.Any(a => a.Equals("--autostart", StringComparison.OrdinalIgnoreCase)
                   || a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
}

using System.Diagnostics;
using System.Reflection;

namespace LiaDesktop.Services;

public static class AppVersion
{
    static readonly Lazy<Version> _current = new(() =>
    {
        var asm = Assembly.GetExecutingAssembly().GetName().Version;
        if (asm != null && !(asm.Major == 0 && asm.Minor == 0))
            return new Version(asm.Major, asm.Minor, asm.Build >= 0 ? asm.Build : 0);
        try
        {
            var path = Environment.ProcessPath ?? Assembly.GetExecutingAssembly().Location;
            var info = FileVersionInfo.GetVersionInfo(path);
            if (Version.TryParse(info.FileVersion, out var fv))
                return new Version(fv.Major, fv.Minor, fv.Build >= 0 ? fv.Build : 0);
            if (Version.TryParse(info.ProductVersion?.Split('+')[0], out var pv))
                return new Version(pv.Major, pv.Minor, pv.Build >= 0 ? pv.Build : 0);
        }
        catch { /* */ }
        return new Version(0, 7, 6);
    });

    public static Version Current => _current.Value;

    public static string Display => $"{Current.Major}.{Current.Minor}.{Current.Build}";

    public static bool TryParseTag(string? tag, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(tag)) return false;
        var t = tag.Trim();
        if (t.StartsWith('v') || t.StartsWith('V')) t = t[1..];
        // strip pre-release
        var cut = t.IndexOfAny(['-', '+']);
        if (cut >= 0) t = t[..cut];
        if (!Version.TryParse(t, out var v)) return false;
        version = new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
        return true;
    }

    public static bool IsNewer(Version candidate, Version? current = null)
    {
        current ??= Current;
        return candidate > current;
    }
}

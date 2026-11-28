using System.Text.Json;

namespace LiaDesktop.Services;

/// <summary>
/// Pending-file handshake for update_e2e: pre-exit write → post-relaunch confirm.
/// </summary>
public static class UpdateE2eHook
{
    const string PendingName = "update-e2e-pending.json";

    public static string? MarkerPathOverride { get; set; }

    public static void WritePending(string installDir, Version expected)
    {
        var path = Path.Combine(installDir, PendingName);
        var marker = MarkerPathOverride
                     ?? Environment.GetEnvironmentVariable("LIA_UPDATE_E2E_MARKER")
                     ?? Path.Combine(installDir, "update_e2e_marker.txt");
        var json = JsonSerializer.Serialize(new PendingDto
        {
            Expected = $"{expected.Major}.{expected.Minor}.{Math.Max(expected.Build, 0)}",
            MarkerPath = marker,
            WrittenUtc = DateTime.UtcNow,
        });
        File.WriteAllText(path, json);
    }

    /// <summary>
    /// Call on startup. Returns null = no pending; true/false = completed pass/fail
    /// (caller should Environment.Exit — relaunch has no agent env).
    /// </summary>
    public static bool? TryComplete(out string message)
    {
        message = "";
        try
        {
            var installDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
            var pendingPath = Path.Combine(installDir, PendingName);
            if (!File.Exists(pendingPath)) return null;
            var dto = JsonSerializer.Deserialize<PendingDto>(File.ReadAllText(pendingPath));
            if (dto == null || string.IsNullOrWhiteSpace(dto.MarkerPath)) return null;
            var ver = AppVersion.Display;
            var ok = string.Equals(ver, dto.Expected, StringComparison.OrdinalIgnoreCase)
                     || (AppVersion.TryParseTag(dto.Expected, out var exp) && AppVersion.Current >= exp);
            message = ok
                ? $"update_e2e: PASS version={ver} expected={dto.Expected}"
                : $"update_e2e: FAIL version={ver} expected={dto.Expected}";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dto.MarkerPath)!);
                File.WriteAllText(dto.MarkerPath, message + Environment.NewLine);
            }
            catch { /* */ }
            try { File.Delete(pendingPath); } catch { /* */ }
            return ok;
        }
        catch (Exception ex)
        {
            message = "update_e2e: FAIL " + ex.Message;
            return false;
        }
    }

    public static bool IsAgentMode(string[] args) =>
        args.Any(a => a.Equals("--update-e2e-agent", StringComparison.OrdinalIgnoreCase));

    sealed class PendingDto
    {
        public string Expected { get; set; } = "";
        public string MarkerPath { get; set; } = "";
        public DateTime WrittenUtc { get; set; }
    }
}

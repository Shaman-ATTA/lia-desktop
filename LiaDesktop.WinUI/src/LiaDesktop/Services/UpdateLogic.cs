using System.Text;
using System.Text.RegularExpressions;

namespace LiaDesktop.Services;

/// <summary>Pure update helpers (unit-testable; no WinUI / HttpClient).</summary>
public static class UpdateLogic
{
    public static string BuildSilentUpgradeArgs(string? dir = null)
    {
        var sb = new StringBuilder();
        sb.Append("/VERYSILENT /NORESTART /SUPPRESSMSGBOXES ");
        sb.Append("/CLOSEAPPLICATIONS /FORCECLOSEAPPLICATIONS ");
        sb.Append("/LAUNCHAFTER=1");
        if (!string.IsNullOrWhiteSpace(dir))
            sb.Append(" /DIR=\"").Append(dir.TrimEnd('\\')).Append('"');
        return sb.ToString();
    }

    public static bool TryGetSha256(string sumsText, string fileName, out string hash)
    {
        hash = "";
        if (string.IsNullOrWhiteSpace(sumsText) || string.IsNullOrWhiteSpace(fileName))
            return false;
        foreach (var raw in sumsText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var line = raw.Trim();
            if (line.StartsWith('#')) continue;
            var m = Regex.Match(line, @"^([A-Fa-f0-9]{64})\s+\*?(.+)$");
            if (!m.Success) continue;
            var name = Path.GetFileName(m.Groups[2].Value.Trim().Trim('"'));
            if (name.Equals(fileName, StringComparison.OrdinalIgnoreCase) ||
                m.Groups[2].Value.Trim().EndsWith(fileName, StringComparison.OrdinalIgnoreCase))
            {
                hash = m.Groups[1].Value.ToLowerInvariant();
                return true;
            }
        }
        return false;
    }

    /// <summary>Voice: «обнови себя» / «проверь обновления».</summary>
    public static bool IsUpdateCommand(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim().ToLowerInvariant();
        t = Regex.Replace(t, @"^(лиа|лия)\s*[,:]?\s*", "");
        if (Regex.IsMatch(t, @"\b(обнови\s+себя|обновись|обновить\s+себя)\b"))
            return true;
        if (Regex.IsMatch(t, @"\b(проверь\s+обновлен|проверить\s+обновлен|проверка\s+обновлен)\w*\b"))
            return true;
        return false;
    }

    public static bool IsInstallIntent(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim().ToLowerInvariant();
        return Regex.IsMatch(t, @"\b(обнови\s+себя|обновись|установи\s+обновлен|ставь\s+обновлен)\w*\b");
    }
}

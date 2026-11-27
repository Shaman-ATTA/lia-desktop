using System.Text;
using System.Text.RegularExpressions;

namespace LiaDesktop.Services;

public sealed class AppLog
{
    readonly List<(string Line, string? Detail)> _lines = [];
    public event Action<(string Line, string? Detail)>? Added;

    static readonly object FileLock = new();

    public IReadOnlyList<(string Line, string? Detail)> Lines
    {
        get { lock (_lines) return _lines.ToList(); }
    }

    public void Info(string line) => Push(Collapse(line), null);

    public void Error(string line, string? detail = null)
    {
        var shortLine = Collapse(line);
        var shortDetail = string.IsNullOrWhiteSpace(detail) ? null : Collapse(detail, 160);
        // Journal gets one readable line; full text goes to disk
        var journal = shortDetail == null ? shortLine : $"{shortLine}: {shortDetail}";
        if (!string.IsNullOrWhiteSpace(detail) && detail.Length > 160)
            WriteCrash("journal", detail);
        Push(journal, null);
    }

    void Push(string line, string? detail)
    {
        lock (_lines)
        {
            _lines.Add((line, detail));
            if (_lines.Count > 200) _lines.RemoveAt(0);
        }
        Added?.Invoke((line, detail));
    }

    public static string LogDir()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LiaDesktop");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static void WriteCrash(string tag, Exception ex) =>
        WriteCrash(tag, ex.ToString());

    public static void WriteCrash(string tag, string text)
    {
        try
        {
            var path = Path.Combine(LogDir(), "error.log");
            var block = new StringBuilder();
            block.AppendLine($"==== {DateTime.UtcNow:O} [{tag}] ====");
            block.AppendLine(text);
            block.AppendLine();
            lock (FileLock)
            {
                File.AppendAllText(path, block.ToString(), Encoding.UTF8);
            }
        }
        catch { /* never throw from logger */ }
    }

    static string Collapse(string s, int max = 200)
    {
        var t = Regex.Replace(s ?? "", @"\s+", " ").Trim();
        // Strip giant managed traces from the journal line
        var idx = t.IndexOf(" at ", StringComparison.Ordinal);
        if (idx > 40) t = t[..idx].Trim();
        idx = t.IndexOf(" ---> ", StringComparison.Ordinal);
        if (idx > 40) t = t[..idx].Trim();
        if (t.Length > max) t = t[..max] + "…";
        return t;
    }
}

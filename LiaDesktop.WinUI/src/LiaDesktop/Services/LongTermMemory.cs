using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace LiaDesktop.Services;

public sealed class MemoryFact
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Text { get; set; } = "";
    public DateTime SavedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>Durable user facts in %LOCALAPPDATA%\LiaDesktop\memory.json (or PathOverride).</summary>
public sealed class LongTermMemory
{
    public static string? PathOverride { get; set; }

    readonly object _gate = new();
    readonly string _path;
    readonly AppLog _log;
    List<MemoryFact> _facts = [];

    static readonly Regex NameRe = new(
        @"меня\s+зовут\s+([A-Za-zА-Яа-яЁё\-]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex PrefRe = new(
        @"(?:я\s+(?:делаю|работаю\s+над|люблю|предпочитаю)|запомни[,:]?)\s+(.+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public LongTermMemory(AppLog log, string? pathOverride = null)
    {
        _log = log;
        _path = pathOverride
                ?? PathOverride
                ?? System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "LiaDesktop", "memory.json");
        Load();
    }

    public string FilePath => _path;

    public IReadOnlyList<MemoryFact> Snapshot()
    {
        lock (_gate) return _facts.Select(f => new MemoryFact { Id = f.Id, Text = f.Text, SavedUtc = f.SavedUtc }).ToList();
    }

    public MemoryFact Remember(string text)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) throw new ArgumentException("empty fact");
        lock (_gate)
        {
            var existing = _facts.FirstOrDefault(f =>
                string.Equals(f.Text, text, StringComparison.OrdinalIgnoreCase));
            if (existing != null) return existing;
            var fact = new MemoryFact { Text = text };
            _facts.Add(fact);
            SaveUnlocked();
            _log.Info($"память: + {Truncate(text, 80)}");
            return fact;
        }
    }

    public bool Forget(string query)
    {
        query = (query ?? "").Trim();
        lock (_gate)
        {
            var before = _facts.Count;
            _facts = _facts.Where(f =>
                f.Id != query &&
                !f.Text.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
            if (_facts.Count == before) return false;
            SaveUnlocked();
            _log.Info($"память: − «{Truncate(query, 60)}»");
            return true;
        }
    }

    public void ForgetAll()
    {
        lock (_gate)
        {
            _facts.Clear();
            SaveUnlocked();
            _log.Info("память: очищена полностью");
        }
    }

    public IReadOnlyList<MemoryFact> Recall(string? query = null)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(query))
                return Snapshot();
            return _facts
                .Where(f => f.Text.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Select(f => new MemoryFact { Id = f.Id, Text = f.Text, SavedUtc = f.SavedUtc })
                .ToList();
        }
    }

    /// <summary>Extract durable facts from user utterance (name / preferences / projects).</summary>
    public void AutoCaptureFromUser(string userText)
    {
        if (string.IsNullOrWhiteSpace(userText)) return;
        var t = userText.Trim();
        var m = NameRe.Match(t);
        if (m.Success)
            Remember($"пользователя зовут {m.Groups[1].Value}");

        // «я делаю игры ATTA» / «запомни …»
        foreach (Match pref in PrefRe.Matches(t))
        {
            var body = pref.Groups[1].Value.Trim().TrimEnd('.', '!', '?');
            if (body.Length < 3) continue;
            if (body.StartsWith("число", StringComparison.OrdinalIgnoreCase)) continue; // session trivia
            Remember(body);
        }
    }

    public string PromptBlock()
    {
        lock (_gate)
        {
            if (_facts.Count == 0) return "";
            var lines = _facts.Take(40).Select(f => "- " + f.Text);
            return "Долгая память о пользователе (не забывай между сессиями, пока не попросят забыть):\n"
                   + string.Join("\n", lines);
        }
    }

    void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var json = File.ReadAllText(_path);
            var doc = JsonSerializer.Deserialize<Store>(json);
            if (doc?.Facts != null)
                _facts = doc.Facts.Where(f => !string.IsNullOrWhiteSpace(f.Text)).ToList();
        }
        catch (Exception ex)
        {
            _log.FileOnly("ltm load fail", ex.Message);
        }
    }

    void SaveUnlocked()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
            var json = JsonSerializer.Serialize(new Store { Facts = _facts },
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_path, json);
        }
        catch (Exception ex)
        {
            _log.FileOnly("ltm save fail", ex.Message);
        }
    }

    static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    sealed class Store
    {
        [JsonPropertyName("facts")]
        public List<MemoryFact> Facts { get; set; } = [];
    }
}

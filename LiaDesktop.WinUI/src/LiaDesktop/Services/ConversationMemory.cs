using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LiaDesktop.Services;

/// <summary>
/// Session dialog history for Gemini (tool calls/results included).
/// Strips old inline images (keeps latest), caps ~20 turns, persists to LocalAppData.
/// </summary>
public sealed class ConversationMemory
{
    public const int MaxTurns = 20;
    public const int SoftCharBudget = 48_000;

    /// <summary>Override persist path (selftest temp dir).</summary>
    public static string? PathOverride { get; set; }

    readonly AppLog _log;
    readonly string _path;
    readonly object _gate = new();
    JsonArray _contents = [];

    public ConversationMemory(AppLog log, string? pathOverride = null)
    {
        _log = log;
        _path = pathOverride
                ?? PathOverride
                ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "LiaDesktop", "session.json");
        Load();
    }

    public int TurnCount
    {
        get { lock (_gate) return _contents.Count; }
    }

    public JsonArray Snapshot()
    {
        lock (_gate) return (JsonArray)(_contents.DeepClone() ?? new JsonArray());
    }

    public void Replace(JsonArray contents)
    {
        lock (_gate)
        {
            _contents = (JsonArray)(contents.DeepClone() ?? new JsonArray());
            TrimUnlocked();
            SaveUnlocked();
        }
    }

    public void AppendUserText(string text)
    {
        lock (_gate)
        {
            _contents.Add(new JsonObject
            {
                ["role"] = "user",
                ["parts"] = new JsonArray(new JsonObject { ["text"] = text }),
            });
            TrimUnlocked();
            SaveUnlocked();
        }
    }

    public void AppendModelParts(JsonArray modelParts)
    {
        lock (_gate)
        {
            _contents.Add(new JsonObject
            {
                ["role"] = "model",
                ["parts"] = modelParts.DeepClone(),
            });
            TrimUnlocked();
            SaveUnlocked();
        }
    }

    public void AppendUserParts(JsonArray parts)
    {
        lock (_gate)
        {
            _contents.Add(new JsonObject
            {
                ["role"] = "user",
                ["parts"] = parts.DeepClone(),
            });
            TrimUnlocked();
            SaveUnlocked();
        }
    }

    /// <summary>Store what was actually spoken (barge-in truncated reply) as model text.</summary>
    public void AppendSpokenTruncated(string spoken)
    {
        if (string.IsNullOrWhiteSpace(spoken)) return;
        lock (_gate)
        {
            _contents.Add(new JsonObject
            {
                ["role"] = "model",
                ["parts"] = new JsonArray(new JsonObject
                {
                    ["text"] = $"[прервано, сказано] {spoken}",
                }),
            });
            TrimUnlocked();
            SaveUnlocked();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _contents = [];
            try { if (File.Exists(_path)) File.Delete(_path); } catch { /* */ }
            _log.Info("Память диалога очищена.");
        }
    }

    void TrimUnlocked()
    {
        StripOldImagesUnlocked();

        while (_contents.Count > MaxTurns * 2) // user+model pairs roughly
            _contents.RemoveAt(0);

        // Soft char budget — drop oldest until under budget
        while (EstimateCharsUnlocked() > SoftCharBudget && _contents.Count > 2)
            _contents.RemoveAt(0);

        // If still huge, summarize head into one user note
        if (EstimateCharsUnlocked() > SoftCharBudget && _contents.Count > 4)
        {
            var summary = new StringBuilder("Краткое резюме более раннего разговора: ");
            var take = Math.Min(6, _contents.Count - 2);
            for (var i = 0; i < take; i++)
            {
                var node = _contents[i];
                var t = ExtractText(node);
                if (!string.IsNullOrWhiteSpace(t))
                    summary.Append(t.Length > 80 ? t[..80] + "… " : t + " ");
            }
            for (var i = 0; i < take; i++) _contents.RemoveAt(0);
            _contents.Insert(0, new JsonObject
            {
                ["role"] = "user",
                ["parts"] = new JsonArray(new JsonObject { ["text"] = summary.ToString().Trim() }),
            });
        }
    }

    void StripOldImagesUnlocked()
    {
        // Keep inline_data only on the last content node that has one
        var lastImgIdx = -1;
        for (var i = 0; i < _contents.Count; i++)
        {
            if (HasInlineImage(_contents[i])) lastImgIdx = i;
        }
        for (var i = 0; i < _contents.Count; i++)
        {
            if (i == lastImgIdx) continue;
            StripInlineImages(_contents[i]);
        }
    }

    static bool HasInlineImage(JsonNode? node)
    {
        if (node is not JsonObject obj) return false;
        if (obj["parts"] is not JsonArray parts) return false;
        foreach (var p in parts)
        {
            if (p is JsonObject po && (po["inline_data"] != null || po["inlineData"] != null))
                return true;
        }
        return false;
    }

    static void StripInlineImages(JsonNode? node)
    {
        if (node is not JsonObject obj) return;
        if (obj["parts"] is not JsonArray parts) return;
        for (var i = parts.Count - 1; i >= 0; i--)
        {
            if (parts[i] is JsonObject po && (po["inline_data"] != null || po["inlineData"] != null))
            {
                parts[i] = new JsonObject
                {
                    ["text"] = "[изображение экрана удалено из истории]",
                };
            }
        }
    }

    static string ExtractText(JsonNode? node)
    {
        if (node is not JsonObject obj) return "";
        if (obj["parts"] is not JsonArray parts) return "";
        var sb = new StringBuilder();
        foreach (var p in parts)
        {
            if (p is JsonObject po && po["text"] is JsonValue tv)
                sb.Append(tv.GetValue<string>()).Append(' ');
        }
        return sb.ToString().Trim();
    }

    int EstimateCharsUnlocked()
    {
        try { return _contents.ToJsonString().Length; }
        catch { return SoftCharBudget; }
    }

    void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var json = File.ReadAllText(_path);
            var node = JsonNode.Parse(json);
            if (node is JsonArray arr)
            {
                _contents = arr;
                TrimUnlocked();
                _log.Info($"Память: загружено {_contents.Count} сообщ.");
            }
        }
        catch (Exception ex)
        {
            _log.Info("Память: не удалось загрузить — " + ex.Message);
            _contents = [];
        }
    }

    void SaveUnlocked()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            // Persist without heavy images
            var clone = (JsonArray)(_contents.DeepClone() ?? new JsonArray());
            foreach (var n in clone) StripInlineImages(n);
            File.WriteAllText(_path, clone.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
        }
        catch (Exception ex)
        {
            _log.Info("Память: не удалось сохранить — " + ex.Message);
        }
    }
}

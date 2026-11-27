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

        var protect = ProtectedTailCount();
        while (_contents.Count > Math.Max(MaxTurns * 2, protect))
        {
            if (!DropOldestSafeUnlocked(protect)) break;
        }

        // Soft char budget — ignore inline image payloads (they dominate and must stay on the latest turn)
        while (EstimateCharsUnlocked(stripImages: true) > SoftCharBudget && _contents.Count > protect)
        {
            if (!DropOldestSafeUnlocked(protect)) break;
        }

        // If still huge (text-only), summarize head into one user note (never mid tool-exchange)
        if (EstimateCharsUnlocked(stripImages: true) > SoftCharBudget && _contents.Count > protect + 2)
        {
            var summary = new StringBuilder("Краткое резюме более раннего разговора: ");
            var take = Math.Min(6, _contents.Count - protect);
            while (take > 0 && WouldOrphanToolExchange(take)) take--;
            if (take <= 0) return;
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

    /// <summary>How many trailing contents must never be trimmed (active user / tool exchange).</summary>
    int ProtectedTailCount()
    {
        if (_contents.Count == 0) return 0;
        var last = _contents.Count - 1;
        if (HasFunctionResponse(_contents[last]))
        {
            // user(prompt) + model(FC) + user(FR[+image]) — keep all three when present
            var n = 1;
            if (last - 1 >= 0 && HasFunctionCall(_contents[last - 1])) n++;
            if (last - 2 >= 0) n++;
            return n;
        }
        if (HasFunctionCall(_contents[last]))
        {
            // user + model(FC) awaiting FR
            return last - 1 >= 0 ? 2 : 1;
        }
        return 1; // at least the latest turn
    }

    /// <summary>
    /// Drop the oldest content without splitting a functionCall ↔ functionResponse pair
    /// and without touching the protected tail (avoids empty contents → Gemini 400).
    /// </summary>
    bool DropOldestSafeUnlocked(int protectTail)
    {
        if (_contents.Count <= protectTail) return false;
        if (_contents.Count == 0) return false;

        // Drop a complete orphaned FC+FR pair at head
        if (_contents.Count >= 2 && HasFunctionCall(_contents[0]) && HasFunctionResponse(_contents[1]))
        {
            if (_contents.Count - 2 < protectTail) return false;
            _contents.RemoveAt(0);
            _contents.RemoveAt(0);
            return true;
        }
        if (HasFunctionCall(_contents[0]))
        {
            // Orphan FC at head — drop FC and its FR if present
            var remove = 1;
            if (_contents.Count > 1 && HasFunctionResponse(_contents[1])) remove = 2;
            if (_contents.Count - remove < protectTail) return false;
            for (var i = 0; i < remove; i++) _contents.RemoveAt(0);
            return true;
        }

        // Drop oldest user/model text. If that would leave FC at head, drop the whole exchange
        // only when the protected tail still leaves enough room.
        if (_contents.Count > 1 && HasFunctionCall(_contents[1]))
        {
            var remove = 2; // user + FC
            if (_contents.Count > 2 && HasFunctionResponse(_contents[2])) remove = 3;
            if (_contents.Count - remove < protectTail) return false;
            for (var i = 0; i < remove; i++) _contents.RemoveAt(0);
            return true;
        }

        _contents.RemoveAt(0);
        return true;
    }

    bool WouldOrphanToolExchange(int take)
    {
        if (take <= 0 || take >= _contents.Count) return true;
        // If we cut right after a functionCall model turn, FR would be orphaned at new head
        if (HasFunctionCall(_contents[take - 1])) return true;
        if (take < _contents.Count && HasFunctionCall(_contents[take])) return true;
        return false;
    }

    static bool HasFunctionCall(JsonNode? node)
    {
        if (node is not JsonObject obj) return false;
        if (obj["parts"] is not JsonArray parts) return false;
        foreach (var p in parts)
        {
            if (p is JsonObject po && (po["functionCall"] != null || po["function_call"] != null))
                return true;
        }
        return false;
    }

    static bool HasFunctionResponse(JsonNode? node)
    {
        if (node is not JsonObject obj) return false;
        if (obj["parts"] is not JsonArray parts) return false;
        foreach (var p in parts)
        {
            if (p is JsonObject po && (po["functionResponse"] != null || po["function_response"] != null))
                return true;
        }
        return false;
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

    int EstimateCharsUnlocked(bool stripImages = false)
    {
        try
        {
            if (!stripImages) return _contents.ToJsonString().Length;
            var clone = (JsonArray)(_contents.DeepClone() ?? new JsonArray());
            foreach (var n in clone) StripInlineImages(n);
            return clone.ToJsonString().Length;
        }
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

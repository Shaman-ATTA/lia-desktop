using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace LiaDesktop.Services;

public sealed record GeminiModelInfo(string Id, string DisplayName, bool SupportsGenerateContent);

public enum GeminiErrorKind { Auth, Model, Network, Other }

public sealed record ClassifiedGeminiError(
    GeminiErrorKind Kind,
    string UserMessage,
    string Detail,
    int? Status = null,
    string? SuggestedModel = null);

public static class GeminiModels
{
    /// <summary>Default when nothing saved / retired. Live-tested 2026-11-26: 200 in ~0.5s.</summary>
    public const string DefaultModel = "gemini-flash-lite-latest";

    /// <summary>
    /// Preferred fallback order (intersected with ListModels).
    /// User choice is prepended by <see cref="BuildFallbackChain"/> when present in the list.
    /// </summary>
    public static readonly string[] PreferredOrder =
    [
        "gemini-flash-lite-latest",
        "gemini-3.1-flash-lite",
        "gemini-3.5-flash-lite",
        "gemini-3.8-flash",
        "gemini-3.7-flash",
    ];

    public static string StripPrefix(string name) =>
        name.StartsWith("models/", StringComparison.Ordinal) ? name["models/".Length..] : name.Trim();

    public static string? ParseSuggestedModel(string message)
    {
        if (string.IsNullOrEmpty(message)) return null;
        var use = Regex.Match(message,
            @"(?:use|try|switch to|update your code to use)\s+(?:the\s+)?(?:model\s+)?(?:models/)?(gemini-[\w.\-]+)",
            RegexOptions.IgnoreCase);
        if (use.Success) return StripPrefix(use.Groups[1].Value);

        var all = Regex.Matches(message, @"models/(gemini-[\w.\-]+)", RegexOptions.IgnoreCase)
            .Select(m => StripPrefix(m.Groups[1].Value)).ToList();
        if (all.Count >= 2)
        {
            var alt = all.FirstOrDefault(id => id != all[0]);
            if (alt != null) return alt;
        }
        return null;
    }

    public static bool IsModelUnavailable(string message, int? status) =>
        status == 404 || Regex.IsMatch(message, @"no longer available|not found|is not supported", RegexOptions.IgnoreCase);

    public static bool IsOverload(string message, int? status) =>
        status is 429 or 503 ||
        Regex.IsMatch(message, @"high demand|resource.?exhausted|rate.?limit|quota|overloaded|try again later|503|429",
            RegexOptions.IgnoreCase);

    public static bool IsRetired(string? id)
    {
        var n = StripPrefix(id ?? "").ToLowerInvariant();
        if (string.IsNullOrEmpty(n)) return true;
        // 2.0 retired; 2.5 flash no longer available to new users (live 2026-11-26)
        if (n.StartsWith("gemini-2.0-flash")) return true;
        if (n is "gemini-2.5-flash" or "gemini-2.5-flash-001" or "gemini-2.5-flash-latest") return true;
        return false;
    }

    /// <summary>Score for leftover flash models after preferred chain (higher = earlier).</summary>
    public static double ScoreFlash(string id)
    {
        var n = StripPrefix(id).ToLowerInvariant();
        if (!n.Contains("flash")) return double.NegativeInfinity;
        if (Regex.IsMatch(n, @"tts|embed|image-generation")) return double.NegativeInfinity;
        if (IsRetired(n)) return double.NegativeInfinity;

        // Preferred ids win hard
        for (var i = 0; i < PreferredOrder.Length; i++)
        {
            if (n.Equals(PreferredOrder[i], StringComparison.OrdinalIgnoreCase))
                return 100_000 - i * 100;
        }

        double s = 0;
        if (n.Contains("lite")) s += 40;
        if (n.Contains("latest")) s += 30;
        var ver = Regex.Match(n, @"gemini-(\d+)\.(\d+)");
        if (ver.Success) s += int.Parse(ver.Groups[1].Value) * 10 + int.Parse(ver.Groups[2].Value);
        if (Regex.IsMatch(n, @"preview|-exp|thinking")) s -= 50;
        return s;
    }

    public static string? PickBestFlash(IEnumerable<GeminiModelInfo> models)
    {
        var ids = models
            .Where(m => m.SupportsGenerateContent)
            .Select(m => StripPrefix(m.Id))
            .Where(id => !IsRetired(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var p in PreferredOrder)
        {
            if (ids.Contains(p)) return p;
        }

        return models
            .Where(m => m.SupportsGenerateContent && !IsRetired(m.Id))
            .OrderByDescending(m => ScoreFlash(m.Id))
            .Select(m => StripPrefix(m.Id))
            .FirstOrDefault();
    }

    /// <summary>
    /// Fallback chain: [user choice if listed] + PreferredOrder ∩ ListModels + other flash leftovers.
    /// </summary>
    public static IReadOnlyList<string> BuildFallbackChain(
        IEnumerable<GeminiModelInfo> models,
        string? userChoice)
    {
        var listed = models
            .Where(m => m.SupportsGenerateContent)
            .Select(m => StripPrefix(m.Id))
            .Where(id => !string.IsNullOrEmpty(id) && !IsRetired(id))
            .ToList();
        var set = listed.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var chain = new List<string>();

        var cur = StripPrefix(userChoice ?? "");
        if (!string.IsNullOrEmpty(cur) && set.Contains(cur) && !IsRetired(cur))
            chain.Add(listed.First(id => id.Equals(cur, StringComparison.OrdinalIgnoreCase)));

        foreach (var p in PreferredOrder)
        {
            if (!set.Contains(p)) continue;
            if (chain.Any(c => c.Equals(p, StringComparison.OrdinalIgnoreCase))) continue;
            chain.Add(listed.First(id => id.Equals(p, StringComparison.OrdinalIgnoreCase)));
        }

        foreach (var id in listed.OrderByDescending(ScoreFlash))
        {
            if (chain.Any(c => c.Equals(id, StringComparison.OrdinalIgnoreCase))) continue;
            chain.Add(id);
        }

        return chain;
    }

    public static IReadOnlyList<string> NextFlashFallbacks(IEnumerable<GeminiModelInfo> models, string current)
    {
        var cur = StripPrefix(current);
        return BuildFallbackChain(models, null)
            .Where(id => !id.Equals(cur, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public static async Task<IReadOnlyList<GeminiModelInfo>> ListAsync(string apiKey, HttpClient? http = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new HttpRequestException("Нет ключа Gemini", null, System.Net.HttpStatusCode.Unauthorized);

        http ??= new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        var url = $"{GeminiEndpoints.ListModels}?pageSize=100&key={Uri.EscapeDataString(apiKey.Trim())}";
        using var res = await http.GetAsync(url, ct);
        var body = await res.Content.ReadFromJsonAsync<ListModelsResponse>(cancellationToken: ct);
        if (!res.IsSuccessStatusCode)
        {
            var msg = body?.Error?.Message ?? res.ReasonPhrase ?? $"HTTP {(int)res.StatusCode}";
            throw new HttpRequestException(msg, null, res.StatusCode);
        }

        var outList = new List<GeminiModelInfo>();
        foreach (var m in body?.Models ?? [])
        {
            var id = StripPrefix(m.Name ?? "");
            if (string.IsNullOrEmpty(id)) continue;
            var methods = m.SupportedGenerationMethods ?? [];
            if (!methods.Contains("generateContent")) continue;
            outList.Add(new GeminiModelInfo(id, m.DisplayName ?? id, true));
        }
        return outList.OrderByDescending(m => ScoreFlash(m.Id)).ToList();
    }

    public static async Task<(string Model, IReadOnlyList<GeminiModelInfo> Models, bool Migrated)> ResolveAsync(
        string apiKey, string? current, HttpClient? http = null, CancellationToken ct = default)
    {
        var models = await ListAsync(apiKey, http, ct);
        var ids = models.Select(m => m.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var cur = StripPrefix(current ?? "");
        if (!IsRetired(cur) && !string.IsNullOrEmpty(cur) && ids.Contains(cur))
            return (cur, models, false);
        var picked = PickBestFlash(models) ?? throw new InvalidOperationException("Нет доступных моделей Gemini");
        return (picked, models, !cur.Equals(picked, StringComparison.OrdinalIgnoreCase));
    }

    public static ClassifiedGeminiError Classify(Exception err, int? status = null)
    {
        var detail = err.Message;
        if (err is GeminiHttpException ghe)
        {
            status ??= ghe.Status;
            if (!string.IsNullOrWhiteSpace(ghe.ResponseBody) && detail.Length < 40)
                detail = TryMsg(ghe.ResponseBody) ?? detail;
        }
        else if (err is HttpRequestException httpEx && httpEx.StatusCode is { } sc)
            status ??= (int)sc;

        var suggested = ParseSuggestedModel(detail);
        var statusBit = status is int st ? $"HTTP {st}" : "ошибка";

        if (err is TaskCanceledException or OperationCanceledException or TimeoutException)
            return new(GeminiErrorKind.Network,
                $"Таймаут Gemini ({statusBit}). Повторите запрос.",
                detail, status ?? 408);

        if (status == 0 || Regex.IsMatch(detail, @"fetch failed|ECONNREFUSED|ENOTFOUND|network|ETIMEDOUT|сете|HttpRequestException", RegexOptions.IgnoreCase))
            return new(GeminiErrorKind.Network, "Нет связи с Gemini. Проверьте интернет.", detail, status);

        if (status is 401 or 403 || (status == 400 && Regex.IsMatch(detail, @"API key|API_KEY|not valid", RegexOptions.IgnoreCase)) ||
            Regex.IsMatch(detail, @"API key not valid|API_KEY_INVALID|PERMISSION_DENIED", RegexOptions.IgnoreCase))
            return new(GeminiErrorKind.Auth, "Ключ API отклонён. Проверьте ключ в Настройках (aistudio.google.com).", detail, status);

        if (IsOverload(detail, status))
            return new(GeminiErrorKind.Model,
                $"Модель перегружена ({statusBit}): {ShortRu(detail)}",
                detail, status ?? 503, suggested);

        if (IsModelUnavailable(detail, status) || suggested != null)
            return new(GeminiErrorKind.Model,
                suggested != null
                    ? $"Модель недоступна ({statusBit}) → {suggested}"
                    : $"Модель Gemini недоступна ({statusBit}). Выберите другую в Настройках.",
                detail, status ?? 404, suggested);

        return new(GeminiErrorKind.Other,
            $"Ошибка Gemini ({statusBit}): {ShortRu(detail)}",
            detail, status);
    }

    static string ShortRu(string detail)
    {
        var d = detail.Replace('\n', ' ').Trim();
        if (d.Length > 160) d = d[..160] + "…";
        return d;
    }

    static string? TryMsg(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("error").GetProperty("message").GetString();
        }
        catch { return null; }
    }

    sealed class ListModelsResponse
    {
        [JsonPropertyName("models")] public List<ModelDto>? Models { get; set; }
        [JsonPropertyName("error")] public ErrorDto? Error { get; set; }
    }
    sealed class ModelDto
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
        [JsonPropertyName("supportedGenerationMethods")] public List<string>? SupportedGenerationMethods { get; set; }
    }
    sealed class ErrorDto
    {
        [JsonPropertyName("message")] public string? Message { get; set; }
    }
}

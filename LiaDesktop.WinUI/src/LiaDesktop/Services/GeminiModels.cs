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
    const string ListUrl = "https://generativelanguage.googleapis.com/v1beta/models";

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
        Regex.IsMatch(message, @"high demand|resource.?exhausted|unavailable|rate.?limit|quota|overloaded|try again later",
            RegexOptions.IgnoreCase);

    public static double ScoreFlash(string id)
    {
        var n = StripPrefix(id).ToLowerInvariant();
        if (!n.Contains("flash")) return double.NegativeInfinity;
        if (Regex.IsMatch(n, @"tts|embed|image-generation")) return double.NegativeInfinity;
        double s = 0;
        var ver = Regex.Match(n, @"gemini-(\d+)\.(\d+)");
        if (ver.Success) s += int.Parse(ver.Groups[1].Value) * 1000 + int.Parse(ver.Groups[2].Value) * 20;
        if (Regex.IsMatch(n, @"preview|-exp|thinking")) s -= 200;
        if (n.Contains("lite")) s -= 15;
        if (Regex.IsMatch(n, @"^gemini-\d+\.\d+-flash$")) s += 80;
        return s;
    }

    public static string? PickBestFlash(IEnumerable<GeminiModelInfo> models)
    {
        var list = models.Where(m => m.SupportsGenerateContent).ToList();
        var flash = list.Where(m => m.Id.Contains("flash", StringComparison.OrdinalIgnoreCase)).ToList();
        var pool = flash.Count > 0 ? flash : list;
        return pool.OrderByDescending(m => ScoreFlash(m.Id)).Select(m => m.Id).FirstOrDefault();
    }

    public static IReadOnlyList<string> NextFlashFallbacks(IEnumerable<GeminiModelInfo> models, string current)
    {
        var cur = StripPrefix(current);
        var ranked = models
            .Where(m => m.SupportsGenerateContent && m.Id.Contains("flash", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(m => ScoreFlash(m.Id))
            .Select(m => m.Id)
            .Where(id => id != cur)
            .ToList();
        var stable = ranked.Where(id => !Regex.IsMatch(id, @"preview|-exp|thinking", RegexOptions.IgnoreCase)).ToList();
        var preview = ranked.Except(stable).ToList();
        return stable.Concat(preview).ToList();
    }

    public static async Task<IReadOnlyList<GeminiModelInfo>> ListAsync(string apiKey, HttpClient? http = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new HttpRequestException("Нет ключа Gemini", null, System.Net.HttpStatusCode.Unauthorized);

        http ??= new HttpClient();
        var url = $"{ListUrl}?pageSize=100&key={Uri.EscapeDataString(apiKey.Trim())}";
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
        var retired = string.IsNullOrEmpty(cur) || cur.StartsWith("gemini-2.0-flash", StringComparison.OrdinalIgnoreCase);
        if (!retired && ids.Contains(cur))
            return (cur, models, false);
        var picked = PickBestFlash(models) ?? throw new InvalidOperationException("Нет доступных моделей Gemini");
        return (picked, models, cur != picked);
    }

    public static ClassifiedGeminiError Classify(Exception err, int? status = null)
    {
        var detail = err.Message;
        if (err is HttpRequestException httpEx && httpEx.StatusCode is { } sc)
            status ??= (int)sc;

        var suggested = ParseSuggestedModel(detail);
        if (status == 0 || Regex.IsMatch(detail, @"fetch failed|ECONNREFUSED|ENOTFOUND|network|ETIMEDOUT|сете|HttpRequestException", RegexOptions.IgnoreCase))
            return new(GeminiErrorKind.Network, "Нет связи с Gemini. Проверьте интернет.", detail, status);

        if (status is 401 or 403 || (status == 400 && Regex.IsMatch(detail, @"API key|API_KEY|not valid", RegexOptions.IgnoreCase)) ||
            Regex.IsMatch(detail, @"API key not valid|API_KEY_INVALID|PERMISSION_DENIED", RegexOptions.IgnoreCase))
            return new(GeminiErrorKind.Auth, "Ключ API отклонён. Проверьте ключ в Настройках (aistudio.google.com).", detail, status);

        if (IsOverload(detail, status))
            return new(GeminiErrorKind.Model, "Модель перегружена. Повторяю запрос…", detail, status ?? 503, suggested);

        if (IsModelUnavailable(detail, status) || suggested != null)
            return new(GeminiErrorKind.Model,
                suggested != null ? $"Модель недоступна → {suggested}" : "Модель Gemini недоступна. Выберите другую в Настройках.",
                detail, status ?? 404, suggested);

        return new(GeminiErrorKind.Other, "Ошибка Gemini. Подробности в журнале.", detail, status);
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

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LiaDesktop.Services;

/// <summary>Gemini generateContent + tool loop helpers (v1beta REST).</summary>
public sealed class GeminiClient
{
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(90) };
    readonly SecretsStore _secrets;
    readonly AppLog _log;

    public string ActiveModel { get; private set; } = "";

    public event Action<string>? ModelChanged;

    public GeminiClient(SecretsStore secrets, AppLog log)
    {
        _secrets = secrets;
        _log = log;
    }

    public async Task EnsureModelAsync(CancellationToken ct = default)
    {
        var cfg = _secrets.Load();
        if (cfg.Provider != "gemini" || string.IsNullOrWhiteSpace(cfg.ApiKey)) return;
        try
        {
            var (model, _, migrated) = await GeminiModels.ResolveAsync(cfg.ApiKey, cfg.Model, _http, ct);
            if (migrated || cfg.Model != model)
            {
                cfg.Model = model;
                cfg.VisionModel = model;
                _secrets.Save(cfg);
                _log.Info($"Модель: {model}");
            }
            SetModel(model);
        }
        catch (Exception ex)
        {
            var c = GeminiModels.Classify(ex);
            _log.Error(c.UserMessage, c.Detail);
        }
    }

    void SetModel(string model)
    {
        ActiveModel = model;
        ModelChanged?.Invoke(model);
    }

    public async Task<GeminiTurnResult> GenerateTurnAsync(
        string system,
        JsonArray contents,
        IReadOnlyList<object>? tools,
        CancellationToken ct = default)
    {
        var cfg = _secrets.Load();
        if (string.IsNullOrWhiteSpace(cfg.ApiKey))
            throw new HttpRequestException("Нет ключа Gemini. Введите ключ в Настройках.", null, System.Net.HttpStatusCode.Unauthorized);

        await EnsureModelAsync(ct);
        cfg = _secrets.Load();
        var model = string.IsNullOrWhiteSpace(cfg.Model) ? ActiveModel : cfg.Model;
        if (string.IsNullOrWhiteSpace(model))
            throw new InvalidOperationException("Модель не выбрана.");

        var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var overloadRetries = 0;

        while (true)
        {
            tried.Add(model);
            SetModel(model);
            try
            {
                return await GenerateOnceAsync(cfg.ApiKey, model, system, contents, tools, ct);
            }
            catch (Exception ex)
            {
                var status = (ex as HttpRequestException)?.StatusCode is { } sc ? (int?)sc : null;
                var msg = ex.Message;

                if (GeminiModels.IsModelUnavailable(msg, status))
                {
                    var suggested = GeminiModels.ParseSuggestedModel(msg);
                    if (suggested != null && tried.Add(suggested))
                    {
                        _log.Info($"Модель недоступна → {suggested}");
                        SwitchPersist(suggested);
                        model = suggested;
                        overloadRetries = 0;
                        continue;
                    }
                    var models = await GeminiModels.ListAsync(cfg.ApiKey, _http, ct);
                    var next = GeminiModels.NextFlashFallbacks(models, model).FirstOrDefault(id => tried.Add(id));
                    if (next != null)
                    {
                        _log.Info($"Модель недоступна → {next}");
                        SwitchPersist(next);
                        model = next;
                        overloadRetries = 0;
                        continue;
                    }
                }

                if (GeminiModels.IsOverload(msg, status))
                {
                    if (overloadRetries < 3)
                    {
                        var wait = 800 * (1 << overloadRetries);
                        _log.Info($"Модель перегружена, жду {wait / 1000.0:0.#}с…");
                        await Task.Delay(wait, ct);
                        overloadRetries++;
                        continue;
                    }
                    var models = await GeminiModels.ListAsync(cfg.ApiKey, _http, ct);
                    var next = GeminiModels.NextFlashFallbacks(models, model).FirstOrDefault(id => tried.Add(id));
                    if (next != null)
                    {
                        _log.Info($"Модель перегружена, переключаюсь на {next}");
                        SwitchPersist(next);
                        model = next;
                        overloadRetries = 0;
                        continue;
                    }
                }

                var classified = GeminiModels.Classify(ex, status);
                throw new GeminiUserException(classified);
            }
        }
    }

    /// <summary>Simple text-only helper used by settings smoke / short chats.</summary>
    public async Task<string> GenerateAsync(
        string system,
        IReadOnlyList<(string Role, string Text)> history,
        IReadOnlyList<object>? tools,
        string? imagePngBase64,
        CancellationToken ct = default)
    {
        var contents = new JsonArray();
        foreach (var (role, text) in history)
        {
            var r = role is "assistant" or "model" ? "model" : "user";
            contents.Add(new JsonObject
            {
                ["role"] = r,
                ["parts"] = new JsonArray(new JsonObject { ["text"] = text }),
            });
        }
        if (!string.IsNullOrEmpty(imagePngBase64))
        {
            contents.Add(new JsonObject
            {
                ["role"] = "user",
                ["parts"] = new JsonArray(
                    new JsonObject { ["text"] = "[screen]" },
                    new JsonObject
                    {
                        ["inline_data"] = new JsonObject
                        {
                            ["mime_type"] = "image/png",
                            ["data"] = imagePngBase64,
                        },
                    }),
            });
        }
        var turn = await GenerateTurnAsync(system, contents, tools, ct);
        return turn.Text ?? "Готово.";
    }

    void SwitchPersist(string model)
    {
        var cfg = _secrets.Load();
        cfg.Model = model;
        cfg.VisionModel = model;
        _secrets.Save(cfg);
        SetModel(model);
    }

    async Task<GeminiTurnResult> GenerateOnceAsync(
        string apiKey,
        string model,
        string system,
        JsonArray contents,
        IReadOnlyList<object>? tools,
        CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["system_instruction"] = new JsonObject
            {
                ["parts"] = new JsonArray(new JsonObject { ["text"] = system }),
            },
            ["contents"] = contents.DeepClone(),
        };
        if (tools is { Count: > 0 })
        {
            body["tools"] = new JsonArray(new JsonObject
            {
                ["function_declarations"] = JsonSerializer.SerializeToNode(tools),
            });
        }

        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(model)}:generateContent?key={Uri.EscapeDataString(apiKey)}";
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        using var res = await _http.SendAsync(req, ct);
        var json = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new HttpRequestException(TryExtractError(json) ?? res.ReasonPhrase ?? "Gemini error", null, res.StatusCode);

        using var doc = JsonDocument.Parse(json);
        var parts = doc.RootElement
            .GetProperty("candidates")[0]
            .GetProperty("content")
            .GetProperty("parts");

        string? text = null;
        var calls = new List<GeminiFunctionCall>();
        var modelParts = new JsonArray();
        foreach (var part in parts.EnumerateArray())
        {
            if (part.TryGetProperty("text", out var t))
            {
                text = (text ?? "") + t.GetString();
                modelParts.Add(new JsonObject { ["text"] = t.GetString() });
            }
            if (part.TryGetProperty("functionCall", out var fc))
            {
                var name = fc.GetProperty("name").GetString() ?? "";
                var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (fc.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Object)
                {
                    foreach (var p in a.EnumerateObject())
                        args[p.Name] = p.Value.ValueKind == JsonValueKind.String
                            ? p.Value.GetString() ?? ""
                            : p.Value.ToString();
                }
                calls.Add(new GeminiFunctionCall(name, args));
                modelParts.Add(JsonNode.Parse(part.GetRawText())!);
            }
        }

        return new GeminiTurnResult(text, calls, modelParts);
    }

    static string? TryExtractError(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("error").GetProperty("message").GetString();
        }
        catch { return null; }
    }
}

public sealed record GeminiFunctionCall(string Name, Dictionary<string, string> Args);

public sealed record GeminiTurnResult(string? Text, IReadOnlyList<GeminiFunctionCall> Calls, JsonArray ModelParts);

public sealed class GeminiUserException : Exception
{
    public ClassifiedGeminiError Classified { get; }
    public GeminiUserException(ClassifiedGeminiError c) : base(c.UserMessage) => Classified = c;
}

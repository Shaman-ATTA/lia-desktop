using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LiaDesktop.Services;

/// <summary>Gemini generateContent + tool loop helpers (v1beta REST).</summary>
public sealed class GeminiClient
{
    public static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(90);

    readonly HttpClient _http = new() { Timeout = HttpTimeout };
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
            if (migrated || !string.Equals(cfg.Model, model, StringComparison.OrdinalIgnoreCase))
            {
                cfg.Model = model;
                cfg.VisionModel = model;
                _secrets.Save(cfg);
                _log.Info($"Модель: {model}");
            }
            SetModel(model);
        }
        catch (OperationCanceledException) { throw; }
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

        // Always validate against ListModels — never keep a phantom name like gemini-3.8-flash.
        try
        {
            var (resolved, _, migrated) = await GeminiModels.ResolveAsync(cfg.ApiKey, cfg.Model, _http, ct);
            if (migrated || !string.Equals(cfg.Model, resolved, StringComparison.OrdinalIgnoreCase))
            {
                _log.Info(string.IsNullOrWhiteSpace(cfg.Model)
                    ? $"Модель: {resolved}"
                    : $"Модель «{cfg.Model}» недоступна → {resolved}");
                SwitchPersist(resolved);
                cfg = _secrets.Load();
            }
            else SetModel(resolved);
        }
        catch (OperationCanceledException) { throw; }

        cfg = _secrets.Load();
        var model = string.IsNullOrWhiteSpace(cfg.Model) ? ActiveModel : cfg.Model;
        if (string.IsNullOrWhiteSpace(model))
            throw new InvalidOperationException("Модель не выбрана.");

        var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var overloadRetries = 0;
        const int maxOverloadTries = 5;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            tried.Add(model);
            SetModel(model);
            try
            {
                return await GenerateOnceAsync(cfg.ApiKey, model, system, contents, tools, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                var status = ex is GeminiHttpException ghe ? ghe.Status
                    : (ex as HttpRequestException)?.StatusCode is { } sc ? (int)sc : (int?)null;
                var msg = ex.Message;
                // HttpClient timeout surfaces as TaskCanceledException without abort
                if (ex is TaskCanceledException or TimeoutException && !ct.IsCancellationRequested)
                {
                    status ??= 408;
                    msg = "Таймаут запроса к Gemini (>90с)";
                }

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
                    if (overloadRetries < maxOverloadTries)
                    {
                        var waitSec = RetryWaitSeconds(ex, overloadRetries);
                        _log.Info($"Модель перегружена (HTTP {status?.ToString() ?? "?"}), жду {waitSec:0.#}с…");
                        // Delay uses abort token only — no short linked timeout
                        await Task.Delay(TimeSpan.FromSeconds(waitSec), ct);
                        overloadRetries++;
                        continue;
                    }
                    var models = await GeminiModels.ListAsync(cfg.ApiKey, _http, ct);
                    var next = GeminiModels.NextFlashFallbacks(models, model)
                        .FirstOrDefault(id => tried.Add(id));
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

    /// <summary>1s,2s,4s,8s,16s — honor Retry-After when present.</summary>
    static double RetryWaitSeconds(Exception ex, int attemptZeroBased)
    {
        if (ex is GeminiHttpException { RetryAfterSeconds: > 0 } g)
            return Math.Clamp(g.RetryAfterSeconds.Value, 1, 60);
        return Math.Min(16, 1 << attemptZeroBased); // 1,2,4,8,16
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

    /// <summary>Transcribe Russian speech from WAV/PCM bytes via generateContent inline audio.</summary>
    public async Task<string> TranscribeRuAsync(byte[] wavBytes, CancellationToken ct = default)
    {
        var cfg = _secrets.Load();
        if (string.IsNullOrWhiteSpace(cfg.ApiKey))
            throw new HttpRequestException("Нет ключа Gemini.", null, System.Net.HttpStatusCode.Unauthorized);

        await EnsureModelAsync(ct);
        cfg = _secrets.Load();
        var model = string.IsNullOrWhiteSpace(cfg.Model) ? ActiveModel : cfg.Model;
        if (string.IsNullOrWhiteSpace(model))
        {
            var (resolved, _, _) = await GeminiModels.ResolveAsync(cfg.ApiKey, null, _http, ct);
            model = resolved;
            SwitchPersist(model);
        }

        var b64 = Convert.ToBase64String(wavBytes);
        var contents = new JsonArray
        {
            new JsonObject
            {
                ["role"] = "user",
                ["parts"] = new JsonArray(
                    new JsonObject
                    {
                        ["text"] =
                            "Распознай русскую речь из аудио. Верни только текст сказанного, без кавычек и пояснений.",
                    },
                    new JsonObject
                    {
                        ["inline_data"] = new JsonObject
                        {
                            ["mime_type"] = "audio/wav",
                            ["data"] = b64,
                        },
                    }),
            },
        };
        var turn = await GenerateTurnAsync(
            "Ты — точный транскрибер русской речи. Отвечай только расшифрованным текстом.",
            contents, null, ct);
        return (turn.Text ?? "").Trim();
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

        // Per-request abort+timeout: never shorter than HttpTimeout; abort still works.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(HttpTimeout);
        using var res = await _http.SendAsync(req, timeoutCts.Token);
        var json = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            var errMsg = TryExtractError(json) ?? res.ReasonPhrase ?? "Gemini error";
            throw new GeminiHttpException(errMsg, (int)res.StatusCode, ParseRetryAfter(res.Headers), json);
        }

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

    static int? ParseRetryAfter(HttpResponseHeaders headers)
    {
        if (!headers.TryGetValues("Retry-After", out var vals)) return null;
        var v = vals.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(v)) return null;
        if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sec))
            return sec;
        if (DateTimeOffset.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when))
            return (int)Math.Max(1, (when - DateTimeOffset.UtcNow).TotalSeconds);
        return null;
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

using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LiaDesktop.Services;

/// <summary>Gemini generateContent + tool loop helpers (v1beta REST).</summary>
public sealed class GeminiClient
{
    /// <summary>HttpClient ceiling — per-attempt uses <see cref="AttemptTimeout"/>.</summary>
    public static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(120);
    /// <summary>Per generateContent attempt (~25s). 503/429/timeout → retry once, then next model.</summary>
    public static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(25);

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
        if (string.IsNullOrWhiteSpace(cfg.ApiKey?.Trim()))
            throw new HttpRequestException("Нет ключа Gemini. Введите ключ в Настройках.", null, System.Net.HttpStatusCode.Unauthorized);

        IReadOnlyList<GeminiModelInfo> listed;
        try
        {
            listed = await GeminiModels.ListAsync(cfg.ApiKey, _http, ct);
        }
        catch (OperationCanceledException) { throw; }

        var userChoice = cfg.Model;
        var chain = GeminiModels.BuildFallbackChain(listed, userChoice);
        if (chain.Count == 0)
            throw new InvalidOperationException("Нет доступных моделей Gemini (ListModels пуст).");

        // Persist default if saved model was retired / missing
        var head = chain[0];
        if (!string.Equals(cfg.Model, head, StringComparison.OrdinalIgnoreCase))
        {
            _log.Info(string.IsNullOrWhiteSpace(cfg.Model) || GeminiModels.IsRetired(cfg.Model)
                ? $"Модель: {head}"
                : $"Модель «{cfg.Model}» недоступна → {head}");
            SwitchPersist(head);
        }
        else SetModel(head);

        Exception? last = null;
        foreach (var model in chain)
        {
            ct.ThrowIfCancellationRequested();
            SetModel(model);
            // Per model: attempt + one retry on 503/429/timeout, then next model
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    _log.Info(attempt == 0
                        ? $"Запрос → {model} (лимит {AttemptTimeout.TotalSeconds:0}с)"
                        : $"Повтор → {model}");
                    var turn = await GenerateOnceAsync(cfg.ApiKey, model, system, contents, tools, ct);
                    _log.Info($"Ответ от {model}");
                    SwitchPersist(model); // remember what worked
                    return turn;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    last = ex;
                    var status = ex is GeminiHttpException ghe ? ghe.Status
                        : (ex as HttpRequestException)?.StatusCode is { } sc ? (int)sc : (int?)null;
                    var msg = ex.Message;
                    var timedOut = ex is TaskCanceledException or TimeoutException && !ct.IsCancellationRequested;
                    if (timedOut)
                    {
                        status ??= 408;
                        msg = $"Таймаут {AttemptTimeout.TotalSeconds:0}с";
                    }

                    if (GeminiModels.IsModelUnavailable(msg, status))
                    {
                        var suggested = GeminiModels.ParseSuggestedModel(msg);
                        _log.Info($"Модель {model} недоступна (HTTP {status?.ToString() ?? "?"})");
                        if (suggested != null &&
                            chain.Any(c => c.Equals(suggested, StringComparison.OrdinalIgnoreCase)))
                        {
                            // Jump: next loop iterations will hit suggested if still ahead; otherwise continue chain
                        }
                        break; // next model — no second attempt on hard 404
                    }

                    if (GeminiModels.IsOverload(msg, status) || timedOut || status is 408)
                    {
                        _log.Info($"{model}: {(timedOut ? "таймаут" : $"HTTP {status}")} — {(attempt == 0 ? "повтор" : "следующая модель")}");
                        if (attempt == 0)
                        {
                            var wait = RetryWaitSeconds(ex);
                            await Task.Delay(TimeSpan.FromSeconds(wait), ct);
                            continue;
                        }
                        break; // next model
                    }

                    // Auth / other — don't burn the whole chain
                    throw new GeminiUserException(GeminiModels.Classify(ex, status));
                }
            }
        }

        var classified = GeminiModels.Classify(last ?? new Exception("Все модели недоступны"), 503);
        throw new GeminiUserException(classified);
    }

    /// <summary>Short pause before same-model retry; honor Retry-After (clamped 1–5s).</summary>
    static double RetryWaitSeconds(Exception ex)
    {
        if (ex is GeminiHttpException { RetryAfterSeconds: > 0 } g)
            return Math.Clamp(g.RetryAfterSeconds.Value, 1, 5);
        return 1;
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

        var url = $"{GeminiEndpoints.GenerateContent(model)}?key={Uri.EscapeDataString(apiKey)}";
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };

        // Per-attempt ~25s; vision turns with inline_data need more headroom.
        var attemptLimit = AttemptTimeout;
        try
        {
            var blob = contents.ToJsonString();
            if (blob.Contains("inline_data", StringComparison.OrdinalIgnoreCase) ||
                blob.Contains("inlineData", StringComparison.OrdinalIgnoreCase))
                attemptLimit = TimeSpan.FromSeconds(60);
        }
        catch { /* keep default */ }
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(attemptLimit);
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
        // Preserve EVERY part verbatim (thought / thoughtSignature / functionCall).
        // Stripping thought signatures breaks the next generateContent with a 400 about
        // function-call turn ordering on Gemini 2.x / thinking models.
        foreach (var part in parts.EnumerateArray())
        {
            modelParts.Add(JsonNode.Parse(part.GetRawText())!);
            if (part.TryGetProperty("text", out var t))
                text = (text ?? "") + t.GetString();
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

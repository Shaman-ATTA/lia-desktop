using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LiaDesktop.Services.Tools;

namespace LiaDesktop.Services;

public sealed class AgentService
{
    public const string SystemPrompt =
        "Ты — Лиа, женский голосовой ИИ-ассистент ATTA CORP (N-33). Отвечай коротко по-русски, тёплым живым тоном, женский род. " +
        "Ты управляешь ПК пользователя (личное некоммерческое использование). Кратко озвучивай действия. " +
        "Когда нужно — вызывай инструменты (файлы, PowerShell, браузер, скриншот, мышь/клавиатура).";

    static readonly Regex StopRe = new(@"лиа[,\s]+стоп", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    readonly GeminiClient _gemini;
    readonly SecretsStore _secrets;
    readonly AppLog _log;
    readonly ToolHost _tools;
    readonly EdgeTts _tts;
    CancellationTokenSource? _cts;

    public event Action<string>? Narrate;
    public event Action<float, float, float>? Mouth; // jaw, funnel, wide
    public event Action<string>? SpeakFile;

    public AgentService(GeminiClient gemini, SecretsStore secrets, AppLog log, ToolHost tools, EdgeTts tts)
    {
        _gemini = gemini;
        _secrets = secrets;
        _log = log;
        _tools = tools;
        _tts = tts;
    }

    public void Abort()
    {
        _cts?.Cancel();
        _tts.Stop();
        _log.Info("Остановлено.");
    }

    public static bool IsStopPhrase(string text) => StopRe.IsMatch(text);

    public async Task RunAsync(string userText)
    {
        if (IsStopPhrase(userText))
        {
            Abort();
            return;
        }

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _log.Info($"→ {userText}");

        try
        {
            var cfg = _secrets.Load();
            string reply;
            if (cfg.Provider == "ollama")
                reply = await OllamaChatAsync(cfg, userText, ct);
            else
                reply = await GeminiAgentLoopAsync(userText, ct);

            if (ct.IsCancellationRequested) return;
            Narrate?.Invoke(reply);
            _log.Info(reply.Length > 160 ? reply[..160] + "…" : reply);

            _ = DriveMouthWhileSpeakAsync(reply, ct);
            var path = await _tts.SpeakToFileAsync(reply, ct);
            if (path != null && File.Exists(path))
                SpeakFile?.Invoke(path);
        }
        catch (OperationCanceledException)
        {
            _log.Info("Отменено.");
        }
        catch (GeminiUserException g)
        {
            _log.Error(g.Classified.UserMessage, g.Classified.Detail);
            Narrate?.Invoke(g.Classified.UserMessage);
        }
        catch (Exception ex)
        {
            var c = GeminiModels.Classify(ex);
            _log.Error(c.UserMessage, c.Detail);
            Narrate?.Invoke(c.UserMessage);
        }
    }

    async Task<string> GeminiAgentLoopAsync(string userText, CancellationToken ct)
    {
        var contents = new JsonArray
        {
            new JsonObject
            {
                ["role"] = "user",
                ["parts"] = new JsonArray(new JsonObject { ["text"] = userText }),
            },
        };

        for (var step = 0; step < 8; step++)
        {
            ct.ThrowIfCancellationRequested();
            var turn = await _gemini.GenerateTurnAsync(SystemPrompt, contents, ToolDefs.Declarations, ct);

            if (turn.Calls.Count == 0)
                return string.IsNullOrWhiteSpace(turn.Text) ? "Готово." : turn.Text;

            contents.Add(new JsonObject
            {
                ["role"] = "model",
                ["parts"] = turn.ModelParts.DeepClone(),
            });

            var responseParts = new JsonArray();
            foreach (var call in turn.Calls)
            {
                _log.Info($"⚙ {call.Name}");
                object result;
                try
                {
                    result = await _tools.RunAsync(call.Name, call.Args, ct);
                }
                catch (Exception ex)
                {
                    result = new { error = ex.Message };
                }
                var payload = JsonSerializer.Serialize(result);
                if (payload.Length > 6000) payload = payload[..6000] + "…";
                responseParts.Add(new JsonObject
                {
                    ["functionResponse"] = new JsonObject
                    {
                        ["name"] = call.Name,
                        ["response"] = new JsonObject
                        {
                            ["name"] = call.Name,
                            ["content"] = payload,
                        },
                    },
                });
            }
            contents.Add(new JsonObject
            {
                ["role"] = "user",
                ["parts"] = responseParts,
            });
        }

        return "Готово (лимит шагов агента).";
    }

    async Task DriveMouthWhileSpeakAsync(string text, CancellationToken ct)
    {
        try
        {
            var vowels = text.Where(c => "аеёиоуыэюяaeiouАЕЁИОУЫЭЮЯ".Contains(c)).ToArray();
            if (vowels.Length == 0) vowels = ['а'];
            for (var i = 0; i < Math.Max(12, vowels.Length) && !ct.IsCancellationRequested; i++)
            {
                var shape = Visemes.FromChar(vowels[i % vowels.Length]);
                var (j, f, w) = Visemes.Morph(shape, 0.85f);
                Mouth?.Invoke(j, f, w);
                await Task.Delay(120, ct);
            }
            Mouth?.Invoke(0, 0, 0);
        }
        catch (OperationCanceledException) { /* */ }
    }

    async Task<string> OllamaChatAsync(LiaConfig cfg, string userText, CancellationToken ct)
    {
        using var http = new HttpClient { BaseAddress = new Uri(cfg.BaseUrl.TrimEnd('/') + "/") };
        var body = new
        {
            model = string.IsNullOrWhiteSpace(cfg.Model) ? "qwen2.5-vl" : cfg.Model,
            messages = new object[]
            {
                new { role = "system", content = SystemPrompt },
                new { role = "user", content = userText },
            },
            stream = false,
        };
        using var res = await http.PostAsJsonAsync("chat/completions", body, ct);
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()
               ?? "Готово.";
    }
}

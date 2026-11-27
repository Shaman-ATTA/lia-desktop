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
        "Когда нужно — вызывай инструменты (файлы, PowerShell, браузер, экран, мышь/клавиатура). " +
        "Если пользователь спрашивает «что на экране», «посмотри», «нажми на …» — сначала вызови look_at_screen " +
        "(или screenshot), дождись изображения, опиши экран и кликай mouse_click по координатам ЭТОГО изображения. " +
        "Помни факты из текущего разговора, пока пользователь не скажет «забудь» / «новый разговор».";

    static readonly Regex StopRe = new(@"лиа[,\s]+стоп", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex ClearMemRe = new(
        @"^\s*(забудь|новый\s+разговор|новый\s+диалог|очисти\s+память)\s*[.!]?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.IgnorePatternWhitespace);
    static readonly Regex VisionAskRe = new(
        @"что\s+на\s+экране|посмотри|нажми\s+на|что\s+написано",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    readonly GeminiClient _gemini;
    readonly SecretsStore _secrets;
    readonly AppLog _log;
    readonly ToolHost _tools;
    readonly EdgeTts _tts;
    readonly BargeInService _barge;
    readonly ConversationMemory _memory;
    CancellationTokenSource? _cts;
    string? _activeReply;
    bool _bargeHandlingSuspended;

    public event Action<string>? Narrate;
    public event Action<float, float, float>? Mouth; // jaw, funnel, wide
    public event Action<string>? SpeakFile;
    public event Action? BargeInListening; // HUD → СЛУШАЮ
    public event Action? BargeInEnded;

    public string? ActiveReply => _activeReply;
    public ConversationMemory Memory => _memory;

    public AgentService(
        GeminiClient gemini,
        SecretsStore secrets,
        AppLog log,
        ToolHost tools,
        EdgeTts tts,
        BargeInService barge,
        ConversationMemory? memory = null)
    {
        _gemini = gemini;
        _secrets = secrets;
        _log = log;
        _tools = tools;
        _tts = tts;
        _barge = barge;
        _memory = memory ?? new ConversationMemory(log);

        _barge.MouthStop += () => Mouth?.Invoke(0, 0, 0);
        _barge.Listening += () => BargeInListening?.Invoke();
        _barge.Decision += OnBargeDecision;
    }

    public void Abort()
    {
        _cts?.Cancel();
        _tts.Stop();
        _barge.AbortPlayback();
        _activeReply = null;
        _log.Info("Остановлено.");
    }

    public void ClearMemory()
    {
        _memory.Clear();
        _log.Info("Память очищена (HUD/команда).");
    }

    /// <summary>Selftest: ignore barge Decision→RunAsync so scenarios don't interleave agent turns.</summary>
    public void SuspendBargeHandling(bool suspend) => _bargeHandlingSuspended = suspend;

    public static bool IsStopPhrase(string text) => StopRe.IsMatch(text);
    public static bool IsClearMemoryPhrase(string text) => ClearMemRe.IsMatch(text?.Trim() ?? "");

    public async Task RunAsync(string userText)
    {
        if (IsStopPhrase(userText))
        {
            Abort();
            return;
        }

        if (IsClearMemoryPhrase(userText))
        {
            ClearMemory();
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            await SpeakReplyAsync("Хорошо, начинаем новый разговор.", _cts.Token);
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
            await SpeakReplyAsync(reply, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _log.Info("Отменено.");
        }
        catch (OperationCanceledException ex)
        {
            var c = GeminiModels.Classify(ex, 408);
            _log.Error(c.UserMessage, c.Detail);
            Narrate?.Invoke(c.UserMessage);
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

    async Task SpeakReplyAsync(string reply, CancellationToken ct)
    {
        _activeReply = reply;
        Narrate?.Invoke(reply);
        _log.Info(reply.Length > 160 ? reply[..160] + "…" : reply);

        _ = DriveMouthWhileSpeakAsync(reply, ct);
        var path = await _tts.SpeakToFileAsync(reply, ct);
        if (path == null || !File.Exists(path)) return;

        SpeakFile?.Invoke(path);
        // Playback + barge-in run async so RunAsync can return after TTS file is ready
        // (selftest / UI). Decision handler owns resume/stop/new-request.
        _ = MonitorPlaybackAsync(path, reply, ct);
    }

    async Task MonitorPlaybackAsync(string path, string reply, CancellationToken ct)
    {
        try
        {
            var end = await _barge.PlayFileAsync(path, reply, ct);
            if (end == BargeInService.PlayEnd.Completed)
            {
                if (_activeReply == reply) _activeReply = null;
                BargeInEnded?.Invoke();
            }
        }
        catch (Exception ex)
        {
            _log.Info("playback monitor: " + ex.Message);
            BargeInEnded?.Invoke();
        }
    }

    void OnBargeDecision(InterruptKind kind, string userText, string spoken, string remaining)
    {
        if (_bargeHandlingSuspended) return;
        _ = HandleBargeDecisionAsync(kind, userText, spoken, remaining);
    }

    async Task HandleBargeDecisionAsync(
        InterruptKind kind, string userText, string spoken, string remaining)
    {
        try
        {
            switch (kind)
            {
                case InterruptKind.Stop:
                    _log.Info("Barge-in: стоп — молчу.");
                    if (!string.IsNullOrWhiteSpace(spoken))
                        _memory.AppendSpokenTruncated(spoken);
                    _activeReply = null;
                    _cts?.Cancel();
                    BargeInEnded?.Invoke();
                    break;

                case InterruptKind.Backchannel:
                    _log.Info("Barge-in: backchannel — продолжаю.");
                    if (string.IsNullOrWhiteSpace(remaining))
                    {
                        BargeInEnded?.Invoke();
                        break;
                    }
                    // Resume remainder without a new Gemini turn
                    _cts ??= new CancellationTokenSource();
                    var ctResume = _cts.Token;
                    await SpeakReplyAsync(remaining, ctResume);
                    break;

                case InterruptKind.NewRequest:
                default:
                    _log.Info("Barge-in: новый запрос.");
                    if (!string.IsNullOrWhiteSpace(spoken))
                        _memory.AppendSpokenTruncated(spoken);
                    _activeReply = null;
                    _cts?.Cancel();
                    var ctx =
                        "Пользователь прервал ответ. " +
                        (string.IsNullOrWhiteSpace(spoken)
                            ? "Ты ещё почти ничего не сказала. "
                            : $"Ты уже сказала: «{spoken}». ") +
                        "Остаток прежнего ответа отменён. " +
                        $"Новый запрос пользователя: {userText}";
                    await RunAsync(ctx);
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.Error("Barge-in handle", ex.Message);
            BargeInEnded?.Invoke();
        }
    }

    async Task<string> GeminiAgentLoopAsync(string userText, CancellationToken ct)
    {
        _memory.AppendUserText(userText);
        var contents = _memory.Snapshot();

        for (var step = 0; step < 8; step++)
        {
            ct.ThrowIfCancellationRequested();
            var turn = await _gemini.GenerateTurnAsync(SystemPrompt, contents, ToolDefs.Declarations, ct);

            if (turn.Calls.Count == 0)
            {
                var text = string.IsNullOrWhiteSpace(turn.Text) ? "Готово." : turn.Text;
                _memory.AppendModelParts(new JsonArray(new JsonObject { ["text"] = text }));
                return text;
            }

            _memory.AppendModelParts(turn.ModelParts);

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

                ScreenShot? shot = result as ScreenShot;
                object meta = shot?.Meta() ?? result;
                var payload = JsonSerializer.Serialize(meta);
                if (payload.Length > 6000) payload = payload[..6000] + "…";

                // functionResponse-only first (Gemini requires call → response adjacency).
                // Image follows as sibling parts in the SAME user turn (no extra text-before-FR).
                var fr = new JsonObject
                {
                    ["functionResponse"] = new JsonObject
                    {
                        ["name"] = call.Name,
                        ["response"] = JsonNode.Parse(payload) is JsonObject jo
                            ? jo
                            : new JsonObject { ["content"] = payload },
                    },
                };
                responseParts.Add(fr);

                if (shot != null && !string.IsNullOrEmpty(shot.Base64))
                {
                    responseParts.Add(new JsonObject
                    {
                        ["inline_data"] = new JsonObject
                        {
                            ["mime_type"] = shot.MimeType,
                            ["data"] = shot.Base64,
                        },
                    });
                }
            }
            _memory.AppendUserParts(responseParts);
            contents = _memory.Snapshot();
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
        if (VisionAskRe.IsMatch(userText))
        {
            return "Сейчас я на Ollama без зрения — экран не вижу. Переключись на Gemini, и посмотрю.";
        }

        // Keep short local history for Ollama too (text-only)
        _memory.AppendUserText(userText);
        var hist = _memory.Snapshot();
        var messages = new List<object> { new { role = "system", content = SystemPrompt } };
        foreach (var node in hist)
        {
            if (node is not JsonObject obj) continue;
            var role = obj["role"]?.GetValue<string>() ?? "user";
            var text = "";
            if (obj["parts"] is JsonArray parts)
            {
                foreach (var p in parts)
                {
                    if (p is JsonObject po && po["text"] is JsonValue tv)
                        text += tv.GetValue<string>() + " ";
                }
            }
            text = text.Trim();
            if (string.IsNullOrWhiteSpace(text)) continue;
            messages.Add(new { role = role == "model" ? "assistant" : "user", content = text });
        }

        using var http = new HttpClient { BaseAddress = new Uri(cfg.BaseUrl.TrimEnd('/') + "/") };
        var body = new
        {
            model = string.IsNullOrWhiteSpace(cfg.Model) ? "qwen2.5-vl" : cfg.Model,
            messages,
            stream = false,
        };
        using var res = await http.PostAsJsonAsync("chat/completions", body, ct);
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        var reply = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()
               ?? "Готово.";
        _memory.AppendModelParts(new JsonArray(new JsonObject { ["text"] = reply }));
        return reply;
    }
}

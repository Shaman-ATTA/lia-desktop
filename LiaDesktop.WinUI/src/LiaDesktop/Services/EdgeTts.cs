using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using Windows.Media.SpeechSynthesis;
using WinSpeech = Windows.Media.SpeechSynthesis.SpeechSynthesizer;
using SysSpeech = System.Speech.Synthesis.SpeechSynthesizer;

namespace LiaDesktop.Services;

/// <summary>
/// Edge neural TTS (ru-RU-SvetlanaNeural @ -4%) via C# WebSocket + Sec-MS-GEC v2.
/// No Python. Offline fallback: Windows SAPI / OneCore (prefer ru-RU).
/// </summary>
public sealed class EdgeTts
{
    public const string Voice = "ru-RU-SvetlanaNeural";
    public const string Rate = "-4%";

    /// <summary>When true, skip Edge WebSocket (selftest SAPI path / forced offline).</summary>
    public static bool DisableWebSocket { get; set; }

    /// <summary>When true, skip SAPI fallback (selftest tts_ws_only).</summary>
    public static bool DisableSapi { get; set; }

    readonly AppLog _log;
    static readonly HttpClient DateProbe = new() { Timeout = TimeSpan.FromSeconds(8) };

    public EdgeTts(AppLog log) => _log = log;

    public void Stop() { /* WS/SAPI are per-call; nothing long-lived to kill */ }

    public async Task<string?> SpeakToFileAsync(string text, CancellationToken ct = default)
    {
        text = Regex.Replace(text, @"\s+", " ").Trim();
        if (string.IsNullOrEmpty(text)) return null;

        Exception? last = null;
        if (!DisableWebSocket)
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                var outPath = Path.Combine(Path.GetTempPath(), $"lia-tts-{Guid.NewGuid():N}.mp3");
                try
                {
                    await ViaWebSocketAsync(text, outPath, ct);
                    if (TtsAudioGate.TryValidate(outPath, out _, out _, out _))
                        return outPath;
                    try { if (File.Exists(outPath)) File.Delete(outPath); } catch { /* */ }
                }
                catch (Exception ex)
                {
                    last = ex;
                    _log.Info($"edge-tts ws try{attempt + 1}: {OneLine(ex.Message)}");
                    AppLog.WriteCrash("edge-tts-ws", ex);
                    try { if (File.Exists(outPath)) File.Delete(outPath); } catch { /* */ }

                    if (IsForbidden(ex))
                    {
                        await TryAdjustClockSkewAsync(ct);
                    }
                    await Task.Delay(350 * (attempt + 1), ct);
                }
            }
        }

        if (!DisableSapi)
        {
            try
            {
                var wav = await ViaSapiAsync(text, ct);
                if (wav != null && TtsAudioGate.TryValidate(wav, out _, out _, out var sapiOk))
                {
                    _log.Info($"TTS: SAPI/OneCore офлайн ({sapiOk})");
                    return wav;
                }
            }
            catch (Exception ex)
            {
                last = ex;
                _log.Info($"TTS SAPI: {OneLine(ex.Message)}");
                AppLog.WriteCrash("tts-sapi", ex);
            }
        }

        _log.Error("TTS недоступен", OneLine(last?.Message ?? "no backend"));
        if (last != null) AppLog.WriteCrash("tts-final", last);
        return null;
    }

    /// <summary>WebSocket-only path for CI gate (no SAPI).</summary>
    public async Task<string?> SpeakWebSocketOnlyAsync(string text, CancellationToken ct = default)
    {
        var prevWs = DisableWebSocket;
        var prevSapi = DisableSapi;
        DisableWebSocket = false;
        DisableSapi = true;
        try { return await SpeakToFileAsync(text, ct); }
        finally
        {
            DisableWebSocket = prevWs;
            DisableSapi = prevSapi;
        }
    }

    /// <summary>SAPI-only path for CI gate.</summary>
    public async Task<string?> SpeakSapiOnlyAsync(string text, CancellationToken ct = default)
    {
        var prevWs = DisableWebSocket;
        var prevSapi = DisableSapi;
        DisableWebSocket = true;
        DisableSapi = false;
        try { return await SpeakToFileAsync(text, ct); }
        finally
        {
            DisableWebSocket = prevWs;
            DisableSapi = prevSapi;
        }
    }

    async Task ViaWebSocketAsync(string text, string outPath, CancellationToken ct)
    {
        var connId = Guid.NewGuid().ToString("N");
        var url = EdgeMsDrm.BuildWssUrl(connId);
        using var ws = new ClientWebSocket();
        // Cookie via container — SetRequestHeader("Cookie") is rejected by ClientWebSocket
        ws.Options.Cookies = new CookieContainer();
        ws.Options.Cookies.Add(
            new Uri("https://speech.platform.bing.com"),
            new Cookie("muid", EdgeMsDrm.GenerateMuid()));

        foreach (var (k, v) in EdgeMsDrm.WssHeadersWithoutCookie())
        {
            // Sec-WebSocket-* are set by the runtime; Accept-Encoding can break the upgrade
            if (k.StartsWith("Sec-WebSocket", StringComparison.OrdinalIgnoreCase)) continue;
            if (k.Equals("Accept-Encoding", StringComparison.OrdinalIgnoreCase)) continue;
            try { ws.Options.SetRequestHeader(k, v); }
            catch { /* some headers are restricted */ }
        }

        using var link = CancellationTokenSource.CreateLinkedTokenSource(ct);
        link.CancelAfter(TimeSpan.FromSeconds(45));
        await ws.ConnectAsync(new Uri(url), link.Token);

        var config =
            "Content-Type:application/json; charset=utf-8\r\nPath:speech.config\r\n\r\n" +
            "{\"context\":{\"synthesis\":{\"audio\":{\"metadataoptions\":{\"sentenceBoundaryEnabled\":\"false\",\"wordBoundaryEnabled\":\"true\"},\"outputFormat\":\"audio-24khz-48kbitrate-mono-mp3\"}}}}\r\n";
        await ws.SendAsync(Encoding.UTF8.GetBytes(config), WebSocketMessageType.Text, true, link.Token);

        var ssml =
            $"Content-Type:application/ssml+xml\r\nX-RequestId:{Guid.NewGuid():N}\r\nPath:ssml\r\n\r\n" +
            $"<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='ru-RU'>" +
            $"<voice name='{Voice}'><prosody rate='{Rate}'>{System.Security.SecurityElement.Escape(text)}</prosody></voice></speak>";
        await ws.SendAsync(Encoding.UTF8.GetBytes(ssml), WebSocketMessageType.Text, true, link.Token);

        await using var fs = File.Create(outPath);
        var buf = new byte[256 * 1024];
        while (ws.State == WebSocketState.Open)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(buf, link.Token);
                if (result.MessageType == WebSocketMessageType.Close) break;
                ms.Write(buf, 0, result.Count);
            } while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Close) break;
            var msg = ms.ToArray();
            if (result.MessageType == WebSocketMessageType.Binary)
            {
                if (msg.Length < 2) continue;
                var headerLen = (msg[0] << 8) | msg[1];
                var dataStart = 2 + headerLen;
                if (msg.Length > dataStart)
                    await fs.WriteAsync(msg.AsMemory(dataStart), link.Token);
            }
            else
            {
                var s = Encoding.UTF8.GetString(msg);
                if (s.Contains("Path:turn.end", StringComparison.Ordinal)) break;
            }
        }
        if (fs.Length == 0) throw new InvalidOperationException("Edge TTS returned empty audio");
    }

    async Task<string?> ViaSapiAsync(string text, CancellationToken ct)
    {
        // Prefer classic System.Speech → real PCM WAV (WinRT path often wrote header-only 46B files on GHA).
        try
        {
            var path = await Task.Run(() => SpeakSystemSpeechToWav(text), ct);
            if (TtsAudioGate.TryValidate(path, out _, out _, out var ok))
            {
                _log.Info($"TTS: System.Speech ({ok})");
                return path;
            }
            _log.Info($"TTS System.Speech rejected: {ok}");
            try { if (path != null) File.Delete(path); } catch { /* */ }
        }
        catch (Exception ex)
        {
            _log.Info($"TTS System.Speech: {OneLine(ex.Message)}");
        }

        ct.ThrowIfCancellationRequested();
        return await SpeakWinRtToWavAsync(text, ct);
    }

    static string SpeakSystemSpeechToWav(string text)
    {
        using var synth = new SysSpeech();
        var installed = synth.GetInstalledVoices().Where(v => v.Enabled).ToList();
        if (installed.Count == 0)
            throw new InvalidOperationException("no System.Speech voices installed");

        var pick =
            installed.FirstOrDefault(v =>
                v.VoiceInfo.Culture.Name.StartsWith("ru", StringComparison.OrdinalIgnoreCase) &&
                (v.VoiceInfo.Name.Contains("Irina", StringComparison.OrdinalIgnoreCase)
                 || v.VoiceInfo.Name.Contains("Ирина", StringComparison.OrdinalIgnoreCase)))
            ?? installed.FirstOrDefault(v =>
                v.VoiceInfo.Culture.Name.StartsWith("ru", StringComparison.OrdinalIgnoreCase)
                && (v.VoiceInfo.Name.Contains("Pavel", StringComparison.OrdinalIgnoreCase)
                    || v.VoiceInfo.Name.Contains("Павел", StringComparison.OrdinalIgnoreCase)))
            ?? installed.FirstOrDefault(v =>
                v.VoiceInfo.Culture.Name.StartsWith("ru", StringComparison.OrdinalIgnoreCase))
            ?? installed.FirstOrDefault(v => v.VoiceInfo.Gender == System.Speech.Synthesis.VoiceGender.Female)
            ?? installed[0];

        synth.SelectVoice(pick.VoiceInfo.Name);
        try { synth.Rate = -1; } catch { /* */ }

        var outPath = Path.Combine(Path.GetTempPath(), $"lia-tts-sapi-{Guid.NewGuid():N}.wav");
        synth.SetOutputToWaveFile(outPath);
        try { synth.Speak(text); }
        finally { try { synth.SetOutputToNull(); } catch { /* */ } }

        if (!File.Exists(outPath))
            throw new InvalidOperationException("System.Speech produced no file");
        return outPath;
    }

    async Task<string?> SpeakWinRtToWavAsync(string text, CancellationToken ct)
    {
        using var synth = new WinSpeech();
        try
        {
            var voices = WinSpeech.AllVoices;
            var ru = voices.FirstOrDefault(v =>
                v.Language.StartsWith("ru", StringComparison.OrdinalIgnoreCase) &&
                (v.DisplayName.Contains("Irina", StringComparison.OrdinalIgnoreCase)
                 || v.DisplayName.Contains("Pavel", StringComparison.OrdinalIgnoreCase)
                 || v.DisplayName.Contains("Ирина", StringComparison.OrdinalIgnoreCase)));
            ru ??= voices.FirstOrDefault(v => v.Language.StartsWith("ru", StringComparison.OrdinalIgnoreCase));
            ru ??= voices.FirstOrDefault();
            if (ru != null) synth.Voice = ru;
        }
        catch { /* keep default */ }

        ct.ThrowIfCancellationRequested();
        var stream = await synth.SynthesizeTextToStreamAsync(text);
        var outPath = Path.Combine(Path.GetTempPath(), $"lia-tts-winrt-{Guid.NewGuid():N}.wav");
        try
        {
            // Copy full stream (not stream.Size via DataReader — that produced 46B header-only WAVs)
            await using var input = stream.AsStreamForRead();
            await using var fs = File.Create(outPath);
            await input.CopyToAsync(fs, ct);
            await fs.FlushAsync(ct);
        }
        finally
        {
            stream.Dispose();
        }

        if (!TtsAudioGate.TryValidate(outPath, out _, out _, out var detail))
        {
            try { File.Delete(outPath); } catch { /* */ }
            throw new InvalidOperationException("WinRT SpeechSynthesis inaudible: " + detail);
        }
        _log.Info($"TTS: WinRT SpeechSynthesis ({detail})");
        return outPath;
    }

    static bool IsForbidden(Exception ex)
    {
        var m = ex.Message ?? "";
        return m.Contains("403", StringComparison.Ordinal)
               || m.Contains("'101'", StringComparison.Ordinal);
    }

    static async Task TryAdjustClockSkewAsync(CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://speech.platform.bing.com/");
            req.Headers.TryAddWithoutValidation("User-Agent", EdgeTtsConstants.UserAgent);
            using var res = await DateProbe.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            string? date = null;
            if (res.Headers.Date.HasValue)
                date = res.Headers.Date.Value.UtcDateTime.ToString("r");
            else if (res.Headers.TryGetValues("Date", out var vals))
                date = vals.FirstOrDefault();
            EdgeMsDrm.AdjustClockSkewFromServerDate(date);
        }
        catch
        {
            // Best-effort — next attempt still regenerates a fresh token
        }
    }

    static string OneLine(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var t = Regex.Replace(s, @"\s+", " ").Trim();
        return t.Length <= 160 ? t : t[..160] + "…";
    }
}

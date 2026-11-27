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

    /// <summary>
    /// Selftest: skip real Edge WS — write audible audio then throw abrupt-close exception
    /// (must still succeed via handshake-tolerant path).
    /// </summary>
    public static bool SelfTestOfflineAbruptClose { get; set; }

    /// <summary>Last successful synthesis backend: edge-ws | sapi | none.</summary>
    public string LastPathChosen { get; private set; } = "none";

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
                    {
                        LastPathChosen = "edge-ws";
                        _log.Info($"tts_path_chosen=edge-ws file={Path.GetFileName(outPath)}");
                        return outPath;
                    }
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
                    LastPathChosen = "sapi";
                    _log.Info($"tts_path_chosen=sapi ({sapiOk})");
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

        LastPathChosen = "none";
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

    /// <summary>SAPI-only path for CI gate — throws with detail if all offline backends fail.</summary>
    public async Task<string> SpeakSapiOnlyAsync(string text, CancellationToken ct = default)
    {
        var path = await ViaSapiAsync(text, ct);
        if (!TtsAudioGate.TryValidate(path, out _, out _, out var detail))
            throw new InvalidOperationException("SAPI-only produced no audible audio: " + detail);
        return path!;
    }

    async Task ViaWebSocketAsync(string text, string outPath, CancellationToken ct)
    {
        if (SelfTestOfflineAbruptClose)
        {
            // Seed audible bytes (SAPI wav), then simulate remote drop without close handshake
            var seed = await ViaSapiAsync(text, ct)
                ?? throw new InvalidOperationException("selftest abrupt-close: no seed audio");
            await File.WriteAllBytesAsync(outPath, await File.ReadAllBytesAsync(seed, ct), ct);
            try
            {
                throw new WebSocketException(
                    "The remote party closed the WebSocket connection without completing the close handshake.");
            }
            catch (Exception ex) when (IsAbruptWsClose(ex))
            {
                if (new FileInfo(outPath).Length > 0)
                {
                    _log.Info($"edge-tts: abrupt WS close after {new FileInfo(outPath).Length}B audio — treating as OK");
                    return;
                }
                throw;
            }
        }

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
        var sawTurnEnd = false;
        try
        {
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
                    if (s.Contains("Path:turn.end", StringComparison.Ordinal))
                    {
                        sawTurnEnd = true;
                        break;
                    }
                }
            }
        }
        catch (Exception ex) when (IsAbruptWsClose(ex))
        {
            // Remote closed without close handshake — if we already have audio (or turn.end), SUCCESS
            if (fs.Length > 0 || sawTurnEnd)
            {
                _log.Info($"edge-tts: abrupt WS close after {fs.Length}B audio — treating as OK");
                await fs.FlushAsync(link.Token);
                if (fs.Length > 0) return;
            }
            throw;
        }
        if (fs.Length == 0) throw new InvalidOperationException("Edge TTS returned empty audio");
    }

    static bool IsAbruptWsClose(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException!)
        {
            var m = e.Message ?? "";
            if (m.Contains("without completing the close handshake", StringComparison.OrdinalIgnoreCase))
                return true;
            // Connection reset mid-stream after audio — common Edge quirk
            if (e is WebSocketException &&
                (m.Contains("connection", StringComparison.OrdinalIgnoreCase) ||
                 m.Contains("closed", StringComparison.OrdinalIgnoreCase) ||
                 m.Contains("aborted", StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        return false;
    }

    async Task<string?> ViaSapiAsync(string text, CancellationToken ct)
    {
        // Offline stack (in order). System.Speech/COM need STA. WinRT Size-only reads
        // previously produced 46B header-only WAVs — we drain the stream in chunks.
        var errors = new List<string>();

        try
        {
            var path = await RunStaAsync(() => SpeakSystemSpeechToWav(text), ct);
            if (TtsAudioGate.TryValidate(path, out _, out _, out var ok))
            {
                _log.Info($"TTS: System.Speech ({ok})");
                return path;
            }
            errors.Add("System.Speech: " + ok);
            try { File.Delete(path); } catch { /* */ }
        }
        catch (Exception ex)
        {
            errors.Add("System.Speech: " + OneLine(ex.Message));
            AppLog.WriteCrash("tts-system-speech", ex);
        }

        try
        {
            var path = await RunStaAsync(() => SpeakComSapiToWav(text), ct);
            if (TtsAudioGate.TryValidate(path, out _, out _, out var ok))
            {
                _log.Info($"TTS: COM SAPI.SpVoice ({ok})");
                return path;
            }
            errors.Add("COM SAPI: " + ok);
            try { File.Delete(path); } catch { /* */ }
        }
        catch (Exception ex)
        {
            errors.Add("COM SAPI: " + OneLine(ex.Message));
            AppLog.WriteCrash("tts-com-sapi", ex);
        }

        try
        {
            ct.ThrowIfCancellationRequested();
            return await SpeakWinRtToWavAsync(text, ct);
        }
        catch (Exception ex)
        {
            errors.Add("WinRT: " + OneLine(ex.Message));
            AppLog.WriteCrash("tts-winrt", ex);
            _log.Error("TTS SAPI unavailable", string.Join(" | ", errors));
            return null;
        }
    }

    static Task<string> RunStaAsync(Func<string> work, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var th = new Thread(() =>
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                tcs.TrySetResult(work());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        th.IsBackground = true;
        th.SetApartmentState(ApartmentState.STA);
        th.Name = "lia-sapi-sta";
        th.Start();
        return tcs.Task.WaitAsync(ct);
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
        using (var ms = new MemoryStream())
        {
            synth.SetOutputToWaveStream(ms);
            try { synth.Speak(text); }
            finally { try { synth.SetOutputToNull(); } catch { /* */ } }
            if (ms.Length < TtsAudioGate.MinBytes)
                throw new InvalidOperationException(
                    $"System.Speech wave too small ({ms.Length}B) voice={pick.VoiceInfo.Name} "
                    + $"culture={pick.VoiceInfo.Culture.Name} voices=[{string.Join(", ", installed.Select(v => v.VoiceInfo.Name))}]");
            File.WriteAllBytes(outPath, ms.ToArray());
        }
        return outPath;
    }

    static string SpeakComSapiToWav(string text)
    {
        var voiceType = Type.GetTypeFromProgID("SAPI.SpVoice")
            ?? throw new InvalidOperationException("SAPI.SpVoice ProgID missing");
        var fileType = Type.GetTypeFromProgID("SAPI.SpFileStream")
            ?? throw new InvalidOperationException("SAPI.SpFileStream ProgID missing");
        dynamic voice = Activator.CreateInstance(voiceType)
            ?? throw new InvalidOperationException("SAPI.SpVoice create failed");
        dynamic fs = Activator.CreateInstance(fileType)
            ?? throw new InvalidOperationException("SAPI.SpFileStream create failed");

        var outPath = Path.Combine(Path.GetTempPath(), $"lia-tts-com-{Guid.NewGuid():N}.wav");
        // SSFMCreateForWrite = 3
        fs.Open(outPath, 3);
        try
        {
            voice.AudioOutputStream = fs;
            // Prefer ru-RU token if present
            try
            {
                dynamic tokens = voice.GetVoices();
                var count = (int)tokens.Count;
                for (var i = 0; i < count; i++)
                {
                    dynamic tok = tokens.Item(i);
                    string desc = tok.GetDescription();
                    if (desc.Contains("Irina", StringComparison.OrdinalIgnoreCase)
                        || desc.Contains("Russian", StringComparison.OrdinalIgnoreCase)
                        || desc.Contains("ru-", StringComparison.OrdinalIgnoreCase))
                    {
                        voice.Voice = tok;
                        break;
                    }
                }
            }
            catch { /* default voice */ }

            voice.Speak(text, 0); // SVSFDefault = sync
        }
        finally
        {
            try { fs.Close(); } catch { /* */ }
        }

        if (!File.Exists(outPath) || new FileInfo(outPath).Length < TtsAudioGate.MinBytes)
            throw new InvalidOperationException(
                $"COM SAPI wave too small ({(File.Exists(outPath) ? new FileInfo(outPath).Length : 0)}B)");
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
            // Drain the full IRandomAccessStream in chunks (don't trust stream.Size alone)
            await using var fs = File.Create(outPath);
            using var input = stream.GetInputStreamAt(0);
            using var reader = new Windows.Storage.Streams.DataReader(input);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var n = await reader.LoadAsync(64 * 1024);
                if (n == 0) break;
                var chunk = new byte[n];
                reader.ReadBytes(chunk);
                await fs.WriteAsync(chunk, ct);
            }
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

using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;

namespace LiaDesktop.Services;

/// <summary>
/// Free Edge neural TTS (ru-RU-SvetlanaNeural @ -4%).
/// Prefers Python edge-tts under the Job Object; falls back to Edge websocket protocol.
/// </summary>
public sealed class EdgeTts
{
    public const string Voice = "ru-RU-SvetlanaNeural";
    public const string Rate = "-4%";

    readonly AppLog _log;
    Process? _active;

    public EdgeTts(AppLog log) => _log = log;

    public void Stop()
    {
        try { _active?.Kill(entireProcessTree: true); } catch { /* */ }
        _active = null;
    }

    public async Task<string?> SpeakToFileAsync(string text, CancellationToken ct = default)
    {
        text = Regex.Replace(text, @"\s+", " ").Trim();
        if (string.IsNullOrEmpty(text)) return null;
        var outPath = Path.Combine(Path.GetTempPath(), $"lia-tts-{Guid.NewGuid():N}.mp3");
        try
        {
            await ViaPythonAsync(text, outPath, ct);
            return outPath;
        }
        catch (Exception ex)
        {
            _log.Info($"edge-tts python: {ex.Message}");
            try
            {
                await ViaWebSocketAsync(text, outPath, ct);
                return outPath;
            }
            catch (Exception ex2)
            {
                _log.Error("TTS недоступен", ex2.Message);
                return null;
            }
        }
    }

    async Task ViaPythonAsync(string text, string outPath, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "python",
            Arguments = $"-m edge_tts --voice {Voice} --rate={Rate} --write-media \"{outPath}\" --text \"{text.Replace("\"", "\\\"")}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        _active = Process.Start(psi) ?? throw new InvalidOperationException("python start failed");
        await _active.WaitForExitAsync(ct);
        _active = null;
        if (!File.Exists(outPath) || new FileInfo(outPath).Length == 0)
            throw new InvalidOperationException("edge-tts produced empty file");
    }

    async Task ViaWebSocketAsync(string text, string outPath, CancellationToken ct)
    {
        // Minimal Edge TTS websocket client
        var connId = Guid.NewGuid().ToString("N");
        var url =
            $"wss://speech.platform.bing.com/consumer/speech/synthesize/readaloud/edge/v1?TrustedClientToken=6A5AA1D4EAFF4E9FB37E23D68491D6F4&ConnectionId={connId}";
        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36 Edg/120.0.0.0");
        await ws.ConnectAsync(new Uri(url), ct);

        var config =
            "Content-Type:application/json; charset=utf-8\r\nPath:speech.config\r\n\r\n" +
            "{\"context\":{\"synthesis\":{\"audio\":{\"metadataoptions\":{\"sentenceBoundaryEnabled\":\"false\",\"wordBoundaryEnabled\":\"true\"},\"outputFormat\":\"audio-24khz-48kbitrate-mono-mp3\"}}}}\r\n";
        await ws.SendAsync(Encoding.UTF8.GetBytes(config), WebSocketMessageType.Text, true, ct);

        var ssml =
            $"Content-Type:application/ssml+xml\r\nX-RequestId:{Guid.NewGuid():N}\r\nPath:ssml\r\n\r\n" +
            $"<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='ru-RU'>" +
            $"<voice name='{Voice}'><prosody rate='{Rate}'>{System.Security.SecurityElement.Escape(text)}</prosody></voice></speak>";
        await ws.SendAsync(Encoding.UTF8.GetBytes(ssml), WebSocketMessageType.Text, true, ct);

        await using var fs = File.Create(outPath);
        var buf = new byte[256 * 1024];
        while (ws.State == WebSocketState.Open)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(buf, ct);
                if (result.MessageType == WebSocketMessageType.Close) break;
                ms.Write(buf, 0, result.Count);
            } while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Close) break;
            var msg = ms.ToArray();
            if (result.MessageType == WebSocketMessageType.Binary)
            {
                // Edge protocol: uint16 BE header length + header + audio payload
                if (msg.Length < 2) continue;
                var headerLen = (msg[0] << 8) | msg[1];
                var dataStart = 2 + headerLen;
                if (msg.Length > dataStart)
                    await fs.WriteAsync(msg.AsMemory(dataStart), ct);
            }
            else
            {
                var s = Encoding.UTF8.GetString(msg);
                if (s.Contains("Path:turn.end", StringComparison.Ordinal)) break;
            }
        }
        if (fs.Length == 0) throw new InvalidOperationException("Edge TTS returned empty audio");
    }
}


using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;

namespace LiaDesktop.Services;

/// <summary>
/// Free Edge neural TTS (ru-RU-SvetlanaNeural @ -4%).
/// Prefers Edge websocket (reliable UTF-8); falls back to Python edge-tts via a UTF-8 script file.
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

        Exception? last = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var outPath = Path.Combine(Path.GetTempPath(), $"lia-tts-{Guid.NewGuid():N}.mp3");
            try
            {
                // Websocket first — avoids Windows console encoding breakage on Cyrillic
                await ViaWebSocketAsync(text, outPath, ct);
                if (File.Exists(outPath) && new FileInfo(outPath).Length > 0)
                    return outPath;
            }
            catch (Exception ex)
            {
                last = ex;
                _log.Info($"edge-tts ws try{attempt + 1}: {ex.Message}");
                try { if (File.Exists(outPath)) File.Delete(outPath); } catch { /* */ }
            }

            try
            {
                outPath = Path.Combine(Path.GetTempPath(), $"lia-tts-{Guid.NewGuid():N}.mp3");
                await ViaPythonAsync(text, outPath, ct);
                if (File.Exists(outPath) && new FileInfo(outPath).Length > 0)
                    return outPath;
            }
            catch (Exception ex)
            {
                last = ex;
                _log.Info($"edge-tts py try{attempt + 1}: {ex.Message}");
                try { if (File.Exists(outPath)) File.Delete(outPath); } catch { /* */ }
            }

            await Task.Delay(400 * (attempt + 1), ct);
        }

        _log.Error("TTS недоступен", last?.Message);
        return null;
    }

    async Task ViaPythonAsync(string text, string outPath, CancellationToken ct)
    {
        // Avoid argv encoding issues: write a tiny UTF-8 runner script
        var script = Path.Combine(Path.GetTempPath(), $"lia-edge-tts-{Guid.NewGuid():N}.py");
        var textPath = Path.Combine(Path.GetTempPath(), $"lia-tts-in-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(textPath, text, new UTF8Encoding(false), ct);
        var py =
            "import asyncio, edge_tts, pathlib, sys\n"
            + f"text = pathlib.Path(r'''{textPath}''').read_text(encoding='utf-8')\n"
            + f"out = r'''{outPath}'''\n"
            + f"async def main():\n"
            + f"    await edge_tts.Communicate(text, '{Voice}', rate='{Rate}').save(out)\n"
            + "asyncio.run(main())\n";
        await File.WriteAllTextAsync(script, py, new UTF8Encoding(false), ct);
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "python",
                Arguments = $"\"{script}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            _active = Process.Start(psi) ?? throw new InvalidOperationException("python start failed");
            var err = await _active.StandardError.ReadToEndAsync(ct);
            await _active.WaitForExitAsync(ct);
            var code = _active.ExitCode;
            _active = null;
            if (!File.Exists(outPath) || new FileInfo(outPath).Length == 0)
                throw new InvalidOperationException($"edge-tts empty (exit {code}): {err}");
        }
        finally
        {
            try { File.Delete(script); } catch { /* */ }
            try { File.Delete(textPath); } catch { /* */ }
        }
    }

    async Task ViaWebSocketAsync(string text, string outPath, CancellationToken ct)
    {
        var connId = Guid.NewGuid().ToString("N");
        var url =
            $"wss://speech.platform.bing.com/consumer/speech/synthesize/readaloud/edge/v1?TrustedClientToken=6A5AA1D4EAFF4E9FB37E23D68491D6F4&ConnectionId={connId}";
        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36 Edg/120.0.0.0");
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
}

using System.Collections.Concurrent;
using NAudio.Wave;

namespace LiaDesktop.Services;

/// <summary>
/// Default-mic capture (WaveInEvent) + energy VAD → Gemini inline-audio transcription (ru).
/// Used when Windows.Media.SpeechRecognition (ru-RU) is not installed.
/// Injectable capture (selftest): set <see cref="UseInjectableCapture"/> and feed via <see cref="PushCapturePcm"/>.
/// No-speech hard timeout → <see cref="TimedOut"/> (never hang forever).
/// </summary>
public sealed class MicGeminiListen : IDisposable
{
    public const int NoSpeechTimeoutMs = 8000;

    readonly GeminiClient _gemini;
    readonly AppLog _log;
    readonly ConcurrentQueue<byte[]> _chunks = new();
    WaveInEvent? _capture;
    WaveFormat? _format;
    CancellationTokenSource? _session;
    Task? _pipeline;
    bool _armed;

    public bool IsListening { get; private set; }

    /// <summary>When true, StartAsync skips WaveIn — feed PCM via PushCapturePcm / MockCapturePull.</summary>
    public bool UseInjectableCapture { get; set; }

    /// <summary>Optional pull (100ms cadence) when UseInjectableCapture; null = wait for PushCapturePcm.</summary>
    public Func<byte[]?>? MockCapturePull { get; set; }

    public event Action<string>? FinalResult;

    /// <summary>No speech within <see cref="NoSpeechTimeoutMs"/> — caller should return to ОЖИДАНИЕ.</summary>
    public event Action? TimedOut;

    public MicGeminiListen(GeminiClient gemini, AppLog log)
    {
        _gemini = gemini;
        _log = log;
    }

    public async Task StartAsync()
    {
        if (IsListening) return;
        _session?.Cancel();
        _session = new CancellationTokenSource();
        var ct = _session.Token;

        while (_chunks.TryDequeue(out _)) { }

        _format = new WaveFormat(16000, 16, 1);
        IsListening = true;
        _armed = true;

        if (UseInjectableCapture || MockCapturePull != null)
        {
            _log.Info("Микрофон: Gemini STT (injectable capture)…");
            _pipeline = Task.Run(() => PipelineAsync(ct), ct);
            if (MockCapturePull != null)
                _ = Task.Run(() => PullLoopAsync(ct), ct);
            await Task.CompletedTask;
            return;
        }

        // WaveInEvent is in core NAudio.Wave (WasapiCapture needs NAudio.Wasapi pack).
        _capture = new WaveInEvent
        {
            WaveFormat = _format,
            BufferMilliseconds = 50,
        };
        _capture.DataAvailable += OnData;
        _capture.RecordingStopped += (_, _) => { };
        _capture.StartRecording();
        _log.Info("Микрофон: Gemini STT (WaveIn)…");
        _pipeline = Task.Run(() => PipelineAsync(ct), ct);
        await Task.CompletedTask;
    }

    /// <summary>Inject 16 kHz mono PCM16 into the active session (selftest fake mic).</summary>
    public void PushCapturePcm(byte[] pcm16le, int bytes = -1)
    {
        if (!_armed) return;
        var n = bytes < 0 ? pcm16le.Length : bytes;
        if (n <= 0) return;
        var buf = new byte[n];
        Buffer.BlockCopy(pcm16le, 0, buf, 0, n);
        _chunks.Enqueue(buf);
    }

    public async Task StopAsync()
    {
        _armed = false;
        try { _capture?.StopRecording(); } catch { /* */ }
        _session?.Cancel();
        if (_pipeline != null)
        {
            try { await _pipeline; } catch { /* */ }
        }
        CleanupCapture();
        IsListening = false;
    }

    void OnData(object? sender, WaveInEventArgs e)
    {
        if (!_armed || e.BytesRecorded <= 0) return;
        var buf = new byte[e.BytesRecorded];
        Buffer.BlockCopy(e.Buffer, 0, buf, 0, e.BytesRecorded);
        _chunks.Enqueue(buf);
    }

    async Task PullLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _armed)
        {
            try
            {
                var chunk = MockCapturePull?.Invoke();
                if (chunk is { Length: > 0 })
                    _chunks.Enqueue(chunk);
                await Task.Delay(50, ct);
            }
            catch (OperationCanceledException) { break; }
            catch { /* */ }
        }
    }

    async Task PipelineAsync(CancellationToken ct)
    {
        var pcm = new MemoryStream();
        var speechStarted = false;
        var silenceMs = 0;
        var speechMs = 0;
        const int maxMs = 12000;
        const int silenceEndMs = 900;
        const int minSpeechMs = 350;
        var frameMsEst = 20;
        var timedOut = false;

        try
        {
            var idleMs = 0;
            while (!ct.IsCancellationRequested && _armed)
            {
                if (!_chunks.TryDequeue(out var chunk))
                {
                    await Task.Delay(30, ct);
                    idleMs += 30;
                    if (!speechStarted && idleMs >= NoSpeechTimeoutMs)
                    {
                        // HARD timeout — never hang waiting for speech
                        _log.Info($"Микрофон: таймаут {NoSpeechTimeoutMs}ms без речи — возврат в ОЖИДАНИЕ.");
                        timedOut = true;
                        break;
                    }
                    if (speechStarted)
                    {
                        silenceMs += 30;
                        if (silenceMs >= silenceEndMs && speechMs >= minSpeechMs)
                            break;
                    }
                    continue;
                }

                var rms = Rms16(_format!, chunk);
                frameMsEst = Math.Max(10, chunk.Length * 1000 / Math.Max(1, _format!.AverageBytesPerSecond));

                if (!speechStarted)
                {
                    if (rms > 0.02)
                    {
                        speechStarted = true;
                        idleMs = 0;
                        silenceMs = 0;
                        pcm.Write(chunk);
                        speechMs += frameMsEst;
                    }
                    else
                    {
                        // Quiet pre-roll still counts toward no-speech timeout
                        idleMs += frameMsEst;
                        if (idleMs >= NoSpeechTimeoutMs)
                        {
                            _log.Info($"Микрофон: таймаут {NoSpeechTimeoutMs}ms без речи — возврат в ОЖИДАНИЕ.");
                            timedOut = true;
                            break;
                        }
                    }
                    continue;
                }

                idleMs = 0;
                pcm.Write(chunk);
                speechMs += frameMsEst;
                if (rms < 0.012) silenceMs += frameMsEst;
                else silenceMs = 0;

                if (silenceMs >= silenceEndMs && speechMs >= minSpeechMs) break;
                if (speechMs >= maxMs) break;
            }

            if (timedOut)
            {
                TimedOut?.Invoke();
                return;
            }

            if (pcm.Length < 1000)
            {
                _log.Info("Микрофон: нет речи — возврат в ОЖИДАНИЕ.");
                TimedOut?.Invoke();
                return;
            }

            var wav = ToWav(pcm.ToArray(), _format!);
            _log.Info("Микрофон: распознаю через Gemini…");
            var text = await _gemini.TranscribeRuAsync(wav, ct);
            if (!string.IsNullOrWhiteSpace(text))
                FinalResult?.Invoke(text.Trim());
            else
                _log.Info("Микрофон: пустая расшифровка.");
        }
        catch (OperationCanceledException) { /* stop */ }
        catch (Exception ex)
        {
            var c = GeminiModels.Classify(ex);
            _log.Error($"Микрофон STT: {c.UserMessage}", c.Detail);
        }
        finally
        {
            CleanupCapture();
            IsListening = false;
            _armed = false;
        }
    }

    void CleanupCapture()
    {
        if (_capture != null)
        {
            try { _capture.DataAvailable -= OnData; } catch { /* */ }
            try { _capture.Dispose(); } catch { /* */ }
            _capture = null;
        }
    }

    static float Rms16(WaveFormat fmt, byte[] chunk)
    {
        if (fmt.BitsPerSample != 16) return 0.05f;
        var n = chunk.Length / 2;
        if (n <= 0) return 0;
        double sum = 0;
        for (var i = 0; i + 1 < chunk.Length; i += 2)
        {
            short s = (short)(chunk[i] | (chunk[i + 1] << 8));
            var f = s / 32768.0;
            sum += f * f;
        }
        return (float)Math.Sqrt(sum / n);
    }

    static byte[] ToWav(byte[] pcm, WaveFormat fmt)
    {
        var path = Path.Combine(Path.GetTempPath(), $"lia-mic-{Guid.NewGuid():N}.wav");
        try
        {
            using (var writer = new WaveFileWriter(path, fmt))
                writer.Write(pcm, 0, pcm.Length);
            return File.ReadAllBytes(path);
        }
        finally
        {
            try { File.Delete(path); } catch { /* */ }
        }
    }

    public void Dispose()
    {
        _armed = false;
        _session?.Cancel();
        CleanupCapture();
    }
}

using System.Collections.Concurrent;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace LiaDesktop.Services;

/// <summary>
/// Keep mic open during Edge TTS playback with software echo-cancel VAD.
/// Playback uses default MULTIMEDIA render (never Communications) + WaveOut fallback.
/// Echo reference = tap of the actual render stream.
/// </summary>
public sealed class BargeInService : IDisposable
{
    readonly GeminiClient _gemini;
    readonly AppLog _log;
    readonly AudioOutput _audio;
    readonly bool _ownsAudio;
    readonly EchoCancelVad _vad = new();

    CancellationTokenSource? _cts;
    IWavePlayer? _output;
    IWaveIn? _capture;
    WaveFormat? _capFormat;
    readonly ConcurrentQueue<float[]> _micQ = new();
    string? _activeReply;
    long _playbackBytes;
    long _playbackTotalBytes;
    bool _playing;

    /// <summary>Injected PCM mic source for selftest (16 kHz mono float frames).</summary>
    public Func<float[]?>? MockMicPull { get; set; }
    /// <summary>Injected STT for selftest.</summary>
    public Func<byte[], CancellationToken, Task<string>>? MockStt { get; set; }
    /// <summary>When set, skip real Wasapi and drive playback clock from this PCM (16k float).</summary>
    public float[]? MockPlaybackPcm { get; set; }

    public bool IsPlaying => _playing;
    public EchoCancelVad Vad => _vad;
    public AudioOutput Audio => _audio;
    public string LastRenderDevice => _audio.LastDeviceFriendlyName;
    public Role LastRenderRole => _audio.LastRole;

    public event Action? Listening;          // СЛУШАЮ
    public event Action? PlaybackStarted;
    public event Action? PlaybackStopped;
    public event Action? MouthStop;
    public event Action<InterruptKind, string, string, string>? Decision;
    // kind, userText, spokenSoFar, remaining

    public BargeInService(GeminiClient gemini, AppLog log, AudioOutput? audio = null)
    {
        _gemini = gemini;
        _log = log;
        _ownsAudio = audio == null;
        _audio = audio ?? new AudioOutput(log);
    }

    public void SetPreferredOutputDevice(string? deviceId) => _audio.SetPreferredDeviceId(deviceId);

    public void AbortPlayback()
    {
        try { _output?.Stop(); } catch { /* */ }
        _cts?.Cancel();
        _playing = false;
        MouthStop?.Invoke();
        PlaybackStopped?.Invoke();
    }

    public enum PlayEnd { Completed, Barged, Cancelled }

    public async Task<PlayEnd> PlayFileAsync(string path, string replyText, CancellationToken external = default)
    {
        AbortPlayback();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(external);
        var ct = _cts.Token;
        _activeReply = replyText;
        _vad.Reset();
        _playbackBytes = 0;
        _playbackTotalBytes = 1;
        while (_micQ.TryDequeue(out _)) { }

        _playing = true;
        PlaybackStarted?.Invoke();
        var end = PlayEnd.Completed;

        try
        {
            if (MockPlaybackPcm != null || MockMicPull != null)
                end = await PlayMockAsync(ct);
            else
                end = await PlayRealAsync(path, ct);
        }
        catch (OperationCanceledException)
        {
            end = PlayEnd.Cancelled;
        }
        catch (Exception ex)
        {
            _log.Info("BargeIn play: " + ex.Message);
            AppLog.WriteCrash("barge-in", ex);
            end = PlayEnd.Cancelled;
        }
        finally
        {
            CleanupCapture();
            try { _output?.Dispose(); } catch { /* */ }
            _output = null;
            _playing = false;
            MouthStop?.Invoke();
            PlaybackStopped?.Invoke();
        }
        return end;
    }

    async Task<PlayEnd> PlayMockAsync(CancellationToken ct)
    {
        var play = MockPlaybackPcm ?? Array.Empty<float>();
        _playbackTotalBytes = Math.Max(1, play.Length);
        const int frame = 320; // 20ms @ 16k
        var offset = 0;

        while (offset < play.Length && !ct.IsCancellationRequested)
        {
            var n = Math.Min(frame, play.Length - offset);
            var slice = play.AsSpan(offset, n).ToArray();
            _vad.PushPlayback(slice);
            _playbackBytes = offset + n;

            float[]? mic = MockMicPull?.Invoke();
            if (mic is { Length: > 0 })
            {
                if (_vad.PushMic(mic))
                {
                    await HandleBargeInAsync(ct);
                    return PlayEnd.Barged;
                }
            }
            else
            {
                // Default mock: mic = attenuated playback (loopback) — must NOT barge
                var loop = new float[n];
                for (var i = 0; i < n; i++) loop[i] = slice[i] * 0.7f;
                if (_vad.PushMic(loop))
                {
                    await HandleBargeInAsync(ct);
                    return PlayEnd.Barged;
                }
            }

            offset += n;
            await Task.Delay(1, ct); // selftest: don't wall-clock 20ms × N
        }
        return PlayEnd.Completed;
    }

    async Task<PlayEnd> PlayRealAsync(string path, CancellationToken ct)
    {
        StartCapture();

        using var reader = new AudioFileReader(path);
        _playbackTotalBytes = Math.Max(1, reader.Length);

        // Tap mono 16k for echo reference (= actual render stream)
        var resampler = new WdlResamplingSampleProvider(reader.ToMono(), EchoCancelVad.SampleRate);
        var tap = new TapSampleProvider(resampler, OnPlaybackSamples);

        Exception? playEx = null;
        var stoppedWithError = false;

        void OnStopped(object? s, StoppedEventArgs e)
        {
            if (e.Exception != null)
            {
                playEx = e.Exception;
                stoppedWithError = true;
                _log.Info("PlaybackStopped error: " + e.Exception.Message);
            }
        }

        // Try Wasapi multimedia; on failure / PlaybackStopped error → WaveOutEvent
        if (!TryStartPlayer(tap, OnStopped, preferWasapi: true, out var friendly, out var role, out var waveFb))
        {
            reader.Position = 0;
            if (!TryStartPlayer(tap, OnStopped, preferWasapi: false, out friendly, out role, out waveFb))
                throw new InvalidOperationException("No audio output device available");
        }

        _log.Info($"TTS → {friendly} (role={role}, waveOut={waveFb})");
        _log.Info($"BargeIn: WaveIn capture + echo-cancel VAD (render={friendly})");

        var barged = false;
        while (_output!.PlaybackState == PlaybackState.Playing && !ct.IsCancellationRequested)
        {
            if (stoppedWithError)
            {
                // Hot-swap to WaveOutEvent mid-failure
                try { _output.PlaybackStopped -= OnStopped; _output.Stop(); _output.Dispose(); } catch { /* */ }
                _output = null;
                reader.Position = 0;
                if (!TryStartPlayer(tap, OnStopped, preferWasapi: false, out friendly, out role, out waveFb))
                    throw playEx ?? new InvalidOperationException("playback failed");
                _log.Info($"TTS → {friendly} (fallback after error, role={role})");
                stoppedWithError = false;
                playEx = null;
            }

            if (_audio.DefaultDirty)
            {
                // Default multimedia device changed — reopen on new default
                try { _output.PlaybackStopped -= OnStopped; _output.Stop(); _output.Dispose(); } catch { /* */ }
                _output = null;
                var pos = reader.Position;
                reader.Position = pos;
                if (!TryStartPlayer(tap, OnStopped, preferWasapi: true, out friendly, out role, out waveFb))
                {
                    reader.Position = pos;
                    TryStartPlayer(tap, OnStopped, preferWasapi: false, out friendly, out role, out waveFb);
                }
                _log.Info($"TTS → {friendly} (default device changed, role={role})");
            }

            _playbackBytes = reader.Position;
            DrainMicVad(ref barged, ct);
            if (barged)
            {
                try { _output?.Stop(); } catch { /* */ }
                await HandleBargeInAsync(ct);
                return PlayEnd.Barged;
            }
            await Task.Delay(20, ct);
        }

        if (_output != null)
            try { _output.PlaybackStopped -= OnStopped; } catch { /* */ }

        return ct.IsCancellationRequested ? PlayEnd.Cancelled : PlayEnd.Completed;
    }

    bool TryStartPlayer(
        ISampleProvider tap,
        EventHandler<StoppedEventArgs> onStopped,
        bool preferWasapi,
        out string friendly,
        out Role role,
        out bool waveOut)
    {
        friendly = "";
        role = Role.Multimedia;
        waveOut = false;
        try
        {
            IWavePlayer player;
            if (preferWasapi)
            {
                player = _audio.OpenPlayer(out friendly, out role, out _, out waveOut);
            }
            else
            {
                player = new WaveOutEvent { DesiredLatency = 100 };
                friendly = "WAVE_MAPPER (WaveOutEvent)";
                role = Role.Multimedia;
                waveOut = true;
            }

            player.PlaybackStopped += onStopped;
            // WasapiOut/WaveOutEvent accept ISampleProvider via Init
            if (player is IWavePlayer wp)
            {
                // Convert sample provider → wave provider for Init
                var wave = tap.ToWaveProvider16();
                wp.Init(wave);
                wp.Play();
                _output = wp;
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            _log.Info("TryStartPlayer: " + ex.Message);
            return false;
        }
    }

    void OnPlaybackSamples(float[] mono16k)
    {
        _vad.PushPlayback(mono16k);
    }

    void DrainMicVad(ref bool barged, CancellationToken ct)
    {
        while (_micQ.TryDequeue(out var frame))
        {
            if (barged) continue;
            if (_vad.PushMic(frame))
                barged = true;
        }
        if (!barged && MockMicPull != null)
        {
            var m = MockMicPull();
            if (m is { Length: > 0 } && _vad.PushMic(m))
                barged = true;
        }
    }

    async Task HandleBargeInAsync(CancellationToken ct)
    {
        MouthStop?.Invoke();
        Listening?.Invoke();
        _log.Info("Barge-in: слушаю…");

        var frac = EchoCancelVad.ClampFrac(_playbackBytes / (double)_playbackTotalBytes);
        var (spoken, remaining) = EchoCancelVad.SplitReply(_activeReply ?? "", frac);

        var wav = await CaptureUtteranceAsync(ct);
        string text;
        if (MockStt != null)
            text = await MockStt(wav, ct);
        else if (wav.Length > 100)
            text = await _gemini.TranscribeRuAsync(wav, ct);
        else
            text = "";

        text = (text ?? "").Trim();
        var kind = InterruptClassifier.Classify(text);
        _log.Info($"Barge-in: «{text}» → {kind}");
        Decision?.Invoke(kind, text, spoken, remaining);
    }

    async Task<byte[]> CaptureUtteranceAsync(CancellationToken ct)
    {
        var pcm = new MemoryStream();
        var speechMs = 0;
        var silenceMs = 0;
        var started = false;
        var idle = 0;

        while (!ct.IsCancellationRequested && idle < 40)
        {
            float[]? frame = null;
            if (_micQ.TryDequeue(out var q)) frame = q;
            else if (MockMicPull != null) frame = MockMicPull();

            if (frame == null || frame.Length == 0)
            {
                await Task.Delay(20, ct);
                idle++;
                if (started) silenceMs += 20;
                if (started && silenceMs >= 700 && speechMs >= 200) break;
                continue;
            }

            idle = 0;
            var bytes = new byte[frame.Length * 2];
            for (var i = 0; i < frame.Length; i++)
            {
                var s = (short)Math.Clamp(frame[i] * 32767f, short.MinValue, short.MaxValue);
                bytes[i * 2] = (byte)(s & 0xff);
                bytes[i * 2 + 1] = (byte)((s >> 8) & 0xff);
            }
            pcm.Write(bytes);

            var rms = Rms(frame);
            var ms = frame.Length * 1000 / EchoCancelVad.SampleRate;
            if (!started)
            {
                if (rms > 0.02f) { started = true; speechMs = ms; }
            }
            else
            {
                speechMs += ms;
                if (rms < 0.012f) silenceMs += ms; else silenceMs = 0;
                if (silenceMs >= 700 && speechMs >= 200) break;
                if (speechMs >= 8000) break;
            }
            await Task.Delay(10, ct);
        }

        if (pcm.Length < 500)
            return Array.Empty<byte>();

        var fmt = new WaveFormat(EchoCancelVad.SampleRate, 16, 1);
        var path = Path.Combine(Path.GetTempPath(), $"lia-barge-{Guid.NewGuid():N}.wav");
        try
        {
            using (var w = new WaveFileWriter(path, fmt))
                w.Write(pcm.ToArray(), 0, (int)pcm.Length);
            return await File.ReadAllBytesAsync(path, ct);
        }
        finally
        {
            try { File.Delete(path); } catch { /* */ }
        }
    }

    void StartCapture()
    {
        CleanupCapture();
        var waveIn = new WaveInEvent
        {
            WaveFormat = new WaveFormat(16000, 16, 1),
            BufferMilliseconds = 20,
        };
        _capFormat = waveIn.WaveFormat;
        waveIn.DataAvailable += OnCapData;
        waveIn.StartRecording();
        _capture = waveIn;
    }

    void OnCapData(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0 || _capFormat == null) return;
        var samples = ToMono16k(_capFormat, e.Buffer, e.BytesRecorded);
        if (samples.Length > 0) _micQ.Enqueue(samples);
    }

    static float[] ToMono16k(WaveFormat fmt, byte[] buffer, int bytes)
    {
        if (fmt.Encoding != WaveFormatEncoding.Pcm || fmt.BitsPerSample != 16)
            return Array.Empty<float>();

        var ch = fmt.Channels;
        var srcRate = fmt.SampleRate;
        var frames = bytes / (2 * ch);
        var mono = new float[frames];
        for (var i = 0; i < frames; i++)
        {
            float sum = 0;
            for (var c = 0; c < ch; c++)
            {
                var idx = (i * ch + c) * 2;
                short s = (short)(buffer[idx] | (buffer[idx + 1] << 8));
                sum += s / 32768f;
            }
            mono[i] = sum / ch;
        }

        if (srcRate == EchoCancelVad.SampleRate) return mono;
        var outLen = (int)(frames * (EchoCancelVad.SampleRate / (double)srcRate));
        if (outLen <= 0) return Array.Empty<float>();
        var dst = new float[outLen];
        for (var i = 0; i < outLen; i++)
        {
            var srcPos = i * (srcRate / (double)EchoCancelVad.SampleRate);
            var i0 = (int)srcPos;
            var f = (float)(srcPos - i0);
            var a = mono[Math.Clamp(i0, 0, frames - 1)];
            var b = mono[Math.Clamp(i0 + 1, 0, frames - 1)];
            dst[i] = a + (b - a) * f;
        }
        return dst;
    }

    static float Rms(float[] x)
    {
        if (x.Length == 0) return 0;
        double s = 0;
        foreach (var v in x) s += v * v;
        return (float)Math.Sqrt(s / x.Length);
    }

    void CleanupCapture()
    {
        if (_capture == null) return;
        try { _capture.DataAvailable -= OnCapData; } catch { /* */ }
        try { _capture.StopRecording(); } catch { /* */ }
        try { _capture.Dispose(); } catch { /* */ }
        _capture = null;
    }

    public void Dispose()
    {
        AbortPlayback();
        CleanupCapture();
        _output?.Dispose();
        if (_ownsAudio) _audio.Dispose();
    }

    sealed class TapSampleProvider : ISampleProvider
    {
        readonly ISampleProvider _source;
        readonly Action<float[]> _onMono16k;
        readonly List<float> _acc = new();

        public TapSampleProvider(ISampleProvider source, Action<float[]> onMono16k)
        {
            _source = source;
            _onMono16k = onMono16k;
            WaveFormat = source.WaveFormat;
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            var read = _source.Read(buffer, offset, count);
            if (read <= 0) return read;

            var ch = WaveFormat.Channels;
            var frames = read / ch;
            for (var i = 0; i < frames; i++)
            {
                float m = 0;
                for (var c = 0; c < ch; c++) m += buffer[offset + i * ch + c];
                m /= ch;
                _acc.Add(m);
            }

            var srcRate = WaveFormat.SampleRate;
            var need = (int)(0.02 * srcRate);
            while (_acc.Count >= need)
            {
                var chunk = _acc.GetRange(0, need);
                _acc.RemoveRange(0, need);
                var outN = EchoCancelVad.SampleRate / 50;
                var dst = new float[outN];
                for (var i = 0; i < outN; i++)
                {
                    var srcPos = i * (srcRate / (double)EchoCancelVad.SampleRate);
                    var i0 = Math.Clamp((int)srcPos, 0, chunk.Count - 1);
                    dst[i] = chunk[i0];
                }
                _onMono16k(dst);
            }
            return read;
        }
    }
}

using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Vosk;

namespace LiaDesktop.Services;

/// <summary>
/// Offline always-on wake word «Лиа» via Vosk small-ru + restricted grammar.
/// Echo-guard: ignores mic while own TTS is playing (barge PlaybackStarted/Stopped).
/// Inline: «Лиа, открой блокнот» → command after free-form pass on the same utterance.
/// </summary>
public sealed class WakeWordService : IDisposable
{
    public const int SampleRate = 16000;
    const int RingSeconds = 4;
    const int PostWakeMs = 2200;
    /// <summary>Sleep-mode buffer length. Lower = more CPU (NAudio). 200ms keeps listen idle cheap.</summary>
    const int CaptureBufferMs = 200;
    /// <summary>~300 ms pre-roll so VAD open doesn't clip the start of «Лиа».</summary>
    const int PreRollMs = 300;
    const int PreRollBytes = SampleRate * 2 * PreRollMs / 1000;
    const int VadHangoverMs = 450;
    /// <summary>Below this = digital/cable silence — never open VAD / never FloatMix.</summary>
    const double VadDigitalSilenceRms = 80.0;
    /// <summary>Min open threshold above silence; -20 dBFS speech RMS is typically ≫ this.</summary>
    const double VadMinThresholdRms = 100.0;
    /// <summary>Shipped default (SecretsStore.WakeWordSensitivity).</summary>
    public const float DefaultSensitivity = 0.55f;
    /// <summary>AGC target ≈ -6 dBFS peak for VAD-open segments fed to Vosk.</summary>
    const int AgcTargetPeak = 16384;
    /// <summary>Don't boost below ~-26 dBFS (cable dither / wake_negative noise).</summary>
    const int AgcMinPeak = 1500;
    const float AgcMaxGain = 10f;

    readonly AppLog _log;
    readonly object _gate = new();
    Model? _model;
    WaveInEvent? _waveIn;
    SleepWasapiCapture? _wasapi;
    WaveFormat? _wasapiFormat;
    IntPtr _grmNative; // grammar recognizer
    // ring buffer of recent PCM (16-bit LE)
    readonly List<byte> _ring = new(SampleRate * 2 * RingSeconds);
    List<byte>? _postWake;
    DateTime _wakeStartedUtc;
    bool _capturingPost;
    bool _running;
    bool _echoGuard;
    bool _enabled = true;
    float _sensitivity = DefaultSensitivity;
    byte[] _agcScratch = new byte[SampleRate * 2];
    bool _chimeEnabled = true;
    DateTime _grammarCooldownUntilUtc;
    string _pendingGrammarText = "";
    float _pendingGrammarConf;

    // Energy VAD + pre-roll (shared by live capture and PushPcm injection)
    readonly byte[] _preroll = new byte[PreRollBytes];
    int _prerollWrite;
    int _prerollCount;
    double _noiseRms = 80;
    bool _vadOpen;
    DateTime _vadHangoverUntil = DateTime.MinValue;
    // Reused convert buffers — no per-chunk alloc on the capture thread
    byte[] _pcmScratch = new byte[SampleRate * 2]; // up to 1s mono pcm16
    WaveFormat? _waveInNativeFormat;

    public int DetectCount { get; private set; }
    public int RejectCount { get; private set; }
    public int FalseWakeCount { get; private set; }
    /// <summary>Last grammar/freeform reject reason for selftest diagnostics.</summary>
    public string LastRejectDetail { get; private set; } = "";

    public bool IsRunning => _running;
    public bool IsReady => _model != null;
    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            if (!value) Stop();
        }
    }

    /// <summary>0..1 — higher = stricter (fewer false wakes).</summary>
    public float Sensitivity
    {
        get => _sensitivity;
        set => _sensitivity = Math.Clamp(value, 0.05f, 0.95f);
    }

    public bool ChimeEnabled
    {
        get => _chimeEnabled;
        set => _chimeEnabled = value;
    }

    /// <summary>Fired on wake. Args: full text, command after wake (may be empty), confidence.</summary>
    public event Action<string, string, float>? Woken;

    public WakeWordService(AppLog log) => _log = log;

    public void SetEchoGuard(bool active)
    {
        lock (_gate)
        {
            _echoGuard = active;
            if (active)
            {
                _capturingPost = false;
                _postWake = null;
                ResetGrammar_NoLock();
            }
        }
    }

    public async Task EnsureModelAsync(CancellationToken ct = default)
    {
        if (_model != null) return;
        var path = await VoskModelStore.EnsureAsync(_log, null, ct);
        lock (_gate)
        {
            _model?.Dispose();
            Vosk.Vosk.SetLogLevel(-1);
            _model = new Model(path);
            _log.Info("wake: vosk model loaded");
        }
    }

    public enum CaptureMode
    {
        /// <summary>Production: WaveIn when available. Selftest default: PushPcm-only.</summary>
        Auto,
        /// <summary>Prefer WaveIn when a capture device exists (wake_cpu on CI).</summary>
        ForceWaveIn,
        /// <summary>Never open WaveIn — PushPcm injection only.</summary>
        PushOnly,
    }

    /// <summary>Last Start() capture mode: wavein|wasapi|loopback|pushonly.</summary>
    public string LastCaptureMode { get; private set; } = "pushonly";

    /// <summary>Last capture open error (for selftest diagnostics).</summary>
    public string LastCaptureError { get; private set; } = "";

    /// <summary>True when all capture backends failed (PushPcm-only fallback).</summary>
    public bool MicCaptureFailed { get; private set; }

    /// <summary>Raised once when capture open fails and HUD should show mic-unavailable.</summary>
    public event Action? MicFailed;

    /// <summary>True when energy VAD is currently feeding Vosk (speech or hangover).</summary>
    public bool VadOpen => _vadOpen;

    public void Start(CaptureMode mode = CaptureMode.Auto)
    {
        if (!_enabled) return;
        lock (_gate)
        {
            if (_running) return;
            if (_model == null)
            {
                _log.Info("wake: model not ready — skip start");
                return;
            }
            CreateGrammar_NoLock();
            LastCaptureError = "";
            MicCaptureFailed = false;
            ResetVad_NoLock();

            var wantCapture = mode switch
            {
                CaptureMode.ForceWaveIn => HasAnyCaptureDevice(),
                CaptureMode.PushOnly => false,
                _ => !SelfTestRunner.IsSelfTest && HasAnyCaptureDevice(),
            };

            if (!wantCapture)
            {
                _running = true;
                LastCaptureMode = "pushonly";
                _log.Info("wake: PushPcm-only mode");
                return;
            }

            // Prefer WASAPI capture (event-driven 100ms), then WaveIn. ForceWaveIn: loopback
            // on default Multimedia when mic/capture is denied (CABLE Output E_ACCESSDENIED on CI).
            if (TryStartWasapi_NoLock())
                return;
            if (TryStartWaveIn_NoLock())
                return;
            if (mode == CaptureMode.ForceWaveIn && TryStartWasapiLoopback_NoLock())
                return;

            _running = true;
            LastCaptureMode = "pushonly";
            if (string.IsNullOrEmpty(LastCaptureError))
                LastCaptureError = "all capture backends failed";
            MicCaptureFailed = true;
            _log.MicUnavailable(LastCaptureError);
            MicFailed?.Invoke();
        }
    }

    void ResetVad_NoLock()
    {
        _prerollWrite = 0;
        _prerollCount = 0;
        _noiseRms = 80;
        _vadOpen = false;
        _vadHangoverUntil = DateTime.MinValue;
        Array.Clear(_preroll);
    }

    bool TryStartWasapi_NoLock()
    {
        var errors = new List<string>();
        try
        {
            using var en = new MMDeviceEnumerator();
            var candidates = new List<MMDevice>();
            foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                candidates.Add(d);

            var ordered = candidates
                .OrderByDescending(d =>
                {
                    var n = d.FriendlyName ?? "";
                    return n.Contains("CABLE", StringComparison.OrdinalIgnoreCase)
                           || n.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                })
                .ToList();

            if (ordered.Count == 0)
            {
                try
                {
                    ordered.Add(en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications));
                }
                catch (Exception ex)
                {
                    errors.Add("default capture: " + ex.Message);
                }
            }

            foreach (var pick in ordered)
            {
                try
                {
                    CleanupCapture_NoLock();
                    // Fixed-sleep pump (NAudio sleep uses BufferSize/2 ≈ 5ms → ~1 core on CI).
                    _wasapi = new SleepWasapiCapture(pick, CaptureBufferMs, loopback: false);
                    _wasapiFormat = _wasapi.WaveFormat;
                    _wasapi.DataAvailable += OnWasapiData;
                    _wasapi.StartRecording();
                    _running = true;
                    LastCaptureMode = "wasapi";
                    LastCaptureError = "";
                    _log.Info($"wake: listening (ОЖИДАНИЕ) WasapiCapture device={pick.FriendlyName} buf={CaptureBufferMs}ms fixedSleep={CaptureBufferMs / 2}ms fmt={_wasapiFormat}");
                    return true;
                }
                catch (Exception ex)
                {
                    errors.Add($"{pick.FriendlyName}: {ex.Message}");
                    CleanupCapture_NoLock();
                }
            }
        }
        catch (Exception ex)
        {
            errors.Add("enumerate: " + ex.Message);
            CleanupCapture_NoLock();
        }

        LastCaptureError = "wasapi: " + string.Join(" | ", errors);
        _log.FileOnly("wake: WasapiCapture fail", LastCaptureError);
        return false;
    }

    /// <summary>
    /// Selftest-only: event-driven loopback of default Multimedia render (100ms buffers).
    /// Never used in production Auto mode (echo hazard).
    /// </summary>
    bool TryStartWasapiLoopback_NoLock()
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            MMDevice render;
            try
            {
                render = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            }
            catch (Exception ex)
            {
                LastCaptureError = (string.IsNullOrEmpty(LastCaptureError) ? "" : LastCaptureError + " ; ")
                                   + "loopback default render: " + ex.Message;
                return false;
            }

            CleanupCapture_NoLock();
            // Fixed 100ms sleep pump — NAudio WasapiCapture sleep tracks period (~5ms) not request.
            _wasapi = new SleepWasapiCapture(render, CaptureBufferMs, loopback: true);
            _wasapiFormat = _wasapi.WaveFormat;
            _wasapi.DataAvailable += OnWasapiData;
            _wasapi.StartRecording();
            _running = true;
            LastCaptureMode = "loopback";
            LastCaptureError = "";
            _log.Info($"wake: listening (ОЖИДАНИЕ) WasapiLoopback device={render.FriendlyName} buf={CaptureBufferMs}ms fixedSleep={CaptureBufferMs / 2}ms fmt={_wasapiFormat}");
            return true;
        }
        catch (Exception ex)
        {
            LastCaptureError = (string.IsNullOrEmpty(LastCaptureError) ? "" : LastCaptureError + " ; ")
                               + "loopback: " + ex.Message;
            _log.FileOnly("wake: WasapiLoopback fail", ex.Message);
            CleanupCapture_NoLock();
            return false;
        }
    }

    bool TryStartWaveIn_NoLock()
    {
        if (WaveIn.DeviceCount <= 0) return false;
        var device = PickWaveInDevice();
        var order = new List<int> { device.Index };
        for (var i = 0; i < WaveIn.DeviceCount; i++)
            if (i != device.Index) order.Add(i);

        var formats = new (int Rate, int Ch)[]
        {
            (SampleRate, 1), (44100, 1), (48000, 1), (44100, 2), (48000, 2),
        };

        foreach (var i in order)
        {
            var name = SafeWaveInName(i);
            foreach (var (rate, ch) in formats)
            {
                try
                {
                    CleanupCapture_NoLock();
                    _waveIn = new WaveInEvent
                    {
                        DeviceNumber = i,
                        WaveFormat = new WaveFormat(rate, 16, ch),
                        BufferMilliseconds = CaptureBufferMs,
                    };
                    _waveInNativeFormat = rate == SampleRate && ch == 1 ? null : _waveIn.WaveFormat;
                    _waveIn.DataAvailable += _waveInNativeFormat == null ? OnData : OnWaveInResampleData;
                    _waveIn.StartRecording();
                    _running = true;
                    LastCaptureMode = "wavein";
                    LastCaptureError = "";
                    MicCaptureFailed = false;
                    _log.Info($"wake: listening (ОЖИДАНИЕ) WaveIn device={i}/{name} fmt={rate}Hz/{ch}ch buf={CaptureBufferMs}ms");
                    return true;
                }
                catch (Exception ex)
                {
                    _log.FileOnly($"wake: WaveIn device {i} {rate}/{ch} fail", ex.Message);
                    LastCaptureError = (string.IsNullOrEmpty(LastCaptureError) ? "" : LastCaptureError + " ; ")
                                       + $"wavein#{i}@{rate}/{ch}: {ex.Message}";
                    CleanupCapture_NoLock();
                }
            }
        }
        return false;
    }

    void OnWaveInResampleData(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0 || _waveInNativeFormat == null) return;
        try
        {
            var ch = Math.Max(1, _waveInNativeFormat.Channels);
            var srcFrames = e.BytesRecorded / (2 * ch);
            var outFrames = Math.Max(1, (int)(srcFrames * (SampleRate / (double)Math.Max(1, _waveInNativeFormat.SampleRate))));
            EnsureScratch(outFrames * 2);
            var n = Pcm16ResampleTo16kMonoInto(
                e.Buffer, e.BytesRecorded, _waveInNativeFormat, _pcmScratch);
            if (n > 0) PushPcm(_pcmScratch, n);
        }
        catch (Exception ex)
        {
            _log.Info("wake wavein resample: " + ex.Message);
        }
    }

    void OnWasapiData(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0 || _wasapiFormat == null) return;
        try
        {
            if (_wasapiFormat.Encoding == WaveFormatEncoding.IeeeFloat)
            {
                var rms = FloatRmsAsPcm16(e.Buffer, e.BytesRecorded, _wasapiFormat.Channels);
                // Cheap reject before convert — capture thread stays event-driven & running
                if (!PeekWantsConvert(rms) && !_capturingPost)
                    return;
                var outFrames = Math.Max(1, (int)((e.BytesRecorded / 4.0 / Math.Max(1, _wasapiFormat.Channels))
                    * (SampleRate / (double)Math.Max(1, _wasapiFormat.SampleRate))));
                EnsureScratch(outFrames * 2);
                var n = FloatMixToPcm16kMonoInto(e.Buffer, e.BytesRecorded, _wasapiFormat, _pcmScratch);
                if (n > 0) PushPcm(_pcmScratch, n);
                return;
            }

            if (_wasapiFormat.Encoding == WaveFormatEncoding.Pcm && _wasapiFormat.BitsPerSample == 16)
            {
                var ch = Math.Max(1, _wasapiFormat.Channels);
                var srcFrames = e.BytesRecorded / (2 * ch);
                var outFrames = Math.Max(1, (int)(srcFrames * (SampleRate / (double)Math.Max(1, _wasapiFormat.SampleRate))));
                EnsureScratch(outFrames * 2);
                var n = Pcm16ResampleTo16kMonoInto(
                    e.Buffer, e.BytesRecorded, _wasapiFormat, _pcmScratch);
                if (n > 0) PushPcm(_pcmScratch, n);
                return;
            }

            using var src = new RawSourceWaveStream(
                new MemoryStream(e.Buffer, 0, e.BytesRecorded), _wasapiFormat);
            using var resampler = new MediaFoundationResampler(
                src, new WaveFormat(SampleRate, 16, 1))
            { ResamplerQuality = 30 };
            EnsureScratch(SampleRate * 2);
            int read;
            while ((read = resampler.Read(_pcmScratch, 0, _pcmScratch.Length)) > 0)
                PushPcm(_pcmScratch, read);
        }
        catch (Exception ex)
        {
            _log.Info("wake wasapi resample: " + ex.Message);
        }
    }

    /// <summary>Lock-free peek: skip FloatMix on digital silence; noise EMA only.</summary>
    bool PeekWantsConvert(double rmsPcm16)
    {
        // Hard digital/cable silence — never convert (this was the wake_cpu 28% hot path when
        // tiny loopback noise kept VAD open and FloatMix+Vosk ran every packet).
        if (rmsPcm16 < VadDigitalSilenceRms)
        {
            _noiseRms = _noiseRms * 0.99 + rmsPcm16 * 0.01;
            return DateTime.UtcNow < _vadHangoverUntil; // finish utterance only
        }
        var thresh = Math.Max(VadMinThresholdRms, _noiseRms * 2.5);
        if (rmsPcm16 >= thresh || DateTime.UtcNow < _vadHangoverUntil)
            return true;
        _noiseRms = _noiseRms * 0.95 + rmsPcm16 * 0.05;
        return false;
    }

    void EnsureScratch(int bytes)
    {
        if (_pcmScratch.Length < bytes)
            _pcmScratch = new byte[bytes];
    }

    /// <summary>Update noise floor / hangover. Returns true if VAD wants audio fed to Vosk.</summary>
    bool ObserveEnergy(double rmsPcm16)
    {
        var now = DateTime.UtcNow;
        if (rmsPcm16 < VadDigitalSilenceRms)
        {
            _noiseRms = _noiseRms * 0.99 + rmsPcm16 * 0.01;
            if (now < _vadHangoverUntil)
            {
                _vadOpen = true;
                return true;
            }
            _vadOpen = false;
            return false;
        }
        var thresh = Math.Max(VadMinThresholdRms, _noiseRms * 2.5);
        if (rmsPcm16 >= thresh)
        {
            _vadHangoverUntil = now.AddMilliseconds(VadHangoverMs);
            _vadOpen = true;
            return true;
        }
        _noiseRms = _noiseRms * 0.95 + rmsPcm16 * 0.05;
        if (now < _vadHangoverUntil)
        {
            _vadOpen = true;
            return true;
        }
        _vadOpen = false;
        return false;
    }

    static double FloatRmsAsPcm16(byte[] buffer, int bytes, int channels)
    {
        var ch = Math.Max(1, channels);
        var floatCount = bytes / 4;
        var frames = floatCount / ch;
        if (frames <= 0) return 0;
        // Stride sample for cheap RMS (every 4th frame)
        double sumSq = 0;
        var n = 0;
        for (var f = 0; f < frames; f += 4)
        {
            double mix = 0;
            var baseF = f * ch;
            for (var c = 0; c < ch; c++)
            {
                var bi = (baseF + c) * 4;
                if (bi + 4 > bytes) break;
                mix += BitConverter.ToSingle(buffer, bi);
            }
            var s = mix / ch * 32767.0;
            sumSq += s * s;
            n++;
        }
        return n == 0 ? 0 : Math.Sqrt(sumSq / n);
    }

    static double Pcm16Rms(byte[] buffer, int bytes)
    {
        var samples = bytes / 2;
        if (samples <= 0) return 0;
        double sumSq = 0;
        for (var i = 0; i + 1 < bytes; i += 2)
        {
            short s = (short)(buffer[i] | (buffer[i + 1] << 8));
            sumSq += (double)s * s;
        }
        return Math.Sqrt(sumSq / samples);
    }

    /// <summary>Float mix → 16 kHz mono PCM16 into <paramref name="dst"/>. Returns byte count.</summary>
    static int FloatMixToPcm16kMonoInto(byte[] buffer, int bytes, WaveFormat fmt, byte[] dst)
    {
        var ch = Math.Max(1, fmt.Channels);
        var srcRate = Math.Max(1, fmt.SampleRate);
        var floatCount = bytes / 4;
        var frames = floatCount / ch;
        if (frames <= 0) return 0;
        var outFrames = Math.Max(1, (int)(frames * (SampleRate / (double)srcRate)));
        var need = outFrames * 2;
        if (dst.Length < need) return 0; // caller must EnsureScratch first
        for (var o = 0; o < outFrames; o++)
        {
            var srcIdx = Math.Min(frames - 1, (int)(o * (srcRate / (double)SampleRate)));
            double sum = 0;
            var baseF = srcIdx * ch;
            for (var c = 0; c < ch; c++)
            {
                var bi = (baseF + c) * 4;
                if (bi + 4 > bytes) break;
                sum += BitConverter.ToSingle(buffer, bi);
            }
            var s = (short)Math.Clamp((int)(sum / ch * 32767.0), short.MinValue, short.MaxValue);
            dst[o * 2] = (byte)(s & 0xff);
            dst[o * 2 + 1] = (byte)((s >> 8) & 0xff);
        }
        return need;
    }

    /// <summary>PCM16 any-rate/ch → 16 kHz mono into dst (point resample, no alloc).</summary>
    static int Pcm16ResampleTo16kMonoInto(byte[] buffer, int bytes, WaveFormat fmt, byte[] dst)
    {
        var ch = Math.Max(1, fmt.Channels);
        var srcRate = Math.Max(1, fmt.SampleRate);
        var srcFrames = bytes / (2 * ch);
        if (srcFrames <= 0) return 0;
        var outFrames = Math.Max(1, (int)(srcFrames * (SampleRate / (double)srcRate)));
        var need = outFrames * 2;
        if (dst.Length < need)
        {
            // grow once outside hot silence path
            return 0;
        }
        for (var o = 0; o < outFrames; o++)
        {
            var srcIdx = Math.Min(srcFrames - 1, (int)(o * (srcRate / (double)SampleRate)));
            int sum = 0;
            for (var c = 0; c < ch; c++)
            {
                var bi = (srcIdx * ch + c) * 2;
                if (bi + 1 >= bytes) break;
                sum += (short)(buffer[bi] | (buffer[bi + 1] << 8));
            }
            var s = (short)Math.Clamp(sum / ch, short.MinValue, short.MaxValue);
            dst[o * 2] = (byte)(s & 0xff);
            dst[o * 2 + 1] = (byte)((s >> 8) & 0xff);
        }
        return need;
    }

    /// <summary>
    /// WASAPI capture/loopback with a fixed sleep (bufferMs/2), not NAudio's BufferSize/2.
    /// Shared-mode period is often ~10ms; NAudio then Sleep(5) and pegs a core on CI (~27%).
    /// </summary>
    sealed class SleepWasapiCapture : IDisposable
    {
        const long ReftimesPerMillisec = 10000;
        readonly MMDevice _device;
        readonly bool _loopback;
        readonly int _bufferMs;
        readonly int _sleepMs;
        AudioClient? _client;
        Thread? _thread;
        WaveFormat _mixFormat;
        WaveFormat _waveFormat;
        byte[] _recordBuffer = Array.Empty<byte>();
        int _bytesPerFrame;
        volatile int _state; // 0=stopped 1=starting 2=capturing 3=stopping

        public event EventHandler<WaveInEventArgs>? DataAvailable;
        public WaveFormat WaveFormat => _waveFormat;

        public SleepWasapiCapture(MMDevice device, int bufferMs, bool loopback)
        {
            _device = device;
            _loopback = loopback;
            _bufferMs = Math.Max(50, bufferMs);
            _sleepMs = Math.Max(50, _bufferMs / 2);
            _client = device.AudioClient;
            _mixFormat = _client.MixFormat;
            _waveFormat = _mixFormat.AsStandardWaveFormat();
        }

        public void StartRecording()
        {
            if (_state != 0) throw new InvalidOperationException("already recording");
            _state = 1;
            var client = _client ?? throw new ObjectDisposedException(nameof(SleepWasapiCapture));
            var flags = AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality;
            if (_loopback) flags |= AudioClientStreamFlags.Loopback;
            var duration = ReftimesPerMillisec * _bufferMs;
            client.Initialize(AudioClientShareMode.Shared, flags, duration, 0, _mixFormat, Guid.Empty);
            _mixFormat = client.MixFormat;
            _waveFormat = _mixFormat.AsStandardWaveFormat();
            _bytesPerFrame = _waveFormat.Channels * _waveFormat.BitsPerSample / 8;
            var frames = Math.Max(client.BufferSize, _waveFormat.SampleRate * _bufferMs / 1000);
            _recordBuffer = new byte[Math.Max(frames * _bytesPerFrame, _bytesPerFrame * 4096)];
            _thread = new Thread(CaptureThread) { IsBackground = true, Name = "wake-sleep-wasapi" };
            _thread.Start();
        }

        public void StopRecording()
        {
            if (_state == 2 || _state == 1) _state = 3;
        }

        void CaptureThread()
        {
            var client = _client;
            if (client == null) { _state = 0; return; }
            try
            {
                var capture = client.AudioCaptureClient;
                client.Start();
                if (_state == 1) _state = 2;
                while (_state == 2)
                {
                    Thread.Sleep(_sleepMs);
                    if (_state != 2) break;
                    ReadPackets(capture);
                }
            }
            catch { /* stop */ }
            finally
            {
                try { client.Stop(); } catch { /* */ }
                _state = 0;
            }
        }

        void ReadPackets(AudioCaptureClient capture)
        {
            var packet = capture.GetNextPacketSize();
            var offset = 0;
            while (packet != 0)
            {
                var ptr = capture.GetBuffer(out var frames, out var flags);
                var bytes = frames * _bytesPerFrame;
                if (offset > 0 && offset + bytes > _recordBuffer.Length)
                {
                    DataAvailable?.Invoke(this, new WaveInEventArgs(_recordBuffer, offset));
                    offset = 0;
                }
                if (bytes > _recordBuffer.Length) bytes = _recordBuffer.Length;
                if ((flags & AudioClientBufferFlags.Silent) != AudioClientBufferFlags.Silent)
                    Marshal.Copy(ptr, _recordBuffer, offset, bytes);
                else
                    Array.Clear(_recordBuffer, offset, bytes);
                offset += bytes;
                capture.ReleaseBuffer(frames);
                packet = capture.GetNextPacketSize();
            }
            // Always raise (incl. WASAPI Silent→zeros) so VAD hangover can finalize Vosk.
            // Idle cost is Sleep(_sleepMs) + PeekWantsConvert, not EventCallback quantum.
            if (offset > 0)
                DataAvailable?.Invoke(this, new WaveInEventArgs(_recordBuffer, offset));
        }

        public void Dispose()
        {
            StopRecording();
            try { _thread?.Join(1000); } catch { /* */ }
            _thread = null;
            try { _client?.Dispose(); } catch { /* */ }
            _client = null;
        }
    }

    public static bool HasAnyCaptureDevice()
    {
        if (WaveIn.DeviceCount > 0) return true;
        try
        {
            using var en = new MMDeviceEnumerator();
            return en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).Count > 0;
        }
        catch { return false; }
    }

    static (int Index, string Name) PickWaveInDevice()
    {
        // Prefer VB-CABLE Output (capture of virtual render) on CI runners
        for (var i = 0; i < WaveIn.DeviceCount; i++)
        {
            var name = SafeWaveInName(i);
            if (name.Contains("CABLE", StringComparison.OrdinalIgnoreCase)
                || name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase))
                return (i, name);
        }
        return (0, SafeWaveInName(0));
    }

    static string SafeWaveInName(int i)
    {
        try { return WaveIn.GetCapabilities(i).ProductName ?? $"#{i}"; }
        catch { return $"#{i}"; }
    }

    public void Stop()
    {
        lock (_gate)
        {
            CleanupCapture_NoLock();
            DestroyGrammar_NoLock();
            _running = false;
            _capturingPost = false;
            _postWake = null;
            _ring.Clear();
        }
    }

    /// <summary>Feed 16 kHz mono PCM16 for live/selftest injection.</summary>
    public void PushPcm(byte[] pcm16le) => PushPcm(pcm16le, pcm16le.Length);

    public void PushPcm(byte[] buffer, int bytes)
    {
        if (bytes <= 0) return;
        byte[]? confirmPcm = null;
        lock (_gate)
        {
            if (!_running || _model == null || _echoGuard || !_enabled) return;
            AppendRing_NoLock(buffer, bytes);
            AppendPreroll_NoLock(buffer, bytes);

            if (_capturingPost)
            {
                _postWake ??= new List<byte>();
                _postWake.AddRange(buffer.AsSpan(0, bytes).ToArray());
                var postBytesNeeded = SampleRate * 2 * PostWakeMs / 1000;
                var wallDone = (DateTime.UtcNow - _wakeStartedUtc).TotalMilliseconds >= PostWakeMs;
                var audioDone = _postWake.Count >= postBytesNeeded;
                if (wallDone || audioDone)
                    confirmPcm = TakePostCapturePcm_NoLock();
            }
            else
            {
                var wasOpen = _vadOpen || DateTime.UtcNow < _vadHangoverUntil;
                var open = ObserveEnergy(Pcm16Rms(buffer, bytes));
                if (open && _grmNative != IntPtr.Zero && DateTime.UtcNow >= _grammarCooldownUntilUtc)
                {
                    if (!wasOpen)
                    {
                        // Onset: flush ~300ms pre-roll (includes this chunk) so «Лиа» isn't clipped
                        FeedPrerollToVosk_NoLock();
                    }
                    else
                    {
                        AcceptWaveform_NoLock(buffer, bytes);
                    }
                }
                // else silence: skip Vosk; pre-roll retained for next onset
            }
        }
        if (confirmPcm != null)
            ConfirmWakeFromPcm(confirmPcm);
    }

    void AppendPreroll_NoLock(byte[] buffer, int bytes)
    {
        if (bytes <= 0) return;
        if (bytes >= PreRollBytes)
        {
            Buffer.BlockCopy(buffer, bytes - PreRollBytes, _preroll, 0, PreRollBytes);
            _prerollWrite = 0;
            _prerollCount = PreRollBytes;
            return;
        }
        var first = Math.Min(bytes, PreRollBytes - _prerollWrite);
        Buffer.BlockCopy(buffer, 0, _preroll, _prerollWrite, first);
        _prerollWrite = (_prerollWrite + first) % PreRollBytes;
        if (first < bytes)
        {
            Buffer.BlockCopy(buffer, first, _preroll, _prerollWrite, bytes - first);
            _prerollWrite = (_prerollWrite + (bytes - first)) % PreRollBytes;
        }
        _prerollCount = Math.Min(PreRollBytes, _prerollCount + bytes);
    }

    void FeedPrerollToVosk_NoLock()
    {
        if (_grmNative == IntPtr.Zero || _prerollCount <= 0) return;
        // Linearize circular buffer into scratch (one shot on onset only)
        EnsureScratch(_prerollCount);
        if (_prerollCount < PreRollBytes)
        {
            Buffer.BlockCopy(_preroll, 0, _pcmScratch, 0, _prerollCount);
            AcceptWaveform_NoLock(_pcmScratch, _prerollCount);
            return;
        }
        var tail = PreRollBytes - _prerollWrite;
        Buffer.BlockCopy(_preroll, _prerollWrite, _pcmScratch, 0, tail);
        if (_prerollWrite > 0)
            Buffer.BlockCopy(_preroll, 0, _pcmScratch, tail, _prerollWrite);
        AcceptWaveform_NoLock(_pcmScratch, PreRollBytes);
    }

    void AcceptWaveform_NoLock(byte[] buffer, int bytes)
    {
        if (_grmNative == IntPtr.Zero || bytes <= 0) return;
        // Production AGC: quiet VAD-open speech (e.g. -20 dBFS) → ~-6 dBFS before Vosk.
        // Cap x10; skip boost for peaks below AgcMinPeak so noise isn't amplified into false wakes.
        var feed = buffer;
        var feedBytes = bytes;
        var gain = ComputeAgcGain(buffer, bytes);
        if (gain > 1.01f)
        {
            EnsureAgcScratch(bytes);
            ApplyGainTo(buffer, bytes, gain, _agcScratch);
            feed = _agcScratch;
            feedBytes = bytes;
        }
        var ok = vosk_recognizer_accept_waveform(_grmNative, feed, feedBytes);
        if (ok != 0)
        {
            var json = PtrToUtf8(vosk_recognizer_result(_grmNative));
            ResetGrammar_NoLock();
            TryHandleGrammarFinal_NoLock(json);
        }
    }

    void EnsureAgcScratch(int bytes)
    {
        if (_agcScratch.Length < bytes)
            _agcScratch = new byte[bytes];
    }

    static float ComputeAgcGain(byte[] buffer, int bytes)
    {
        var peak = 1;
        for (var i = 0; i + 1 < bytes; i += 2)
        {
            var s = Math.Abs((short)(buffer[i] | (buffer[i + 1] << 8)));
            if (s > peak) peak = s;
        }
        if (peak < AgcMinPeak || peak >= AgcTargetPeak)
            return 1f;
        return Math.Min(AgcMaxGain, AgcTargetPeak / (float)peak);
    }

    static void ApplyGainTo(byte[] src, int bytes, float gain, byte[] dst)
    {
        for (var i = 0; i + 1 < bytes; i += 2)
        {
            var s = (short)(src[i] | (src[i + 1] << 8));
            var v = (short)Math.Clamp((int)Math.Round(s * gain), short.MinValue, short.MaxValue);
            dst[i] = (byte)(v & 0xff);
            dst[i + 1] = (byte)((v >> 8) & 0xff);
        }
    }

    /// <summary>Whole-utterance AGC copy for offline/confirm decodes (peak → ~-6 dBFS, ≤×10).</summary>
    static byte[] NormalizeUtteranceForVosk(byte[] pcm)
    {
        if (pcm.Length < 4) return pcm;
        var gain = ComputeAgcGain(pcm, pcm.Length);
        if (gain <= 1.01f) return pcm;
        var dst = new byte[pcm.Length];
        ApplyGainTo(pcm, pcm.Length, gain, dst);
        return dst;
    }

    /// <summary>
    /// Selftest / offline helper: grammar (live path) + free-form confirm, same strip rules.
    /// Returns true if wake word detected. Also raises Woken.
    /// </summary>
    public bool ProcessUtterance(byte[] pcm16le, out string full, out string command, out float conf)
    {
        full = ""; command = ""; conf = 0;
        if (_model == null || pcm16le.Length < 3200) return false;

        // Free-form first when it has a wake or is a substantial non-wake sentence.
        // Sparse freeform crumbs («лет») fall through to grammar (short «Лиа» clips).
        var (fText, fConf) = FreeFormDecode(pcm16le);
        fText = WakeText.NormalizeAsrWake(fText);
        var (gText, gConf) = GrammarDecodeUtterance(pcm16le);
        gText = WakeText.NormalizeAsrWake(gText);

        string text;
        float c;
        if (WakeText.ContainsWake(fText))
        {
            text = fText;
            c = fConf;
        }
        else if (WakeText.IsSubstantialNonWake(fText))
        {
            // e.g. «могу ли я…» — never promote grammar hallucination «лия»
            full = fText;
            conf = fConf;
            RejectCount++;
            return false;
        }
        else if (WakeText.ContainsWake(gText))
        {
            text = gText;
            c = gConf;
        }
        else
        {
            full = string.IsNullOrWhiteSpace(fText) ? gText : fText;
            conf = string.IsNullOrWhiteSpace(fText) ? gConf : fConf;
            RejectCount++;
            return false;
        }

        full = text;
        conf = c;
        var floor = ConfidenceFloor();
        if (c < floor * 0.85f) // slightly softer for offline batch
        {
            RejectCount++;
            _log.Info($"wake: reject (utterance) conf={c:F2} text={text}");
            return false;
        }
        command = WakeText.StripWake(text);
        DetectCount++;
        _log.Info($"wake: DETECT conf={c:F2} text=\"{text}\" cmd=\"{command}\"");
        RaiseWoken(text, command, c);
        return true;
    }

    /// <summary>Flush pending post-wake capture + force grammar final (selftest end-of-stream).</summary>
    public void Flush()
    {
        byte[]? confirmPcm;
        lock (_gate)
        {
            confirmPcm = _capturingPost ? TakePostCapturePcm_NoLock() : null;
            if (confirmPcm == null && _grmNative != IntPtr.Zero)
            {
                try
                {
                    var json = PtrToUtf8(vosk_recognizer_final_result(_grmNative));
                    ResetGrammar_NoLock();
                    TryHandleGrammarFinal_NoLock(json);
                    // Forced final may open post-capture with no more audio arriving — confirm now
                    // from ring pre-roll (quiet «Лиа» + AGC in FreeFormDecode).
                    if (_capturingPost)
                        confirmPcm = TakePostCapturePcm_NoLock();
                }
                catch { /* */ }
            }
        }
        if (confirmPcm != null) ConfirmWakeFromPcm(confirmPcm);
    }

    void OnData(object? sender, WaveInEventArgs e) => PushPcm(e.Buffer, e.BytesRecorded);

    void TryHandleGrammarFinal_NoLock(string json)
    {
        var (text, conf) = ParseResult(json);
        if (string.IsNullOrWhiteSpace(text) || text == "[unk]")
        {
            RejectCount++;
            LastRejectDetail = $"grammar empty/unk conf={conf:F2}";
            return;
        }
        if (!WakeText.ContainsWake(text))
        {
            RejectCount++;
            LastRejectDetail = $"grammar no-wake text=\"{text}\" conf={conf:F2}";
            return;
        }
        var floor = ConfidenceFloor();
        if (conf < floor)
        {
            RejectCount++;
            LastRejectDetail = $"grammar conf={conf:F2} < floor={floor:F2} text={text}";
            _log.Info($"wake: reject conf={conf:F2} < {floor:F2} text={text}");
            return;
        }

        // Start post-wake capture to catch inline command in same breath
        _capturingPost = true;
        _wakeStartedUtc = DateTime.UtcNow;
        _postWake = new List<byte>();
        _pendingGrammarText = text;
        _pendingGrammarConf = conf;
        _log.Info($"wake: grammar hit conf={conf:F2} text=\"{text}\" — capturing post…");
    }

    /// <summary>Snapshot + reset post-capture; caller must hold _gate. Returns PCM for confirm.</summary>
    byte[] TakePostCapturePcm_NoLock()
    {
        _capturingPost = false;
        var preBytes = Math.Min(_ring.Count, SampleRate * 2 * 8 / 10); // 0.8s pre-roll
        var pre = _ring.Count <= preBytes
            ? _ring.ToArray()
            : _ring.Skip(_ring.Count - preBytes).ToArray();
        var post = _postWake?.ToArray() ?? Array.Empty<byte>();
        _postWake = null;
        var maxConfirm = SampleRate * 2 * 2; // 2.0s total
        var takePost = Math.Min(post.Length, Math.Max(0, maxConfirm - pre.Length));
        var all = new byte[pre.Length + takePost];
        Buffer.BlockCopy(pre, 0, all, 0, pre.Length);
        if (takePost > 0) Buffer.BlockCopy(post, 0, all, pre.Length, takePost);
        ResetGrammar_NoLock();
        return all;
    }

    void ConfirmWakeFromPcm(byte[] pcm)
    {
        // Ring/post PCM is raw capture level — AGC inside FreeFormDecode so quiet «Лиа» confirms.
        var (text, conf) = FreeFormDecode(pcm);
        text = WakeText.NormalizeAsrWake(text);
        lock (_gate)
        {
            var floor = ConfidenceFloor();
            if (!WakeText.ContainsWake(text))
            {
                // Wake-only: freeform often returns empty after silence hangover. Grammar already
                // cleared the shipped floor on AGC'd audio — trust it (still reject if freeform
                // heard a non-wake phrase).
                if (string.IsNullOrWhiteSpace(text)
                    && WakeText.ContainsWake(_pendingGrammarText)
                    && _pendingGrammarConf >= floor)
                {
                    var gCmd = WakeText.StripWake(_pendingGrammarText);
                    DetectCount++;
                    LastRejectDetail = "";
                    _log.Info($"wake: DETECT (grammar, freeform silent) conf={_pendingGrammarConf:F2} text=\"{_pendingGrammarText}\" cmd=\"{gCmd}\"");
                    RaiseWoken(_pendingGrammarText, gCmd, _pendingGrammarConf);
                    _pendingGrammarText = "";
                    _pendingGrammarConf = 0;
                    return;
                }
                RejectCount++;
                FalseWakeCount++;
                LastRejectDetail = $"post no-wake text=\"{text}\" conf={conf:F2} g=\"{_pendingGrammarText}\"";
                _grammarCooldownUntilUtc = DateTime.UtcNow.AddMilliseconds(1200);
                _pendingGrammarText = "";
                _pendingGrammarConf = 0;
                _log.Info($"wake: post reject (no wake in freeform) text=\"{text}\" conf={conf:F2}");
                return;
            }
            if (conf < floor * 0.7f)
            {
                RejectCount++;
                LastRejectDetail = $"post conf={conf:F2} < {floor * 0.7f:F2} text={text}";
                _grammarCooldownUntilUtc = DateTime.UtcNow.AddMilliseconds(800);
                _pendingGrammarText = "";
                _pendingGrammarConf = 0;
                _log.Info($"wake: post reject conf={conf:F2} text={text}");
                return;
            }
            var cmd = WakeText.StripWake(text);
            DetectCount++;
            LastRejectDetail = "";
            _pendingGrammarText = "";
            _pendingGrammarConf = 0;
            _log.Info($"wake: DETECT conf={conf:F2} text=\"{text}\" cmd=\"{cmd}\"");
            RaiseWoken(text, cmd, conf);
        }
    }

    (string Text, float Conf) FreeFormDecode(byte[] pcm)
    {
        Model? model;
        lock (_gate) model = _model;
        if (model == null || pcm.Length == 0) return ("", 0);
        try
        {
            // Utterance AGC before Vosk (quiet wake / confirm path)
            pcm = NormalizeUtteranceForVosk(pcm);
            using var rec = new VoskRecognizer(model, SampleRate);
            rec.SetWords(true);
            const int chunk = 8000; // 0.25s
            // Cap decode input at 2.5s to keep confirm cheap
            var limit = Math.Min(pcm.Length, SampleRate * 2 * 25 / 10);
            for (var i = 0; i < limit; i += chunk)
            {
                var n = Math.Min(chunk, limit - i);
                var slice = new byte[n];
                Buffer.BlockCopy(pcm, i, slice, 0, n);
                rec.AcceptWaveform(slice, n);
            }
            return ParseResult(rec.FinalResult());
        }
        catch (Exception ex)
        {
            _log.Info("wake freeform: " + ex.Message);
            return ("", 0);
        }
    }

    /// <summary>One-shot grammar decode (same vocabulary as live WaveIn path).</summary>
    (string Text, float Conf) GrammarDecodeUtterance(byte[] pcm)
    {
        Model? model;
        lock (_gate) model = _model;
        if (model == null || pcm.Length == 0) return ("", 0);
        IntPtr grm = IntPtr.Zero;
        try
        {
            pcm = NormalizeUtteranceForVosk(pcm);
            var grammar = "[\"лиа\",\"лия\",\"[unk]\"]";
            var bytes = Encoding.UTF8.GetBytes(grammar + "\0");
            var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                grm = vosk_recognizer_new_grm(GetHandle(model), SampleRate, pin.AddrOfPinnedObject());
            }
            finally { pin.Free(); }
            if (grm == IntPtr.Zero) return ("", 0);
            vosk_recognizer_set_words(grm, 1);
            const int chunk = 8000;
            var limit = Math.Min(pcm.Length, SampleRate * 2 * 25 / 10);
            for (var i = 0; i < limit; i += chunk)
            {
                var n = Math.Min(chunk, limit - i);
                var slice = new byte[n];
                Buffer.BlockCopy(pcm, i, slice, 0, n);
                vosk_recognizer_accept_waveform(grm, slice, n);
            }
            var json = PtrToUtf8(vosk_recognizer_final_result(grm));
            return ParseResult(json);
        }
        catch (Exception ex)
        {
            _log.Info("wake grammar-utt: " + ex.Message);
            return ("", 0);
        }
        finally
        {
            if (grm != IntPtr.Zero)
                try { vosk_recognizer_free(grm); } catch { /* */ }
        }
    }

    static (string Text, float Conf) ParseResult(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            var root = doc.RootElement;
            var text = root.TryGetProperty("text", out var t) ? (t.GetString() ?? "") : "";
            text = text.Trim().ToLowerInvariant();
            float conf = 1f;
            if (root.TryGetProperty("result", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                double sum = 0;
                var n = 0;
                foreach (var w in arr.EnumerateArray())
                {
                    if (w.TryGetProperty("conf", out var c))
                    {
                        sum += c.GetDouble();
                        n++;
                    }
                }
                if (n > 0) conf = (float)(sum / n);
            }
            return (text, conf);
        }
        catch
        {
            return ("", 0);
        }
    }

    float ConfidenceFloor() => 0.15f + _sensitivity * 0.7f;

    /// <inheritdoc cref="WakeText.NormalizeAsrWake"/>
    public static string NormalizeAsrWake(string text) => WakeText.NormalizeAsrWake(text);

    public static string StripWake(string text) => WakeText.StripWake(text);

    void RaiseWoken(string full, string cmd, float conf)
    {
        if (_chimeEnabled)
            try { PlaySoftChime(); } catch { /* */ }
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { Woken?.Invoke(full, cmd, conf); }
            catch (Exception ex) { _log.Info("wake handler: " + ex.Message); }
        });
    }

    static void PlaySoftChime()
    {
        // Quiet 880Hz sine ~70ms @ 16k mono — does not go through barge/TTS path
        const int sr = 16000;
        const int ms = 70;
        var n = sr * ms / 1000;
        var samples = new short[n];
        for (var i = 0; i < n; i++)
        {
            var env = i < n / 8 ? i / (float)(n / 8) : (n - i) / (float)(n * 7 / 8);
            env = Math.Clamp(env, 0, 1);
            samples[i] = (short)(MathF.Sin(2 * MathF.PI * 880f * i / sr) * 4000f * env);
        }
        var bytes = new byte[samples.Length * 2];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        var stream = new RawSourceWaveStream(new MemoryStream(bytes), new WaveFormat(sr, 16, 1));
        var wo = new WaveOutEvent { Volume = 0.25f };
        wo.Init(stream);
        wo.PlaybackStopped += (_, _) =>
        {
            try { wo.Dispose(); stream.Dispose(); } catch { /* */ }
        };
        wo.Play();
    }

    void AppendRing_NoLock(byte[] buffer, int bytes)
    {
        if (bytes <= 0) return;
        var max = SampleRate * 2 * RingSeconds;
        if (bytes >= max)
        {
            _ring.Clear();
            _ring.AddRange(buffer.AsSpan(bytes - max, max).ToArray());
            return;
        }
        _ring.AddRange(buffer.AsSpan(0, bytes).ToArray());
        if (_ring.Count > max)
            _ring.RemoveRange(0, _ring.Count - max);
    }

    void CreateGrammar_NoLock()
    {
        DestroyGrammar_NoLock();
        if (_model == null) return;
        var grammar = "[\"лиа\",\"лия\",\"[unk]\"]";
        var bytes = Encoding.UTF8.GetBytes(grammar + "\0");
        var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            var modelPtr = GetHandle(_model);
            _grmNative = vosk_recognizer_new_grm(modelPtr, SampleRate, pin.AddrOfPinnedObject());
            if (_grmNative == IntPtr.Zero)
                throw new InvalidOperationException("vosk_recognizer_new_grm failed");
            vosk_recognizer_set_words(_grmNative, 1);
        }
        finally
        {
            pin.Free();
        }
    }

    void ResetGrammar_NoLock()
    {
        if (_model == null) return;
        CreateGrammar_NoLock();
    }

    void DestroyGrammar_NoLock()
    {
        if (_grmNative != IntPtr.Zero)
        {
            try { vosk_recognizer_free(_grmNative); } catch { /* */ }
            _grmNative = IntPtr.Zero;
        }
    }

    void CleanupCapture_NoLock()
    {
        if (_wasapi != null)
        {
            try { _wasapi.DataAvailable -= OnWasapiData; } catch { /* */ }
            try { _wasapi.StopRecording(); } catch { /* */ }
            try { _wasapi.Dispose(); } catch { /* */ }
            _wasapi = null;
            _wasapiFormat = null;
        }
        if (_waveIn != null)
        {
            try { _waveIn.DataAvailable -= OnData; } catch { /* */ }
            try { _waveIn.DataAvailable -= OnWaveInResampleData; } catch { /* */ }
            try { _waveIn.StopRecording(); } catch { /* */ }
            try { _waveIn.Dispose(); } catch { /* */ }
            _waveIn = null;
            _waveInNativeFormat = null;
        }
    }

    static IntPtr GetHandle(Model model)
    {
        var f = typeof(Model).GetField("handle",
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Public);
        if (f == null) throw new InvalidOperationException("Vosk.Model.handle missing");
        return ((HandleRef)f.GetValue(model)!).Handle;
    }

    static string PtrToUtf8(IntPtr p)
    {
        if (p == IntPtr.Zero) return "";
        var len = 0;
        while (Marshal.ReadByte(p, len) != 0) len++;
        var buf = new byte[len];
        Marshal.Copy(p, buf, 0, len);
        return Encoding.UTF8.GetString(buf);
    }

    public void Dispose()
    {
        Stop();
        lock (_gate)
        {
            _model?.Dispose();
            _model = null;
        }
    }

    [DllImport("libvosk", CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr vosk_recognizer_new_grm(IntPtr model, float sample_rate, IntPtr grammar);

    [DllImport("libvosk", CallingConvention = CallingConvention.Cdecl)]
    static extern int vosk_recognizer_accept_waveform(IntPtr recognizer, byte[] data, int length);

    [DllImport("libvosk", CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr vosk_recognizer_result(IntPtr recognizer);

    [DllImport("libvosk", CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr vosk_recognizer_final_result(IntPtr recognizer);

    [DllImport("libvosk", CallingConvention = CallingConvention.Cdecl)]
    static extern void vosk_recognizer_set_words(IntPtr recognizer, int words);

    [DllImport("libvosk", CallingConvention = CallingConvention.Cdecl)]
    static extern void vosk_recognizer_free(IntPtr recognizer);
}

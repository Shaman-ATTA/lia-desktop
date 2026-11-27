using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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

    static readonly Regex WakeRe = new(
        @"(^|[\s,.:;!?])(лиа|лия)($|[\s,.:;!?])",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    readonly AppLog _log;
    readonly object _gate = new();
    Model? _model;
    WaveInEvent? _waveIn;
    IntPtr _grmNative; // grammar recognizer
    // ring buffer of recent PCM (16-bit LE)
    readonly List<byte> _ring = new(SampleRate * 2 * RingSeconds);
    List<byte>? _postWake;
    DateTime _wakeStartedUtc;
    bool _capturingPost;
    bool _running;
    bool _echoGuard;
    bool _enabled = true;
    float _sensitivity = 0.55f;
    bool _chimeEnabled = true;

    public int DetectCount { get; private set; }
    public int RejectCount { get; private set; }
    public int FalseWakeCount { get; private set; }

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

    public void Start()
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
            if (WaveIn.DeviceCount > 0)
            {
                _waveIn = new WaveInEvent
                {
                    WaveFormat = new WaveFormat(SampleRate, 16, 1),
                    BufferMilliseconds = 100,
                };
                _waveIn.DataAvailable += OnData;
                try
                {
                    _waveIn.StartRecording();
                    _running = true;
                    _log.Info("wake: listening (ОЖИДАНИЕ)");
                }
                catch (Exception ex)
                {
                    _log.Info("wake: WaveIn start fail — " + ex.Message);
                    CleanupCapture_NoLock();
                    // still allow PushPcm
                    _running = true;
                }
            }
            else
            {
                _running = true;
                _log.Info("wake: no WaveIn — PushPcm-only mode");
            }
        }
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
        lock (_gate)
        {
            if (!_running || _model == null || _echoGuard || !_enabled) return;
            AppendRing_NoLock(buffer, bytes);
            if (_capturingPost)
            {
                _postWake ??= new List<byte>();
                _postWake.AddRange(buffer.AsSpan(0, bytes).ToArray());
                var postBytesNeeded = SampleRate * 2 * PostWakeMs / 1000;
                var wallDone = (DateTime.UtcNow - _wakeStartedUtc).TotalMilliseconds >= PostWakeMs;
                var audioDone = _postWake.Count >= postBytesNeeded;
                if (wallDone || audioDone)
                    FinishPostCapture_NoLock();
                return;
            }
            if (_grmNative == IntPtr.Zero) return;
            var ok = vosk_recognizer_accept_waveform(_grmNative, buffer, bytes);
            if (ok == 0) return;
            var json = PtrToUtf8(vosk_recognizer_result(_grmNative));
            ResetGrammar_NoLock();
            TryHandleGrammarFinal_NoLock(json);
        }
    }

    /// <summary>
    /// Selftest helper: decode a whole utterance offline (free-form + wake strip).
    /// Returns true if wake word detected. Also raises Woken.
    /// </summary>
    public bool ProcessUtterance(byte[] pcm16le, out string full, out string command, out float conf)
    {
        full = ""; command = ""; conf = 0;
        if (_model == null || pcm16le.Length < 3200) return false;
        var (text, c) = FreeFormDecode(pcm16le);
        full = text;
        conf = c;
        if (!ContainsWake(text))
        {
            RejectCount++;
            return false;
        }
        var floor = ConfidenceFloor();
        if (c < floor * 0.85f) // slightly softer for offline batch
        {
            RejectCount++;
            _log.Info($"wake: reject (utterance) conf={c:F2} text={text}");
            return false;
        }
        command = StripWake(text);
        DetectCount++;
        _log.Info($"wake: DETECT conf={c:F2} text=\"{text}\" cmd=\"{command}\"");
        RaiseWoken(text, command, c);
        return true;
    }

    /// <summary>Flush pending post-wake capture (selftest end-of-stream).</summary>
    public void Flush()
    {
        lock (_gate)
        {
            if (_capturingPost) FinishPostCapture_NoLock();
        }
    }

    void OnData(object? sender, WaveInEventArgs e) => PushPcm(e.Buffer, e.BytesRecorded);

    void TryHandleGrammarFinal_NoLock(string json)
    {
        var (text, conf) = ParseResult(json);
        if (string.IsNullOrWhiteSpace(text) || text == "[unk]")
        {
            RejectCount++;
            return;
        }
        if (!ContainsWake(text))
        {
            RejectCount++;
            return;
        }
        var floor = ConfidenceFloor();
        if (conf < floor)
        {
            RejectCount++;
            _log.Info($"wake: reject conf={conf:F2} < {floor:F2} text={text}");
            return;
        }

        // Start post-wake capture to catch inline command in same breath
        _capturingPost = true;
        _wakeStartedUtc = DateTime.UtcNow;
        _postWake = new List<byte>();
        _log.Info($"wake: grammar hit conf={conf:F2} text=\"{text}\" — capturing post…");
    }

    void FinishPostCapture_NoLock()
    {
        _capturingPost = false;
        // Build utterance: last ~1.2s of ring (pre) + post
        var preBytes = Math.Min(_ring.Count, SampleRate * 2 * 12 / 10); // 1.2s
        var pre = _ring.Skip(Math.Max(0, _ring.Count - preBytes)).ToArray();
        var post = _postWake?.ToArray() ?? Array.Empty<byte>();
        _postWake = null;
        var all = new byte[pre.Length + post.Length];
        Buffer.BlockCopy(pre, 0, all, 0, pre.Length);
        Buffer.BlockCopy(post, 0, all, pre.Length, post.Length);

        var (text, conf) = FreeFormDecode(all);
        // Grammar can false-fire on [unk]→лиа; free-form must confirm the wake word.
        if (!ContainsWake(text))
        {
            RejectCount++;
            FalseWakeCount++; // grammar-only ghost
            _log.Info($"wake: post reject (no wake in freeform) text=\"{text}\" conf={conf:F2}");
            ResetGrammar_NoLock();
            return;
        }
        var floor = ConfidenceFloor();
        if (conf < floor * 0.7f)
        {
            RejectCount++;
            _log.Info($"wake: post reject conf={conf:F2} text={text}");
            ResetGrammar_NoLock();
            return;
        }
        var cmd = StripWake(text);
        DetectCount++;
        _log.Info($"wake: DETECT conf={conf:F2} text=\"{text}\" cmd=\"{cmd}\"");
        RaiseWoken(text, cmd, conf);
        ResetGrammar_NoLock();
    }

    (string Text, float Conf) FreeFormDecode(byte[] pcm)
    {
        if (_model == null || pcm.Length == 0) return ("", 0);
        try
        {
            using var rec = new VoskRecognizer(_model, SampleRate);
            rec.SetWords(true);
            // feed in chunks
            const int chunk = 8000; // 0.25s
            for (var i = 0; i < pcm.Length; i += chunk)
            {
                var n = Math.Min(chunk, pcm.Length - i);
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

    static bool ContainsWake(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (text is "лиа" or "лия") return true;
        return WakeRe.IsMatch(text);
    }

    public static string StripWake(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var s = Regex.Replace(text.Trim().ToLowerInvariant(),
            @"^\s*(лиа|лия)\s*[,.:;!]?\s*", "", RegexOptions.CultureInvariant);
        return s.Trim();
    }

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
        if (_waveIn != null)
        {
            try { _waveIn.DataAvailable -= OnData; } catch { /* */ }
            try { _waveIn.StopRecording(); } catch { /* */ }
            try { _waveIn.Dispose(); } catch { /* */ }
            _waveIn = null;
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
    static extern void vosk_recognizer_set_words(IntPtr recognizer, int words);

    [DllImport("libvosk", CallingConvention = CallingConvention.Cdecl)]
    static extern void vosk_recognizer_free(IntPtr recognizer);
}

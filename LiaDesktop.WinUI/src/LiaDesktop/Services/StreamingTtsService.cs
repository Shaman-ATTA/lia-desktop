using System.Diagnostics;

namespace LiaDesktop.Services;

/// <summary>
/// Speak-from-first-sentence pipeline: split → prefetch synth (window ~2) → ordered play.
/// Barge-in / Abort cancels current playback and all queued sentences.
/// </summary>
public sealed class StreamingTtsService
{
    readonly EdgeTts _tts;
    readonly BargeInService _barge;
    readonly AppLog _log;

    CancellationTokenSource? _pipeCts;
    readonly object _gate = new();
    readonly List<int> _playedOrder = [];

    /// <summary>Origin tick (Stopwatch) for the current user turn — set by AgentService.</summary>
    public long TurnOriginTimestamp { get; set; }

    public int LastTextReadyMs { get; private set; } = -1;
    public int LastFirstAudioByteMs { get; private set; } = -1;
    public int LastPlaybackStartMs { get; private set; } = -1;
    public int LastFullSynthMs { get; private set; } = -1;
    public IReadOnlyList<int> LastPlayedOrder
    {
        get { lock (_gate) return _playedOrder.ToList(); }
    }

    public event Action<string>? SpeakFile;
    public event Action<float, float, float>? Mouth;
    public event Action? PipelineEnded;

    public StreamingTtsService(EdgeTts tts, BargeInService barge, AppLog log)
    {
        _tts = tts;
        _barge = barge;
        _log = log;
        _barge.MouthStop += () => Mouth?.Invoke(0, 0, 0);
    }

    public void Cancel()
    {
        try { _pipeCts?.Cancel(); } catch { /* */ }
        _barge.AbortPlayback();
    }

    public async Task SpeakAsync(string reply, CancellationToken external)
    {
        Cancel();
        var pipeCts = CancellationTokenSource.CreateLinkedTokenSource(external);
        _pipeCts = pipeCts;
        var ct = pipeCts.Token;
        lock (_gate) _playedOrder.Clear();
        LastTextReadyMs = LastFirstAudioByteMs = LastPlaybackStartMs = LastFullSynthMs = -1;

        var origin = TurnOriginTimestamp > 0 ? TurnOriginTimestamp : Stopwatch.GetTimestamp();
        int ElapsedMs() => (int)Stopwatch.GetElapsedTime(origin).TotalMilliseconds;

        LastTextReadyMs = ElapsedMs();
        _log.Info($"t_text_ready={LastTextReadyMs}ms");

        var sentences = SentenceSplitter.Split(reply);
        if (sentences.Count == 0) return;

        _log.Info($"tts_pipeline: {sentences.Count} sentence(s)");
        try { await _tts.WarmAsync(ct); } catch (Exception ex) { _log.Info("tts warm: " + ex.Message); }

        var synthSw = Stopwatch.StartNew();
        var synthTasks = new Task<(int Idx, string Text, string? Path, int FirstAudioMs)>[sentences.Count];
        var synthGate = new object();

        Task<(int Idx, string Text, string? Path, int FirstAudioMs)> EnsureSynth(int idx)
        {
            lock (synthGate)
            {
                if (synthTasks[idx] != null) return synthTasks[idx];
                var t = SynthOneAsync(idx, sentences[idx], ElapsedMs, ct);
                synthTasks[idx] = t;
                return t;
            }
        }

        // Critical path: sentence 0 only at first. Prefetch i+1 while playing i.
        _ = EnsureSynth(0);

        var fullSynthTask = Task.Run(async () =>
        {
            try
            {
                // Measure true full-synthesis span (all sentences, sequential kick for timing)
                for (var i = 0; i < sentences.Count; i++)
                    await EnsureSynth(i);
            }
            catch { /* cancelled */ }
            finally
            {
                LastFullSynthMs = (int)synthSw.ElapsedMilliseconds;
                _log.Info($"t_full_synth={LastFullSynthMs}ms sentences={sentences.Count}");
            }
        }, CancellationToken.None);

        try
        {
            for (var i = 0; i < sentences.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (i + 1 < sentences.Count) _ = EnsureSynth(i + 1); // prefetch next

                var item = await EnsureSynth(i);
                if (i == 0)
                {
                    LastFirstAudioByteMs = item.FirstAudioMs;
                    _log.Info($"t_first_audio_byte={LastFirstAudioByteMs}ms");
                }

                if (string.IsNullOrEmpty(item.Path) || !File.Exists(item.Path))
                {
                    _log.Info($"tts_pipeline: skip empty sentence[{item.Idx}]");
                    continue;
                }

                SpeakFile?.Invoke(item.Path);
                if (i == 0)
                {
                    LastPlaybackStartMs = ElapsedMs();
                    _log.Info($"t_playback_start={LastPlaybackStartMs}ms");
                }

                _log.Info($"tts_play_seq={item.Idx} text={Truncate(item.Text, 60)}");
                lock (_gate) _playedOrder.Add(item.Idx);

                using var mouthCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var mouthTask = DriveMouthAsync(item.Text, mouthCts.Token);
                try
                {
                    var end = await _barge.PlayFileAsync(item.Path!, item.Text, ct);
                    if (end is BargeInService.PlayEnd.Barged or BargeInService.PlayEnd.Cancelled)
                    {
                        _log.Info($"tts_pipeline: stop after sentence[{item.Idx}] end={end} — cancel queue");
                        pipeCts.Cancel();
                        break;
                    }
                }
                finally
                {
                    mouthCts.Cancel();
                    try { await mouthTask; } catch { /* */ }
                }
            }
        }
        catch (OperationCanceledException)
        {
            _log.Info("tts_pipeline: cancelled");
        }
        finally
        {
            try { await fullSynthTask; } catch { /* */ }
            PipelineEnded?.Invoke();
        }
    }

    async Task<(int Idx, string Text, string? Path, int FirstAudioMs)> SynthOneAsync(
        int idx, string text, Func<int> elapsedMs, CancellationToken ct)
    {
        int? firstByteAt = null;
        void OnFirst(long _)
        {
            if (firstByteAt == null) firstByteAt = elapsedMs();
        }
        var path = await _tts.SpeakToFileAsync(text, ct, OnFirst);
        return (idx, text, path, firstByteAt ?? elapsedMs());
    }

    async Task DriveMouthAsync(string text, CancellationToken ct)
    {
        try
        {
            var vowels = text.Where(c => "аеёиоуыэюяaeiouАЕЁИОУЫЭЮЯ".Contains(c)).ToArray();
            if (vowels.Length == 0) vowels = ['а'];
            for (var i = 0; i < Math.Max(8, vowels.Length) && !ct.IsCancellationRequested; i++)
            {
                var shape = Visemes.FromChar(vowels[i % vowels.Length]);
                var (j, f, w) = Visemes.Morph(shape, 0.85f);
                Mouth?.Invoke(j, f, w);
                await Task.Delay(120, ct);
            }
            Mouth?.Invoke(0, 0, 0);
        }
        catch (OperationCanceledException) { Mouth?.Invoke(0, 0, 0); }
    }

    static string Truncate(string s, int n) =>
        s.Length <= n ? s : s[..n] + "…";
}

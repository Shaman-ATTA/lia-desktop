namespace LiaDesktop.Services;

/// <summary>
/// Energy/spectral VAD on mic residual after subtracting a delayed playback reference
/// (software AEC). Used while Lia speaks so her own TTS loopback does not
/// trigger barge-in; real user speech ≥ <see cref="MinSpeechMs"/> does.
/// </summary>
public sealed class EchoCancelVad
{
    public const int SampleRate = 16000;
    public const int MinSpeechMs = 300;

    readonly float[] _playRing;
    readonly int _ringMask;
    int _playWrite;
    int _playSamplesPushed;
    int _delaySamples;
    float _alpha = 0.9f;
    int _speechMs;
    int _silenceMs;
    bool _inSpeech;
    bool _triggered;

    public EchoCancelVad(int ringSeconds = 2)
    {
        var n = SampleRate * Math.Max(1, ringSeconds);
        var pow = 1;
        while (pow < n) pow <<= 1;
        _playRing = new float[pow];
        _ringMask = pow - 1;
        _delaySamples = (int)(0.04 * SampleRate);
    }

    public int SpeechMs => _speechMs;
    public bool Triggered => _triggered;
    public float LastResidualRms { get; private set; }
    public float LastMicRms { get; private set; }
    public float LastCorr { get; private set; }

    public void Reset()
    {
        Array.Clear(_playRing);
        _playWrite = 0;
        _playSamplesPushed = 0;
        _speechMs = 0;
        _silenceMs = 0;
        _inSpeech = false;
        _triggered = false;
        LastResidualRms = 0;
        LastMicRms = 0;
        LastCorr = 0;
        _alpha = 0.9f;
        _delaySamples = (int)(0.04 * SampleRate);
    }

    public void PushPlayback(ReadOnlySpan<float> mono16k)
    {
        for (var i = 0; i < mono16k.Length; i++)
        {
            _playRing[_playWrite] = mono16k[i];
            _playWrite = (_playWrite + 1) & _ringMask;
        }
        _playSamplesPushed += mono16k.Length;
    }

    /// <summary>
    /// Feed mic samples; returns true once residual user speech has been continuous ≥ MinSpeechMs.
    /// </summary>
    public bool PushMic(ReadOnlySpan<float> mono16k)
    {
        if (_triggered || mono16k.Length == 0) return _triggered;

        // Need a filled delay line before trusting residual
        if (_playSamplesPushed < _delaySamples + mono16k.Length)
            return false;

        double micE = 0, resE = 0, playE = 0, corr = 0;
        for (var i = 0; i < mono16k.Length; i++)
        {
            var m = mono16k[i];
            micE += m * m;
            var pi = (_playWrite - _delaySamples - (mono16k.Length - i)) & _ringMask;
            var p = _playRing[pi];
            playE += p * p;
            corr += m * p;
            var r = m - _alpha * p;
            resE += r * r;
        }

        var n = mono16k.Length;
        LastMicRms = (float)Math.Sqrt(micE / n);
        LastResidualRms = (float)Math.Sqrt(resE / n);
        var playRms = (float)Math.Sqrt(playE / n);
        var denom = Math.Sqrt(micE * playE) + 1e-9;
        LastCorr = (float)(corr / denom);

        // Hunt delay when playback is strong
        if (playRms > 0.02f)
            HuntDelay(mono16k);

        // Recompute residual with best delay + adapt alpha toward mic/play match
        if (playRms > 0.008f)
        {
            resE = 0; corr = 0; playE = 0;
            for (var i = 0; i < n; i++)
            {
                var m = mono16k[i];
                var pi = (_playWrite - _delaySamples - (n - i)) & _ringMask;
                var p = _playRing[pi];
                playE += p * p;
                corr += m * p;
                var r = m - _alpha * p;
                resE += r * r;
            }
            LastResidualRms = (float)Math.Sqrt(resE / n);
            playRms = (float)Math.Sqrt(playE / n);
            LastCorr = (float)(corr / (Math.Sqrt(micE * playE) + 1e-9));
            if (LastCorr > 0.5f && playRms > 0.01f)
            {
                var target = Math.Clamp(LastMicRms / (playRms + 1e-6f), 0.3f, 1.25f);
                _alpha += (target - _alpha) * 0.15f;
            }
        }

        var frameMs = Math.Max(1, n * 1000 / SampleRate);

        // Echo gate: high correlation with playback ⇒ her own voice / loopback
        var isEcho = LastCorr >= 0.55f && LastMicRms < playRms * 1.6f + 0.02f;
        // User speech: residual energy above echo floor AND not echo-coherent
        var floor = Math.Max(0.025f, playRms * 0.15f);
        var isSpeech = !isEcho
                       && LastResidualRms > floor
                       && LastMicRms > 0.03f
                       && LastCorr < 0.75f;

        if (!_inSpeech)
        {
            if (isSpeech)
            {
                _inSpeech = true;
                _speechMs = frameMs;
                _silenceMs = 0;
            }
        }
        else
        {
            if (isSpeech)
            {
                _speechMs += frameMs;
                _silenceMs = 0;
            }
            else
            {
                _silenceMs += frameMs;
                if (_silenceMs > 100)
                {
                    _inSpeech = false;
                    _speechMs = 0;
                }
            }
        }

        if (_speechMs >= MinSpeechMs)
            _triggered = true;
        return _triggered;
    }

    void HuntDelay(ReadOnlySpan<float> mic)
    {
        var best = _delaySamples;
        var bestScore = double.MinValue;
        var len = Math.Min(mic.Length, 320);
        for (var d = 0; d <= (int)(0.12 * SampleRate); d += 40)
        {
            double c = 0, pe = 0;
            for (var i = 0; i < len; i++)
            {
                var pi = (_playWrite - d - (len - i)) & _ringMask;
                var p = _playRing[pi];
                c += mic[i] * p;
                pe += p * p;
            }
            var score = c / (Math.Sqrt(pe) + 1e-9);
            if (score > bestScore)
            {
                bestScore = score;
                best = d;
            }
        }
        _delaySamples = best;
    }

    public static double ClampFrac(double frac) => Math.Clamp(frac, 0, 1);

    public static (string Spoken, string Remaining) SplitReply(string full, double playedFrac)
    {
        if (string.IsNullOrEmpty(full)) return ("", "");
        playedFrac = ClampFrac(playedFrac);
        if (playedFrac <= 0.02) return ("", full);
        if (playedFrac >= 0.98) return (full, "");

        var cut = (int)Math.Round(full.Length * playedFrac);
        cut = Math.Clamp(cut, 0, full.Length);

        var windowLo = Math.Max(0, cut - 40);
        var windowHi = Math.Min(full.Length - 1, cut + 40);
        var best = cut;
        for (var i = cut; i <= windowHi; i++)
        {
            if (i < full.Length && ".!?…".Contains(full[i]))
            {
                best = i + 1;
                break;
            }
        }
        if (best == cut)
        {
            for (var i = cut; i >= windowLo; i--)
            {
                if (i < full.Length && (full[i] == ' ' || ".!?…,".Contains(full[i])))
                {
                    best = i + 1;
                    break;
                }
            }
        }
        best = Math.Clamp(best, 0, full.Length);
        return (full[..best].Trim(), full[best..].Trim());
    }
}

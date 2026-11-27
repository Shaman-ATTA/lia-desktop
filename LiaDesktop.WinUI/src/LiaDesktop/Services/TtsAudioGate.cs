using NAudio.Wave;

namespace LiaDesktop.Services;

/// <summary>Reject empty/header-only TTS files (e.g. 46-byte WAV) and silent PCM.</summary>
public static class TtsAudioGate
{
    public const long MinBytes = 10_000;
    public const float MinRms = 0.01f;

    public static bool TryValidate(string? path, out long bytes, out float rms, out string detail)
    {
        bytes = 0;
        rms = 0;
        detail = "";
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            detail = "missing file";
            return false;
        }
        bytes = new FileInfo(path).Length;
        if (bytes < MinBytes)
        {
            detail = $"too small ({bytes} < {MinBytes})";
            return false;
        }

        try
        {
            rms = MeasureRms(path);
        }
        catch (Exception ex)
        {
            detail = "decode failed: " + ex.Message;
            return false;
        }

        if (rms < MinRms)
        {
            detail = $"silent/near-silent rms={rms:F4} < {MinRms}";
            return false;
        }

        detail = $"bytes={bytes} rms={rms:F4}";
        return true;
    }

    public static float MeasureRms(string path)
    {
        using var reader = CreateReader(path);
        var provider = reader.ToSampleProvider();
        var buf = new float[provider.WaveFormat.SampleRate * provider.WaveFormat.Channels / 5]; // 200ms
        double sum = 0;
        long n = 0;
        int read;
        // Sample up to ~3s for speed
        var budget = provider.WaveFormat.SampleRate * provider.WaveFormat.Channels * 3;
        while (n < budget && (read = provider.Read(buf, 0, buf.Length)) > 0)
        {
            for (var i = 0; i < read; i++)
                sum += buf[i] * buf[i];
            n += read;
        }
        if (n <= 0) return 0;
        return (float)Math.Sqrt(sum / n);
    }

    static WaveStream CreateReader(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".wav")
            return new WaveFileReader(path);
        // mp3 / other — MediaFoundation on Windows
        return new MediaFoundationReader(path);
    }
}

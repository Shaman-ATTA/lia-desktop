using System.IO.Compression;
using System.Net.Http;

namespace LiaDesktop.Services;

/// <summary>
/// Resolve / download vosk-model-small-ru under LocalAppData (or override for selftest).
/// </summary>
public static class VoskModelStore
{
    public const string ModelFolderName = "vosk-model-small-ru-0.22";
    public const string ModelZipUrl = "https://alphacephei.com/vosk/models/vosk-model-small-ru-0.22.zip";

    /// <summary>Selftest / CI: parent dir that contains vosk-model-small-ru-0.22.</summary>
    public static string? ModelRootOverride { get; set; }

    /// <summary>Selftest / CI: absolute path to the model folder (am/final.mdl inside).</summary>
    public static string? ModelPathOverride { get; set; }

    public static string DefaultRoot()
    {
        if (!string.IsNullOrWhiteSpace(ModelRootOverride)) return ModelRootOverride!;
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LiaDesktop", "vosk");
    }

    public static string ModelPath()
    {
        if (!string.IsNullOrWhiteSpace(ModelPathOverride)) return ModelPathOverride!;
        return Path.Combine(DefaultRoot(), ModelFolderName);
    }

    public static bool IsReady() =>
        Directory.Exists(ModelPath()) &&
        File.Exists(Path.Combine(ModelPath(), "am", "final.mdl"));

    /// <summary>Honor LIA_VOSK_MODEL env (folder with am/final.mdl or parent vosk root).</summary>
    public static void ApplyEnvOverride()
    {
        var env = Environment.GetEnvironmentVariable("LIA_VOSK_MODEL");
        if (string.IsNullOrWhiteSpace(env)) return;
        env = env.Trim().Trim('"');
        if (File.Exists(Path.Combine(env, "am", "final.mdl")))
        {
            ModelPathOverride = env;
            return;
        }
        var nested = Path.Combine(env, ModelFolderName);
        if (File.Exists(Path.Combine(nested, "am", "final.mdl")))
        {
            ModelRootOverride = env;
            ModelPathOverride = nested;
        }
    }

    public static async Task<string> EnsureAsync(
        AppLog log,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (IsReady()) return ModelPath();

        var root = DefaultRoot();
        Directory.CreateDirectory(root);
        var zip = Path.Combine(root, ModelFolderName + ".zip");
        log.Info("vosk: downloading small-ru model…");

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        using var res = await http.GetAsync(ModelZipUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        res.EnsureSuccessStatusCode();
        var total = res.Content.Headers.ContentLength ?? -1;
        await using (var src = await res.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(zip))
        {
            var buf = new byte[256 * 1024];
            long read = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                read += n;
                if (total > 0) progress?.Report(read / (double)total);
            }
        }

        log.Info("vosk: extracting model…");
        var dest = Path.Combine(root, ModelFolderName);
        if (Directory.Exists(dest)) Directory.Delete(dest, true);
        ZipFile.ExtractToDirectory(zip, root);
        try { File.Delete(zip); } catch { /* */ }

        // Zip may extract as vosk-model-small-ru-0.22 already
        if (!IsReady())
        {
            var found = Directory.GetDirectories(root, "vosk-model-small-ru*")
                .FirstOrDefault(d => File.Exists(Path.Combine(d, "am", "final.mdl")));
            if (found != null && !string.Equals(found, dest, StringComparison.OrdinalIgnoreCase))
            {
                if (Directory.Exists(dest)) Directory.Delete(dest, true);
                Directory.Move(found, dest);
            }
        }

        if (!IsReady())
            throw new InvalidOperationException("vosk model extract failed — am/final.mdl missing");

        log.Info("vosk: model ready at " + ModelPath());
        progress?.Report(1);
        return ModelPath();
    }
}

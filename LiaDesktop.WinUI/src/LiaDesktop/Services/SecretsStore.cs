using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LiaDesktop.Services;

public sealed class LiaConfig
{
    public string Provider { get; set; } = "gemini"; // gemini | ollama
    public string ApiKey { get; set; } = "";
    public string BaseUrl { get; set; } = "http://127.0.0.1:11434/v1";
    public string Model { get; set; } = "";
    public string VisionModel { get; set; } = "";
    public string TtsVoice { get; set; } = "SvetlanaNeural";
    /// <summary>Empty = system default multimedia render. Else MMDevice.ID.</summary>
    public string AudioOutputDeviceId { get; set; } = "";
    public string WorkspacePath { get; set; } = "";
}

public sealed class SecretsStore
{
    static readonly HashSet<string> Retired = new(StringComparer.OrdinalIgnoreCase)
    {
        "gemini-2.0-flash", "gemini-2.0-flash-001", "gemini-2.0-flash-lite",
        "models/gemini-2.0-flash",
        // Live 2026-11-26: 2.5 flash "no longer available to new users"
        "gemini-2.5-flash", "gemini-2.5-flash-001", "gemini-2.5-flash-latest",
        "models/gemini-2.5-flash",
    };

    /// <summary>Override config dir (selftest uses a temp folder so user DPAPI is untouched).</summary>
    public static string? ConfigDirOverride { get; set; }

    readonly string _path;

    public SecretsStore()
    {
        var dir = ConfigDirOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LiaDesktop");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "lia-config.dpapi");
    }

    public LiaConfig Load()
    {
        var cfg = new LiaConfig
        {
            WorkspacePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "LiaWorkspace"),
        };
        if (!File.Exists(_path)) return Scrub(cfg);
        try
        {
            var enc = File.ReadAllBytes(_path);
            var json = Encoding.UTF8.GetString(ProtectedData.Unprotect(enc, null, DataProtectionScope.CurrentUser));
            var loaded = JsonSerializer.Deserialize<LiaConfig>(json) ?? cfg;
            if (string.IsNullOrWhiteSpace(loaded.WorkspacePath))
                loaded.WorkspacePath = cfg.WorkspacePath;
            return Scrub(loaded);
        }
        catch
        {
            return Scrub(cfg);
        }
    }

    public LiaConfig Save(LiaConfig cfg)
    {
        cfg = Scrub(cfg);
        var json = JsonSerializer.Serialize(cfg);
        var enc = ProtectedData.Protect(Encoding.UTF8.GetBytes(json), null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(_path, enc);
        return cfg;
    }

    static LiaConfig Scrub(LiaConfig cfg)
    {
        var m = (cfg.Model ?? "").Replace("models/", "");
        if (string.IsNullOrWhiteSpace(m) || Retired.Contains(m) || GeminiModels.IsRetired(m)
            || (!string.IsNullOrEmpty(cfg.Model) && Retired.Contains(cfg.Model)))
            cfg.Model = "";
        if (string.IsNullOrWhiteSpace(cfg.VisionModel) || Retired.Contains(cfg.VisionModel!)
            || GeminiModels.IsRetired(cfg.VisionModel))
            cfg.VisionModel = cfg.Model;
        return cfg;
    }
}

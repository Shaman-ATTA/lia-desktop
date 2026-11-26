namespace LiaDesktop.Services;

/// <summary>Gemini v1beta base URL — override with --gemini-base= or LIA_GEMINI_BASE for mock CI.</summary>
public static class GeminiEndpoints
{
    public const string DefaultBase = "https://generativelanguage.googleapis.com/v1beta";

    static string _base = DefaultBase;

    public static string Base
    {
        get => _base.TrimEnd('/');
        set => _base = string.IsNullOrWhiteSpace(value) ? DefaultBase : value.TrimEnd('/');
    }

    public static string ListModels => $"{Base}/models";
    public static string GenerateContent(string model) =>
        $"{Base}/models/{Uri.EscapeDataString(model)}:generateContent";

    public static void ApplyFromArgs(string[] args)
    {
        foreach (var a in args)
        {
            if (a.StartsWith("--gemini-base=", StringComparison.OrdinalIgnoreCase))
            {
                Base = a["--gemini-base=".Length..];
                return;
            }
        }
        var env = Environment.GetEnvironmentVariable("LIA_GEMINI_BASE");
        if (!string.IsNullOrWhiteSpace(env))
            Base = env;
    }
}

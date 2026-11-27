using LiaDesktop.Services.Tools;

namespace LiaDesktop.Services;

public sealed class AppServices
{
    public AppLog Log { get; } = new();
    public SecretsStore Secrets { get; } = new();
    public GeminiClient Gemini { get; private set; } = null!;
    public EdgeTts Tts { get; private set; } = null!;
    public ToolHost Tools { get; private set; } = null!;
    public AgentService Agent { get; private set; } = null!;
    public SpeechListen Speech { get; } = new();
    public MicGeminiListen MicGemini { get; private set; } = null!;
    public BargeInService BargeIn { get; private set; } = null!;
    public ConversationMemory Memory { get; private set; } = null!;
    public LiaConfig Config { get; private set; } = new();

    /// <summary>True when Windows ru-RU speech works; otherwise Gemini mic is the default.</summary>
    public bool WindowsSpeechOk => Speech.IsAvailable;

    public void Bootstrap()
    {
        Config = Secrets.Load();
        Directory.CreateDirectory(Config.WorkspacePath);
        Gemini = new GeminiClient(Secrets, Log);
        Tts = new EdgeTts(Log);
        Tools = new ToolHost(Log, () => Secrets.Load().WorkspacePath);
        BargeIn = new BargeInService(Gemini, Log);
        Memory = new ConversationMemory(Log);
        Agent = new AgentService(Gemini, Secrets, Log, Tools, Tts, BargeIn, Memory);
        MicGemini = new MicGeminiListen(Gemini, Log);
        _ = Gemini.EnsureModelAsync();
        _ = InitSpeechAsync();
    }

    async Task InitSpeechAsync()
    {
        await Speech.InitAsync();
        if (!Speech.IsAvailable)
            Log.Info("Windows Speech (ru-RU) недоступен — микрофон через Gemini STT.");
    }

    public async Task ShutdownAsync()
    {
        Agent.Abort();
        Tts.Stop();
        try { BargeIn.AbortPlayback(); } catch { /* */ }
        try { await MicGemini.StopAsync(); } catch { /* */ }
        BargeIn.Dispose();
        MicGemini.Dispose();
        Speech.Dispose();
        await Tools.DisposeAsync();
    }
}

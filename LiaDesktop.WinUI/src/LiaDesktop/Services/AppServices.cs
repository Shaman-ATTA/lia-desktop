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
    public LiaConfig Config { get; private set; } = new();

    public void Bootstrap()
    {
        Config = Secrets.Load();
        Directory.CreateDirectory(Config.WorkspacePath);
        Gemini = new GeminiClient(Secrets, Log);
        Tts = new EdgeTts(Log);
        Tools = new ToolHost(Log, () => Secrets.Load().WorkspacePath);
        Agent = new AgentService(Gemini, Secrets, Log, Tools, Tts);
        _ = Gemini.EnsureModelAsync();
        _ = Speech.InitAsync();
    }

    public async Task ShutdownAsync()
    {
        Agent.Abort();
        Tts.Stop();
        Speech.Dispose();
        await Tools.DisposeAsync();
    }
}

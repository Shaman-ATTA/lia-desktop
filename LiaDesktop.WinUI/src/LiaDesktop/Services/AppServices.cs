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
    public AudioOutput Audio { get; private set; } = null!;
    public ConversationMemory Memory { get; private set; } = null!;
    public LongTermMemory LongTerm { get; private set; } = null!;
    public WakeWordService Wake { get; private set; } = null!;
    public SystemTelemetry Telemetry { get; } = new();
    public LiaConfig Config { get; private set; } = new();

    /// <summary>True when Windows ru-RU speech works; otherwise Gemini mic is the default.</summary>
    public bool WindowsSpeechOk => Speech.IsAvailable;

    public void Bootstrap()
    {
        Config = Secrets.Load();
        Directory.CreateDirectory(Config.WorkspacePath);
        Gemini = new GeminiClient(Secrets, Log);
        Tts = new EdgeTts(Log);
        LongTerm = new LongTermMemory(Log);
        Tools = new ToolHost(
            Log,
            () => Secrets.Load().WorkspacePath,
            () => LongTerm,
            () => Secrets.Load().ConfirmDestructiveActions);
        Audio = new AudioOutput(Log);
        Audio.SetPreferredDeviceId(Config.AudioOutputDeviceId);
        BargeIn = new BargeInService(Gemini, Log, Audio);
        Memory = new ConversationMemory(Log);
        Agent = new AgentService(Gemini, Secrets, Log, Tools, Tts, BargeIn, Memory, LongTerm);
        MicGemini = new MicGeminiListen(Gemini, Log);
        Wake = new WakeWordService(Log);
        Wake.Enabled = Config.WakeWordEnabled;
        Wake.Sensitivity = Config.WakeWordSensitivity;
        // Echo/loopback guard — own TTS must not self-wake
        BargeIn.PlaybackStarted += () => Wake.SetEchoGuard(true);
        BargeIn.PlaybackStopped += () => Wake.SetEchoGuard(false);
        try { Log.WriteStartupDiagnostics(this); } catch { /* */ }
        _ = Gemini.EnsureModelAsync();
        _ = InitSpeechAsync();
        _ = InitWakeAsync();
    }

    async Task InitSpeechAsync()
    {
        await Speech.InitAsync();
        if (!Speech.IsAvailable)
            Log.Info("Windows Speech (ru-RU) недоступен — микрофон через Gemini STT.");
    }

    async Task InitWakeAsync()
    {
        if (SelfTestRunner.IsSelfTest) return; // selftest loads model explicitly
        if (!Config.WakeWordEnabled)
        {
            Log.Info("wake: disabled in settings");
            return;
        }
        try
        {
            await Wake.EnsureModelAsync();
            Wake.Start();
        }
        catch (Exception ex)
        {
            Log.Info("wake: model unavailable (offline?) — " + ex.Message);
        }
    }

    public void ApplyWakeSettings(LiaConfig cfg)
    {
        Config = cfg;
        Wake.Enabled = cfg.WakeWordEnabled;
        Wake.Sensitivity = cfg.WakeWordSensitivity;
        if (cfg.WakeWordEnabled)
        {
            if (Wake.IsReady) Wake.Start();
            else _ = InitWakeAsync();
        }
        else Wake.Stop();
    }

    public async Task ShutdownAsync()
    {
        Agent.Abort();
        Tts.Stop();
        try { Wake.Stop(); } catch { /* */ }
        try { BargeIn.AbortPlayback(); } catch { /* */ }
        try { await MicGemini.StopAsync(); } catch { /* */ }
        Wake.Dispose();
        BargeIn.Dispose();
        Audio.Dispose();
        MicGemini.Dispose();
        Speech.Dispose();
        await Tools.DisposeAsync();
    }
}

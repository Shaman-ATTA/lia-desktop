using Windows.Globalization;
using Windows.Media.SpeechRecognition;

namespace LiaDesktop.Services;

/// <summary>Windows speech recognition (ru-RU). Falls back to manual text if unavailable.</summary>
public sealed class SpeechListen : IDisposable
{
    SpeechRecognizer? _rec;
    public bool IsAvailable { get; private set; }
    public event Action<string>? Hypothesis;
    public event Action<string>? FinalResult;

    public async Task InitAsync()
    {
        try
        {
            var lang = new Language("ru-RU");
            _rec = new SpeechRecognizer(lang);
            await _rec.CompileConstraintsAsync();
            _rec.HypothesisGenerated += (_, e) =>
            {
                var t = e.Result?.Text;
                if (!string.IsNullOrWhiteSpace(t)) Hypothesis?.Invoke(t);
            };
            _rec.ContinuousRecognitionSession.ResultGenerated += (_, e) =>
            {
                var t = e.Result?.Text;
                if (!string.IsNullOrWhiteSpace(t)) FinalResult?.Invoke(t);
            };
            IsAvailable = true;
        }
        catch
        {
            IsAvailable = false;
            _rec?.Dispose();
            _rec = null;
        }
    }

    public async Task StartAsync()
    {
        if (_rec == null) return;
        await _rec.ContinuousRecognitionSession.StartAsync();
    }

    public async Task StopAsync()
    {
        if (_rec == null) return;
        try { await _rec.ContinuousRecognitionSession.StopAsync(); } catch { /* */ }
    }

    public void Dispose()
    {
        _rec?.Dispose();
        _rec = null;
    }
}

using LiaDesktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using WinRT.Interop;

namespace LiaDesktop;

public sealed partial class MainWindow : Window
{
    readonly AppServices _s = App.Services;
    readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly MediaPlayer _player = new();
    HotkeyService? _hotkey;
    bool _listening;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar); // dedicated drag region; caption buttons clear of НАСТРОЙКИ
        VoiceLine.Text = "VOICE  ОЖИДАНИЕ";
        RefreshModelLine();
        Closed += OnClosed;

        _s.Log.Added += OnLog;
        _s.Gemini.ModelChanged += _ => DispatcherQueue.TryEnqueue(RefreshModelLine);
        _s.Agent.Mouth += (j, f, w) => DispatcherQueue.TryEnqueue(() =>
        {
            Holo.Renderer.Jaw = j;
            Holo.Renderer.Funnel = f;
            Holo.Renderer.Wide = w;
        });
        _s.Agent.Narrate += t => DispatcherQueue.TryEnqueue(() =>
        {
            StatusCaption.Text = "ГОВОРЮ";
            Subtitle.Text = t.Length > 140 ? t[..140] : t;
            VoiceLine.Text = "VOICE  ГОВОРЮ";
        });
        _s.Agent.SpeakFile += path => DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                _player.Source = MediaSource.CreateFromUri(new Uri(path));
                _player.Play();
                _player.MediaEnded += (_, _) => DispatcherQueue.TryEnqueue(() =>
                {
                    StatusCaption.Text = "ОЖИДАНИЕ";
                    VoiceLine.Text = "VOICE  ОЖИДАНИЕ";
                    Subtitle.Text = "Скажите «Лиа» или нажмите микрофон";
                    Holo.Renderer.Jaw = 0;
                    Holo.Renderer.Funnel = 0;
                    Holo.Renderer.Wide = 0;
                });
            }
            catch { /* */ }
        });

        _s.Speech.FinalResult += OnSpeechFinal;
        _clock.Tick += (_, _) =>
        {
            var now = DateTime.Now;
            SysLine.Text = $"{now:yyyy.MM.dd}  {now:HH:mm:ss}  LINK OK";
            var model = ModelLabel();
            SysPanel.Text = $"TIME  {now:HH:mm:ss}\nDATE  {now:yyyy.MM.dd}\nMODEL {model}";
            // Keep STATUS MODEL in sync even before ModelChanged fires
            if (ModelLine.Text is null || !ModelLine.Text.Contains(model, StringComparison.Ordinal))
                ModelLine.Text = $"MODEL  {model}";
        };
        _clock.Start();

        Activated += OnActivated;
        foreach (var (line, detail) in _s.Log.Lines) AddLog(line, detail);

        ApplySmokeOverrides();
    }

    void RefreshModelLine() => ModelLine.Text = $"MODEL  {ModelLabel()}";

    string ModelLabel()
    {
        var cfg = _s.Secrets.Load();
        if (cfg.Provider == "gemini" && string.IsNullOrWhiteSpace(cfg.ApiKey))
            return "нет ключа";
        if (!string.IsNullOrWhiteSpace(_s.Gemini.ActiveModel))
            return _s.Gemini.ActiveModel;
        if (!string.IsNullOrWhiteSpace(cfg.Model))
            return cfg.Model;
        return cfg.Provider == "ollama" ? "ollama" : "нет ключа";
    }

    void ApplySmokeOverrides()
    {
        // LIA_SMOKE_YAW=35 LIA_SMOKE_JAW=0.7 — CI capture modes
        float? yaw = null, jaw = null;
        if (float.TryParse(Environment.GetEnvironmentVariable("LIA_SMOKE_YAW"), out var y)) yaw = y;
        if (float.TryParse(Environment.GetEnvironmentVariable("LIA_SMOKE_JAW"),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var j)) jaw = j;
        foreach (var a in Environment.GetCommandLineArgs())
        {
            if (a.StartsWith("--smoke-yaw=", StringComparison.OrdinalIgnoreCase) &&
                float.TryParse(a["--smoke-yaw=".Length..], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var y2)) yaw = y2;
            if (a.StartsWith("--smoke-jaw=", StringComparison.OrdinalIgnoreCase) &&
                float.TryParse(a["--smoke-jaw=".Length..], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var j2)) jaw = j2;
        }
        if (yaw.HasValue || jaw.HasValue)
        {
            Holo.Renderer.ForcePose(yaw, jaw);
            if (jaw is > 0.05f)
            {
                StatusCaption.Text = "ГОВОРЮ";
                VoiceLine.Text = "VOICE  ГОВОРЮ";
                Subtitle.Text = "Демонстрация визем · губы открыты";
            }
            else if (yaw is { } yAbs && Math.Abs(yAbs) >= 20f)
            {
                StatusCaption.Text = "3/4";
                Subtitle.Text = "Ракурс · idle orbit";
            }
            else if (yaw.HasValue)
            {
                StatusCaption.Text = "ФРОНТ";
                Subtitle.Text = "Скажите «Лиа» или нажмите микрофон";
            }
        }
    }

    void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_hotkey != null) return;
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            _hotkey = new HotkeyService(hwnd);
            _hotkey.Triggered += () => DispatcherQueue.TryEnqueue(() =>
            {
                _s.Agent.Abort();
                StatusCaption.Text = "ОСТАНОВЛЕНО";
                VoiceLine.Text = "VOICE  СТОП";
            });
        }
        catch (Exception ex)
        {
            _s.Log.Error("Хоткей недоступен", ex.Message);
        }
    }

    async void OnClosed(object sender, WindowEventArgs args)
    {
        _clock.Stop();
        _hotkey?.Dispose();
        _player.Dispose();
        await _s.ShutdownAsync();
    }

    void OnLog((string Line, string? Detail) e) =>
        DispatcherQueue.TryEnqueue(() => AddLog(e.Line, e.Detail));

    void AddLog(string line, string? detail)
    {
        if (detail == null)
        {
            LogPanel.Children.Insert(0, new TextBlock
            {
                Text = line,
                Foreground = (Brush)Application.Current.Resources["LiaCyan"],
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
            });
        }
        else
        {
            var exp = new Expander
            {
                Header = line,
                IsExpanded = false,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            exp.Content = new TextBlock
            {
                Text = detail,
                FontSize = 10,
                Foreground = (Brush)Application.Current.Resources["LiaMuted"],
                TextWrapping = TextWrapping.Wrap,
            };
            LogPanel.Children.Insert(0, exp);
        }
        while (LogPanel.Children.Count > 80) LogPanel.Children.RemoveAt(LogPanel.Children.Count - 1);
    }

    void Send_Click(object sender, RoutedEventArgs e) => _ = SendAsync();
    void CommandBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) _ = SendAsync();
    }

    async Task SendAsync()
    {
        var text = CommandBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(text)) return;
        CommandBox.Text = "";
        StatusCaption.Text = "ДУМАЮ";
        VoiceLine.Text = "VOICE  ДУМАЮ";
        await _s.Agent.RunAsync(text);
    }

    async void Mic_Click(object sender, RoutedEventArgs e)
    {
        if (!_s.Speech.IsAvailable)
        {
            _s.Log.Info("Распознавание речи Windows недоступно — введите текст.");
            CommandBox.Focus(FocusState.Programmatic);
            return;
        }
        if (_listening)
        {
            await _s.Speech.StopAsync();
            _listening = false;
            MicBtn.Content = "🎤";
            VoiceLine.Text = "VOICE  ОЖИДАНИЕ";
        }
        else
        {
            await _s.Speech.StartAsync();
            _listening = true;
            MicBtn.Content = "■";
            VoiceLine.Text = "VOICE  СЛУШАЮ";
            StatusCaption.Text = "СЛУШАЮ";
        }
    }

    void OnSpeechFinal(string text)
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (AgentService.IsStopPhrase(text))
            {
                _s.Agent.Abort();
                return;
            }
            CommandBox.Text = text;
            await SendAsync();
        });
    }

    void Settings_Click(object sender, RoutedEventArgs e)
    {
        var cfg = _s.Secrets.Load();
        ProviderBox.SelectedIndex = cfg.Provider == "ollama" ? 1 : 0;
        ApiKeyBox.Password = cfg.ApiKey;
        WorkspaceBox.Text = cfg.WorkspacePath;
        ModelBox.Items.Clear();
        if (!string.IsNullOrEmpty(cfg.Model))
        {
            ModelBox.Items.Add(cfg.Model);
            ModelBox.SelectedIndex = 0;
        }
        SettingsOverlay.Visibility = Visibility.Visible;
        if (cfg.Provider != "ollama" && !string.IsNullOrEmpty(cfg.ApiKey))
            _ = RefreshModelsAsync();
    }

    void SettingsCancel_Click(object sender, RoutedEventArgs e) =>
        SettingsOverlay.Visibility = Visibility.Collapsed;

    async void SettingsSave_Click(object sender, RoutedEventArgs e)
    {
        var cfg = _s.Secrets.Load();
        cfg.Provider = ProviderBox.SelectedIndex == 1 ? "ollama" : "gemini";
        if (!string.IsNullOrWhiteSpace(ApiKeyBox.Password))
            cfg.ApiKey = ApiKeyBox.Password;
        cfg.WorkspacePath = WorkspaceBox.Text?.Trim() ?? cfg.WorkspacePath;
        cfg.Model = ModelBox.SelectedItem?.ToString() ?? cfg.Model;
        cfg.VisionModel = cfg.Model;
        _s.Secrets.Save(cfg);
        SettingsOverlay.Visibility = Visibility.Collapsed;
        await _s.Gemini.EnsureModelAsync();
        RefreshModelLine();
        _s.Log.Info($"Сохранено · {ModelLabel()}");
    }

    async void RefreshModels_Click(object sender, RoutedEventArgs e) => await RefreshModelsAsync();

    async Task RefreshModelsAsync()
    {
        ModelsHint.Text = "Загрузка ListModels…";
        try
        {
            var key = string.IsNullOrWhiteSpace(ApiKeyBox.Password)
                ? _s.Secrets.Load().ApiKey
                : ApiKeyBox.Password;
            var models = await GeminiModels.ListAsync(key);
            ModelBox.Items.Clear();
            foreach (var m in models) ModelBox.Items.Add(m.Id);
            if (ModelBox.Items.Count > 0) ModelBox.SelectedIndex = 0;
            ModelsHint.Text = $"{models.Count} моделей";
        }
        catch (Exception ex)
        {
            var c = GeminiModels.Classify(ex);
            ModelsHint.Text = c.UserMessage;
        }
    }
}

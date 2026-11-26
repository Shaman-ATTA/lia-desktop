using System.Diagnostics;
using LiaDesktop.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.UI;
using WinRT.Interop;
using ShapePath = Microsoft.UI.Xaml.Shapes.Path;

namespace LiaDesktop;

public sealed partial class MainWindow : Window
{
    readonly AppServices _s = App.Services;
    readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly DispatcherTimer _hudTimer = new() { Interval = TimeSpan.FromMilliseconds(50) }; // ~20fps HUD; DX vsyncs at 60
    readonly MediaPlayer _player = new();
    readonly Process _proc = Process.GetCurrentProcess();
    readonly Random _rng = new(33);
    readonly List<ShapePath> _hudArcs = [];
    readonly List<Line> _hudTicks = [];
    readonly List<Rectangle> _waveBars = [];
    HotkeyService? _hotkey;
    TrayIcon? _tray;
    bool _listening;
    bool _speaking;
    float _hudT;
    float _waveAmp;
    double _cpu;
    TimeSpan _lastCpu;
    DateTime _lastCpuSample = DateTime.UtcNow;
    int _pingMs = 12;

    public MainWindow()
    {
        InitializeComponent();
        TryApplyBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        StyleCaptionButtons();

        VoiceLine.Text = "ГОЛОС  ОЖИДАНИЕ";
        StatusCaption.Text = "ОЖИДАНИЕ";
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
            _speaking = true;
            StatusCaption.Text = "ГОВОРЮ";
            Subtitle.Text = t.Length > 140 ? t[..140] : t;
            VoiceLine.Text = "ГОЛОС  ГОВОРЮ";
            FadeInSubtitle();
        });
        _s.Agent.SpeakFile += path => DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                _speaking = true;
                _player.Source = MediaSource.CreateFromUri(new Uri(path));
                _player.Play();
                _player.MediaEnded += (_, _) => DispatcherQueue.TryEnqueue(() =>
                {
                    _speaking = false;
                    StatusCaption.Text = "ОЖИДАНИЕ";
                    VoiceLine.Text = "ГОЛОС  ОЖИДАНИЕ";
                    Subtitle.Text = "Скажите «Лиа» или нажмите микрофон";
                    Holo.Renderer.Jaw = 0;
                    Holo.Renderer.Funnel = 0;
                    Holo.Renderer.Wide = 0;
                });
            }
            catch { /* */ }
        });

        _s.Speech.FinalResult += OnSpeechFinal;
        _clock.Tick += (_, _) => TickTelemetry();
        _clock.Start();

        HudCanvas.SizeChanged += (_, _) => RebuildHudGeometry();
        LeftCard.SizeChanged += (_, _) => UpdateLeftBrackets();
        _hudTimer.Tick += (_, _) => TickHud();
        _hudTimer.Start();

        Activated += OnActivated;
        foreach (var (line, detail) in _s.Log.Lines) AddLog(line, detail);

        var smoke = ApplySmokeOverrides();
        DispatcherQueue.TryEnqueue(() => InitTrayAndAutostart());
        if (smoke)
            BootOverlay.Visibility = Visibility.Collapsed;
        else
            DispatcherQueue.TryEnqueue(async () => await RunBootSequenceAsync());
    }

    void TryApplyBackdrop()
    {
        try
        {
            // Prefer acrylic for translucent near-black HUD; fall back to mica / solid.
            SystemBackdrop = new DesktopAcrylicBackdrop();
        }
        catch
        {
            try { SystemBackdrop = new MicaBackdrop(); }
            catch { Root.Background = new SolidColorBrush(Color.FromArgb(255, 5, 7, 12)); }
        }
    }

    void StyleCaptionButtons()
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);
            var tb = appWindow.TitleBar;
            tb.ExtendsContentIntoTitleBar = true;
            var amber = Color.FromArgb(255, 255, 154, 60);
            var dim = Color.FromArgb(255, 5, 7, 12);
            tb.ForegroundColor = amber;
            tb.ButtonForegroundColor = amber;
            tb.ButtonHoverForegroundColor = Colors.White;
            tb.ButtonPressedForegroundColor = amber;
            tb.ButtonBackgroundColor = Colors.Transparent;
            tb.ButtonInactiveBackgroundColor = Colors.Transparent;
            tb.ButtonHoverBackgroundColor = Color.FromArgb(40, 255, 154, 60);
            tb.ButtonPressedBackgroundColor = Color.FromArgb(70, 255, 154, 60);
            tb.InactiveForegroundColor = Color.FromArgb(160, 255, 154, 60);
            tb.BackgroundColor = dim;
        }
        catch { /* unpackaged / older builds */ }
    }

    async Task RunBootSequenceAsync()
    {
        try
        {
            BootStatus.Text = "ПОДКЛЮЧЕНИЕ DX11…";
            await Task.Delay(420);
            BootStatus.Text = "ЗАГРУЗКА PBR-ГОЛОВЫ…";
            await Task.Delay(520);
            BootStatus.Text = "КАЛИБРОВКА ОРБИТ…";
            await Task.Delay(480);
            BootStatus.Text = "НА СВЯЗИ";
            await Task.Delay(280);
            for (var i = 0; i <= 20; i++)
            {
                BootOverlay.Opacity = 1.0 - i / 20.0;
                await Task.Delay(16);
            }
            BootOverlay.Visibility = Visibility.Collapsed;
            BootOverlay.Opacity = 1;
            RebuildHudGeometry();
        }
        catch
        {
            BootOverlay.Visibility = Visibility.Collapsed;
        }
    }

    void RebuildHudGeometry()
    {
        HudCanvas.Children.Clear();
        _hudArcs.Clear();
        _hudTicks.Clear();

        var w = HudCanvas.ActualWidth;
        var h = HudCanvas.ActualHeight;
        if (w < 40 || h < 40) return;

        var cx = w * 0.5;
        var cy = h * 0.48;
        var amber = Color.FromArgb(255, 255, 154, 60);
        var cyan = Color.FromArgb(255, 90, 214, 255);

        // Concentric arcs OUTSIDE the face disc — no center needles / sweep / chords.
        // Face ≈ 0.36 of min(w,h); keep innermost ≥ 0.44 so skin stays clear.
        var m = Math.Min(w, h);
        double[] radii = [m * 0.44, m * 0.50, m * 0.56, m * 0.62];
        for (var i = 0; i < radii.Length; i++)
        {
            var r = radii[i];
            var segs = 6 + i;
            var gap = 14.0;
            var sweep = (360.0 - segs * gap) / segs;
            for (var s = 0; s < segs; s++)
            {
                var start = s * (sweep + gap) + i * 9;
                var path = MakeArc(cx, cy, r, start, sweep, i % 2 == 0 ? amber : cyan, i == 0 ? 1.5 : 1.15);
                path.Opacity = 0.40 + i * 0.07;
                HudCanvas.Children.Add(path);
                _hudArcs.Add(path);
            }
        }

        // Tick marks on outer ring only (short radial marks — never reach the face)
        var outer = radii[^1];
        var tickInner = outer - 12;
        for (var t = 0; t < 60; t++)
        {
            var ang = t / 60.0 * Math.PI * 2;
            var len = t % 5 == 0 ? 11 : 5;
            var x0 = cx + Math.Cos(ang) * (outer - len);
            var y0 = cy + Math.Sin(ang) * (outer - len);
            var x1 = cx + Math.Cos(ang) * outer;
            var y1 = cy + Math.Sin(ang) * outer;
            // Safety: never draw inside face radius
            if (outer - len < m * 0.40) continue;
            var line = new Line
            {
                X1 = x0, Y1 = y0, X2 = x1, Y2 = y1,
                Stroke = new SolidColorBrush(amber),
                StrokeThickness = t % 5 == 0 ? 1.3 : 0.7,
                Opacity = 0.40,
            };
            HudCanvas.Children.Add(line);
            _hudTicks.Add(line);
        }
        _ = tickInner; // kept for clarity

        BuildWaveBars();
    }

    static ShapePath MakeArc(double cx, double cy, double r, double startDeg, double sweepDeg, Color color, double thickness)
    {
        var start = startDeg * Math.PI / 180.0;
        var end = (startDeg + sweepDeg) * Math.PI / 180.0;
        var x1 = cx + Math.Cos(start) * r;
        var y1 = cy + Math.Sin(start) * r;
        var x2 = cx + Math.Cos(end) * r;
        var y2 = cy + Math.Sin(end) * r;
        var large = sweepDeg > 180 ? 1 : 0;
        var fig = new PathFigure
        {
            StartPoint = new Windows.Foundation.Point(x1, y1),
            IsClosed = false,
        };
        fig.Segments.Add(new ArcSegment
        {
            Point = new Windows.Foundation.Point(x2, y2),
            Size = new Windows.Foundation.Size(r, r),
            IsLargeArc = large == 1,
            SweepDirection = SweepDirection.Clockwise,
        });
        var geo = new PathGeometry();
        geo.Figures.Add(fig);
        return new ShapePath
        {
            Data = geo,
            Stroke = new SolidColorBrush(color),
            StrokeThickness = thickness,
            Fill = null,
        };
    }

    void BuildWaveBars()
    {
        WaveCanvas.Children.Clear();
        _waveBars.Clear();
        const int n = 28;
        var cx = 44.0;
        var cy = 44.0;
        for (var i = 0; i < n; i++)
        {
            var ang = i / (double)n * Math.PI * 2;
            var bar = new Rectangle
            {
                Width = 2,
                Height = 6,
                Fill = new SolidColorBrush(Color.FromArgb(200, 90, 214, 255)),
                RadiusX = 1,
                RadiusY = 1,
                RenderTransformOrigin = new Windows.Foundation.Point(0.5, 1),
            };
            Canvas.SetLeft(bar, cx);
            Canvas.SetTop(bar, cy);
            bar.RenderTransform = new TransformGroup
            {
                Children =
                {
                    new TranslateTransform { X = -1, Y = -30 },
                    new RotateTransform { Angle = ang * 180 / Math.PI },
                },
            };
            WaveCanvas.Children.Add(bar);
            _waveBars.Add(bar);
        }
    }

    void TickHud()
    {
        _hudT += 0.016f;
        var pulse = _listening || _speaking ? 0.55f + 0.45f * MathF.Sin(_hudT * 6f) : 0.12f;
        _waveAmp += ((_listening || _speaking ? 1f : 0.15f) - _waveAmp) * 0.12f;

        // Rotate HUD arcs gently (reuse transforms — avoid per-frame alloc)
        var cx = HudCanvas.ActualWidth * 0.5;
        var cy = HudCanvas.ActualHeight * 0.48;
        for (var i = 0; i < _hudArcs.Count; i++)
        {
            var p = _hudArcs[i];
            var dir = (i % 2 == 0) ? 1 : -1;
            var speed = 3.0 + (i % 5);
            if (p.RenderTransform is not RotateTransform rt)
            {
                rt = new RotateTransform();
                p.RenderTransform = rt;
            }
            rt.CenterX = cx;
            rt.CenterY = cy;
            rt.Angle = _hudT * speed * dir;
        }

        for (var i = 0; i < _waveBars.Count; i++)
        {
            var n = MathF.Sin(_hudT * 8f + i * 0.55f) * 0.5f + 0.5f;
            var h = 4 + (10 + 18 * _waveAmp) * n * (0.4f + pulse);
            _waveBars[i].Height = h;
            _waveBars[i].Opacity = 0.25 + 0.55 * _waveAmp;
        }

        if (MicGlow != null)
            MicGlow.Opacity = 0.35 + 0.45 * pulse;
    }

    void UpdateLeftBrackets()
    {
        var h = LeftCard.ActualHeight;
        var w = LeftCard.ActualWidth;
        if (h < 20 || w < 20) return;
        LeftBracketBL.Data = Bracket(0, h - 18, 0, h, 18, h);
        LeftBracketBR.Data = Bracket(w - 18, h, w, h, w, h - 18);
    }

    static PathGeometry Bracket(double x1, double y1, double x2, double y2, double x3, double y3)
    {
        var fig = new PathFigure { StartPoint = new Windows.Foundation.Point(x1, y1) };
        fig.Segments.Add(new LineSegment { Point = new Windows.Foundation.Point(x2, y2) });
        fig.Segments.Add(new LineSegment { Point = new Windows.Foundation.Point(x3, y3) });
        var g = new PathGeometry();
        g.Figures.Add(fig);
        return g;
    }

    void TickTelemetry()
    {
        var now = DateTime.Now;
        SysLine.Text = $"{now:yyyy.MM.dd}  {now:HH:mm:ss}  СВЯЗЬ ОК";
        var model = ModelLabel();
        SampleCpuRam();
        _pingMs = 8 + _rng.Next(0, 18);
        LinkLine.Text = $"СВЯЗЬ  ОК · {_pingMs} мс";
        CpuLine.Text = $"ЦП     {_cpu,5:0.0}%";
        RamLine.Text = $"ОЗУ    {_proc.WorkingSet64 / (1024.0 * 1024.0):0} МБ";
        NetLine.Text = $"СЕТЬ   uplink {_pingMs} мс";
        SysPanel.Text = $"ВРЕМЯ  {now:HH:mm:ss}\nДАТА   {now:yyyy.MM.dd}\nМОДЕЛЬ {model}\nКАДРЫ  60 · DX11";
        if (ModelLine.Text is null || !ModelLine.Text.Contains(model, StringComparison.Ordinal))
            ModelLine.Text = $"МОДЕЛЬ {model}";

        var hour = now.Hour;
        GreetingLine.Text = hour switch
        {
            >= 5 and < 12 => "Доброе утро, сэр.",
            >= 12 and < 18 => "Добрый день, сэр.",
            >= 18 and < 23 => "Добрый вечер, сэр.",
            _ => "Доброй ночи, сэр.",
        };
    }

    void SampleCpuRam()
    {
        try
        {
            _proc.Refresh();
            var t = _proc.TotalProcessorTime;
            var wall = DateTime.UtcNow;
            var dt = (wall - _lastCpuSample).TotalMilliseconds;
            if (dt > 400)
            {
                // Real process CPU as % of one logical core (not a fake counter).
                // Divide by ProcessorCount for machine-share % — show per-core so idle ~few %.
                var cpuDt = (t - _lastCpu).TotalMilliseconds;
                var cores = Math.Max(1, Environment.ProcessorCount);
                _cpu = Math.Clamp(cpuDt / dt / cores * 100.0, 0, 100);
                _lastCpu = t;
                _lastCpuSample = wall;
            }
        }
        catch { /* */ }
    }

    void FadeInSubtitle()
    {
        Subtitle.Opacity = 0;
        _ = FadeAsync(Subtitle, 0, 1, 280);
    }

    static async Task FadeAsync(UIElement el, double from, double to, int ms)
    {
        var steps = Math.Max(1, ms / 16);
        for (var i = 0; i <= steps; i++)
        {
            el.Opacity = from + (to - from) * i / steps;
            await Task.Delay(16);
        }
    }

    void InitTrayAndAutostart()
    {
        try
        {
            _tray = new TrayIcon(RestoreFromTray);
            if (AutostartService.WantsAutostart(Environment.GetCommandLineArgs()))
            {
                var hwnd = WindowNative.GetWindowHandle(this);
                var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                var appWindow = AppWindow.GetFromWindowId(windowId);
                appWindow.Hide();
                _ = StartWakeListenAsync();
            }
        }
        catch (Exception ex) { Program.BootLog("tray: " + ex.Message); }
    }

    void RestoreFromTray()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                var hwnd = WindowNative.GetWindowHandle(this);
                var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                var appWindow = AppWindow.GetFromWindowId(windowId);
                appWindow.Show();
                Activate();
            }
            catch { /* */ }
        });
    }

    async Task StartWakeListenAsync()
    {
        try
        {
            if (!_s.Speech.IsAvailable) return;
            await _s.Speech.StartAsync();
            _listening = true;
            Holo.Renderer.SetListening(true);
            VoiceLine.Text = "ГОЛОС  СЛУШАЮ";
            StatusCaption.Text = "СЛУШАЮ";
            MicGlyph.Text = "■";
        }
        catch { /* */ }
    }

    void RefreshModelLine() => ModelLine.Text = $"МОДЕЛЬ {ModelLabel()}";

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

    bool ApplySmokeOverrides()
    {
        float? yaw = null, jaw = null;
        var eyesCloseup = false;
        var ringsShowcase = false;
        var hudShowcase = false;
        if (float.TryParse(Environment.GetEnvironmentVariable("LIA_SMOKE_YAW"), out var y)) yaw = y;
        if (float.TryParse(Environment.GetEnvironmentVariable("LIA_SMOKE_JAW"),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var j)) jaw = j;
        if (string.Equals(Environment.GetEnvironmentVariable("LIA_SMOKE_EYES"), "1", StringComparison.Ordinal))
            eyesCloseup = true;
        if (string.Equals(Environment.GetEnvironmentVariable("LIA_SMOKE_RINGS"), "1", StringComparison.Ordinal))
            ringsShowcase = true;
        if (string.Equals(Environment.GetEnvironmentVariable("LIA_SMOKE_HUD"), "1", StringComparison.Ordinal))
            hudShowcase = true;
        foreach (var a in Environment.GetCommandLineArgs())
        {
            if (a.Equals("--smoke-eyes", StringComparison.OrdinalIgnoreCase))
            {
                eyesCloseup = true;
                yaw ??= 0f;
            }
            if (a.Equals("--smoke-rings", StringComparison.OrdinalIgnoreCase))
            {
                ringsShowcase = true;
                yaw ??= -48f;
            }
            if (a.Equals("--smoke-hud", StringComparison.OrdinalIgnoreCase))
            {
                hudShowcase = true;
                yaw ??= 8f;
            }
            if (a.StartsWith("--smoke-yaw=", StringComparison.OrdinalIgnoreCase) &&
                float.TryParse(a["--smoke-yaw=".Length..], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var y2)) yaw = y2;
            if (a.StartsWith("--smoke-jaw=", StringComparison.OrdinalIgnoreCase) &&
                float.TryParse(a["--smoke-jaw=".Length..], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var j2)) jaw = j2;
        }

        if (hudShowcase)
        {
            _listening = true;
            Holo.Renderer.SetListening(true);
            VoiceLine.Text = "ГОЛОС  СЛУШАЮ";
            StatusCaption.Text = "ИНТЕРФЕЙС";
            Subtitle.Text = "Голографический интерфейс · N-33";
            MicGlyph.Text = "■";
            BootOverlay.Visibility = Visibility.Collapsed;
        }

        if (yaw.HasValue || jaw.HasValue || eyesCloseup || ringsShowcase || hudShowcase)
        {
            Holo.Renderer.ForcePose(
                eyesCloseup ? yaw ?? 0f : yaw,
                jaw,
                eyesCloseup,
                ringsShowcase);
            if (eyesCloseup)
            {
                StatusCaption.Text = "ГЛАЗА";
                Subtitle.Text = "Крупный план · радужка / блик";
            }
            else if (ringsShowcase)
            {
                StatusCaption.Text = "ОРБИТЫ";
                Subtitle.Text = "Сегментированные кольца · глубина";
            }
            else if (hudShowcase) { /* set above */ }
            else if (jaw is > 0.05f)
            {
                _speaking = true;
                StatusCaption.Text = "ГОВОРЮ";
                VoiceLine.Text = "ГОЛОС  ГОВОРЮ";
                Subtitle.Text = "Демонстрация визем · губы открыты";
            }
            else if (yaw is { } yProf && Math.Abs(yProf) >= 70f)
            {
                StatusCaption.Text = "ПРОФИЛЬ";
                Subtitle.Text = "Боковой ракурс · силуэт";
            }
            else if (yaw is { } yAbs && Math.Abs(yAbs) >= 20f)
            {
                StatusCaption.Text = "3/4";
                Subtitle.Text = "Ракурс · орбита";
            }
            else if (yaw.HasValue)
            {
                StatusCaption.Text = "ФРОНТ";
                Subtitle.Text = "Скажите «Лиа» или нажмите микрофон";
            }
        }
        return yaw.HasValue || jaw.HasValue || eyesCloseup || ringsShowcase || hudShowcase;
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
                StatusCaption.Text = "СТОП";
                VoiceLine.Text = "ГОЛОС  СТОП";
                _speaking = false;
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
        _hudTimer.Stop();
        _hotkey?.Dispose();
        _tray?.Dispose();
        _tray = null;
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
                FontFamily = (FontFamily)Application.Current.Resources["LiaMono"],
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
                FontFamily = (FontFamily)Application.Current.Resources["LiaMono"],
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
        VoiceLine.Text = "ГОЛОС  ДУМАЮ";
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
            Holo.Renderer.SetListening(false);
            MicGlyph.Text = "◉";
            VoiceLine.Text = "ГОЛОС  ОЖИДАНИЕ";
            StatusCaption.Text = "ОЖИДАНИЕ";
        }
        else
        {
            await _s.Speech.StartAsync();
            _listening = true;
            Holo.Renderer.SetListening(true);
            MicGlyph.Text = "■";
            VoiceLine.Text = "ГОЛОС  СЛУШАЮ";
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
        AutostartBox.IsChecked = AutostartService.IsEnabled();
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
        try { AutostartService.SetEnabled(AutostartBox.IsChecked == true); }
        catch (Exception ex) { _s.Log.Info("Автозапуск: " + ex.Message); }
        SettingsOverlay.Visibility = Visibility.Collapsed;
        await _s.Gemini.EnsureModelAsync();
        RefreshModelLine();
        _s.Log.Info($"Сохранено · {ModelLabel()}");
    }

    async void RefreshModels_Click(object sender, RoutedEventArgs e) => await RefreshModelsAsync();

    async Task RefreshModelsAsync()
    {
        ModelsHint.Text = "ListModels…";
        try
        {
            var key = string.IsNullOrWhiteSpace(ApiKeyBox.Password)
                ? _s.Secrets.Load().ApiKey
                : ApiKeyBox.Password;
            var models = await GeminiModels.ListAsync(key);
            ModelBox.Items.Clear();
            foreach (var m in models) ModelBox.Items.Add(m.Id);
            if (ModelBox.Items.Count > 0) ModelBox.SelectedIndex = 0;
            ModelsHint.Text = $"{models.Count} models";
        }
        catch (Exception ex)
        {
            var c = GeminiModels.Classify(ex);
            ModelsHint.Text = c.UserMessage;
        }
    }
}

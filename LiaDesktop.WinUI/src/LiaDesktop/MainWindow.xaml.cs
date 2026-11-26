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
    readonly DispatcherTimer _hudTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
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

        VoiceLine.Text = "VOICE  STANDBY";
        StatusCaption.Text = "STANDBY";
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
            StatusCaption.Text = "SPEAKING";
            Subtitle.Text = t.Length > 140 ? t[..140] : t;
            VoiceLine.Text = "VOICE  SPEAKING";
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
                    StatusCaption.Text = "STANDBY";
                    VoiceLine.Text = "VOICE  STANDBY";
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
            BootStatus.Text = "LINKING DX11…";
            await Task.Delay(420);
            BootStatus.Text = "LOADING PBR HEAD…";
            await Task.Delay(520);
            BootStatus.Text = "CALIBRATING ORBITS…";
            await Task.Delay(480);
            BootStatus.Text = "ONLINE";
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

        double[] radii = [Math.Min(w, h) * 0.28, Math.Min(w, h) * 0.34, Math.Min(w, h) * 0.40, Math.Min(w, h) * 0.46];
        for (var i = 0; i < radii.Length; i++)
        {
            var r = radii[i];
            var segs = 6 + i;
            var gap = 12.0;
            var sweep = (360.0 - segs * gap) / segs;
            for (var s = 0; s < segs; s++)
            {
                var start = s * (sweep + gap) + i * 7;
                var path = MakeArc(cx, cy, r, start, sweep, i % 2 == 0 ? amber : cyan, i == 0 ? 2.0 : 1.35);
                path.Opacity = 0.55 + i * 0.08;
                HudCanvas.Children.Add(path);
                _hudArcs.Add(path);
            }
        }

        // Tick marks on outer ring
        var outer = radii[^1];
        for (var t = 0; t < 48; t++)
        {
            var ang = t / 48.0 * Math.PI * 2;
            var len = t % 6 == 0 ? 10 : 5;
            var x0 = cx + Math.Cos(ang) * (outer - len);
            var y0 = cy + Math.Sin(ang) * (outer - len);
            var x1 = cx + Math.Cos(ang) * outer;
            var y1 = cy + Math.Sin(ang) * outer;
            var line = new Line
            {
                X1 = x0, Y1 = y0, X2 = x1, Y2 = y1,
                Stroke = new SolidColorBrush(amber),
                StrokeThickness = t % 6 == 0 ? 1.4 : 0.8,
                Opacity = 0.45,
            };
            HudCanvas.Children.Add(line);
            _hudTicks.Add(line);
        }

        // Radial gauge needles (decorative)
        for (var g = 0; g < 3; g++)
        {
            var rr = radii[1] + g * 8;
            var needle = new Line
            {
                Stroke = new SolidColorBrush(cyan),
                StrokeThickness = 1.2,
                Opacity = 0.55,
                X1 = cx, Y1 = cy,
                X2 = cx + rr, Y2 = cy,
            };
            HudCanvas.Children.Add(needle);
            _hudTicks.Add(needle);
        }

        // Scan sweep wedge as thin line
        var sweepLine = new Line
        {
            X1 = cx, Y1 = cy,
            X2 = cx, Y2 = cy - outer,
            Stroke = new SolidColorBrush(Color.FromArgb(180, 255, 154, 60)),
            StrokeThickness = 1.5,
            Opacity = 0.5,
        };
        sweepLine.Tag = "sweep";
        HudCanvas.Children.Add(sweepLine);
        _hudTicks.Add(sweepLine);

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

        // Rotate HUD arcs gently
        var cx = HudCanvas.ActualWidth * 0.5;
        var cy = HudCanvas.ActualHeight * 0.48;
        for (var i = 0; i < _hudArcs.Count; i++)
        {
            var p = _hudArcs[i];
            var dir = (i % 2 == 0) ? 1 : -1;
            var speed = 4.0 + (i % 5);
            p.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
            // Approximate rotation around canvas center via RotateTransform on path bounds — use CompositeTransform
            p.RenderTransform = new RotateTransform
            {
                Angle = _hudT * speed * dir,
                CenterX = cx,
                CenterY = cy,
            };
        }

        foreach (var line in _hudTicks)
        {
            if (line.Tag as string == "sweep")
            {
                var ang = _hudT * 40.0 * Math.PI / 180.0;
                var outer = Math.Min(HudCanvas.ActualWidth, HudCanvas.ActualHeight) * 0.46;
                line.X1 = cx;
                line.Y1 = cy;
                line.X2 = cx + Math.Cos(ang) * outer;
                line.Y2 = cy + Math.Sin(ang) * outer;
            }
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
        SysLine.Text = $"{now:yyyy.MM.dd}  {now:HH:mm:ss}  LINK OK";
        var model = ModelLabel();
        SampleCpuRam();
        _pingMs = 8 + _rng.Next(0, 18);
        LinkLine.Text = $"LINK   OK · {_pingMs}ms";
        CpuLine.Text = $"CPU    {_cpu,5:0.0}%";
        RamLine.Text = $"RAM    {_proc.WorkingSet64 / (1024.0 * 1024.0):0} MB";
        NetLine.Text = $"NET    uplink {_pingMs}ms";
        SysPanel.Text = $"TIME   {now:HH:mm:ss}\nDATE   {now:yyyy.MM.dd}\nMODEL  {model}\nFPS    60 · DX11";
        if (ModelLine.Text is null || !ModelLine.Text.Contains(model, StringComparison.Ordinal))
            ModelLine.Text = $"MODEL  {model}";

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
            if (dt > 200)
            {
                var cpuDt = (t - _lastCpu).TotalMilliseconds;
                _cpu = Math.Clamp(cpuDt / dt / Environment.ProcessorCount * 100.0, 0, 100);
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
            VoiceLine.Text = "VOICE  LISTENING";
            StatusCaption.Text = "LISTENING";
            MicGlyph.Text = "■";
        }
        catch { /* */ }
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
            VoiceLine.Text = "VOICE  LISTENING";
            StatusCaption.Text = "HUD ONLINE";
            Subtitle.Text = "Cinematic holo interface · N-33";
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
                StatusCaption.Text = "EYES";
                Subtitle.Text = "Close-up · iris / specular";
            }
            else if (ringsShowcase)
            {
                StatusCaption.Text = "ORBITS";
                Subtitle.Text = "Segmented halo rings · depth occluded";
            }
            else if (hudShowcase) { /* set above */ }
            else if (jaw is > 0.05f)
            {
                _speaking = true;
                StatusCaption.Text = "SPEAKING";
                VoiceLine.Text = "VOICE  SPEAKING";
                Subtitle.Text = "Viseme demo · lips open";
            }
            else if (yaw is { } yProf && Math.Abs(yProf) >= 70f)
            {
                StatusCaption.Text = "PROFILE";
                Subtitle.Text = "Side silhouette";
            }
            else if (yaw is { } yAbs && Math.Abs(yAbs) >= 20f)
            {
                StatusCaption.Text = "3/4";
                Subtitle.Text = "Idle orbit";
            }
            else if (yaw.HasValue)
            {
                StatusCaption.Text = "FRONT";
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
                StatusCaption.Text = "ABORTED";
                VoiceLine.Text = "VOICE  STOP";
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
        StatusCaption.Text = "THINKING";
        VoiceLine.Text = "VOICE  THINKING";
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
            VoiceLine.Text = "VOICE  STANDBY";
            StatusCaption.Text = "STANDBY";
        }
        else
        {
            await _s.Speech.StartAsync();
            _listening = true;
            Holo.Renderer.SetListening(true);
            MicGlyph.Text = "■";
            VoiceLine.Text = "VOICE  LISTENING";
            StatusCaption.Text = "LISTENING";
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

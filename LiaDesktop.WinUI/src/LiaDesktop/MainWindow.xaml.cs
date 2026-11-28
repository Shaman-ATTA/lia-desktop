using System.Diagnostics;
using LiaDesktop.Rendering;
using LiaDesktop.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.Win32;
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
    bool _screenLocked;
    bool _windowOccluded;
    bool _micUnavailable;
    HoloPanel.RenderPace? _renderPaceOverride;
    float _hudT;
    float _waveAmp;
    double _cpu;
    TimeSpan _lastCpu;
    DateTime _lastCpuSample = DateTime.UtcNow;
    int _pingMs = 12;
    TaskCompletionSource<bool>? _confirmTcs;

    /// <summary>Selftest: force Idle/Active/Paused. Null = auto from speak/listen/visibility.</summary>
    public void SetRenderPaceOverride(HoloPanel.RenderPace? pace)
    {
        _renderPaceOverride = pace;
        RefreshRenderPace();
    }

    public HoloPanel.RenderPace CurrentRenderPace => Holo.Pace;
    public string VoiceStatusText => VoiceLine.Text ?? "";
    public void NotifyMicUnavailable() => OnMicUnavailable();

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
            RefreshRenderPace();
        });
        _player.Volume = 1.0;
        _player.AudioCategory = MediaPlayerAudioCategory.Speech;
        // Playback is owned by BargeInService (WASAPI communications + AEC/VAD).
        // SpeakFile only updates HUD chrome; MediaPlayer kept as emergency fallback.
        _s.Agent.SpeakFile += path => DispatcherQueue.TryEnqueue(() =>
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                _s.Log.Error("TTS: файл не найден", path ?? "");
                return;
            }
            _speaking = true;
            StatusCaption.Text = "ГОВОРЮ";
            VoiceLine.Text = "ГОЛОС  ГОВОРЮ";
            var dev = _s.BargeIn.LastRenderDevice;
            _s.Log.Info(string.IsNullOrWhiteSpace(dev) || dev == "(none)"
                ? "TTS: воспроизведение SvetlanaNeural (barge-in)"
                : $"TTS → {dev}");
        });
        _s.Agent.BargeInListening += () => DispatcherQueue.TryEnqueue(() =>
        {
            _speaking = false;
            _listening = true;
            Holo.Renderer.SetListening(true);
            Holo.Renderer.Jaw = 0;
            Holo.Renderer.Funnel = 0;
            Holo.Renderer.Wide = 0;
            StatusCaption.Text = "СЛУШАЮ";
            VoiceLine.Text = "ГОЛОС  СЛУШАЮ";
            MicGlyph.Text = "■";
            Subtitle.Text = "Перебивание · слушаю вас";
            RefreshRenderPace();
        });
        _s.Agent.BargeInEnded += () => DispatcherQueue.TryEnqueue(OnSpeakEnded);

        _s.Speech.FinalResult += OnSpeechFinal;
        _s.MicGemini.FinalResult += text => DispatcherQueue.TryEnqueue(() =>
        {
            // Gemini mic session ends after one utterance — reset listening chrome
            _listening = false;
            Holo.Renderer.SetListening(false);
            MicGlyph.Text = "◉";
            OnSpeechFinal(text);
        });
        _s.Wake.Woken += OnWakeWord;
        _s.Wake.MicFailed += () => DispatcherQueue.TryEnqueue(OnMicUnavailable);
        _s.MicGemini.TimedOut += OnMicGeminiTimedOut;
        _s.Tools.ConfirmDestructiveAsync = AskDestructiveConfirmAsync;
        _clock.Tick += (_, _) => TickTelemetry();
        _clock.Start();

        HudCanvas.SizeChanged += (_, _) => RebuildHudGeometry();
        LeftCard.SizeChanged += (_, _) => UpdateLeftBrackets();
        _hudTimer.Tick += (_, _) => TickHud();
        _hudTimer.Start();

        Activated += OnActivated;
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);
            appWindow.Changed += (_, e) =>
            {
                if (e.DidVisibilityChange || e.DidPresenterChange)
                    DispatcherQueue.TryEnqueue(RefreshRenderPace);
            };
        }
        catch { /* */ }
        try
        {
            SystemEvents.SessionSwitch += OnSessionSwitch;
        }
        catch { /* */ }
        foreach (var (line, detail) in _s.Log.Lines) AddLog(line, detail);

        var smoke = ApplySmokeOverrides();
        var virtualSmoke = TryScheduleVirtualSmokeDump();
        DispatcherQueue.TryEnqueue(() => InitTrayAndAutostart());
        DispatcherQueue.TryEnqueue(RefreshRenderPace);
        if (SelfTestRunner.IsSelfTest)
        {
            BootOverlay.Visibility = Visibility.Collapsed;
            // Let DX/swapchain settle, then run E2E and Environment.Exit
            DispatcherQueue.TryEnqueue(async () =>
            {
                await Task.Delay(1500);
                await SelfTestRunner.RunAndExitAsync(_s);
            });
        }
        else if (smoke || virtualSmoke)
            BootOverlay.Visibility = Visibility.Collapsed;
        else
            DispatcherQueue.TryEnqueue(async () => await RunBootSequenceAsync());
    }

    void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionLock) _screenLocked = true;
        else if (e.Reason == SessionSwitchReason.SessionUnlock) _screenLocked = false;
        else return;
        DispatcherQueue.TryEnqueue(RefreshRenderPace);
    }

    /// <summary>
    /// Idle ≈30fps (ОЖИДАНИЕ); Active ≈60fps while speaking/listening; Paused when
    /// minimized/occluded/screen-locked. Lip sync morph targets still update via Agent.Mouth.
    /// </summary>
    public void RefreshRenderPace()
    {
        try
        {
            if (_renderPaceOverride.HasValue)
            {
                Holo.SetRenderPace(_renderPaceOverride.Value);
                ApplyHudTimerForPace(_renderPaceOverride.Value);
                return;
            }

            var hwnd = WindowNative.GetWindowHandle(this);
            var iconic = IsIconic(hwnd);
            var visible = Visible;
            try
            {
                var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                var appWindow = AppWindow.GetFromWindowId(windowId);
                visible = appWindow.IsVisible && !iconic;
            }
            catch { visible = !iconic; }

            _windowOccluded = iconic || !visible;
            HoloPanel.RenderPace pace;
            if (_screenLocked || _windowOccluded)
                pace = HoloPanel.RenderPace.Paused;
            else if (_speaking || _listening || Holo.Renderer.WakePulseActive)
                pace = HoloPanel.RenderPace.Active;
            else
                pace = HoloPanel.RenderPace.Idle;

            Holo.SetRenderPace(pace);
            ApplyHudTimerForPace(pace);
        }
        catch { /* */ }
    }

    void ApplyHudTimerForPace(HoloPanel.RenderPace pace)
    {
        if (pace == HoloPanel.RenderPace.Paused)
            _hudTimer.Stop();
        else if (!_hudTimer.IsEnabled)
            _hudTimer.Start();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool IsIconic(IntPtr hWnd);

    void OnMicGeminiTimedOut()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _listening = false;
            Holo.Renderer.SetListening(false);
            MicGlyph.Text = "◉";
            StatusCaption.Text = "ОЖИДАНИЕ";
            VoiceLine.Text = "ГОЛОС  ОЖИДАНИЕ";
            Subtitle.Text = "Скажите «Лиа» или нажмите микрофон";
            _s.Log.Info("wake follow-up: timeout → ОЖИДАНИЕ");
            RefreshRenderPace();
        });
    }

    void OnWakeWord(string full, string command, float conf)
    {
        // Most selftest wake_* gates only need DETECT counts — skip follow-up unless
        // EnableWakeFollowUp (wake_e2e / wake_timeout exercise the real path).
        if (SelfTestRunner.IsSelfTest && !SelfTestRunner.EnableWakeFollowUp)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                try { Holo.Renderer.TriggerWakePulse(); } catch { /* */ }
                _s.Log.Info($"wake UI (selftest): conf={conf:F2} text=\"{full}\" cmd=\"{command}\"");
            });
            return;
        }

        DispatcherQueue.TryEnqueue(async () =>
        {
            Holo.Renderer.TriggerWakePulse();
            _listening = true;
            Holo.Renderer.SetListening(true);
            StatusCaption.Text = "СЛУШАЮ";
            VoiceLine.Text = "ГОЛОС  СЛУШАЮ";
            MicGlyph.Text = "■";
            Subtitle.Text = string.IsNullOrWhiteSpace(command)
                ? "Лиа · слушаю…"
                : $"Лиа · {command}";
            RefreshRenderPace();

            // Inline only for real commands — ignore ASR crumbs after wake («лет», «ли»)
            if (WakeText.IsLikelyInlineCommand(command))
            {
                // One breath: «Лиа, открой блокнот» → NewRequest with command text
                CommandBox.Text = command;
                await SendAsync();
                return;
            }

            // Wake only — capture following utterance.
            // Prefer MicGemini when injectable (selftest e2e) or Windows STT unavailable.
            try
            {
                var forceMic = SelfTestRunner.EnableWakeFollowUp
                               || _s.MicGemini.UseInjectableCapture
                               || _s.MicGemini.MockCapturePull != null;
                if (forceMic || !_s.WindowsSpeechOk)
                {
                    // Selftest mock Gemini accepts any key; production needs a real one.
                    var key = _s.Secrets.Load().ApiKey;
                    if (string.IsNullOrWhiteSpace(key) && !SelfTestRunner.EnableWakeFollowUp)
                    {
                        _s.Log.Info("wake follow-up: нет ключа Gemini для MicGemini");
                        OnMicGeminiTimedOut();
                        return;
                    }
                    await _s.MicGemini.StartAsync();
                }
                else
                    await _s.Speech.StartAsync();
            }
            catch (Exception ex)
            {
                _s.Log.Info("wake follow-up STT: " + ex.Message);
                OnMicGeminiTimedOut();
            }
        });
    }

    /// <summary>Called when AppWindow size/presenter changes (maximize, DPI move).</summary>
    public void NotifyStageResized()
    {
        try
        {
            // Invalidate stage layout so HoloPanel ActualWidth catches up before buffer resize
            StageGrid.InvalidateMeasure();
            StageGrid.InvalidateArrange();
            Root.UpdateLayout();
            Holo.InvalidateMeasure();
            Holo.UpdateLayout();
            StageGrid.UpdateLayout();
            Program.BootLog(
                $"NotifyStageResized root={Root.ActualWidth:0}x{Root.ActualHeight:0} "
                + $"stage={StageGrid.ActualWidth:0}x{StageGrid.ActualHeight:0} "
                + $"holo={Holo.ActualWidth:0}x{Holo.ActualHeight:0}");
            Holo.ForceRecreateSwapChain();
        }
        catch (Exception ex) { Program.BootLog("NotifyStageResized: " + ex.Message); }
        RebuildHudGeometry();
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
            Subtitle.Text = "Скажите «Лиа» или нажмите микрофон";
            // Ensure offline wake is up after HUD is visible
            if (_s.Config.WakeWordEnabled && !_s.Wake.IsRunning)
                _ = EnsureWakeListeningAsync();
        }
        catch
        {
            BootOverlay.Visibility = Visibility.Collapsed;
        }
    }

    async Task EnsureWakeListeningAsync()
    {
        try
        {
            BootStatus.Text = VoskModelStore.IsReady() ? "ПРОБУЖДЕНИЕ…" : "ЗАГРУЗКА МОДЕЛИ «ЛИА»…";
            await _s.Wake.EnsureModelAsync();
            _s.Wake.Start();
        }
        catch (Exception ex)
        {
            _s.Log.Info("wake boot: " + ex.Message);
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

        // Dense concentric arcs live in HoloRenderer (drawn before the head so 3D occludes).
        // HudCanvas stays empty — XAML-over-SwapChain was painting arcs on skin.
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
        var cx = 36.0;
        var cy = 36.0;
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
                    new TranslateTransform { X = -1, Y = -26 },
                    new RotateTransform { Angle = ang * 180 / Math.PI },
                },
            };
            WaveCanvas.Children.Add(bar);
            _waveBars.Add(bar);
        }
    }

    void TickHud()
    {
        _hudT += 0.05f;
        var wakePulse = Holo.Renderer.WakePulseActive;
        var active = _listening || _speaking || wakePulse;
        var pulse = active
            ? 0.55f + 0.45f * MathF.Sin(_hudT * (wakePulse ? 10f : 6f))
            : 0.12f;
        _waveAmp += ((active ? (wakePulse ? 1.15f : 1f) : 0.15f) - _waveAmp) * 0.12f;

        // Rotate HUD arcs gently (reuse transforms — avoid per-frame alloc)
        var cx = HudCanvas.ActualWidth * 0.5;
        var cy = HudCanvas.ActualHeight * 0.48;
        for (var i = 0; i < _hudArcs.Count; i++)
        {
            var p = _hudArcs[i];
            var dir = (i % 2 == 0) ? 1 : -1;
            var speed = 1.2 + (i % 4) * 0.35; // slow drift
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
        try { _s.Telemetry.Sample(); } catch { /* */ }
        var tel = _s.Telemetry;
        _cpu = tel.SystemCpuPct;
        if (tel.PingMs >= 0) _pingMs = tel.PingMs;
        LinkLine.Text = $"СВЯЗЬ  ОК · {_pingMs} мс";
        CpuLine.Text = $"ЦП     {_cpu,5:0.0}%";
        GpuLine.Text = tel.GpuPct < 0 ? "ГПУ    н/д" : $"ГПУ    {tel.GpuPct,5:0.0}%";
        RamLine.Text = $"ОЗУ    {tel.RamUsedGb:0.0}/{tel.RamTotalGb:0.0} ГБ";
        NetLine.Text = $"СЕТЬ   ↓{tel.DownMbps:0.00} ↑{tel.UpMbps:0.00} Мбит/с";
        LiaResLine.Text = $"ЛИА ЦП {tel.LiaCpuPct:0.0}% · ОЗУ {tel.LiaRamMb:0} МБ";
        if (_micUnavailable && !_speaking && !_listening)
            VoiceLine.Text = "ГОЛОС  МИК НЕДОСТ.";
        var fps = Holo.Pace == HoloPanel.RenderPace.Active ? 60
            : Holo.Pace == HoloPanel.RenderPace.Idle ? 30 : 0;
        SysPanel.Text = $"ВРЕМЯ  {now:HH:mm:ss}\nДАТА   {now:yyyy.MM.dd}\nМОДЕЛЬ {model}\nКАДРЫ  {fps} · DX11";
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

    void OnMicUnavailable()
    {
        _micUnavailable = true;
        VoiceLine.Text = "ГОЛОС  МИК НЕДОСТ.";
        Subtitle.Text = AppLog.MicUnavailableJournalLine;
    }

    Task<bool> AskDestructiveConfirmAsync(string summary)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _confirmTcs = tcs;
        DispatcherQueue.TryEnqueue(() =>
        {
            ConfirmSummary.Text = summary;
            ConfirmOverlay.Visibility = Visibility.Visible;
        });
        return tcs.Task;
    }

    void ConfirmYes_Click(object sender, RoutedEventArgs e)
    {
        ConfirmOverlay.Visibility = Visibility.Collapsed;
        _confirmTcs?.TrySetResult(true);
        _confirmTcs = null;
    }

    void ConfirmNo_Click(object sender, RoutedEventArgs e)
    {
        ConfirmOverlay.Visibility = Visibility.Collapsed;
        _confirmTcs?.TrySetResult(false);
        _confirmTcs = null;
    }

    void MemoryForgetSelected_Click(object sender, RoutedEventArgs e)
    {
        if (MemoryFactsBox.SelectedItem is string line)
        {
            var id = line.Split(' ', 2)[0];
            _s.LongTerm.Forget(id);
            RefreshMemoryFactsBox();
        }
    }

    void MemoryForgetAll_Click(object sender, RoutedEventArgs e)
    {
        _s.LongTerm.ForgetAll();
        RefreshMemoryFactsBox();
        _s.Log.Info("Память: забыла всё о вас");
    }

    void RefreshMemoryFactsBox()
    {
        MemoryFactsBox.Items.Clear();
        foreach (var f in _s.LongTerm.Snapshot())
            MemoryFactsBox.Items.Add($"{f.Id}  {f.Text}");
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
            // Tray/autostart: offline Vosk wake (not Windows STT always-on).
            if (!_s.Config.WakeWordEnabled) return;
            await EnsureWakeListeningAsync();
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

    /// <summary>
    /// Offscreen exact-size dumps: --smoke-virtual-size=WxH [--smoke-virtual-resize-after=WxH]
    /// --smoke-dump=path.png. Centre-column oversized swapchain → full-window navy composite.
    /// </summary>
    bool TryScheduleVirtualSmokeDump()
    {
        if (!TryParseArgSize("--smoke-virtual-size=", out var vw, out var vh) &&
            !TryParseEnvSize("LIA_SMOKE_VIRTUAL_SIZE", out vw, out vh))
            return false;
        if (!TryParseArgPath("--smoke-dump=", out var dumpPath) &&
            string.IsNullOrWhiteSpace(dumpPath = Environment.GetEnvironmentVariable("LIA_SMOKE_DUMP")))
        {
            Program.BootLog("smoke-virtual-size set but no --smoke-dump= path");
            return false;
        }

        TryParseArgSize("--smoke-virtual-resize-after=", out var rw, out var rh);
        Program.BootLog(
            $"smoke-virtual scheduled {vw}x{vh}"
            + (rw > 0 ? $" → resize-after {rw}x{rh}" : "")
            + $" dump={dumpPath}");

        // Small→large: virtual-size=SMALL, resize-after=LARGE. Already-max: virtual-size=target.
        var startW = vw;
        var startH = vh;
        var finalW = rw > 0 ? rw : vw;
        var finalH = rh > 0 ? rh : vh;

        var big = finalW >= 3000 || finalH >= 2000;
        DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                await Task.Delay(big ? 2000 : 1200);
                ApplyVirtualWindowSize(startW, startH);
                await Task.Delay(big ? 3500 : 1800);
                if (rw > 0)
                {
                    Program.BootLog($"smoke-virtual-resize-after → {finalW}x{finalH}");
                    ApplyVirtualWindowSize(finalW, finalH);
                    await Task.Delay(big ? 4500 : 2500);
                }
                else
                    await Task.Delay(big ? 2000 : 800);

                // A few frames so scene color is current
                for (var i = 0; i < (big ? 8 : 4); i++)
                {
                    Holo.Renderer.Tick(0.016f);
                    await Task.Delay(big ? 80 : 40);
                }

                DumpVirtualComposite(dumpPath!, finalW, finalH);
                Program.BootLog("smoke-dump ok, exiting 0");
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                Program.BootLog("smoke-dump FAILED: " + ex);
                Environment.Exit(2);
            }
        });
        return true;
    }

    void ApplyVirtualWindowSize(int winW, int winH)
    {
        // StageGrid: Margin 0,36,0,120 — centre column 0.56 of width
        var stageH = Math.Max(2, winH - 36 - 120);
        var colW = Math.Max(2, winW * 0.56);
        try
        {
            Root.Width = winW;
            Root.Height = winH;
            Root.UpdateLayout();
        }
        catch { /* */ }
        Holo.SetVirtualDipSize(colW, stageH);
        NotifyStageResized();
        Program.BootLog(
            $"ApplyVirtualWindowSize win={winW}x{winH} holoDip={colW:0}x{stageH}");
    }

    void DumpVirtualComposite(string path, int winW, int winH)
    {
        var tmp = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"lia_holo_dump_{Environment.ProcessId}.png");
        try
        {
            Holo.Renderer.SaveBackbufferPng(tmp);
            using var holo = new System.Drawing.Bitmap(tmp);
            using var full = new System.Drawing.Bitmap(winW, winH);
            using var g = System.Drawing.Graphics.FromImage(full);
            g.Clear(System.Drawing.Color.FromArgb(255, 5, 7, 12));
            var stageTop = 36f;
            var stageH = winH - 36f - 120f;
            var colX = (float)(winW * 0.22);
            var colW = (float)(winW * 0.56);
            g.DrawImage(holo, colX, stageTop, colW, stageH);
            var dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            full.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            Program.BootLog($"DumpVirtualComposite {winW}x{winH} (holo {holo.Width}x{holo.Height}) → {path}");
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* */ }
        }
    }

    static bool TryParseArgSize(string prefix, out int w, out int h)
    {
        w = h = 0;
        foreach (var a in Environment.GetCommandLineArgs())
        {
            if (!a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var parts = a[prefix.Length..].Split('x', 'X');
            if (parts.Length == 2
                && int.TryParse(parts[0], out w) && int.TryParse(parts[1], out h)
                && w >= 640 && h >= 480)
                return true;
        }
        return false;
    }

    static bool TryParseEnvSize(string env, out int w, out int h)
    {
        w = h = 0;
        var s = Environment.GetEnvironmentVariable(env);
        if (string.IsNullOrWhiteSpace(s)) return false;
        var parts = s.Split('x', 'X');
        return parts.Length == 2
               && int.TryParse(parts[0], out w) && int.TryParse(parts[1], out h)
               && w >= 640 && h >= 480;
    }

    static bool TryParseArgPath(string prefix, out string? path)
    {
        path = null;
        foreach (var a in Environment.GetCommandLineArgs())
        {
            if (!a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            path = a[prefix.Length..].Trim('"');
            return !string.IsNullOrWhiteSpace(path);
        }
        return false;
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
        try { SystemEvents.SessionSwitch -= OnSessionSwitch; } catch { /* */ }
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
        // One short journal line only — full traces live in %LOCALAPPDATA%\LiaDesktop\error.log
        var text = string.IsNullOrWhiteSpace(detail) ? line : $"{line}: {detail}";
        if (text.Length > 180) text = text[..180] + "…";
        LogPanel.Children.Insert(0, new TextBlock
        {
            Text = text,
            Foreground = (Brush)Application.Current.Resources["LiaCyan"],
            FontFamily = (FontFamily)Application.Current.Resources["LiaMono"],
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
        });
        while (LogPanel.Children.Count > 80) LogPanel.Children.RemoveAt(LogPanel.Children.Count - 1);
    }

    void Send_Click(object sender, RoutedEventArgs e) => _ = SendAsync();
    void ClearMemory_Click(object sender, RoutedEventArgs e)
    {
        StatusCaption.Text = "ОЖИДАНИЕ";
        Subtitle.Text = "Новый разговор — память очищена";
        _ = _s.Agent.RunAsync("забудь");
    }
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

    void OnSpeakEnded()
    {
        _speaking = false;
        // Barge-in listening chrome ends with playback; dedicated mic session keeps _listening
        if (!_s.BargeIn.IsPlaying)
        {
            _listening = false;
            Holo.Renderer.SetListening(false);
            MicGlyph.Text = "◉";
        }
        StatusCaption.Text = "ОЖИДАНИЕ";
        VoiceLine.Text = "ГОЛОС  ОЖИДАНИЕ";
        Subtitle.Text = "Скажите «Лиа» или нажмите микрофон";
        Holo.Renderer.Jaw = 0;
        Holo.Renderer.Funnel = 0;
        Holo.Renderer.Wide = 0;
        RefreshRenderPace();
    }

    async void Mic_Click(object sender, RoutedEventArgs e)
    {
        if (_listening)
        {
            if (_s.WindowsSpeechOk) await _s.Speech.StopAsync();
            else await _s.MicGemini.StopAsync();
            _listening = false;
            Holo.Renderer.SetListening(false);
            MicGlyph.Text = "◉";
            VoiceLine.Text = "ГОЛОС  ОЖИДАНИЕ";
            StatusCaption.Text = "ОЖИДАНИЕ";
            RefreshRenderPace();
            return;
        }

        // Default: Windows STT when available; else Gemini mic (WASAPI + VAD).
        if (_s.WindowsSpeechOk)
        {
            await _s.Speech.StartAsync();
        }
        else
        {
            var cfg = _s.Secrets.Load();
            if (string.IsNullOrWhiteSpace(cfg.ApiKey))
            {
                _s.Log.Info("Распознавание речи Windows недоступно — нужен ключ Gemini для микрофона, или введите текст.");
                CommandBox.Focus(FocusState.Programmatic);
                return;
            }
            try { await _s.MicGemini.StartAsync(); }
            catch
            {
                OnMicUnavailable();
                return;
            }
        }
        _micUnavailable = false;
        _listening = true;
        Holo.Renderer.SetListening(true);
        MicGlyph.Text = "■";
        VoiceLine.Text = "ГОЛОС  СЛУШАЮ";
        StatusCaption.Text = "СЛУШАЮ";
        RefreshRenderPace();
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

    void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            AppLog.OpenLogFolder();
            _s.Log.Info("Открыта папка логов: " + AppLog.LogDir());
        }
        catch (Exception ex)
        {
            _s.Log.Error("Логи", ex.Message);
        }
    }

    void SaveLogDesktop_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dest = AppLog.CopyLatestToDesktop();
            if (dest == null)
            {
                _s.Log.Error("Лог", "файл не найден");
                Subtitle.Text = "Лог ещё не создан";
            }
            else
            {
                _s.Log.Info("Лог скопирован → " + dest);
                Subtitle.Text = "Лог на рабочем столе: Lia-log.txt";
            }
        }
        catch (Exception ex)
        {
            _s.Log.Error("Лог", ex.Message);
        }
    }

    void Settings_Click(object sender, RoutedEventArgs e)
    {
        var cfg = _s.Secrets.Load();
        ProviderBox.SelectedIndex = cfg.Provider == "ollama" ? 1 : 0;
        ApiKeyBox.Password = cfg.ApiKey;
        WorkspaceBox.Text = cfg.WorkspacePath;
        AutostartBox.IsChecked = AutostartService.IsEnabled();
        WakeWordBox.IsChecked = cfg.WakeWordEnabled;
        WakeSensSlider.Value = Math.Clamp(cfg.WakeWordSensitivity, 0.1, 0.9);
        ConfirmDestructiveBox.IsChecked = cfg.ConfirmDestructiveActions;
        ModelBox.Items.Clear();
        if (!string.IsNullOrEmpty(cfg.Model))
        {
            ModelBox.Items.Add(cfg.Model);
            ModelBox.SelectedIndex = 0;
        }
        FillAudioOutBox(cfg.AudioOutputDeviceId);
        RefreshMemoryFactsBox();
        SettingsOverlay.Visibility = Visibility.Visible;
        if (cfg.Provider != "ollama" && !string.IsNullOrEmpty(cfg.ApiKey))
            _ = RefreshModelsAsync();
    }

    void FillAudioOutBox(string selectedId)
    {
        AudioOutBox.Items.Clear();
        var devices = AudioOutput.ListRenderDevices();
        var sel = 0;
        for (var i = 0; i < devices.Count; i++)
        {
            var (id, name) = devices[i];
            AudioOutBox.Items.Add(new AudioDeviceItem(id, name));
            if (!string.IsNullOrEmpty(selectedId) &&
                string.Equals(id, selectedId, StringComparison.OrdinalIgnoreCase))
                sel = i;
        }
        AudioOutBox.SelectedIndex = sel;
    }

    sealed record AudioDeviceItem(string Id, string Name)
    {
        public override string ToString() => Name;
    }

    async void TestVoice_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ApplyAudioOutFromUi();
            StatusCaption.Text = "ГОЛОС";
            Subtitle.Text = "Проверка вывода…";
            var path = await _s.Tts.SpeakToFileAsync(
                "Привет, я Лиа. Проверка голоса и устройства вывода.", CancellationToken.None);
            if (path == null || !File.Exists(path))
            {
                _s.Log.Error("Проверка голоса", "TTS не создал файл");
                Subtitle.Text = "TTS недоступен";
                return;
            }
            await _s.BargeIn.PlayFileAsync(path, "Проверка голоса.", CancellationToken.None);
            Subtitle.Text = $"Голос → {_s.BargeIn.LastRenderDevice}";
            StatusCaption.Text = "ОЖИДАНИЕ";
        }
        catch (Exception ex)
        {
            _s.Log.Error("Проверка голоса", ex.Message);
            Subtitle.Text = "Ошибка вывода: " + ex.Message;
        }
    }

    void ApplyAudioOutFromUi()
    {
        var id = "";
        if (AudioOutBox.SelectedItem is AudioDeviceItem it)
            id = it.Id ?? "";
        _s.BargeIn.SetPreferredOutputDevice(id);
        var cfg = _s.Secrets.Load();
        cfg.AudioOutputDeviceId = id;
        _s.Secrets.Save(cfg);
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
        var picked = ModelBox.SelectedItem?.ToString()?.Trim() ?? "";

        // Validate against ListModels — never persist a phantom id.
        if (cfg.Provider == "gemini" && !string.IsNullOrWhiteSpace(cfg.ApiKey))
        {
            try
            {
                var models = await GeminiModels.ListAsync(cfg.ApiKey);
                var ids = models.Select(m => m.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (string.IsNullOrEmpty(picked) || !ids.Contains(picked) || GeminiModels.IsRetired(picked))
                {
                    var best = GeminiModels.PickBestFlash(models);
                    if (best == null)
                    {
                        ModelsHint.Text = "Нет flash-моделей в ListModels";
                        return;
                    }
                    if (!string.IsNullOrEmpty(picked))
                        _s.Log.Info($"Модель «{picked}» недоступна → {best}");
                    picked = best;
                }
                ModelBox.Items.Clear();
                foreach (var m in models) ModelBox.Items.Add(m.Id);
                ModelBox.SelectedItem = picked;
            }
            catch (Exception ex)
            {
                var c = GeminiModels.Classify(ex);
                ModelsHint.Text = c.UserMessage;
                _s.Log.Error(c.UserMessage, c.Detail);
                return;
            }
        }

        cfg.Model = picked;
        cfg.VisionModel = cfg.Model;
        if (AudioOutBox.SelectedItem is AudioDeviceItem a)
            cfg.AudioOutputDeviceId = a.Id ?? "";
        cfg.WakeWordEnabled = WakeWordBox.IsChecked == true;
        cfg.WakeWordSensitivity = (float)WakeSensSlider.Value;
        cfg.ConfirmDestructiveActions = ConfirmDestructiveBox.IsChecked == true;
        _s.BargeIn.SetPreferredOutputDevice(cfg.AudioOutputDeviceId);
        _s.Secrets.Save(cfg);
        _s.ApplyWakeSettings(cfg);
        try { AutostartService.SetEnabled(AutostartBox.IsChecked == true); }
        catch (Exception ex) { _s.Log.Info("Автозапуск: " + ex.Message); }
        SettingsOverlay.Visibility = Visibility.Collapsed;
        await _s.Gemini.EnsureModelAsync();
        RefreshModelLine();
        _s.Log.Info($"Сохранено · {ModelLabel()} · wake={(cfg.WakeWordEnabled ? "on" : "off")} sens={cfg.WakeWordSensitivity:F2}");
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
            var prev = ModelBox.SelectedItem?.ToString();
            ModelBox.Items.Clear();
            foreach (var m in models) ModelBox.Items.Add(m.Id);
            if (!string.IsNullOrEmpty(prev))
            {
                for (var i = 0; i < ModelBox.Items.Count; i++)
                {
                    if (string.Equals(ModelBox.Items[i]?.ToString(), prev, StringComparison.OrdinalIgnoreCase))
                    {
                        ModelBox.SelectedIndex = i;
                        break;
                    }
                }
            }
            if (ModelBox.SelectedIndex < 0 && ModelBox.Items.Count > 0)
                ModelBox.SelectedIndex = 0;
            ModelsHint.Text = $"{models.Count} flash/generateContent";
        }
        catch (Exception ex)
        {
            var c = GeminiModels.Classify(ex);
            ModelsHint.Text = c.UserMessage;
        }
    }
}

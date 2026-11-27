using System.Numerics;
using System.Runtime.InteropServices;
using LiaDesktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Vortice.DXGI;
using WinRT;

namespace LiaDesktop.Rendering;

/// <summary>SwapChainPanel hosting DX11 hologram renderer.</summary>
public sealed class HoloPanel : SwapChainPanel
{
    readonly HoloRenderer _renderer = new();
    IDXGISwapChain1? _swap;
    DispatcherTimer? _timer;
    bool _inited;
    int _tickErrors;
    DateTime _last = DateTime.UtcNow;
    int _lastW, _lastH;
    double _lastScaleX = -1, _lastScaleY = -1;
    double? _forceScale;
    int _settlePassesLeft;
    double _pendingDipW, _pendingDipH;

    public HoloRenderer Renderer => _renderer;

    public HoloPanel()
    {
        _forceScale = ParseForceScale();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
        CompositionScaleChanged += OnCompositionScaleChanged;
        PointerMoved += OnPointerMoved;
        ActualThemeChanged += (_, _) => { };
    }

    static double? ParseForceScale()
    {
        foreach (var a in Environment.GetCommandLineArgs())
        {
            if (a.StartsWith("--force-scale=", StringComparison.OrdinalIgnoreCase) &&
                double.TryParse(a["--force-scale=".Length..],
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var s) &&
                s >= 0.5 && s <= 8.0)
                return s;
        }
        var env = Environment.GetEnvironmentVariable("LIA_FORCE_SCALE");
        if (double.TryParse(env, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var e) &&
            e >= 0.5 && e <= 8.0)
            return e;
        return null;
    }

    void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(this);
        var x = (float)(p.Position.X / Math.Max(1, ActualWidth) * 2 - 1);
        var y = (float)(-(p.Position.Y / Math.Max(1, ActualHeight) * 2 - 1));
        _renderer.SetPointer(x, y);
    }

    void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_inited) return;
        try
        {
            if (XamlRoot != null)
                XamlRoot.Changed += (_, _) => RequestSwapChain(force: true, settle: true);

            _renderer.InitDevice();
            var mesh = Path.Combine(AppContext.BaseDirectory, "Assets", "models", "lia_pbr_head.bin");
            if (!File.Exists(mesh))
                mesh = Path.Combine(AppContext.BaseDirectory, "Assets", "models", "lia_holo_mesh.bin");
            if (File.Exists(mesh)) _renderer.LoadMesh(mesh);
            _inited = true;
            RequestSwapChain(force: true, settle: true);

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            var ticking = false;
            _timer.Tick += (_, _) =>
            {
                if (ticking) return;
                ticking = true;
                try
                {
                    // Catch maximize/resize where SizeChanged raced ahead of ActualWidth,
                    // or AppWindow.Changed fired before XAML layout finished (100% DPI TVs).
                    SyncSwapChainToPanel();

                    var now = DateTime.UtcNow;
                    var dt = (float)(now - _last).TotalSeconds;
                    _last = now;
                    _renderer.Tick(Math.Clamp(dt, 0.001f, 0.1f));
                }
                catch (Exception ex)
                {
                    if (_tickErrors < 3)
                    {
                        _tickErrors++;
                        Program.BootLog("Tick: " + ex);
                    }
                }
                finally { ticking = false; }
            };
            _timer.Start();
        }
        catch (Exception ex)
        {
            Program.BootLog("HoloPanel init FATAL: " + ex);
            System.Diagnostics.Debug.WriteLine("HoloPanel init: " + ex);
        }
    }

    void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _timer?.Stop();
        _timer = null;
        _swap?.Dispose();
        _swap = null;
        _renderer.Dispose();
        _inited = false;
    }

    void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _pendingDipW = e.NewSize.Width;
        _pendingDipH = e.NewSize.Height;
        RequestSwapChain(force: false, settle: true);
    }

    void OnCompositionScaleChanged(SwapChainPanel sender, object args)
    {
        InvalidateMeasure();
        RequestSwapChain(force: true, settle: true);
    }

    /// <summary>Force buffer recreate after maximize / AppWindow size change.</summary>
    public void ForceRecreateSwapChain()
    {
        RequestSwapChain(force: true, settle: true);
    }

    void RequestSwapChain(bool force, bool settle)
    {
        if (force)
        {
            _lastW = _lastH = 0;
            _lastScaleX = _lastScaleY = -1;
        }
        if (settle)
            _settlePassesLeft = Math.Max(_settlePassesLeft, 12); // ~400ms of follow-up syncs

        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
        {
            CreateSwapChain(ReadDipSize());
            // Layout often lands 1–3 frames after AppWindow maximize at 100% scale
            ScheduleSettle();
        });
    }

    void ScheduleSettle()
    {
        foreach (var delayMs in new[] { 33, 100, 250, 500 })
        {
            var ms = delayMs;
            DispatcherQueueDelay.After(DispatcherQueue, TimeSpan.FromMilliseconds(ms), () =>
            {
                _lastW = _lastH = 0; // force compare against latest DIPs
                CreateSwapChain(ReadDipSize());
            });
        }
    }

    (double W, double H) ReadDipSize()
    {
        // Prefer SizeChanged NewSize when fresher; else live Actual*
        var w = ActualWidth;
        var h = ActualHeight;
        if (_pendingDipW >= 2 && _pendingDipH >= 2)
        {
            // If Actual lags NewSize by a lot, trust the larger (post-maximize) value
            if (_pendingDipW > w + 1) w = _pendingDipW;
            if (_pendingDipH > h + 1) h = _pendingDipH;
        }
        return (w, h);
    }

    void SyncSwapChainToPanel()
    {
        if (!_inited) return;
        var (dipW, dipH) = ReadDipSize();
        var (sx, sy) = CompositionAxes();
        var w = Math.Max(1, (int)Math.Round(dipW * sx));
        var h = Math.Max(1, (int)Math.Round(dipH * sy));
        if (w < 2 || h < 2) return;

        var drifted = Math.Abs(w - _lastW) > 1 || Math.Abs(h - _lastH) > 1
                      || Math.Abs(sx - _lastScaleX) > 0.001
                      || Math.Abs(sy - _lastScaleY) > 0.001;
        if (!drifted && _settlePassesLeft <= 0) return;
        if (_settlePassesLeft > 0) _settlePassesLeft--;
        if (drifted)
            CreateSwapChain((dipW, dipH));
    }

    (double Sx, double Sy) CompositionAxes()
    {
        if (_forceScale is { } f)
            return (f, f);

        // CompositionScaleX/Y are float; RasterizationScale is double — keep everything double
        double sx = CompositionScaleX;
        double sy = CompositionScaleY;
        if (sx < 0.1) sx = XamlRoot?.RasterizationScale ?? 1.0;
        if (sy < 0.1) sy = XamlRoot?.RasterizationScale ?? 1.0;
        if (sx < 0.1) sx = 1.0;
        if (sy < 0.1) sy = 1.0;
        return (sx, sy);
    }

    void CreateSwapChain((double DipW, double DipH) dip)
    {
        if (!_inited || _renderer.Device == null) return;
        var (sx, sy) = CompositionAxes();
        var w = Math.Max(1, (int)Math.Round(dip.DipW * sx));
        var h = Math.Max(1, (int)Math.Round(dip.DipH * sy));
        if (w < 2 || h < 2) return;
        if (w == _lastW && h == _lastH
            && Math.Abs(sx - _lastScaleX) < 0.0001
            && Math.Abs(sy - _lastScaleY) < 0.0001)
            return;

        Program.BootLog(
            $"SwapChain {w}x{h} (was {_lastW}x{_lastH}) scale={sx:0.###}x{sy:0.###} "
            + $"dip={dip.DipW:0}x{dip.DipH:0} actual={ActualWidth:0}x{ActualHeight:0}"
            + (_forceScale is { } fs ? $" force={fs:0.###}" : ""));

        _lastW = w;
        _lastH = h;
        _lastScaleX = sx;
        _lastScaleY = sy;

        try
        {
            // Prefer ResizeBuffers when a composition swapchain already exists —
            // dispose+recreate can race with maximize layout and leave a stale small buffer.
            if (_swap != null)
            {
                try
                {
                    // Drop RTVs before ResizeBuffers
                    _renderer.ReleaseSizeDependent();
                    _swap.ResizeBuffers(2, (uint)w, (uint)h, Format.B8G8R8A8_UNorm, SwapChainFlags.None);
                    ApplyMatrixTransform(sx, sy);
                    _renderer.Resize(w, h, _swap);
                    return;
                }
                catch (Exception ex)
                {
                    Program.BootLog("ResizeBuffers failed, recreating: " + ex.Message);
                    try { _swap.Dispose(); } catch { /* */ }
                    _swap = null;
                }
            }

            using var factory = _renderer.GetDxgiFactory();
            var desc = new SwapChainDescription1
            {
                Width = (uint)w,
                Height = (uint)h,
                Format = Format.B8G8R8A8_UNorm,
                Stereo = false,
                SampleDescription = new SampleDescription(1, 0),
                BufferUsage = Usage.RenderTargetOutput,
                BufferCount = 2,
                Scaling = Scaling.Stretch,
                SwapEffect = SwapEffect.FlipSequential,
                AlphaMode = AlphaMode.Premultiplied,
                Flags = SwapChainFlags.None,
            };
            _swap = factory.CreateSwapChainForComposition(_renderer.Device, desc);
            ApplyMatrixTransform(sx, sy);
            AttachSwapChainNative(_swap);
            _renderer.Resize(w, h, _swap);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("CreateSwapChain: " + ex);
            Program.BootLog("CreateSwapChain: " + ex.Message);
            AppLog.WriteCrash("swapchain", ex);
            // Allow retry next tick
            _lastW = _lastH = 0;
            _lastScaleX = _lastScaleY = -1;
        }
    }

    void ApplyMatrixTransform(double sx, double sy)
    {
        if (_swap == null) return;
        using var swap2 = _swap.QueryInterface<IDXGISwapChain2>();
        swap2.MatrixTransform = new Matrix3x2(
            1f / (float)sx, 0f,
            0f, 1f / (float)sy,
            0f, 0f);
    }

    void AttachSwapChainNative(IDXGISwapChain1 swap)
    {
        var panelPtr = ((IWinRTObject)this).NativeObject.ThisPtr;
        var iid = typeof(ISwapChainPanelNative).GUID;
        var hr = Marshal.QueryInterface(panelPtr, ref iid, out var nativePtr);
        if (hr != 0 || nativePtr == IntPtr.Zero)
            throw new InvalidOperationException($"ISwapChainPanelNative QI failed: 0x{hr:X8}");
        try
        {
            var panelNative = (ISwapChainPanelNative)Marshal.GetObjectForIUnknown(nativePtr);
            var setHr = panelNative.SetSwapChain(swap.NativePointer);
            if (setHr != 0)
                throw new InvalidOperationException($"SetSwapChain failed: 0x{setHr:X8}");
        }
        finally
        {
            Marshal.Release(nativePtr);
        }
    }
}

[ComImport]
[Guid("63aad0b8-7c24-40ff-85a8-640d944cc325")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface ISwapChainPanelNative
{
    [PreserveSig] int SetSwapChain(IntPtr swapChain);
}

/// <summary>One-shot dispatcher delay without holding a DispatcherTimer forever.</summary>
file static class DispatcherQueueDelay
{
    public static void After(
        Microsoft.UI.Dispatching.DispatcherQueue q,
        TimeSpan delay,
        Action action)
    {
        var t = q.CreateTimer();
        t.IsRepeating = false;
        t.Interval = delay;
        t.Tick += (_, _) =>
        {
            try { action(); }
            finally { t.Stop(); }
        };
        t.Start();
    }
}

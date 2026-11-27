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
                XamlRoot.Changed += (_, _) => QueueCreateSwapChain();

            _renderer.InitDevice();
            var mesh = Path.Combine(AppContext.BaseDirectory, "Assets", "models", "lia_pbr_head.bin");
            if (!File.Exists(mesh))
                mesh = Path.Combine(AppContext.BaseDirectory, "Assets", "models", "lia_holo_mesh.bin");
            if (File.Exists(mesh)) _renderer.LoadMesh(mesh);
            _inited = true;
            CreateSwapChain();
            // Second pass after first layout settle (maximize / DPI often land late)
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                _lastW = _lastH = 0;
                CreateSwapChain();
            });
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            var ticking = false;
            _timer.Tick += (_, _) =>
            {
                if (ticking) return;
                ticking = true;
                try
                {
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

    void OnSizeChanged(object sender, SizeChangedEventArgs e) => QueueCreateSwapChain();

    void OnCompositionScaleChanged(SwapChainPanel sender, object args)
    {
        InvalidateMeasure();
        QueueCreateSwapChain();
    }

    void QueueCreateSwapChain()
    {
        // Coalesce burst of SizeChanged + CompositionScaleChanged + XamlRoot.Changed
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, CreateSwapChain);
    }

    /// <summary>
    /// Composition scale axes used for buffer size AND inverse MatrixTransform.
    /// Must match XAML composition — do not mix RasterizationScale into only one side.
    /// </summary>
    (double Sx, double Sy) CompositionAxes()
    {
        if (_forceScale is { } f)
            return (f, f);

        var sx = CompositionScaleX;
        var sy = CompositionScaleY;
        // Before first layout these can be 0
        if (sx < 0.1) sx = XamlRoot?.RasterizationScale ?? 1.0;
        if (sy < 0.1) sy = XamlRoot?.RasterizationScale ?? 1.0;
        if (sx < 0.1) sx = 1.0;
        if (sy < 0.1) sy = 1.0;
        return (sx, sy);
    }

    void CreateSwapChain()
    {
        if (!_inited || _renderer.Device == null) return;
        var (sx, sy) = CompositionAxes();
        var w = Math.Max(1, (int)Math.Round(ActualWidth * sx));
        var h = Math.Max(1, (int)Math.Round(ActualHeight * sy));
        if (w < 2 || h < 2) return;
        if (w == _lastW && h == _lastH
            && Math.Abs(sx - _lastScaleX) < 0.0001
            && Math.Abs(sy - _lastScaleY) < 0.0001)
            return;
        _lastW = w;
        _lastH = h;
        _lastScaleX = sx;
        _lastScaleY = sy;
        Program.BootLog(
            $"SwapChain {w}x{h} scale={sx:0.###}x{sy:0.###} dip={ActualWidth:0}x{ActualHeight:0}"
            + (_forceScale is { } fs ? $" force={fs:0.###}" : ""));

        try
        {
            using var factory = _renderer.GetDxgiFactory();
            _swap?.Dispose();
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

            // Critical on HiDPI: buffer is physical pixels; tell composition to inverse-scale
            // so the full buffer maps to the DIP-sized panel (else top-left crop + grey box).
            using (var swap2 = _swap.QueryInterface<IDXGISwapChain2>())
            {
                swap2.MatrixTransform = new Matrix3x2(
                    (float)(1.0 / sx), 0,
                    0, (float)(1.0 / sy),
                    0, 0);
            }

            var panelPtr = ((IWinRTObject)this).NativeObject.ThisPtr;
            var iid = typeof(ISwapChainPanelNative).GUID;
            var hr = Marshal.QueryInterface(panelPtr, ref iid, out var nativePtr);
            if (hr != 0 || nativePtr == IntPtr.Zero)
                throw new InvalidOperationException($"ISwapChainPanelNative QI failed: 0x{hr:X8}");
            try
            {
                var panelNative = (ISwapChainPanelNative)Marshal.GetObjectForIUnknown(nativePtr);
                var setHr = panelNative.SetSwapChain(_swap.NativePointer);
                if (setHr != 0)
                    throw new InvalidOperationException($"SetSwapChain failed: 0x{setHr:X8}");
            }
            finally
            {
                Marshal.Release(nativePtr);
            }

            _renderer.Resize(w, h, _swap);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("CreateSwapChain: " + ex);
            Program.BootLog("CreateSwapChain: " + ex.Message);
            AppLog.WriteCrash("swapchain", ex);
        }
    }

    /// <summary>Force buffer recreate (e.g. after maximize from smoke harness).</summary>
    public void ForceRecreateSwapChain()
    {
        _lastW = _lastH = 0;
        _lastScaleX = _lastScaleY = -1;
        CreateSwapChain();
    }
}

[ComImport]
[Guid("63aad0b8-7c24-40ff-85a8-640d944cc325")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface ISwapChainPanelNative
{
    [PreserveSig] int SetSwapChain(IntPtr swapChain);
}

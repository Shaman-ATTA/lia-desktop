using System.Runtime.InteropServices;
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
    double _lastScale = -1;

    public HoloRenderer Renderer => _renderer;

    public HoloPanel()
    {
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
        CompositionScaleChanged += (_, _) => CreateSwapChain();
        PointerMoved += OnPointerMoved;
        ActualThemeChanged += (_, _) => { };
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
                XamlRoot.Changed += (_, _) => CreateSwapChain();

            _renderer.InitDevice();
            var mesh = Path.Combine(AppContext.BaseDirectory, "Assets", "models", "lia_pbr_head.bin");
            if (!File.Exists(mesh))
                mesh = Path.Combine(AppContext.BaseDirectory, "Assets", "models", "lia_holo_mesh.bin");
            if (File.Exists(mesh)) _renderer.LoadMesh(mesh);
            _inited = true;
            CreateSwapChain();
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

    void OnSizeChanged(object sender, SizeChangedEventArgs e) => CreateSwapChain();

    /// <summary>Physical pixel scale: max(CompositionScale, XamlRoot.RasterizationScale).</summary>
    double EffectiveScale()
    {
        var sx = Math.Max(CompositionScaleX, CompositionScaleY);
        var root = XamlRoot?.RasterizationScale ?? 0;
        return Math.Max(1.0, Math.Max(sx, root));
    }

    void CreateSwapChain()
    {
        if (!_inited || _renderer.Device == null) return;
        var scale = EffectiveScale();
        var w = Math.Max(1, (int)Math.Round(ActualWidth * scale));
        var h = Math.Max(1, (int)Math.Round(ActualHeight * scale));
        if (w < 2 || h < 2) return;
        if (w == _lastW && h == _lastH && Math.Abs(scale - _lastScale) < 0.001) return;
        _lastW = w;
        _lastH = h;
        _lastScale = scale;
        Program.BootLog($"SwapChain {w}x{h} scale={scale:0.###} dip={ActualWidth:0}x{ActualHeight:0}");

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

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
    DateTime _last = DateTime.UtcNow;

    public HoloRenderer Renderer => _renderer;

    public HoloPanel()
    {
        // SwapChainPanel does not support Background — leave transparent by default.
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
        CompositionScaleChanged += (_, _) => CreateSwapChain();
        PointerMoved += OnPointerMoved;
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
            _renderer.InitDevice();
            var mesh = Path.Combine(AppContext.BaseDirectory, "Assets", "models", "lia_pbr_head.bin");
            if (!File.Exists(mesh))
                mesh = Path.Combine(AppContext.BaseDirectory, "Assets", "models", "lia_holo_mesh.bin");
            if (File.Exists(mesh)) _renderer.LoadMesh(mesh);
            _inited = true;
            CreateSwapChain();
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _timer.Tick += (_, _) =>
            {
                var now = DateTime.UtcNow;
                var dt = (float)(now - _last).TotalSeconds;
                _last = now;
                try { _renderer.Tick(Math.Clamp(dt, 0.001f, 0.1f)); } catch { /* */ }
            };
            _timer.Start();
        }
        catch (Exception ex)
        {
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

    void CreateSwapChain()
    {
        if (!_inited || _renderer.Device == null) return;
        // Native DPI: render at CompositionScale (not DIP) — no blurry upscale
        var sx = Math.Max(1.0, CompositionScaleX);
        var sy = Math.Max(1.0, CompositionScaleY);
        var w = Math.Max(1, (int)Math.Round(ActualWidth * sx));
        var h = Math.Max(1, (int)Math.Round(ActualHeight * sy));
        if (w < 2 || h < 2) return;

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

            // Bind DXGI swapchain to WinUI SwapChainPanel via ISwapChainPanelNative
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

using System.Drawing;
using System.Runtime.InteropServices;

namespace LiaDesktop.Services;

/// <summary>Topmost popup for selftest vision/click without WinForms (WinUI-safe).</summary>
public sealed class Win32TestOverlay : IDisposable
{
    static readonly object Gate = new();
    static bool _classRegistered;
    static WndProc? _wndProcKeepAlive;

    readonly IntPtr _hwnd;
    readonly string _text;
    readonly Color _bg;
    readonly Color _fg;
    readonly float _fontPt;
    bool _disposed;

    Win32TestOverlay(IntPtr hwnd, string text, Color bg, Color fg, float fontPt)
    {
        _hwnd = hwnd;
        _text = text;
        _bg = bg;
        _fg = fg;
        _fontPt = fontPt;
    }

    public IntPtr Handle => _hwnd;

    public Rectangle ScreenBounds
    {
        get
        {
            GetWindowRect(_hwnd, out var r);
            return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        }
    }

    public static Win32TestOverlay Show(
        string text,
        int x, int y, int w, int h,
        Color? bg = null,
        Color? fg = null,
        float fontPt = 64f)
    {
        lock (Gate)
        {
            EnsureClass();
            var hwnd = CreateWindowEx(
                0x00000008 | 0x00000080, // WS_EX_TOPMOST | WS_EX_TOOLWINDOW
                "LiaSelfTestOverlay",
                "LIA TEST",
                0x80000000 | 0x10000000, // WS_POPUP | WS_VISIBLE
                x, y, w, h,
                IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
            if (hwnd == IntPtr.Zero)
                throw new InvalidOperationException("CreateWindowEx failed: " + Marshal.GetLastWin32Error());

            var overlay = new Win32TestOverlay(hwnd, text, bg ?? Color.White, fg ?? Color.Black, fontPt);
            var gch = GCHandle.Alloc(overlay);
            SetWindowLongPtr(hwnd, -21 /*GWL_USERDATA*/, GCHandle.ToIntPtr(gch));
            SetWindowPos(hwnd, new IntPtr(-1), x, y, w, h, 0x0040); // HWND_TOPMOST, SWP_SHOWWINDOW
            InvalidateRect(hwnd, IntPtr.Zero, true);
            UpdateWindow(hwnd);
            // Pump a few messages so WM_PAINT lands
            for (var i = 0; i < 20; i++)
            {
                if (!PeekMessage(out var msg, IntPtr.Zero, 0, 0, 1)) break;
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
            return overlay;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            var ptr = GetWindowLongPtr(_hwnd, -21);
            if (ptr != IntPtr.Zero)
            {
                var gch = GCHandle.FromIntPtr(ptr);
                if (gch.IsAllocated) gch.Free();
                SetWindowLongPtr(_hwnd, -21, IntPtr.Zero);
            }
            DestroyWindow(_hwnd);
        }
        catch { /* */ }
    }

    static void EnsureClass()
    {
        if (_classRegistered) return;
        _wndProcKeepAlive = WndProcImpl;
        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            style = 0x0003, // CS_HREDRAW|CS_VREDRAW
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcKeepAlive),
            hInstance = GetModuleHandle(null),
            hCursor = LoadCursor(IntPtr.Zero, 32512),
            hbrBackground = IntPtr.Zero,
            lpszClassName = "LiaSelfTestOverlay",
        };
        if (RegisterClassEx(ref wc) == 0 && Marshal.GetLastWin32Error() != 1410) // already exists
            throw new InvalidOperationException("RegisterClassEx failed: " + Marshal.GetLastWin32Error());
        _classRegistered = true;
    }

    static IntPtr WndProcImpl(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        const uint WM_PAINT = 0x000F;
        const uint WM_ERASEBKGND = 0x0014;
        const uint WM_DESTROY = 0x0002;
        if (msg == WM_ERASEBKGND) return new IntPtr(1);
        if (msg == WM_PAINT)
        {
            var hdc = BeginPaint(hwnd, out var ps);
            try
            {
                Win32TestOverlay? self = null;
                var ptr = GetWindowLongPtr(hwnd, -21);
                if (ptr != IntPtr.Zero)
                {
                    var gch = GCHandle.FromIntPtr(ptr);
                    if (gch.IsAllocated) self = gch.Target as Win32TestOverlay;
                }
                GetClientRect(hwnd, out var rc);
                using var g = Graphics.FromHdc(hdc);
                g.Clear(self?._bg ?? Color.White);
                if (self != null)
                {
                    using var font = new Font("Segoe UI", self._fontPt, FontStyle.Bold, GraphicsUnit.Point);
                    using var brush = new SolidBrush(self._fg);
                    var sf = new StringFormat
                    {
                        Alignment = StringAlignment.Center,
                        LineAlignment = StringAlignment.Center,
                    };
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    g.DrawString(self._text, font, brush,
                        new RectangleF(rc.Left, rc.Top, rc.Right - rc.Left, rc.Bottom - rc.Top), sf);
                }
            }
            finally
            {
                EndPaint(hwnd, ref ps);
            }
            return IntPtr.Zero;
        }
        if (msg == WM_DESTROY) return IntPtr.Zero;
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSEX
    {
        public int cbSize;
        public int style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam, lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PAINTSTRUCT
    {
        public IntPtr hdc;
        public int fErase;
        public RECT rcPaint;
        public int fRestore, fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] rgbReserved;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateWindowEx(
        int dwExStyle, string lpClassName, string lpWindowName, int dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")] static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);
    [DllImport("user32.dll")] static extern bool UpdateWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] static extern IntPtr BeginPaint(IntPtr hWnd, out PAINTSTRUCT lpPaint);
    [DllImport("user32.dll")] static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT lpPaint);
    [DllImport("user32.dll")] static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG lpMsg);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG lpMsg);
    [DllImport("user32.dll")] static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? lpModuleName);

    static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : new IntPtr(GetWindowLong32(hWnd, nIndex));

    static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr value) =>
        IntPtr.Size == 8 ? SetWindowLongPtr64(hWnd, nIndex, value) : new IntPtr(SetWindowLong32(hWnd, nIndex, value.ToInt32()));

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")] static extern int GetWindowLong32(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")] static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLong")] static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")] static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
}

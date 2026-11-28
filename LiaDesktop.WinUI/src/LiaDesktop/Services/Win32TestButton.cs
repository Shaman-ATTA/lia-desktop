using System.Runtime.InteropServices;
using System.Text;

namespace LiaDesktop.Services;

/// <summary>Topmost Win32 window with a real BUTTON for UIA click selftest.</summary>
public sealed class Win32TestButton : IDisposable
{
    static readonly object Gate = new();
    static bool _classRegistered;
    static WndProc? _keepAlive;
    static int _clicks;

    readonly IntPtr _hwnd;
    readonly IntPtr _btn;
    bool _disposed;

    public IntPtr Handle => _hwnd;
    public IntPtr ButtonHandle => _btn;
    public int ClickCount => _clicks;
    public string ButtonName { get; }

    Win32TestButton(IntPtr hwnd, IntPtr btn, string name)
    {
        _hwnd = hwnd;
        _btn = btn;
        ButtonName = name;
    }

    public static Win32TestButton Show(string buttonName, int x, int y, int w, int h)
    {
        lock (Gate)
        {
            EnsureClass();
            _clicks = 0;
            var hwnd = CreateWindowEx(
                0x00000008 | 0x00000080,
                "LiaSelfTestButtonHost",
                "LIA UIA TEST",
                unchecked((int)(0x80000000u | 0x10000000u | 0x00C00000u)), // POPUP|VISIBLE|CAPTION
                x, y, w, h,
                IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
            if (hwnd == IntPtr.Zero)
                throw new InvalidOperationException("CreateWindowEx host failed");

            var btn = CreateWindowEx(
                0, "BUTTON", buttonName,
                0x40000000 | 0x10000000 | 0x00000000, // WS_CHILD|WS_VISIBLE|BS_PUSHBUTTON
                40, 40, Math.Max(80, w - 80), 40,
                hwnd, new IntPtr(1001), GetModuleHandle(null), IntPtr.Zero);
            if (btn == IntPtr.Zero)
                throw new InvalidOperationException("CreateWindowEx button failed");

            SetWindowPos(hwnd, new IntPtr(-1), x, y, w, h, 0x0040);
            for (var i = 0; i < 30; i++)
            {
                if (!PeekMessage(out var msg, IntPtr.Zero, 0, 0, 1)) break;
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
            return new Win32TestButton(hwnd, btn, buttonName);
        }
    }

    public void Pump(int n = 20)
    {
        for (var i = 0; i < n; i++)
        {
            if (!PeekMessage(out var msg, IntPtr.Zero, 0, 0, 1)) break;
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { DestroyWindow(_hwnd); } catch { /* */ }
    }

    static void EnsureClass()
    {
        if (_classRegistered) return;
        _keepAlive = WndProcImpl;
        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            style = 0,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_keepAlive),
            hInstance = GetModuleHandle(null),
            lpszClassName = "LiaSelfTestButtonHost",
            hbrBackground = GetSysColorBrush(15),
        };
        if (RegisterClassEx(ref wc) == 0 && Marshal.GetLastWin32Error() != 1410)
            throw new InvalidOperationException("RegisterClassEx failed");
        _classRegistered = true;
    }

    static IntPtr WndProcImpl(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == 0x0111) // WM_COMMAND
        {
            if ((wParam.ToInt32() & 0xFFFF) == 1001)
                Interlocked.Increment(ref _clicks);
            return IntPtr.Zero;
        }
        if (msg == 0x0002) return IntPtr.Zero; // WM_DESTROY
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSEX
    {
        public int cbSize, style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground, lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam, lParam;
        public uint time;
        public int pt_x, pt_y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern ushort RegisterClassEx(ref WNDCLASSEX pc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateWindowEx(int dwExStyle, string lpClassName, string lpWindowName, int dwStyle,
        int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")] static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG lpMsg);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG lpMsg);
    [DllImport("user32.dll")] static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? lpModuleName);
    [DllImport("user32.dll")] static extern IntPtr GetSysColorBrush(int nIndex);
}

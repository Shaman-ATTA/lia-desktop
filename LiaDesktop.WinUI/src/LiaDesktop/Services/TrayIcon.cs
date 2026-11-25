using System.Runtime.InteropServices;

namespace LiaDesktop.Services;

/// <summary>Win32 tray icon on a dedicated message-only HWND (does not subclass WinUI).</summary>
public sealed class TrayIcon : IDisposable
{
    const int WmTray = 0x0401;
    const int WmLButtonDblClk = 0x0203;
    const int WmRButtonUp = 0x0205;
    const int WmDestroy = 0x0002;
    const int NimAdd = 0;
    const int NimDelete = 2;
    const int NifMessage = 1;
    const int NifIcon = 2;
    const int NifTip = 4;

    readonly Action _onRestore;
    readonly IntPtr _hwnd;
    readonly GCHandle _procPin;
    bool _added;

    public TrayIcon(Action onRestore)
    {
        _onRestore = onRestore;
        var proc = new WndProc(WindowProc);
        _procPin = GCHandle.Alloc(proc);
        var wc = new WndClassEx
        {
            cbSize = (uint)Marshal.SizeOf<WndClassEx>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(proc),
            hInstance = GetModuleHandle(null),
            lpszClassName = "LiaDesktopTrayClass",
        };
        RegisterClassEx(ref wc);
        _hwnd = CreateWindowEx(0, "LiaDesktopTrayClass", "LiaTray", 0,
            0, 0, 0, 0, (IntPtr)(-3) /* HWND_MESSAGE */, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) return;
        AddIcon();
    }

    void AddIcon()
    {
        var data = new NotifyIconData
        {
            cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NifMessage | NifIcon | NifTip,
            uCallbackMessage = WmTray,
            hIcon = LoadIcon(IntPtr.Zero, (IntPtr)32512),
            szTip = "Лиа · ATTA CORP",
        };
        var ico = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        if (File.Exists(ico))
        {
            var h = LoadImage(IntPtr.Zero, ico, 1, 0, 0, 0x00000010);
            if (h != IntPtr.Zero) data.hIcon = h;
        }
        _added = Shell_NotifyIcon(NimAdd, ref data);
    }

    IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WmTray)
        {
            var m = lParam.ToInt32() & 0xFFFF;
            if (m is WmLButtonDblClk or WmRButtonUp)
            {
                try { _onRestore(); } catch { /* */ }
            }
            return IntPtr.Zero;
        }
        if (msg == WmDestroy) return IntPtr.Zero;
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_added)
        {
            var data = new NotifyIconData
            {
                cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
                hWnd = _hwnd,
                uID = 1,
            };
            Shell_NotifyIcon(NimDelete, ref data);
            _added = false;
        }
        if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd);
        if (_procPin.IsAllocated) _procPin.Free();
    }

    delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WndClassEx
    {
        public uint cbSize;
        public uint style;
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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NotifyIconData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern bool Shell_NotifyIcon(int dwMessage, ref NotifyIconData lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern ushort RegisterClassEx(ref WndClassEx lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr CreateWindowEx(int dwExStyle, string lpClassName, string lpWindowName, int dwStyle,
        int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr LoadImage(IntPtr hInst, string name, int type, int cx, int cy, uint fuLoad);

    [DllImport("user32.dll")]
    static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);
}

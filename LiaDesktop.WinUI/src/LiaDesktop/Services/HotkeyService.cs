using System.Runtime.InteropServices;

namespace LiaDesktop.Services;

/// <summary>Ctrl+Alt+S global stop hotkey via RegisterHotKey + window subclass.</summary>
public sealed class HotkeyService : IDisposable
{
    const int HotkeyId = 0x33A1;
    const uint MOD_ALT = 0x0001;
    const uint MOD_CONTROL = 0x0002;
    const uint VK_S = 0x53;
    const uint WM_HOTKEY = 0x0312;
    const nuint SubclassId = 0x33A1;

    readonly IntPtr _hwnd;
    readonly SUBCLASSPROC _proc;
    bool _subclassed;
    public event Action? Triggered;

    public HotkeyService(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _proc = SubclassProc; // keep alive
        if (!RegisterHotKey(_hwnd, HotkeyId, MOD_CONTROL | MOD_ALT, VK_S))
            throw new InvalidOperationException("RegisterHotKey Ctrl+Alt+S failed");
        if (SetWindowSubclass(_hwnd, _proc, SubclassId, 0))
            _subclassed = true;
    }

    nint SubclassProc(IntPtr hWnd, uint uMsg, nint wParam, nint lParam, nuint uId, nuint dwRef)
    {
        if (uMsg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            try { Triggered?.Invoke(); } catch { /* */ }
            return 0;
        }
        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_subclassed)
        {
            RemoveWindowSubclass(_hwnd, _proc, SubclassId);
            _subclassed = false;
        }
        UnregisterHotKey(_hwnd, HotkeyId);
    }

    [DllImport("user32.dll")]
    static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("comctl32.dll", SetLastError = true)]
    static extern bool SetWindowSubclass(IntPtr hWnd, SUBCLASSPROC pfnSubclass, nuint uIdSubclass, nuint dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    static extern bool RemoveWindowSubclass(IntPtr hWnd, SUBCLASSPROC pfnSubclass, nuint uIdSubclass);

    [DllImport("comctl32.dll")]
    static extern nint DefSubclassProc(IntPtr hWnd, uint uMsg, nint wParam, nint lParam);

    delegate nint SUBCLASSPROC(IntPtr hWnd, uint uMsg, nint wParam, nint lParam, nuint uIdSubclass, nuint dwRefData);
}

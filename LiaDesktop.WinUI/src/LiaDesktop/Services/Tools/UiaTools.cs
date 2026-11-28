using System.Diagnostics;
using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

namespace LiaDesktop.Services.Tools;

/// <summary>UI Automation helpers (FlaUI UIA3) — list/focus/find/click by name.</summary>
public static class UiaTools
{
    public static object ListWindows()
    {
        using var auto = new UIA3Automation();
        var desk = auto.GetDesktop();
        var wins = new List<object>();
        foreach (var w in desk.FindAllChildren(cf => cf.ByControlType(ControlType.Window)))
        {
            try
            {
                var name = w.Name ?? "";
                if (string.IsNullOrWhiteSpace(name)) continue;
                var pid = w.Properties.ProcessId.TryGetValue(out var p) ? p : 0;
                var handle = w.Properties.NativeWindowHandle.TryGetValue(out var h) ? h.ToInt64() : 0L;
                wins.Add(new { name, pid, handle });
                if (wins.Count >= 80) break;
            }
            catch { /* */ }
        }
        return new { windows = wins };
    }

    public static object FocusWindow(string titleOrProcess)
    {
        titleOrProcess = (titleOrProcess ?? "").Trim();
        if (titleOrProcess.Length == 0) return new { error = "empty title" };

        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (string.Equals(p.ProcessName, titleOrProcess, StringComparison.OrdinalIgnoreCase)
                    || (p.MainWindowTitle?.Contains(titleOrProcess, StringComparison.OrdinalIgnoreCase) ?? false))
                {
                    if (p.MainWindowHandle == IntPtr.Zero) continue;
                    ShowWindow(p.MainWindowHandle, 9);
                    SetForegroundWindow(p.MainWindowHandle);
                    return new { ok = true, pid = p.Id, title = p.MainWindowTitle, process = p.ProcessName };
                }
            }
            catch { /* */ }
            finally { p.Dispose(); }
        }

        using var auto = new UIA3Automation();
        var desk = auto.GetDesktop();
        var win = FindWindowElement(desk, titleOrProcess);
        if (win == null) return new { error = "window not found", query = titleOrProcess };
        try
        {
            win.Focus();
            if (win.Properties.NativeWindowHandle.TryGetValue(out var hwnd) && hwnd != IntPtr.Zero)
            {
                ShowWindow(hwnd, 9);
                SetForegroundWindow(hwnd);
            }
        }
        catch (Exception ex) { return new { error = ex.Message }; }
        return new { ok = true, name = win.Name };
    }

    public static object WaitForWindow(string titleOrProcess, int timeoutMs = 15000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            var r = FocusWindow(titleOrProcess);
            var json = System.Text.Json.JsonSerializer.Serialize(r);
            if (json.Contains("\"ok\":true", StringComparison.Ordinal) ||
                json.Contains("\"ok\": true", StringComparison.Ordinal))
                return new { ok = true, waited_ms = sw.ElapsedMilliseconds, result = r };
            Thread.Sleep(200);
        }
        return new { error = "timeout", query = titleOrProcess, waited_ms = timeoutMs };
    }

    public static object FindElement(string name, string? windowTitle = null)
    {
        var el = Locate(name, windowTitle);
        if (el == null) return new { error = "not found", name, window = windowTitle };
        var bounds = el.BoundingRectangle;
        return new
        {
            ok = true,
            name = el.Name,
            controlType = el.ControlType.ToString(),
            x = (int)(bounds.X + bounds.Width / 2),
            y = (int)(bounds.Y + bounds.Height / 2),
            left = (int)bounds.X,
            top = (int)bounds.Y,
            width = (int)bounds.Width,
            height = (int)bounds.Height,
        };
    }

    public static object ClickElement(string name, string? windowTitle = null)
    {
        var el = Locate(name, windowTitle);
        if (el == null) return new { error = "not found", name, window = windowTitle };
        try
        {
            el.Focus();
            el.Click();
            var bounds = el.BoundingRectangle;
            return new
            {
                ok = true,
                name = el.Name,
                x = (int)(bounds.X + bounds.Width / 2),
                y = (int)(bounds.Y + bounds.Height / 2),
            };
        }
        catch (Exception ex)
        {
            try
            {
                var bounds = el.BoundingRectangle;
                var cx = (int)(bounds.X + bounds.Width / 2);
                var cy = (int)(bounds.Y + bounds.Height / 2);
                SetCursorPos(cx, cy);
                mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
                mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
                return new { ok = true, fallback = "mouse", x = cx, y = cy, warn = ex.Message };
            }
            catch (Exception ex2)
            {
                return new { error = ex2.Message };
            }
        }
    }

    public static string? ReadEditText(string windowTitleContains)
    {
        using var auto = new UIA3Automation();
        var desk = auto.GetDesktop();
        var win = FindWindowElement(desk, windowTitleContains);
        if (win == null) return null;
        var edit = win.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit))
                   ?? win.FindFirstDescendant(cf => cf.ByControlType(ControlType.Document));
        if (edit == null) return null;
        try
        {
            if (edit.Patterns.Value.IsSupported)
                return edit.Patterns.Value.Pattern.Value;
            return edit.Name;
        }
        catch { return edit.Name; }
    }

    static AutomationElement? FindWindowElement(AutomationElement desk, string titleOrPart)
    {
        foreach (var w in desk.FindAllChildren(cf => cf.ByControlType(ControlType.Window)))
        {
            var n = w.Name ?? "";
            if (n.Equals(titleOrPart, StringComparison.OrdinalIgnoreCase)
                || n.Contains(titleOrPart, StringComparison.OrdinalIgnoreCase))
                return w;
        }
        return desk.FindFirstDescendant(cf =>
            cf.ByName(titleOrPart).And(cf.ByControlType(ControlType.Window)));
    }

    static AutomationElement? Locate(string name, string? windowTitle)
    {
        using var auto = new UIA3Automation();
        var desk = auto.GetDesktop();
        AutomationElement root = desk;
        if (!string.IsNullOrWhiteSpace(windowTitle))
        {
            var win = FindWindowElement(desk, windowTitle);
            if (win != null) root = win;
        }

        return root.FindFirstDescendant(cf => cf.ByName(name))
               ?? root.FindFirstDescendant(cf => cf.ByAutomationId(name));
    }

    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);
}

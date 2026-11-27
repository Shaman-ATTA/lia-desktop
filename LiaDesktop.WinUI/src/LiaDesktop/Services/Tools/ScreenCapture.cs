using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using IoPath = System.IO.Path;

namespace LiaDesktop.Services.Tools;

/// <summary>
/// DPI-aware primary-monitor capture → downscaled image (max long side ~1600)
/// plus mapping back to physical screen pixels for mouse_click.
/// </summary>
public sealed record ScreenShot(
    string FilePath,
    string MimeType,
    string Base64,
    int PhysicalWidth,
    int PhysicalHeight,
    int ImageWidth,
    int ImageHeight,
    int MonitorLeft,
    int MonitorTop,
    double ScaleX,
    double ScaleY,
    int Dpi)
{
    public int ImageToPhysicalX(int imageX) =>
        MonitorLeft + (int)Math.Round(imageX * ScaleX);

    public int ImageToPhysicalY(int imageY) =>
        MonitorTop + (int)Math.Round(imageY * ScaleY);

    /// <summary>Synthetic mapping for click_map selftest (no real capture).</summary>
    public static ScreenShot ForMapping(
        int physicalW, int physicalH, int imageW, int imageH,
        int monitorLeft = 0, int monitorTop = 0, int dpi = 96) =>
        new(
            "", "image/jpeg", "",
            physicalW, physicalH, imageW, imageH,
            monitorLeft, monitorTop,
            physicalW / (double)imageW,
            physicalH / (double)imageH,
            dpi);

    public object Meta() => new
    {
        ok = true,
        path = FilePath,
        mime = MimeType,
        physical = new { w = PhysicalWidth, h = PhysicalHeight, left = MonitorLeft, top = MonitorTop, dpi = Dpi },
        image = new { w = ImageWidth, h = ImageHeight },
        scale = new { x = ScaleX, y = ScaleY },
        note = "mouse_click x/y are in THIS image's pixel space; runtime maps to physical screen.",
    };
}

public static class ScreenCapture
{
    public const int MaxLongSide = 1600;

    public static ScreenShot CapturePrimary(string? savePath = null, bool jpeg = true)
    {
        if (!TryPrimaryMonitor(out var monLeft, out var monTop, out var monW, out var monH, out var dpi))
        {
            monLeft = 0;
            monTop = 0;
            monW = GetSystemMetrics(0);
            monH = GetSystemMetrics(1);
            dpi = 96;
        }

        using var full = new Bitmap(monW, monH, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(full))
        {
            g.CopyFromScreen(monLeft, monTop, 0, 0, new Size(monW, monH), CopyPixelOperation.SourceCopy);
        }

        var longSide = Math.Max(monW, monH);
        var scale = longSide > MaxLongSide ? MaxLongSide / (double)longSide : 1.0;
        var imgW = Math.Max(1, (int)Math.Round(monW * scale));
        var imgH = Math.Max(1, (int)Math.Round(monH * scale));

        Bitmap sized = full;
        Bitmap? scaled = null;
        if (scale < 0.999)
        {
            scaled = new Bitmap(imgW, imgH, PixelFormat.Format24bppRgb);
            using var g = Graphics.FromImage(scaled);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(full, 0, 0, imgW, imgH);
            sized = scaled;
        }

        try
        {
            savePath ??= IoPath.Combine(
                IoPath.GetTempPath(),
                $"lia-screen-{DateTime.UtcNow:yyyyMMddHHmmssfff}.{(jpeg ? "jpg" : "png")}");
            var dir = IoPath.GetDirectoryName(savePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            string mime;
            if (jpeg)
            {
                mime = "image/jpeg";
                var enc = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
                var ep = new EncoderParameters(1);
                ep.Param[0] = new EncoderParameter(Encoder.Quality, 85L);
                sized.Save(savePath, enc, ep);
            }
            else
            {
                mime = "image/png";
                sized.Save(savePath, ImageFormat.Png);
            }

            var bytes = File.ReadAllBytes(savePath);
            var scaleX = monW / (double)imgW;
            var scaleY = monH / (double)imgH;
            return new ScreenShot(
                savePath, mime, Convert.ToBase64String(bytes),
                monW, monH, imgW, imgH, monLeft, monTop, scaleX, scaleY, dpi);
        }
        finally
        {
            scaled?.Dispose();
        }
    }

    static bool TryPrimaryMonitor(out int left, out int top, out int w, out int h, out int dpi)
    {
        left = top = w = h = 0;
        dpi = 96;
        var hMon = MonitorFromPoint(new POINT { X = 0, Y = 0 }, 1); // MONITOR_DEFAULTTOPRIMARY
        if (hMon == IntPtr.Zero) return false;
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(hMon, ref mi)) return false;
        left = mi.rcMonitor.Left;
        top = mi.rcMonitor.Top;
        w = mi.rcMonitor.Right - mi.rcMonitor.Left;
        h = mi.rcMonitor.Bottom - mi.rcMonitor.Top;
        try
        {
            if (GetDpiForMonitor(hMon, 0, out var dx, out _) == 0 && dx > 0)
                dpi = (int)dx;
        }
        catch { /* shcore optional */ }
        return w > 0 && h > 0;
    }

    [DllImport("user32.dll")] static extern int GetSystemMetrics(int nIndex);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
    [DllImport("Shcore.dll")] static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }
}

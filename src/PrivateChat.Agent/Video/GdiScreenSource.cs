using System.Drawing;
using System.Runtime.InteropServices;

namespace PrivateChat.Agent.Video;

/// <summary>
/// GDI 截屏：BitBlt / StretchBlt（HALFTONE 缩放）屏幕 DC 到一张 32 位 DIB，再画上鼠标指针。
/// 进程是 Per-Monitor V2 DPI 感知（Program 里设置），坐标都是物理像素。
/// 比桌面复制（DXGI Desktop Duplication）慢一些，但在远程桌面会话、多显卡、显示器热插拔下都能用，v1 足够。
/// UAC 提示和锁屏在安全桌面上，截不到（截到黑屏），见 docs/DESIGN.md 4.8 的已知限制。
/// </summary>
public sealed class GdiScreenSource : IFrameSource
{
    private IntPtr _memDc;
    private IntPtr _bitmap;
    private IntPtr _oldBitmap;
    private IntPtr _bits;
    private int _width;
    private int _height;
    private byte[] _buffer = [];

    public IReadOnlyList<ScreenInfo> Screens()
        => [.. Screen.AllScreens.Select((s, i) => new ScreenInfo(i, s.Bounds, s.Primary))];

    public CapturedFrame Capture(ScreenInfo screen, int width, int height)
    {
        EnsureBitmap(width, height);
        var b = screen.Bounds;
        var screenDc = Native.GetDC(IntPtr.Zero);
        try
        {
            if (width == b.Width && height == b.Height)
            {
                Native.BitBlt(_memDc, 0, 0, width, height, screenDc, b.X, b.Y, Native.SRCCOPY);
            }
            else
            {
                Native.SetStretchBltMode(_memDc, Native.HALFTONE);
                Native.SetBrushOrgEx(_memDc, 0, 0, IntPtr.Zero);
                Native.StretchBlt(_memDc, 0, 0, width, height, screenDc, b.X, b.Y, b.Width, b.Height, Native.SRCCOPY);
            }
            DrawCursor(b, width, height);
        }
        finally
        {
            Native.ReleaseDC(IntPtr.Zero, screenDc);
        }
        Native.GdiFlush();

        var stride = width * 4;
        if (_buffer.Length < stride * height) _buffer = new byte[stride * height];
        Marshal.Copy(_bits, _buffer, 0, stride * height);
        return new CapturedFrame(_buffer, width, height, stride);
    }

    private void DrawCursor(Rectangle bounds, int width, int height)
    {
        var info = new Native.CURSORINFO { cbSize = Marshal.SizeOf<Native.CURSORINFO>() };
        if (!Native.GetCursorInfo(ref info) || (info.flags & Native.CURSOR_SHOWING) == 0 || info.hCursor == IntPtr.Zero) return;
        if (!bounds.Contains(info.ptScreenPos.X, info.ptScreenPos.Y)) return;
        int hotX = 0, hotY = 0;
        if (Native.GetIconInfo(info.hCursor, out var icon))
        {
            hotX = icon.xHotspot;
            hotY = icon.yHotspot;
            if (icon.hbmMask != IntPtr.Zero) Native.DeleteObject(icon.hbmMask);
            if (icon.hbmColor != IntPtr.Zero) Native.DeleteObject(icon.hbmColor);
        }
        var x = (int)((info.ptScreenPos.X - bounds.X) * (double)width / bounds.Width) - hotX;
        var y = (int)((info.ptScreenPos.Y - bounds.Y) * (double)height / bounds.Height) - hotY;
        Native.DrawIconEx(_memDc, x, y, info.hCursor, 0, 0, 0, IntPtr.Zero, Native.DI_NORMAL);
    }

    private void EnsureBitmap(int width, int height)
    {
        if (_bitmap != IntPtr.Zero && width == _width && height == _height) return;
        FreeBitmap();
        var screenDc = Native.GetDC(IntPtr.Zero);
        try
        {
            _memDc = Native.CreateCompatibleDC(screenDc);
            var header = new Native.BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height, // 自上而下
                biPlanes = 1,
                biBitCount = 32,
            };
            _bitmap = Native.CreateDIBSection(screenDc, ref header, 0, out _bits, IntPtr.Zero, 0);
            if (_bitmap == IntPtr.Zero) throw new InvalidOperationException($"CreateDIBSection {width}x{height} 失败");
            _oldBitmap = Native.SelectObject(_memDc, _bitmap);
            _width = width;
            _height = height;
        }
        finally
        {
            Native.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private void FreeBitmap()
    {
        if (_memDc != IntPtr.Zero)
        {
            if (_oldBitmap != IntPtr.Zero) Native.SelectObject(_memDc, _oldBitmap);
            Native.DeleteDC(_memDc);
        }
        if (_bitmap != IntPtr.Zero) Native.DeleteObject(_bitmap);
        _memDc = _bitmap = _oldBitmap = _bits = IntPtr.Zero;
    }

    public void Dispose() => FreeBitmap();

    private static class Native
    {
        public const int SRCCOPY = 0x00CC0020;
        public const int HALFTONE = 4;
        public const int CURSOR_SHOWING = 1;
        public const int DI_NORMAL = 3;

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct CURSORINFO { public int cbSize; public int flags; public IntPtr hCursor; public POINT ptScreenPos; }

        [StructLayout(LayoutKind.Sequential)]
        public struct ICONINFO { public bool fIcon; public int xHotspot; public int yHotspot; public IntPtr hbmMask; public IntPtr hbmColor; }

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFOHEADER
        {
            public int biSize; public int biWidth; public int biHeight; public short biPlanes; public short biBitCount;
            public int biCompression; public int biSizeImage; public int biXPelsPerMeter; public int biYPelsPerMeter;
            public int biClrUsed; public int biClrImportant;
        }

        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
        [DllImport("user32.dll")] public static extern bool GetCursorInfo(ref CURSORINFO info);
        [DllImport("user32.dll")] public static extern bool GetIconInfo(IntPtr icon, out ICONINFO info);
        [DllImport("user32.dll")] public static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr icon, int cx, int cy, int step, IntPtr brush, int flags);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER header, int usage, out IntPtr bits, IntPtr section, int offset);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
        [DllImport("gdi32.dll")] public static extern bool StretchBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int sw, int sh, int rop);
        [DllImport("gdi32.dll")] public static extern int SetStretchBltMode(IntPtr hdc, int mode);
        [DllImport("gdi32.dll")] public static extern bool SetBrushOrgEx(IntPtr hdc, int x, int y, IntPtr old);
        [DllImport("gdi32.dll")] public static extern bool GdiFlush();
    }
}

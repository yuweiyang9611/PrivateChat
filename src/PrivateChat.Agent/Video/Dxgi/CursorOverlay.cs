using System.Drawing;

namespace PrivateChat.Agent.Video;

/// <summary>
/// 鼠标指针的样子。桌面复制截到的画面里没有指针，指针的形状单独给（IDXGIOutputDuplication::GetFramePointerShape），有三种格式：
/// - 单色（1）：上半是 AND 掩码、下半是 XOR 掩码，每像素 1 位（和 GDI 的单色光标一样：AND=1 XOR=0 透明，AND=0 黑 / 白，AND=1 XOR=1 反色）；
/// - 彩色（2）：32 位 BGRA，A 是不透明度（非预乘，和微软的桌面复制示例一样按 SRC_ALPHA / INV_SRC_ALPHA 混合）；
/// - 带掩码的彩色（4）：32 位 BGRA，A = 0 时用这个颜色盖掉屏幕，A = 0xFF 时和屏幕异或。
/// 这里统一成每个像素一种画法。纯计算（单元测试里也编进来）。
/// </summary>
public sealed class CursorShape
{
    public const int TypeMonochrome = 1;
    public const int TypeColor = 2;
    public const int TypeMaskedColor = 4;

    /// <summary>不画（屏幕原样）。</summary>
    internal const byte None = 0;
    /// <summary>按 <see cref="Pixels"/> 的 A（0–255，非预乘）混合它的 BGR。</summary>
    internal const byte Blend = 1;
    /// <summary>屏幕的 BGR 和 <see cref="Pixels"/> 的 BGR 异或（反色就是和白色异或）。</summary>
    internal const byte Xor = 2;

    private CursorShape(int width, int height, int hotX, int hotY, byte[] ops, uint[] pixels)
    {
        Width = width;
        Height = height;
        HotX = hotX;
        HotY = hotY;
        Ops = ops;
        Pixels = pixels;
    }

    public int Width { get; }
    public int Height { get; }
    /// <summary>热点（指针"指着"的那个点）在形状里的位置。</summary>
    public int HotX { get; }
    public int HotY { get; }
    /// <summary>每个像素怎么画（<see cref="None"/> / <see cref="Blend"/> / <see cref="Xor"/>），按行排。</summary>
    internal byte[] Ops { get; }
    /// <summary>每个像素的 BGRA（B 在最低字节）。</summary>
    internal uint[] Pixels { get; }

    /// <summary>从桌面复制给的形状（DXGI_OUTDUPL_POINTER_SHAPE_INFO + 缓冲区）转过来；格式不认识或者缓冲区不够大时 null（不画指针）。</summary>
    public static CursorShape? FromDxgi(int type, int width, int height, int pitch, int hotX, int hotY, ReadOnlySpan<byte> buffer)
    {
        if (width <= 0 || height <= 0 || pitch <= 0 || width > 1024 || height > 2048) return null;
        switch (type)
        {
            case TypeMonochrome:
            {
                // 高度是两张掩码加起来的
                var rows = height / 2;
                if (rows <= 0 || pitch * 8 < width || buffer.Length < pitch * rows * 2) return null;
                var ops = new byte[width * rows];
                var pixels = new uint[width * rows];
                for (var y = 0; y < rows; y++)
                    for (var x = 0; x < width; x++)
                    {
                        var bit = 0x80 >> (x & 7);
                        var and = (buffer[y * pitch + (x >> 3)] & bit) != 0;
                        var xor = (buffer[(y + rows) * pitch + (x >> 3)] & bit) != 0;
                        var i = y * width + x;
                        (ops[i], pixels[i]) = (and, xor) switch
                        {
                            (true, false) => (None, 0u),
                            (false, false) => (Blend, 0xFF000000u),
                            (false, true) => (Blend, 0xFFFFFFFFu),
                            (true, true) => (Xor, 0x00FFFFFFu),
                        };
                    }
                return new CursorShape(width, rows, hotX, hotY, ops, pixels);
            }
            case TypeColor:
            case TypeMaskedColor:
            {
                if (pitch < width * 4 || buffer.Length < pitch * (height - 1) + width * 4) return null;
                var ops = new byte[width * height];
                var pixels = new uint[width * height];
                for (var y = 0; y < height; y++)
                    for (var x = 0; x < width; x++)
                    {
                        var o = y * pitch + x * 4;
                        var bgr = (uint)(buffer[o] | buffer[o + 1] << 8 | buffer[o + 2] << 16);
                        var a = buffer[o + 3];
                        var i = y * width + x;
                        if (type == TypeColor)
                            (ops[i], pixels[i]) = a == 0 ? (None, 0u) : (Blend, bgr | (uint)a << 24);
                        else if (a == 0)
                            (ops[i], pixels[i]) = (Blend, bgr | 0xFF000000u);
                        else
                            (ops[i], pixels[i]) = bgr == 0 ? (None, 0u) : (Xor, bgr);
                    }
                return new CursorShape(width, height, hotX, hotY, ops, pixels);
            }
            default:
                return null;
        }
    }
}

/// <summary>
/// 把指针画到一帧紧凑排列的 NV12 上（CPU，只动指针那一小块）。和 GDI 截屏时一样：位置按缩放比例换算，指针按原始大小画。
/// 换算颜色用和 <see cref="Nv12.FromBgra"/> 一模一样的 BT.709 有限范围公式（色度取 2×2 平均）：
/// 指针盖住的像素先从 NV12 还原成 RGB，按指针的画法改，再转回去；一个 2×2 块里只要有一个像素被改了，就按四个像素重算这块的色度。
/// 纯计算（单元测试里也编进来）。
/// </summary>
public static class CursorOverlay
{
    /// <summary>YUV（减掉 16 / 128 之后）→ RGB：<see cref="Nv12.FromBgra"/> 那个矩阵的逆。</summary>
    private static readonly double[] Inverse = Invert([47 / 256.0, 157 / 256.0, 16 / 256.0, -26 / 256.0, -87 / 256.0, 112 / 256.0, 112 / 256.0, -102 / 256.0, -10 / 256.0]);

    /// <summary>
    /// 指针左上角画在缩放后的画面（width × height）上的什么位置。x, y：指针形状左上角在源画面（sourceWidth × sourceHeight）上的位置。
    /// 和 GDI 截屏一样：热点的位置按比例缩放，再减去（没缩放的）热点偏移。
    /// </summary>
    public static Point Place(int x, int y, int hotX, int hotY, int sourceWidth, int sourceHeight, int width, int height)
        => new((int)((x + hotX) * (double)width / sourceWidth) - hotX, (int)((y + hotY) * (double)height / sourceHeight) - hotY);

    /// <summary>
    /// 把指针（左上角在 left, top）画到 nv12（width × height，宽高是偶数）上。
    /// 返回改动的区域：裁到画面里、扩到偶数边界（色度按 2×2 存）；指针完全在画面外时是空的。
    /// </summary>
    public static Rectangle Draw(CursorShape shape, Span<byte> nv12, int width, int height, int left, int top)
    {
        var x0 = Math.Max(0, left);
        var y0 = Math.Max(0, top);
        var x1 = Math.Min(width, left + shape.Width);
        var y1 = Math.Min(height, top + shape.Height);
        if (x1 <= x0 || y1 <= y0) return Rectangle.Empty;
        // 宽高是偶数，扩到偶数不会出界
        x0 &= ~1;
        y0 &= ~1;
        x1 = Math.Min(width, (x1 + 1) & ~1);
        y1 = Math.Min(height, (y1 + 1) & ~1);
        var uvStart = width * height;
        for (var by = y0; by < y1; by += 2)
        {
            for (var bx = x0; bx < x1; bx += 2)
            {
                var uvIndex = uvStart + by / 2 * width + bx;
                var u = nv12[uvIndex] - 128;
                var v = nv12[uvIndex + 1] - 128;
                int rs = 0, gs = 0, bs = 0;
                var touched = false;
                for (var k = 0; k < 4; k++)
                {
                    var px = bx + (k & 1);
                    var py = by + (k >> 1);
                    var yIndex = py * width + px;
                    var luma = nv12[yIndex] - 16;
                    var r = Clamp(Inverse[0] * luma + Inverse[1] * u + Inverse[2] * v);
                    var g = Clamp(Inverse[3] * luma + Inverse[4] * u + Inverse[5] * v);
                    var b = Clamp(Inverse[6] * luma + Inverse[7] * u + Inverse[8] * v);
                    var sx = px - left;
                    var sy = py - top;
                    if (sx >= 0 && sy >= 0 && sx < shape.Width && sy < shape.Height)
                    {
                        var i = sy * shape.Width + sx;
                        var op = shape.Ops[i];
                        if (op != CursorShape.None)
                        {
                            var p = shape.Pixels[i];
                            int pb = (int)(p & 0xFF), pg = (int)(p >> 8 & 0xFF), pr = (int)(p >> 16 & 0xFF), pa = (int)(p >> 24);
                            if (op == CursorShape.Blend)
                            {
                                r = (pr * pa + r * (255 - pa) + 127) / 255;
                                g = (pg * pa + g * (255 - pa) + 127) / 255;
                                b = (pb * pa + b * (255 - pa) + 127) / 255;
                            }
                            else
                            {
                                r ^= pr;
                                g ^= pg;
                                b ^= pb;
                            }
                            nv12[yIndex] = Luma(r, g, b);
                            touched = true;
                        }
                    }
                    rs += r;
                    gs += g;
                    bs += b;
                }
                if (!touched) continue;
                nv12[uvIndex] = (byte)(((-26 * rs - 87 * gs + 112 * bs + 512) >> 10) + 128);
                nv12[uvIndex + 1] = (byte)(((112 * rs - 102 * gs - 10 * bs + 512) >> 10) + 128);
            }
        }
        return Rectangle.FromLTRB(x0, y0, x1, y1);
    }

    /// <summary>把 rect（<see cref="Draw"/> 返回的，偶数边界）这块从 clean 拷回 frame：擦掉上次画的指针。</summary>
    public static void Restore(ReadOnlySpan<byte> clean, Span<byte> frame, int width, int height, Rectangle rect)
    {
        var left = Math.Max(0, rect.Left) & ~1;
        var top = Math.Max(0, rect.Top) & ~1;
        var right = Math.Min(width, (rect.Right + 1) & ~1);
        var bottom = Math.Min(height, (rect.Bottom + 1) & ~1);
        if (right <= left || bottom <= top) return;
        var count = right - left;
        for (var y = top; y < bottom; y++)
            clean.Slice(y * width + left, count).CopyTo(frame[(y * width + left)..]);
        var uvStart = width * height;
        for (var y = top / 2; y < bottom / 2; y++)
            clean.Slice(uvStart + y * width + left, count).CopyTo(frame[(uvStart + y * width + left)..]);
    }

    /// <summary>和 <see cref="Nv12.FromBgra"/> 的亮度公式一样。</summary>
    private static byte Luma(int r, int g, int b) => (byte)(((47 * r + 157 * g + 16 * b + 128) >> 8) + 16);

    private static int Clamp(double value) => (int)Math.Clamp(Math.Round(value), 0, 255);

    /// <summary>3×3 矩阵（按行）求逆。</summary>
    private static double[] Invert(double[] m)
    {
        var det = m[0] * (m[4] * m[8] - m[5] * m[7]) - m[1] * (m[3] * m[8] - m[5] * m[6]) + m[2] * (m[3] * m[7] - m[4] * m[6]);
        return
        [
            (m[4] * m[8] - m[5] * m[7]) / det, (m[2] * m[7] - m[1] * m[8]) / det, (m[1] * m[5] - m[2] * m[4]) / det,
            (m[5] * m[6] - m[3] * m[8]) / det, (m[0] * m[8] - m[2] * m[6]) / det, (m[2] * m[3] - m[0] * m[5]) / det,
            (m[3] * m[7] - m[4] * m[6]) / det, (m[1] * m[6] - m[0] * m[7]) / det, (m[0] * m[4] - m[1] * m[3]) / det,
        ];
    }
}

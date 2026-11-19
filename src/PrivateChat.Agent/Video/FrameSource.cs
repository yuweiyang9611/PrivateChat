using System.Drawing;

namespace PrivateChat.Agent.Video;

/// <summary>一块显示器：在虚拟桌面上的位置（物理像素）。</summary>
public sealed record ScreenInfo(int Index, Rectangle Bounds, bool IsPrimary);

/// <summary>采到的一帧：BGRA，已缩放到编码尺寸（偶数宽高）。缓冲区由来源复用，下一次采集前有效。</summary>
public sealed class CapturedFrame(byte[] bgra, int width, int height, int stride)
{
    public byte[] Bgra { get; } = bgra;
    public int Width { get; } = width;
    public int Height { get; } = height;
    public int Stride { get; } = stride;
}

public interface IFrameSource : IDisposable
{
    IReadOnlyList<ScreenInfo> Screens();

    /// <summary>采集一块显示器，缩放到 width × height（调用方按 <see cref="FrameGeometry.Fit"/> 算好）。</summary>
    CapturedFrame Capture(ScreenInfo screen, int width, int height);
}

public static class FrameGeometry
{
    /// <summary>等比缩放到不超过 maxWidth × maxHeight（不放大），宽高取偶数。</summary>
    public static (int Width, int Height) Fit(int width, int height, int maxWidth, int maxHeight)
    {
        var scale = Math.Min(1.0, Math.Min((double)maxWidth / width, (double)maxHeight / height));
        var w = (int)Math.Round(width * scale) & ~1;
        var h = (int)Math.Round(height * scale) & ~1;
        return (Math.Max(2, w), Math.Max(2, h));
    }
}

/// <summary>
/// 测试画面（e2e 用，不碰真实屏幕）：四个纯色象限（左上红、右上绿、左下蓝、右下白），
/// 中间一条横带里有移动的黑块，保证每帧都在变。两块假显示器：1280×720（主）和 1920×1080。
/// </summary>
public sealed class TestPatternSource : IFrameSource
{
    private static readonly ScreenInfo[] Fake =
    [
        new(0, new Rectangle(0, 0, 1280, 720), true),
        new(1, new Rectangle(1280, 0, 1920, 1080), false),
    ];

    private byte[] _buffer = [];
    private int _tick;

    public IReadOnlyList<ScreenInfo> Screens() => Fake;

    public CapturedFrame Capture(ScreenInfo screen, int width, int height)
    {
        var stride = width * 4;
        if (_buffer.Length < stride * height) _buffer = new byte[stride * height];
        var tick = _tick++;
        var bandTop = height * 45 / 100;
        var bandBottom = height * 55 / 100;
        var blockX = tick * 8 % Math.Max(1, width - 32);
        // 第二块显示器的右下象限换成黄色，用来分辨当前是哪块
        var corner = screen.Index == 0 ? (b: (byte)255, g: (byte)255, r: (byte)255) : (b: (byte)0, g: (byte)255, r: (byte)255);
        for (var y = 0; y < height; y++)
        {
            var row = _buffer.AsSpan(y * stride, stride);
            var top = y < height / 2;
            for (var x = 0; x < width; x++)
            {
                var left = x < width / 2;
                var (b, g, r) = (top, left) switch
                {
                    (true, true) => ((byte)0, (byte)0, (byte)255),
                    (true, false) => ((byte)0, (byte)255, (byte)0),
                    (false, true) => ((byte)255, (byte)0, (byte)0),
                    _ => corner,
                };
                if (y >= bandTop && y < bandBottom && x >= blockX && x < blockX + 32) (b, g, r) = (0, 0, 0);
                var p = row.Slice(x * 4, 4);
                p[0] = b;
                p[1] = g;
                p[2] = r;
                p[3] = 255;
            }
        }
        return new CapturedFrame(_buffer, width, height, stride);
    }

    public void Dispose() { }
}

/// <summary>
/// 忙碌的测试画面（弱网测试用，--source busy）：一块 1280×720 的假显示器，
/// - 底图每 8 秒换一次：四色象限 ↔ 满屏的"文字"。像在两个窗口之间切换：整屏都是新内容，是远程画面里最贵的一种帧
///   （这个编码器把质量压到最低也有几十 KB）；
/// - 左上角一排黑白方块是时间码：采集时刻的毫秒数 ÷ 10 的低 20 位 + 4 位校验。控制端从解出来的画面里读回来，
///   和自己的时钟一比就是端到端延迟（同一台电脑上跑测试，两边是同一个时钟）；
/// - 中间 640×360 的区域是一页"文字"，滚 4 秒、停 4 秒：滚动时每帧都有几十 KB 要传，线路慢就会被压住；
///   停下来之后应该一步步变清晰。
/// </summary>
public sealed class BusyPatternSource : IFrameSource
{
    public const int Width = 1280;
    public const int Height = 720;
    /// <summary>时间码方块：左上角起 (8, 8)，每个 32×32，间隔 36，共 24 个（20 位时间 + 4 位校验）。</summary>
    public const int StampBits = 20;
    private const int TextLeft = 320, TextTop = 180, TextWidth = 640, TextHeight = 360, PageHeight = 1440;

    private static readonly ScreenInfo[] Fake = [new(0, new Rectangle(0, 0, Width, Height), true)];
    private readonly byte[] _page = Page();
    private readonly byte[] _native = new byte[Width * Height * 4];
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private byte[] _scaled = [];
    private int _scroll;

    public IReadOnlyList<ScreenInfo> Screens() => Fake;

    /// <summary>一页灰度的"文字"：白底上一行行深色笔画，固定种子。</summary>
    private static byte[] Page()
    {
        var page = new byte[TextWidth * PageHeight];
        Array.Fill(page, (byte)245);
        var random = new Random(20261119);
        for (var y = 6; y < PageHeight - 14; y += 18)
        {
            var x = 10 + random.Next(30);
            var end = TextWidth - 20 - random.Next(200);
            while (x < end)
            {
                if (random.Next(7) == 0) { x += 10; continue; }
                var w = 3 + random.Next(7);
                var shade = (byte)(20 + random.Next(70));
                for (var k = 0; k < 3; k++)
                {
                    var vertical = random.Next(2) == 0;
                    var (sx, sy, sw, sh) = vertical ? (x + random.Next(w), y + 1, 1 + random.Next(2), 11) : (x, y + 1 + random.Next(10), w, 1 + random.Next(2));
                    for (var yy = sy; yy < sy + sh; yy++)
                        for (var xx = sx; xx < Math.Min(TextWidth, sx + sw); xx++) page[yy * TextWidth + xx] = shade;
                }
                x += w + 2;
            }
        }
        return page;
    }

    public CapturedFrame Capture(ScreenInfo screen, int width, int height)
    {
        // 底图：四色象限，或者满屏的文字（每 8 秒换一次）
        var textBackground = _clock.ElapsedMilliseconds / 8000 % 2 == 1;
        for (var y = 0; y < Height; y++)
        {
            var row = _native.AsSpan(y * Width * 4, Width * 4);
            if (textBackground)
            {
                var line = (y + 700) % PageHeight * TextWidth;
                for (var x = 0; x < Width; x++)
                {
                    var v = _page[line + x % TextWidth];
                    row[x * 4] = row[x * 4 + 1] = row[x * 4 + 2] = v;
                    row[x * 4 + 3] = 255;
                }
                continue;
            }
            for (var x = 0; x < Width; x++)
            {
                var (b, g, r) = (y < Height / 2, x < Width / 2) switch
                {
                    (true, true) => ((byte)0, (byte)0, (byte)255),
                    (true, false) => ((byte)0, (byte)255, (byte)0),
                    (false, true) => ((byte)255, (byte)0, (byte)0),
                    _ => ((byte)255, (byte)255, (byte)255),
                };
                row[x * 4] = b;
                row[x * 4 + 1] = g;
                row[x * 4 + 2] = r;
                row[x * 4 + 3] = 255;
            }
        }
        // 文字区域：滚 4 秒、停 4 秒
        if (_clock.ElapsedMilliseconds / 4000 % 2 == 0) _scroll = (_scroll + 8) % PageHeight;
        for (var y = 0; y < TextHeight; y++)
        {
            var source = (y + _scroll) % PageHeight * TextWidth;
            var row = _native.AsSpan(((TextTop + y) * Width + TextLeft) * 4, TextWidth * 4);
            for (var x = 0; x < TextWidth; x++)
            {
                var v = _page[source + x];
                row[x * 4] = row[x * 4 + 1] = row[x * 4 + 2] = v;
            }
        }
        // 时间码
        var stamp = (int)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 10 & ((1 << StampBits) - 1));
        var check = 0;
        for (var i = 0; i < StampBits; i += 4) check += stamp >> i & 0xF;
        var bits = stamp | (check & 0xF) << StampBits;
        for (var i = 0; i < StampBits + 4; i++)
        {
            var v = (bits >> i & 1) != 0 ? (byte)255 : (byte)0;
            for (var y = 8; y < 40; y++)
                _native.AsSpan((y * Width + 8 + i * 36) * 4, 32 * 4).Fill(v);
        }

        if (width == Width && height == Height) return new CapturedFrame(_native, Width, Height, Width * 4);
        // 别的尺寸（网络慢时降了分辨率）：最近邻缩放
        if (_scaled.Length < width * height * 4) _scaled = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            var sy = y * Height / height;
            for (var x = 0; x < width; x++)
                _native.AsSpan((sy * Width + x * Width / width) * 4, 4).CopyTo(_scaled.AsSpan((y * width + x) * 4, 4));
        }
        return new CapturedFrame(_scaled, width, height, width * 4);
    }

    public void Dispose() { }
}

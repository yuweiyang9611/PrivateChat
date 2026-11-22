using System.Drawing;
using System.Runtime.InteropServices;

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
/// 测试画面用的整行画法：e2e 跑的是 Debug 版，逐像素画整屏一帧要几十毫秒（1920×1080 一百多毫秒），
/// 比采集后面的比较、转换、编码加起来还多，自动档会以为是这台电脑忙不过来、把分辨率降下去。
/// 所以不变的部分画一次存着，每帧只改会变的那几块，而且按行用 Fill / CopyTo。
/// </summary>
file static class Paint
{
    /// <summary>一个像素（B、G、R，不透明）按内存里的字节顺序读成 uint：拿它 Fill 一行，和逐字节写出来的一样。</summary>
    public static uint Pixel(byte b, byte g, byte r) => MemoryMarshal.Read<uint>([b, g, r, 255]);

    public static readonly uint Red = Pixel(0, 0, 255), Green = Pixel(0, 255, 0), Blue = Pixel(255, 0, 0),
        White = Pixel(255, 255, 255), Yellow = Pixel(0, 255, 255), Black = Pixel(0, 0, 0);

    public static Span<uint> Pixels(Span<byte> bytes) => MemoryMarshal.Cast<byte, uint>(bytes);

    /// <summary>四色象限：左上红、右上绿、左下蓝、右下 corner（上半是 y &lt; 高 / 2，左半是 x &lt; 宽 / 2）。</summary>
    public static byte[] Quadrants(int width, int height, uint corner)
    {
        var stride = width * 4;
        var image = new byte[stride * height];
        var half = height / 2;
        var top = image.AsSpan(0, stride);
        Pixels(top)[..(width / 2)].Fill(Red);
        Pixels(top)[(width / 2)..].Fill(Green);
        for (var y = 1; y < half; y++) top.CopyTo(image.AsSpan(y * stride));
        var bottom = image.AsSpan(half * stride, stride);
        Pixels(bottom)[..(width / 2)].Fill(Blue);
        Pixels(bottom)[(width / 2)..].Fill(corner);
        for (var y = half + 1; y < height; y++) bottom.CopyTo(image.AsSpan(y * stride));
        return image;
    }
}

/// <summary>
/// 测试画面（e2e 用，不碰真实屏幕）：四个纯色象限（左上红、右上绿、左下蓝、右下白），
/// 中间一条横带里有移动的黑块，保证每帧都在变。两块假显示器：1280×720（主）和 1920×1080。
/// 象限按（显示器、尺寸）画一次存着；每帧只把横带抹回底色、再画黑块（为什么见 <see cref="Paint"/>）。
/// </summary>
public sealed class TestPatternSource : IFrameSource
{
    private static readonly ScreenInfo[] Fake =
    [
        new(0, new Rectangle(0, 0, 1280, 720), true),
        new(1, new Rectangle(1280, 0, 1920, 1080), false),
    ];

    private byte[] _background = [];
    private (int Screen, int Width, int Height) _backgroundFor = (-1, 0, 0);
    private byte[] _buffer = [];
    private int _tick;

    public IReadOnlyList<ScreenInfo> Screens() => Fake;

    public CapturedFrame Capture(ScreenInfo screen, int width, int height)
    {
        var stride = width * 4;
        var tick = _tick++;
        var bandTop = height * 45 / 100;
        var bandBottom = height * 55 / 100;
        if (_backgroundFor != (screen.Index, width, height))
        {
            // 第二块显示器的右下象限换成黄色，用来分辨当前是哪块
            _background = Paint.Quadrants(width, height, screen.Index == 0 ? Paint.White : Paint.Yellow);
            _backgroundFor = (screen.Index, width, height);
            if (_buffer.Length < stride * height) _buffer = new byte[stride * height];
            _background.CopyTo(_buffer, 0);
        }
        else
        {
            // 别的地方一直是底色（缓冲区只有这里写）：只把上一帧画过黑块的横带抹回去
            _background.AsSpan(bandTop * stride, (bandBottom - bandTop) * stride).CopyTo(_buffer.AsSpan(bandTop * stride));
        }
        var blockX = tick * 8 % Math.Max(1, width - 32);
        var blockWidth = Math.Min(32, width - blockX);
        for (var y = bandTop; y < bandBottom; y++)
            Paint.Pixels(_buffer.AsSpan(y * stride + blockX * 4, blockWidth * 4)).Fill(Paint.Black);
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
/// 别的尺寸（降了分辨率）是从 1280×720 最近邻缩出来的。两张底图、横向缩好的每一行文字都按尺寸算一次存着，
/// 每帧只改文字区域和时间码；换底图时整张复制（为什么见 <see cref="Paint"/>）。
/// </summary>
public sealed class BusyPatternSource : IFrameSource
{
    public const int Width = 1280;
    public const int Height = 720;
    /// <summary>时间码方块：左上角起 (8, 8)，每个 32×32，间隔 36，共 24 个（20 位时间 + 4 位校验）。</summary>
    public const int StampBits = 20;
    private const int StampLeft = 8, StampTop = 8, StampSize = 32, StampPitch = 36;
    private const int TextLeft = 320, TextTop = 180, TextWidth = 640, TextHeight = 360, PageHeight = 1440;
    /// <summary>满屏文字那张底图：第 y 行是这一页的第 (y + 700) 行，横着铺两遍。</summary>
    private const int TextBackgroundOffset = 700;

    private static readonly ScreenInfo[] Fake = [new(0, new Rectangle(0, 0, Width, Height), true)];
    private readonly byte[] _page = Page();
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private readonly Dictionary<(int Width, int Height), Layout> _layouts = [];
    private Layout? _layout;
    private byte[] _frame = [];
    /// <summary>_frame 里现在铺的是哪张底图（null：还没铺，或者换了尺寸）。</summary>
    private bool? _textShown;
    private int _scroll;

    public IReadOnlyList<ScreenInfo> Screens() => Fake;

    /// <summary>
    /// 一种尺寸上不变的东西：两张底图；文字区域、时间码方块落在输出画面的哪些行、哪些列；
    /// 这一页每一行文字横向缩到输出尺寸之后的样子（BGRA）。
    /// </summary>
    private sealed class Layout
    {
        public required int Width, Height;
        public required byte[] Quadrants, TextBackground;
        public required (int From, int To) TextRows, TextColumns, StampRows;
        public required (int From, int To)[] StampColumns;
        public required byte[] Lines;
    }

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

    /// <summary>灰度值 → 像素（B = G = R，不透明）。</summary>
    private static readonly uint[] Grays = [.. Enumerable.Range(0, 256).Select(v => Paint.Pixel((byte)v, (byte)v, (byte)v))];

    /// <summary>输出的第 i 个（行或列）取 1280×720 上的第几个：最近邻。</summary>
    private static int Source(int i, int native, int size) => i * native / size;

    /// <summary>输出里取的是原图 [from, to) 的那一段（连续的）行或列。</summary>
    private static (int From, int To) Covering(int from, int to, int native, int size)
    {
        var start = 0;
        while (start < size && Source(start, native, size) < from) start++;
        var end = start;
        while (end < size && Source(end, native, size) < to) end++;
        return (start, end);
    }

    /// <summary>算一种尺寸的 <see cref="Layout"/>：逐像素，但每种尺寸只算一次。</summary>
    private Layout Prepare(int width, int height)
    {
        var stride = width * 4;
        var sourceColumns = new int[width];
        for (var x = 0; x < width; x++) sourceColumns[x] = Source(x, Width, width);
        var text = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            var line = (Source(y, Height, height) + TextBackgroundOffset) % PageHeight * TextWidth;
            var row = Paint.Pixels(text.AsSpan(y * stride, stride));
            for (var x = 0; x < width; x++) row[x] = Grays[_page[line + sourceColumns[x] % TextWidth]];
        }
        var columns = Covering(TextLeft, TextLeft + TextWidth, Width, width);
        var count = columns.To - columns.From;
        var lines = new byte[PageHeight * count * 4];
        for (var p = 0; p < PageHeight; p++)
        {
            var row = Paint.Pixels(lines.AsSpan(p * count * 4, count * 4));
            for (var j = 0; j < count; j++) row[j] = Grays[_page[p * TextWidth + sourceColumns[columns.From + j] - TextLeft]];
        }
        return new Layout
        {
            Width = width,
            Height = height,
            Quadrants = Paint.Quadrants(width, height, Paint.White),
            TextBackground = text,
            TextRows = Covering(TextTop, TextTop + TextHeight, Height, height),
            TextColumns = columns,
            StampRows = Covering(StampTop, StampTop + StampSize, Height, height),
            StampColumns = [.. Enumerable.Range(0, StampBits + 4).Select(i => Covering(StampLeft + i * StampPitch, StampLeft + i * StampPitch + StampSize, Width, width))],
            Lines = lines,
        };
    }

    public CapturedFrame Capture(ScreenInfo screen, int width, int height)
    {
        // 底图：四色象限，或者满屏的文字（每 8 秒换一次）
        var textBackground = _clock.ElapsedMilliseconds / 8000 % 2 == 1;
        if (_layout is not { } layout || layout.Width != width || layout.Height != height)
        {
            if (!_layouts.TryGetValue((width, height), out layout)) _layouts[(width, height)] = layout = Prepare(width, height);
            _layout = layout;
            if (_frame.Length < width * height * 4) _frame = new byte[width * height * 4];
            _textShown = null;
        }
        var stride = width * 4;
        if (_textShown != textBackground)
        {
            (textBackground ? layout.TextBackground : layout.Quadrants).CopyTo(_frame, 0);
            _textShown = textBackground;
        }
        // 文字区域：滚 4 秒、停 4 秒。每一行整行复制（横向已经缩好了），别的地方还是底图
        if (_clock.ElapsedMilliseconds / 4000 % 2 == 0) _scroll = (_scroll + 8) % PageHeight;
        var (left, right) = layout.TextColumns;
        var count = right - left;
        for (var y = layout.TextRows.From; y < layout.TextRows.To; y++)
        {
            var line = (Source(y, Height, height) - TextTop + _scroll) % PageHeight;
            layout.Lines.AsSpan(line * count * 4, count * 4).CopyTo(_frame.AsSpan(y * stride + left * 4));
        }
        // 时间码（四个字节都填：方块是黑的就连不透明度也是 0，和以前一样）
        var stamp = (int)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 10 & ((1 << StampBits) - 1));
        var check = 0;
        for (var i = 0; i < StampBits; i += 4) check += stamp >> i & 0xF;
        var bits = stamp | (check & 0xF) << StampBits;
        for (var i = 0; i < StampBits + 4; i++)
        {
            var v = (bits >> i & 1) != 0 ? (byte)255 : (byte)0;
            var (from, to) = layout.StampColumns[i];
            for (var y = layout.StampRows.From; y < layout.StampRows.To; y++)
                _frame.AsSpan(y * stride + from * 4, (to - from) * 4).Fill(v);
        }
        return new CapturedFrame(_frame, width, height, stride);
    }

    public void Dispose() { }
}

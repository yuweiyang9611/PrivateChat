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

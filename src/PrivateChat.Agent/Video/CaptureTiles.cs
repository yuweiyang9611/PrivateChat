using System.Drawing;
using PrivateChat.Agent.Session;

namespace PrivateChat.Agent.Video;

/// <summary>
/// 桌面复制告诉我们哪些区域变了（变化区域、移动区域的源和目标，都是显示器原始像素），这里换算成编码画面上的
/// <see cref="FrameTiles"/> 块（64×64），给 <see cref="CapturedFrame.ChangedHint"/> 用。纯计算（单元测试里也编进来）。
/// 宁可多标不能漏标：标着的块采集循环还会逐字节比一遍，没标的块直接当没变。
/// </summary>
public static class CaptureTiles
{
    /// <summary>缩放会把一个源像素的变化抹到旁边：源画面上先往外扩这么多像素。</summary>
    public const int SourceBleed = 2;

    /// <summary>缩放滤波器的抽头在输出画面上也有宽度（高质量缩放常见 4–8 抽头）：缩放后再往外扩这么多像素。</summary>
    public const int DestinationBleed = 2;

    /// <summary>
    /// 视频处理器一步最多缩小这么多倍（每个方向）。再多 NVIDIA 的视频处理器就是隔几个像素取一个（3:1 时只取每 3 个里的中间那个），
    /// 1 像素的细线、文字笔画整条不见；正好 2:1 时它取的是相邻两个的平均，一条线都不丢。所以缩得更多时分几步（<see cref="Passes"/>）。
    /// </summary>
    public const int MaxPassRatio = 2;

    /// <summary>
    /// 源画面缩到 width × height 要经过的尺寸（不含源画面，最后一个就是 width × height）：哪个方向还要缩小 2 倍以上，
    /// 就先把那个方向缩一半（向上取整，偶数时正好 2:1），直到最后一步不超过 2 倍。不用缩那么多时只有一步。
    /// 两个方向各算各的，所以宽高对调着算（显示器转了 90°）结果也正好对调。
    /// </summary>
    public static (int Width, int Height)[] Passes(int sourceWidth, int sourceHeight, int width, int height)
    {
        if (width <= 0 || height <= 0) return [(width, height)];
        var passes = new List<(int, int)>();
        var (w, h) = (sourceWidth, sourceHeight);
        while (w > MaxPassRatio * width || h > MaxPassRatio * height)
        {
            if (w > MaxPassRatio * width) w = (w + 1) / 2;
            if (h > MaxPassRatio * height) h = (h + 1) / 2;
            passes.Add((w, h));
        }
        passes.Add((width, height));
        return [.. passes];
    }

    /// <summary>
    /// 一次缩放（from → to）之后，一块变化区域可能影响到的范围（to 上的像素，没裁到画面里）：
    /// 先在 from 上往外扩 <see cref="SourceBleed"/>，按比例换算，再往外扩 <see cref="DestinationBleed"/>。空区域还是空的。
    /// </summary>
    public static Rectangle Scale(Rectangle rect, int fromWidth, int fromHeight, int toWidth, int toHeight)
    {
        if (rect.Width <= 0 || rect.Height <= 0 || fromWidth <= 0 || fromHeight <= 0) return Rectangle.Empty;
        var sx = (double)toWidth / fromWidth;
        var sy = (double)toHeight / fromHeight;
        var left = (int)Math.Floor((rect.Left - SourceBleed) * sx) - DestinationBleed;
        var top = (int)Math.Floor((rect.Top - SourceBleed) * sy) - DestinationBleed;
        var right = (int)Math.Ceiling((rect.Right + SourceBleed) * sx) + DestinationBleed;
        var bottom = (int)Math.Ceiling((rect.Bottom + SourceBleed) * sy) + DestinationBleed;
        return Rectangle.FromLTRB(left, top, right, bottom);
    }

    /// <summary>
    /// 源画面（sourceWidth × sourceHeight，已经和显示器同方向）上的一块变化区域，缩放到 width × height 之后落在哪些块上，标成 true。
    /// 不缩放时也往外扩：显卡转 NV12 时色度可能取左右相邻的像素（色度和左边的亮度对齐时就是这样），会越过块的边界。
    /// </summary>
    public static void MarkSource(Span<bool> hint, Rectangle rect, int sourceWidth, int sourceHeight, int width, int height)
        => MarkDestination(hint, Scale(rect, sourceWidth, sourceHeight, width, height), width, height);

    /// <summary>
    /// 分几步缩放时（passes 来自 <see cref="Passes"/>，和源画面同方向，最后一个是编码画面）：每一步都按一次缩放往外扩，
    /// 最后落在编码画面的哪些块上标成 true。只有一步时和上面那个一样。
    /// </summary>
    public static void MarkSource(Span<bool> hint, Rectangle rect, int sourceWidth, int sourceHeight, ReadOnlySpan<(int Width, int Height)> passes)
    {
        if (passes.IsEmpty) return;
        var (w, h) = (sourceWidth, sourceHeight);
        foreach (var (pw, ph) in passes)
        {
            rect = Scale(rect, w, h, pw, ph);
            (w, h) = (pw, ph);
        }
        MarkDestination(hint, rect, w, h);
    }

    /// <summary>编码画面上（width × height）的一块区域碰到的块标成 true（区域超出画面的部分不管）。</summary>
    public static void MarkDestination(Span<bool> hint, Rectangle rect, int width, int height)
    {
        var left = Math.Max(0, rect.Left);
        var top = Math.Max(0, rect.Top);
        var right = Math.Min(width, rect.Right);
        var bottom = Math.Min(height, rect.Bottom);
        if (right <= left || bottom <= top) return;
        var columns = (width + FrameTiles.Size - 1) / FrameTiles.Size;
        for (var ty = top / FrameTiles.Size; ty <= (bottom - 1) / FrameTiles.Size; ty++)
            for (var tx = left / FrameTiles.Size; tx <= (right - 1) / FrameTiles.Size; tx++)
                hint[ty * columns + tx] = true;
    }

    /// <summary>
    /// 桌面复制的画面（和变化区域）是显示器没转过的方向（textureWidth × textureHeight）；显示器转了（DXGI_MODE_ROTATION）时
    /// 换到人看到的方向：2 = 顺时针 90°、3 = 180°、4 = 270°，其余（0 没指定、1 不转）原样。
    /// 和微软的桌面复制示例（DXGIDesktopDuplication 的 SetDirtyVert）一样的换算。
    /// </summary>
    public static Rectangle Rotate(Rectangle rect, int rotation, int textureWidth, int textureHeight) => rotation switch
    {
        2 => Rectangle.FromLTRB(textureHeight - rect.Bottom, rect.Left, textureHeight - rect.Top, rect.Right),
        3 => Rectangle.FromLTRB(textureWidth - rect.Right, textureHeight - rect.Bottom, textureWidth - rect.Left, textureHeight - rect.Top),
        4 => Rectangle.FromLTRB(rect.Top, textureWidth - rect.Right, rect.Bottom, textureWidth - rect.Left),
        _ => rect,
    };

    /// <summary>转过之后（人看到的方向）画面的宽高。</summary>
    public static (int Width, int Height) Rotated(int rotation, int textureWidth, int textureHeight)
        => rotation is 2 or 4 ? (textureHeight, textureWidth) : (textureWidth, textureHeight);
}

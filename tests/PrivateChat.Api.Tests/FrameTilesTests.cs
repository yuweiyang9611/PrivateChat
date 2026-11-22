using PrivateChat.Agent.Session;
using PrivateChat.Agent.Video;

namespace PrivateChat.Api.Tests;

/// <summary>
/// 逐块比较前后两帧：只比"可能变了"的那些块（桌面复制给的提示），没标的块当没变；NV12 画面（一块 = 它的 Y 加上对应的 UV）。
/// </summary>
public class FrameTilesTests
{
    [Fact]
    public void Only_candidate_tiles_are_compared()
    {
        const int width = 200, height = 130; // 4 × 3 块
        var a = new byte[width * height * 4];
        var b = (byte[])a.Clone();
        b[(10 * width + 70) * 4] = 1;   // 第 1 块变了
        b[(129 * width + 10) * 4] = 1;  // 第 8 块（第三行第一块）变了
        var changed = new bool[12];
        var candidates = new bool[12];
        candidates[1] = true;           // 只说第 1 块和第 5 块可能变了
        candidates[5] = true;
        Assert.Equal(1, FrameTiles.Diff(a, b, width, height, width * 4, changed, candidates));
        Assert.Equal([1], Enumerable.Range(0, 12).Where(i => changed[i]));
        // 没给提示：全比
        Assert.Equal(2, FrameTiles.Diff(a, b, width, height, width * 4, changed, default));
        Assert.Equal([1, 8], Enumerable.Range(0, 12).Where(i => changed[i]));
        // 没有上一帧：标着的块算变了，没标的不算
        Assert.Equal(2, FrameTiles.Diff(default, b, width, height, width * 4, changed, candidates));
        Assert.Equal([1, 5], Enumerable.Range(0, 12).Where(i => changed[i]));
    }

    [Fact]
    public void Candidates_respect_the_stride()
    {
        const int width = 130, height = 70, stride = 136 * 4; // 每行后面有 6 个像素的填充
        var a = new byte[stride * height];
        var b = (byte[])a.Clone();
        b[69 * stride + 129 * 4 + 1] = 9;        // 右下角最后一个像素：第 5 块（3 × 2 块）
        b[0 * stride + 131 * 4] = 9;            // 填充里的变化不算
        var changed = new bool[6];
        var all = Enumerable.Repeat(true, 6).ToArray();
        Assert.Equal(1, FrameTiles.Diff(a, b, width, height, stride, changed, all));
        Assert.True(changed[5]);
    }

    [Fact]
    public void Nv12_tiles_include_their_chroma()
    {
        const int width = 200, height = 130; // 4 × 3 块
        var size = width * height * 3 / 2;
        var a = new byte[size];
        var changed = new bool[12];
        Assert.Equal(12, FrameTiles.DiffNv12(default, a, width, height, changed)); // 没有上一帧：全变
        var b = (byte[])a.Clone();
        Assert.Equal(0, FrameTiles.DiffNv12(a, b, width, height, changed));

        b[10 * width + 70] = 1;                          // Y：第 1 块
        b[width * height + 64 / 2 * width + 199] = 1;    // UV 第 32 行（Y 的 64、65 行）、最后一个字节（x = 199 的 V）：第 7 块
        b[width * height + 129 / 2 * width + 0] = 1;     // UV 最后一行（Y 的 128、129 行）第一个字节：第 8 块
        Assert.Equal(3, FrameTiles.DiffNv12(a, b, width, height, changed));
        Assert.Equal([1, 7, 8], Enumerable.Range(0, 12).Where(i => changed[i]));

        // 只比标着的块
        var candidates = new bool[12];
        candidates[7] = true;
        candidates[2] = true;
        Assert.Equal(1, FrameTiles.DiffNv12(a, b, width, height, changed, candidates));
        Assert.Equal([7], Enumerable.Range(0, 12).Where(i => changed[i]));
        Assert.Equal(2, FrameTiles.DiffNv12(default, b, width, height, changed, candidates));
    }

    /// <summary>BGRA 上某一块变了，转成 NV12（色度按 2×2 取，不会跨块）后也正好是那一块变了：两种比较的结果一样。</summary>
    [Theory]
    [InlineData(1366, 768)]
    [InlineData(200, 130)]
    [InlineData(1280, 720)]
    public void Nv12_and_bgra_comparisons_agree(int width, int height)
    {
        var random = new Random(width * height);
        var bgra = new byte[width * height * 4];
        random.NextBytes(bgra);
        var nv12Before = new byte[width * height * 3 / 2];
        Nv12.FromBgra(bgra, width * 4, width, height, nv12Before);
        var after = (byte[])bgra.Clone();
        for (var i = 0; i < 25; i++)
        {
            var x = random.Next(width);
            var y = random.Next(height);
            after[(y * width + x) * 4 + random.Next(3)] ^= 0x80;
        }
        var nv12After = new byte[nv12Before.Length];
        Nv12.FromBgra(after, width * 4, width, height, nv12After);
        var count = FrameTiles.Count(width, height);
        var fromBgra = new bool[count];
        var fromNv12 = new bool[count];
        Assert.Equal(FrameTiles.Diff(bgra, after, width, height, width * 4, fromBgra), FrameTiles.DiffNv12(nv12Before, nv12After, width, height, fromNv12));
        Assert.Equal(fromBgra, fromNv12);
    }
}

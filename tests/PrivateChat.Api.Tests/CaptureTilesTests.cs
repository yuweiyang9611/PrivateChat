using System.Drawing;
using PrivateChat.Agent.Session;
using PrivateChat.Agent.Video;

namespace PrivateChat.Api.Tests;

/// <summary>
/// 桌面复制的变化区域（源画面像素）换算成编码画面上的 64×64 块：缩放、往外扩（缩放和色度会把变化抹到旁边）、画面边上不满 64 的块、
/// 1366×768 这种不是 64 倍数的尺寸；显示器转了方向时的换算。宁可多标不能漏标。
/// </summary>
public class CaptureTilesTests
{
    private static bool[] Mark(Rectangle rect, int sourceWidth, int sourceHeight, int width, int height)
    {
        var hint = new bool[FrameTiles.Count(width, height)];
        CaptureTiles.MarkSource(hint, rect, sourceWidth, sourceHeight, width, height);
        return hint;
    }

    private static int[] Marked(bool[] hint) => [.. Enumerable.Range(0, hint.Length).Where(i => hint[i])];

    [Fact]
    public void A_change_inside_one_tile_marks_that_tile_and_spills_over_close_edges()
    {
        // 不缩放：左上角一个像素只标第 0 块
        Assert.Equal([0], Marked(Mark(new Rectangle(0, 0, 1, 1), 1280, 720, 1280, 720)));
        // 贴着块边界的像素：色度滤波可能取到隔壁，隔壁那块也标上
        Assert.Equal([0, 1], Marked(Mark(new Rectangle(63, 10, 1, 1), 1280, 720, 1280, 720)));
        // 离边界远的不会
        Assert.Equal([0], Marked(Mark(new Rectangle(30, 30, 4, 4), 1280, 720, 1280, 720)));
    }

    [Fact]
    public void Scaled_changes_land_on_the_scaled_tiles()
    {
        // 1920×1080 → 1280×720（2/3）：屏幕正中一个像素 → 输出 (640, 360) 附近，跨第 9、10 列，第 5 行
        var hint = Mark(new Rectangle(960, 540, 1, 1), 1920, 1080, 1280, 720);
        const int columns = 1280 / 64;
        Assert.Equal([5 * columns + 9, 5 * columns + 10], Marked(hint));
    }

    [Fact]
    public void The_last_partial_tile_of_an_odd_sized_screen_is_reachable()
    {
        // 1366×768：22 列（最后一列只有 22 像素宽）× 12 行
        Assert.Equal(22 * 12, FrameTiles.Count(1366, 768));
        var hint = Mark(new Rectangle(1365, 767, 1, 1), 1366, 768, 1366, 768);
        Assert.True(hint[^1]);
        // 超出画面的部分不管，也不出错
        var outside = Mark(new Rectangle(1300, 700, 500, 500), 1366, 768, 1366, 768);
        Assert.True(outside[^1]);
        Assert.Empty(Marked(Mark(new Rectangle(2000, 2000, 10, 10), 1366, 768, 1366, 768)));
        Assert.Empty(Marked(Mark(Rectangle.Empty, 1366, 768, 1366, 768)));
    }

    /// <summary>
    /// 逐像素核对：输出画面上每个像素取的是源画面上的哪一段（按比例），只要那一段碰到了变化区域，它所在的块就必须标上。
    /// 各种缩放比例（含放不满整数倍的 1366×768 → 1228×690）、各种位置的小区域、贴边的区域。
    /// </summary>
    [Theory]
    [InlineData(1366, 768, 1228, 690)]
    [InlineData(1920, 1080, 1280, 720)]
    [InlineData(2560, 1440, 1366, 768)]
    [InlineData(3840, 2400, 1280, 800)]
    [InlineData(1280, 720, 1280, 720)]
    public void Every_output_pixel_that_reads_a_changed_source_pixel_is_in_a_marked_tile(int sourceWidth, int sourceHeight, int width, int height)
    {
        var random = new Random(20261122);
        for (var round = 0; round < 200; round++)
        {
            var w = 1 + random.Next(round % 4 == 0 ? 300 : 20);
            var h = 1 + random.Next(round % 4 == 0 ? 200 : 20);
            var x = random.Next(sourceWidth - Math.Min(w, sourceWidth - 1));
            var y = random.Next(sourceHeight - Math.Min(h, sourceHeight - 1));
            if (round % 10 == 1) (x, y) = (sourceWidth - w, sourceHeight - h); // 贴右下角
            var rect = new Rectangle(x, y, w, h);
            var hint = Mark(rect, sourceWidth, sourceHeight, width, height);
            var columns = (width + 63) / 64;
            for (var dy = 0; dy < height; dy++)
            {
                var sy0 = (double)dy * sourceHeight / height;
                var sy1 = (double)(dy + 1) * sourceHeight / height;
                if (sy1 <= rect.Top || sy0 >= rect.Bottom) continue;
                for (var dx = 0; dx < width; dx++)
                {
                    var sx0 = (double)dx * sourceWidth / width;
                    var sx1 = (double)(dx + 1) * sourceWidth / width;
                    if (sx1 <= rect.Left || sx0 >= rect.Right) continue;
                    Assert.True(hint[dy / 64 * columns + dx / 64], $"{rect} 缩到 {width}x{height}：像素 ({dx}, {dy}) 所在的块没标");
                }
            }
        }
    }

    [Fact]
    public void Shrinking_more_than_two_times_is_split_into_halving_steps()
    {
        // 4K → 1280×720（3:1）：先一半（正好 2:1），最后 1.5:1
        Assert.Equal([(1920, 1080), (1280, 720)], CaptureTiles.Passes(3840, 2160, 1280, 720));
        // 正好 2:1、不到 2:1、不缩放：一步
        Assert.Equal([(1920, 1080)], CaptureTiles.Passes(3840, 2160, 1920, 1080));
        Assert.Equal([(1280, 720)], CaptureTiles.Passes(1920, 1080, 1280, 720));
        Assert.Equal([(1366, 768)], CaptureTiles.Passes(1366, 768, 1366, 768));
        // 4:1：两次正好 2:1
        Assert.Equal([(1920, 1080), (960, 540)], CaptureTiles.Passes(3840, 2160, 960, 540));
        // 自定义的最小尺寸（6:1）：三步
        Assert.Equal([(960, 540), (480, 270), (320, 180)], CaptureTiles.Passes(1920, 1080, 320, 180));
        // 奇数向上取整
        Assert.Equal([(683, 384), (454, 256)], CaptureTiles.Passes(1366, 768, 454, 256));
        // 只有一个方向要缩那么多时只缩那个方向
        Assert.Equal([(1920, 1080), (1280, 1000)], CaptureTiles.Passes(3840, 1080, 1280, 1000));
    }

    [Fact]
    public void Every_step_shrinks_at_most_two_times_and_the_plan_is_the_same_turned_sideways()
    {
        var random = new Random(20261123);
        for (var i = 0; i < 2000; i++)
        {
            var sourceWidth = 64 + random.Next(7680);
            var sourceHeight = 64 + random.Next(4320);
            var (width, height) = FrameGeometry.Fit(sourceWidth, sourceHeight, 2 + random.Next(sourceWidth), 2 + random.Next(sourceHeight));
            var passes = CaptureTiles.Passes(sourceWidth, sourceHeight, width, height);
            Assert.Equal((width, height), passes[^1]);
            var (w, h) = (sourceWidth, sourceHeight);
            foreach (var (pw, ph) in passes)
            {
                Assert.True(pw <= w && ph <= h, $"{sourceWidth}x{sourceHeight} → {width}x{height}：{w}x{h} → {pw}x{ph} 放大了");
                Assert.True(w <= CaptureTiles.MaxPassRatio * pw && h <= CaptureTiles.MaxPassRatio * ph, $"{sourceWidth}x{sourceHeight} → {width}x{height}：{w}x{h} → {pw}x{ph} 一步缩了 2 倍以上");
                (w, h) = (pw, ph);
            }
            // 显示器转了 90°：VideoConverter 在没转过的方向上分步，变化提示在人看到的方向上分步，两边要是同一组尺寸
            Assert.Equal(passes.Select(p => (p.Height, p.Width)), CaptureTiles.Passes(sourceHeight, sourceWidth, height, width));
        }
    }

    [Fact]
    public void Several_steps_mark_at_least_what_one_step_marks()
    {
        var random = new Random(11);
        foreach (var (sourceWidth, sourceHeight, width, height) in new[] { (3840, 2160, 1280, 720), (3840, 2160, 960, 540), (1920, 1080, 320, 180), (1366, 768, 454, 256) })
        {
            var passes = CaptureTiles.Passes(sourceWidth, sourceHeight, width, height);
            Assert.True(passes.Length > 1);
            for (var i = 0; i < 300; i++)
            {
                var rect = new Rectangle(random.Next(sourceWidth - 40), random.Next(sourceHeight - 40), 1 + random.Next(40), 1 + random.Next(40));
                var one = Mark(rect, sourceWidth, sourceHeight, width, height);
                var several = new bool[one.Length];
                CaptureTiles.MarkSource(several, rect, sourceWidth, sourceHeight, passes);
                for (var t = 0; t < one.Length; t++) Assert.True(several[t] || !one[t], $"{rect}：分步标的少了第 {t} 块");
            }
        }
        // 只有一步时两种标法一样
        var single = new bool[FrameTiles.Count(1280, 720)];
        CaptureTiles.MarkSource(single, new Rectangle(500, 300, 7, 9), 1920, 1080, CaptureTiles.Passes(1920, 1080, 1280, 720));
        Assert.Equal(Mark(new Rectangle(500, 300, 7, 9), 1920, 1080, 1280, 720), single);
        // 空区域什么都不标
        var empty = new bool[FrameTiles.Count(1280, 720)];
        CaptureTiles.MarkSource(empty, Rectangle.Empty, 3840, 2160, CaptureTiles.Passes(3840, 2160, 1280, 720));
        Assert.Empty(Marked(empty));
    }

    /// <summary>分几步缩时也逐像素核对（缩放比例是几步乘起来的，每个输出像素读的源画面那一段还是按比例算）。</summary>
    [Theory]
    [InlineData(3840, 2160, 1280, 720)]
    [InlineData(3840, 2400, 960, 600)]
    [InlineData(1920, 1080, 320, 180)]
    [InlineData(1366, 768, 454, 256)]
    public void Several_steps_still_mark_every_output_pixel_that_reads_a_changed_source_pixel(int sourceWidth, int sourceHeight, int width, int height)
    {
        var passes = CaptureTiles.Passes(sourceWidth, sourceHeight, width, height);
        var random = new Random(5);
        var columns = (width + 63) / 64;
        for (var round = 0; round < 200; round++)
        {
            var w = 1 + random.Next(round % 4 == 0 ? 300 : 20);
            var h = 1 + random.Next(round % 4 == 0 ? 200 : 20);
            var rect = new Rectangle(random.Next(sourceWidth - w), random.Next(sourceHeight - h), w, h);
            var hint = new bool[FrameTiles.Count(width, height)];
            CaptureTiles.MarkSource(hint, rect, sourceWidth, sourceHeight, passes);
            for (var dy = 0; dy < height; dy++)
            {
                if ((double)(dy + 1) * sourceHeight / height <= rect.Top || (double)dy * sourceHeight / height >= rect.Bottom) continue;
                for (var dx = 0; dx < width; dx++)
                {
                    if ((double)(dx + 1) * sourceWidth / width <= rect.Left || (double)dx * sourceWidth / width >= rect.Right) continue;
                    Assert.True(hint[dy / 64 * columns + dx / 64], $"{rect} 分 {passes.Length} 步缩到 {width}x{height}：像素 ({dx}, {dy}) 所在的块没标");
                }
            }
        }
    }

    [Fact]
    public void Destination_rectangles_are_clipped_to_the_frame()
    {
        var hint = new bool[FrameTiles.Count(200, 130)];
        CaptureTiles.MarkDestination(hint, new Rectangle(-10, -10, 20, 20), 200, 130);
        Assert.Equal([0], Marked(hint));
        Array.Clear(hint);
        CaptureTiles.MarkDestination(hint, new Rectangle(195, 129, 50, 50), 200, 130); // 只碰到右下角那块（不满 64）
        Assert.Equal([11], Marked(hint));
        Array.Clear(hint);
        CaptureTiles.MarkDestination(hint, new Rectangle(190, 120, 50, 50), 200, 130); // 跨了四块
        Assert.Equal([6, 7, 10, 11], Marked(hint));
        Array.Clear(hint);
        CaptureTiles.MarkDestination(hint, Rectangle.Empty, 200, 130);
        CaptureTiles.MarkDestination(hint, new Rectangle(300, 0, 10, 10), 200, 130);
        Assert.Empty(Marked(hint));
    }

    [Fact]
    public void Rotated_screens_map_the_unrotated_image_to_what_people_see()
    {
        // 显示器竖着放（DXGI_MODE_ROTATION_ROTATE90）：桌面复制给的画面是没转过的 1920×1080，人看到的是 1080×1920。
        // 顺时针转 90°：画面的左上角跑到右上角
        Assert.Equal((1080, 1920), CaptureTiles.Rotated(2, 1920, 1080));
        Assert.Equal(Rectangle.FromLTRB(1079, 0, 1080, 1), CaptureTiles.Rotate(new Rectangle(0, 0, 1, 1), 2, 1920, 1080));
        Assert.Equal(Rectangle.FromLTRB(1060, 0, 1080, 10), CaptureTiles.Rotate(new Rectangle(0, 0, 10, 20), 2, 1920, 1080));
        // 180°：左上角到右下角
        Assert.Equal((1920, 1080), CaptureTiles.Rotated(3, 1920, 1080));
        Assert.Equal(Rectangle.FromLTRB(1919, 1079, 1920, 1080), CaptureTiles.Rotate(new Rectangle(0, 0, 1, 1), 3, 1920, 1080));
        // 270°：左上角到左下角
        Assert.Equal((1080, 1920), CaptureTiles.Rotated(4, 1920, 1080));
        Assert.Equal(Rectangle.FromLTRB(0, 1919, 1, 1920), CaptureTiles.Rotate(new Rectangle(0, 0, 1, 1), 4, 1920, 1080));
        // 不转（1）和没指定（0）原样
        var r = new Rectangle(100, 200, 30, 40);
        Assert.Equal(r, CaptureTiles.Rotate(r, 1, 1920, 1080));
        Assert.Equal(r, CaptureTiles.Rotate(r, 0, 1920, 1080));
        Assert.Equal((1920, 1080), CaptureTiles.Rotated(1, 1920, 1080));

        // 转 90° 再转 270°（宽高对调）回到原处；转两次 180° 也是；转出来的都还在画面里
        var random = new Random(7);
        for (var i = 0; i < 100; i++)
        {
            var rect = new Rectangle(random.Next(1900), random.Next(1060), 1 + random.Next(20), 1 + random.Next(20));
            var turned = CaptureTiles.Rotate(rect, 2, 1920, 1080);
            Assert.True(new Rectangle(0, 0, 1080, 1920).Contains(turned));
            Assert.Equal(rect.Size, new Size(turned.Height, turned.Width));
            Assert.Equal(rect, CaptureTiles.Rotate(turned, 4, 1080, 1920));
            Assert.Equal(rect, CaptureTiles.Rotate(CaptureTiles.Rotate(rect, 3, 1920, 1080), 3, 1920, 1080));
        }
    }
}

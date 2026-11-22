using System.Drawing;
using PrivateChat.Agent.Video;

namespace PrivateChat.Api.Tests;

/// <summary>
/// 桌面复制的画面里没有鼠标指针，自己画到 NV12 上：三种指针格式（单色、彩色、带掩码的彩色）、颜色公式和 <see cref="Nv12.FromBgra"/> 一样、
/// 画面边上裁掉、擦掉旧指针；位置和 GDI 截屏一样按比例缩放、指针原始大小。
/// </summary>
public class CursorOverlayTests
{
    private const int Width = 64, Height = 32;

    /// <summary>一整幅纯色的 BGRA 和它的 NV12。</summary>
    private static (byte[] Bgra, byte[] Nv12) Solid(byte b, byte g, byte r)
    {
        var bgra = new byte[Width * Height * 4];
        for (var i = 0; i < bgra.Length; i += 4) (bgra[i], bgra[i + 1], bgra[i + 2], bgra[i + 3]) = (b, g, r, 255);
        return (bgra, ToNv12(bgra));
    }

    private static byte[] ToNv12(byte[] bgra)
    {
        var nv12 = new byte[Width * Height * 3 / 2];
        Nv12.FromBgra(bgra, Width * 4, Width, Height, nv12);
        return nv12;
    }

    /// <summary>width × height 的彩色指针（BGRA，A 是不透明度），每个像素 pixel(x, y)。</summary>
    private static CursorShape Color(int width, int height, Func<int, int, (byte B, byte G, byte R, byte A)> pixel, int type = CursorShape.TypeColor)
    {
        var pitch = width * 4 + 8; // 行尾带填充
        var buffer = new byte[pitch * height];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var (b, g, r, a) = pixel(x, y);
                (buffer[y * pitch + x * 4], buffer[y * pitch + x * 4 + 1], buffer[y * pitch + x * 4 + 2], buffer[y * pitch + x * 4 + 3]) = (b, g, r, a);
            }
        return CursorShape.FromDxgi(type, width, height, pitch, 0, 0, buffer)!;
    }

    [Fact]
    public void An_opaque_cursor_gives_the_same_pixels_as_converting_the_composited_picture()
    {
        var (bgra, nv12) = Solid(40, 90, 200);
        // 6×6 的不透明指针，左上角在 (3, 5)（奇数位置：边上的 2×2 块只盖住一部分）；颜色各不相同
        var shape = Color(6, 6, (x, y) => ((byte)(x * 40), (byte)(y * 40), (byte)(200 - x * 10), 255));
        var composited = (byte[])bgra.Clone();
        for (var y = 0; y < 6; y++)
            for (var x = 0; x < 6; x++)
            {
                var o = ((5 + y) * Width + 3 + x) * 4;
                (composited[o], composited[o + 1], composited[o + 2]) = ((byte)(x * 40), (byte)(y * 40), (byte)(200 - x * 10));
            }
        var expected = ToNv12(composited);

        var drawn = CursorOverlay.Draw(shape, nv12, Width, Height, 3, 5);
        Assert.Equal(Rectangle.FromLTRB(2, 4, 10, 12), drawn); // 扩到偶数边界
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
                Assert.Equal(expected[y * Width + x], nv12[y * Width + x]); // 亮度逐像素一样（没盖住的原样）
        for (var i = Width * Height; i < nv12.Length; i++)
        {
            // 色度：整块盖住的完全一样；只盖住一部分的块，没盖住的像素是从 NV12 还原的，差一两个
            var y = (i - Width * Height) / Width * 2;
            var x = (i - Width * Height) % Width & ~1;
            var whole = x >= 4 && x < 8 && y >= 6 && y < 10;
            Assert.InRange(nv12[i] - expected[i], whole ? 0 : -2, whole ? 0 : 2);
        }
    }

    [Fact]
    public void Monochrome_cursors_follow_the_and_xor_masks()
    {
        // 4×1 的单色指针：透明（AND 1 XOR 0）、黑（0 0）、白（0 1）、反色（1 1）。高度是两张掩码加起来的 2 行，每行 4 字节
        var buffer = new byte[8];
        buffer[0] = 0b1001_0000; // AND
        buffer[4] = 0b0011_0000; // XOR
        var shape = CursorShape.FromDxgi(CursorShape.TypeMonochrome, 4, 2, 4, 1, 0, buffer)!;
        Assert.Equal((4, 1, 1, 0), (shape.Width, shape.Height, shape.HotX, shape.HotY));

        var (_, nv12) = Solid(100, 100, 100);
        var before = (byte[])nv12.Clone();
        // 每个像素画在单独一个 2×2 块里：指针放在第 0 行，四个像素是 x = 10..13，块的另一半没盖住
        CursorOverlay.Draw(shape, nv12, Width, Height, 10, 0);
        Assert.Equal(before[10], nv12[10]);               // 透明
        Assert.Equal(16, nv12[11]);                        // 黑：有限范围的 16
        Assert.Equal(235, nv12[12]);                       // 白：235
        var gray = before[13];
        Assert.InRange(nv12[13], 251 - gray - 2, 251 - gray + 2); // 反色：RGB 255 − 100 = 155
        Assert.Equal(before[Width + 10], nv12[Width + 10]); // 第二行没盖住
    }

    [Fact]
    public void Masked_color_cursors_replace_or_xor()
    {
        // 带掩码的彩色指针：A = 0 用这个颜色盖掉屏幕；A = 0xFF 和屏幕异或（和白色异或就是反色、和黑色异或等于没画）
        (byte, byte, byte, byte)[] pixels = [(0, 0, 255, 0), (255, 255, 255, 255), (0, 0, 0, 255)];
        var shape = Color(3, 1, (x, _) => pixels[x], CursorShape.TypeMaskedColor);
        var (bgra, nv12) = Solid(0, 0, 0);
        var before = (byte[])nv12.Clone();
        var drawn = CursorOverlay.Draw(shape, nv12, Width, Height, 20, 2);
        var red = (byte[])bgra.Clone();
        red[(2 * Width + 20) * 4 + 2] = 255;
        Assert.Equal(ToNv12(red)[2 * Width + 20], nv12[2 * Width + 20]); // 盖成红色
        Assert.Equal(235, nv12[2 * Width + 21]);                         // 黑色反色成白色
        Assert.Equal(before[2 * Width + 22], nv12[2 * Width + 22]);      // 和 0 异或：不变
        Assert.Equal(Rectangle.FromLTRB(20, 2, 24, 4), drawn);
    }

    [Fact]
    public void Translucent_pixels_are_blended_and_transparent_ones_are_skipped()
    {
        // 彩色指针：第一个像素半透明白色，第二个完全透明
        var shape = Color(2, 2, (x, y) => x == 0 && y == 0 ? ((byte)255, (byte)255, (byte)255, (byte)128) : ((byte)0, (byte)0, (byte)0, (byte)0));
        var (_, nv12) = Solid(0, 0, 0);
        var before = (byte[])nv12.Clone();
        var drawn = CursorOverlay.Draw(shape, nv12, Width, Height, 0, 0);
        Assert.InRange(nv12[0], 16 + 219 / 2 - 3, 16 + 219 / 2 + 3); // 黑底上的一半白
        Assert.Equal(before[1], nv12[1]);
        Assert.Equal(before[Width], nv12[Width]);
        Assert.Equal(Rectangle.FromLTRB(0, 0, 2, 2), drawn);

        // 整个透明的指针什么都不改（色度也不重算）
        var invisible = Color(4, 4, (_, _) => (9, 9, 9, 0));
        var again = (byte[])nv12.Clone();
        CursorOverlay.Draw(invisible, nv12, Width, Height, 30, 10);
        Assert.Equal(again, nv12);
    }

    [Fact]
    public void Cursors_half_outside_the_picture_are_clipped()
    {
        var shape = Color(8, 8, (_, _) => (255, 255, 255, 255));
        var (_, nv12) = Solid(0, 0, 0);
        // 露在画面里的是 4 列 × 3 行，扩到偶数边界是 4 × 4
        Assert.Equal(Rectangle.FromLTRB(0, 0, 4, 4), CursorOverlay.Draw(shape, nv12, Width, Height, -4, -5));
        Assert.Equal(235, nv12[0]);
        Assert.Equal(Rectangle.FromLTRB(60, 28, 64, 32), CursorOverlay.Draw(shape, nv12, Width, Height, 61, 29));
        Assert.Equal(235, nv12[31 * Width + 63]);
        Assert.Equal(Rectangle.Empty, CursorOverlay.Draw(shape, nv12, Width, Height, Width + 1, 0));
        Assert.Equal(Rectangle.Empty, CursorOverlay.Draw(shape, nv12, Width, Height, -8, 0));
    }

    [Fact]
    public void Restoring_the_drawn_rectangle_erases_the_cursor()
    {
        var (_, clean) = Solid(10, 120, 230);
        var frame = (byte[])clean.Clone();
        var shape = Color(7, 9, (x, y) => ((byte)(x * 30), 0, (byte)(y * 20), 255));
        var drawn = CursorOverlay.Draw(shape, frame, Width, Height, 13, 3);
        Assert.NotEqual(clean, frame);
        CursorOverlay.Restore(clean, frame, Width, Height, drawn);
        Assert.Equal(clean, frame);
        CursorOverlay.Restore(clean, frame, Width, Height, Rectangle.Empty); // 空的什么都不做
        Assert.Equal(clean, frame);
    }

    [Fact]
    public void The_cursor_is_placed_like_gdi_does_scaled_position_native_size()
    {
        // 1920×1080 缩到 1280×720：指针形状左上角在 (100, 50)，热点 (8, 8) → 热点在 (108, 58) → 缩放后 (72, 38.67) → 左上角 (64, 30)
        Assert.Equal(new Point(64, 30), CursorOverlay.Place(100, 50, 8, 8, 1920, 1080, 1280, 720));
        // 不缩放：原样
        Assert.Equal(new Point(100, 50), CursorOverlay.Place(100, 50, 8, 8, 1920, 1080, 1920, 1080));
        // 热点在左上角的箭头
        Assert.Equal(new Point(0, 0), CursorOverlay.Place(0, 0, 0, 0, 1920, 1080, 1280, 720));
    }

    [Fact]
    public void Unknown_or_short_shapes_are_ignored()
    {
        Assert.Null(CursorShape.FromDxgi(8, 4, 4, 16, 0, 0, new byte[64]));                     // 不认识的格式
        Assert.Null(CursorShape.FromDxgi(CursorShape.TypeColor, 4, 4, 16, 0, 0, new byte[40])); // 缓冲区不够
        Assert.Null(CursorShape.FromDxgi(CursorShape.TypeMonochrome, 32, 32, 4, 0, 0, new byte[64]));
        Assert.Null(CursorShape.FromDxgi(CursorShape.TypeMonochrome, 32, 1, 4, 0, 0, new byte[64])); // 单色的高度至少 2（两张掩码）
        Assert.Null(CursorShape.FromDxgi(CursorShape.TypeColor, 0, 4, 16, 0, 0, new byte[64]));
    }
}

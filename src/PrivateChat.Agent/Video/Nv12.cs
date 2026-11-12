namespace PrivateChat.Agent.Video;

/// <summary>BGRA（GDI 截屏的格式）→ NV12（编码器输入），BT.709 有限范围，色度取 2×2 平均。</summary>
public static class Nv12
{
    public static unsafe void FromBgra(ReadOnlySpan<byte> bgra, int stride, int width, int height, Span<byte> nv12)
    {
        if (width % 2 != 0 || height % 2 != 0) throw new ArgumentException("宽高必须是偶数");
        if (bgra.Length < stride * (height - 1) + width * 4 || nv12.Length < width * height * 3 / 2)
            throw new ArgumentException("缓冲区太小");

        fixed (byte* src = bgra)
        fixed (byte* dst = nv12)
        {
            var srcBase = src;
            var dstBase = dst;
            // 按行对并行：1080p 单线程约 5 ms，多核再降一半
            Parallel.For(0, height / 2, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(4, Environment.ProcessorCount) }, pair =>
            {
                var row = pair * 2;
                var r0 = srcBase + (long)row * stride;
                var r1 = r0 + stride;
                var y0 = dstBase + (long)row * width;
                var y1 = y0 + width;
                var uv = dstBase + (long)width * height + (long)pair * width;
                for (var x = 0; x < width; x += 2)
                {
                    int b00 = r0[0], g00 = r0[1], c00 = r0[2];
                    int b01 = r0[4], g01 = r0[5], c01 = r0[6];
                    int b10 = r1[0], g10 = r1[1], c10 = r1[2];
                    int b11 = r1[4], g11 = r1[5], c11 = r1[6];
                    y0[x] = Luma(c00, g00, b00);
                    y0[x + 1] = Luma(c01, g01, b01);
                    y1[x] = Luma(c10, g10, b10);
                    y1[x + 1] = Luma(c11, g11, b11);
                    int rs = c00 + c01 + c10 + c11, gs = g00 + g01 + g10 + g11, bs = b00 + b01 + b10 + b11;
                    uv[x] = (byte)(((-26 * rs - 87 * gs + 112 * bs + 512) >> 10) + 128);
                    uv[x + 1] = (byte)(((112 * rs - 102 * gs - 10 * bs + 512) >> 10) + 128);
                    r0 += 8;
                    r1 += 8;
                }
            });
        }
    }

    private static byte Luma(int r, int g, int b) => (byte)(((47 * r + 157 * g + 16 * b + 128) >> 8) + 16);
}

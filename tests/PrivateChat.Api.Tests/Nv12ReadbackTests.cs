using PrivateChat.Agent.Video;

namespace PrivateChat.Api.Tests;

public class Nv12ReadbackTests
{
    [Theory]
    [InlineData(2_211_840u)] // Radeon 780M 上观察到的值，恰好等于 Y 平面跨度
    [InlineData(0u)]
    [InlineData(3_317_760u)]
    public unsafe void Padded_1080p_map_copies_both_planes_and_preserves_interleaved_chroma(uint depthPitch)
    {
        const int width = 1920, height = 1080, pitch = 2048;
        var rows = height + height / 2;
        var packed = new byte[width * rows];
        var mappedBytes = new byte[pitch * rows];
        Array.Fill(mappedBytes, (byte)0xff);
        for (var y = 0; y < rows; y++)
        {
            var row = packed.AsSpan(y * width, width);
            if (y < height) row.Fill((byte)(16 + y % 220));
            else
                for (var x = 0; x < width; x += 2)
                {
                    row[x] = (byte)(32 + (y - height) % 64);
                    row[x + 1] = (byte)(128 + (x / 2) % 112);
                }
            row.CopyTo(mappedBytes.AsSpan(y * pitch, width));
        }
        var result = new byte[packed.Length + 2];
        Array.Fill(result, (byte)0xfe);
        fixed (byte* pointer = mappedBytes)
        {
            var mapped = new MappedSubresource { Data = (nint)pointer, RowPitch = pitch, DepthPitch = depthPitch };
            mapped.CopyNv12To(width, height, result.AsSpan(1, packed.Length));
        }
        Assert.True(result.AsSpan(1, packed.Length).SequenceEqual(packed));
        Assert.Equal((byte)0xfe, result[0]);
        Assert.Equal((byte)0xfe, result[^1]);
    }

    [Theory]
    [InlineData(0, 2, 2u, 6)]
    [InlineData(4, -2, 4u, 12)]
    [InlineData(3, 2, 4u, 12)]
    [InlineData(2, 3, 2u, 9)]
    [InlineData(4, 2, 3u, 12)]
    [InlineData(4, 2, 4u, 11)]
    [InlineData(4, 2, uint.MaxValue, 12)]
    [InlineData(65536, 65536, 65536u, 12)]
    public unsafe void Invalid_layout_is_rejected_before_copying(int width, int height, uint pitch, int capacity)
    {
        var source = new byte[16];
        var destination = new byte[capacity];
        Array.Fill(destination, (byte)0xaf);
        fixed (byte* pointer = source)
        {
            var map = new MappedSubresource { Data = (nint)pointer, RowPitch = pitch };
            Assert.Throws<InvalidDataException>(() => map.CopyNv12To(width, height, destination));
        }
        Assert.All(destination, b => Assert.Equal((byte)0xaf, b));
    }

    [Fact]
    public void Null_mapped_pointer_is_rejected_before_copying()
    {
        var map = new MappedSubresource { RowPitch = 2 };
        Assert.Throws<InvalidDataException>(() => map.CopyNv12To(2, 2, new byte[6]));
    }
}

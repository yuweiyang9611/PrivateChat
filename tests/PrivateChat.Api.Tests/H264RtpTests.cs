using PrivateChat.Agent.Realtime;

namespace PrivateChat.Api.Tests;

/// <summary>被控端快速线路的 H.264 → RTP 分片（RFC 6184，packetization-mode 1）。</summary>
public class H264RtpTests
{
    private static byte[] Nal(byte header, int bodyLength, byte fill = 0x55)
    {
        var nal = new byte[1 + bodyLength];
        nal[0] = header;
        Array.Fill(nal, fill, 1, bodyLength);
        nal[^1] = 0x80; // NAL 以停止位结尾，末尾不会是 0（切 NAL 时末尾的 0 算进下一个起始码）
        return nal;
    }

    private static byte[] AnnexB(params byte[][] nals) => [.. nals.SelectMany(n => new byte[] { 0, 0, 0, 1 }.Concat(n))];

    /// <summary>按接收端的做法把分片拼回去：单 NAL 原样，FU-A 用指示字节的 F / NRI 加上头字节里的类型还原 NAL 头。</summary>
    private static List<byte[]> Reassemble(IEnumerable<RtpPayload> packets)
    {
        var nals = new List<byte[]>();
        List<byte>? current = null;
        foreach (var p in packets)
        {
            if ((p.Data[0] & 0x1F) != 28)
            {
                Assert.Null(current);
                nals.Add(p.Data);
                continue;
            }
            var (start, end) = ((p.Data[1] & 0x80) != 0, (p.Data[1] & 0x40) != 0);
            if (start)
            {
                Assert.Null(current);
                current = [(byte)((p.Data[0] & 0xE0) | (p.Data[1] & 0x1F))];
            }
            Assert.NotNull(current);
            current.AddRange(p.Data.AsSpan(2));
            if (end)
            {
                nals.Add([.. current]);
                current = null;
            }
        }
        Assert.Null(current);
        return nals;
    }

    [Fact]
    public void Small_nal_units_go_out_one_per_packet_and_only_the_last_packet_is_marked()
    {
        var sps = Nal(0x67, 20);
        var pps = Nal(0x68, 4);
        var idr = Nal(0x65, 900);
        var packets = H264Rtp.Packetize(AnnexB(sps, pps, idr));

        Assert.Equal([sps, pps, idr], packets.Select(p => p.Data));
        Assert.Equal([false, false, true], packets.Select(p => p.Marker));
    }

    [Fact]
    public void A_nal_unit_that_does_not_fit_is_split_into_fu_a_fragments()
    {
        var slice = Nal(0x41, 3000); // NRI = 2，类型 1
        var packets = H264Rtp.Packetize(AnnexB(slice));

        Assert.Equal(3, packets.Count);
        Assert.All(packets, p => Assert.True(p.Data.Length <= H264Rtp.MaxPayload));
        // 指示字节：F、NRI 照抄，类型 28；头字节：第一片带起始位、最后一片带结束位，都带原来的类型
        Assert.All(packets, p => Assert.Equal(0x40 | 28, p.Data[0]));
        Assert.Equal([0x80 | 1, 1, 0x40 | 1], packets.Select(p => (int)p.Data[1]));
        Assert.Equal([false, false, true], packets.Select(p => p.Marker));
        Assert.Equal(slice, Assert.Single(Reassemble(packets)));
    }

    [Theory]
    [InlineData(H264Rtp.MaxPayload, 1)]      // 刚好放得下：不拆
    [InlineData(H264Rtp.MaxPayload + 1, 2)]  // 多一个字节：拆成两片，第二片只有一点点
    [InlineData(2 * (H264Rtp.MaxPayload - 2) + 1, 2)] // 两片刚好装满
    [InlineData(2 * (H264Rtp.MaxPayload - 2) + 2, 3)]
    public void Sizes_right_at_the_limit_are_split_correctly(int nalLength, int expectedPackets)
    {
        var slice = Nal(0x61, nalLength - 1);
        var packets = H264Rtp.Packetize(AnnexB(slice));

        Assert.Equal(expectedPackets, packets.Count);
        Assert.All(packets, p => Assert.InRange(p.Data.Length, 3, H264Rtp.MaxPayload));
        Assert.Equal(slice, Assert.Single(Reassemble(packets)));
        Assert.True(packets[^1].Marker);
    }

    [Fact]
    public void A_key_frame_with_parameter_sets_and_a_large_slice_survives_the_round_trip()
    {
        var nals = new[] { Nal(0x67, 30), Nal(0x68, 6), Nal(0x65, 50_000, 0xAB), Nal(0x41, 10) };
        var packets = H264Rtp.Packetize(AnnexB(nals));

        Assert.Equal(nals, Reassemble(packets));
        Assert.Equal(1, packets.Count(p => p.Marker));
        Assert.True(packets[^1].Marker);
    }

    [Fact]
    public void Nothing_to_send_gives_no_packets()
    {
        Assert.Empty(H264Rtp.Packetize([]));
        Assert.Empty(H264Rtp.Packetize([1, 2, 3])); // 没有起始码
    }
}

using PrivateChat.Contracts.Remote;

namespace PrivateChat.Agent.Realtime;

/// <summary>一个 RTP 包的负载。Marker 标在一帧的最后一包上。</summary>
public readonly record struct RtpPayload(byte[] Data, bool Marker);

/// <summary>
/// H.264 的一帧（Annex B）→ RTP 负载（RFC 6184，packetization-mode 1）：NAL 放得进一包就原样发，放不进拆成 FU-A。
/// 自己拆而不用 SIPSorcery 里的：每个包的序号由我们定（补发丢了的包时要用原来的序号），也能在测试里故意丢包。
/// </summary>
public static class H264Rtp
{
    /// <summary>负载上限。加上 RTP、SRTP、UDP、IP 的头也不到 1280 字节（IPv6 保证走得通的最小 MTU）。</summary>
    public const int MaxPayload = 1200;
    private const int FuA = 28;

    public static List<RtpPayload> Packetize(ReadOnlySpan<byte> annexB, int maxPayload = MaxPayload)
    {
        var packets = new List<RtpPayload>();
        foreach (var nal in H264.Split(annexB))
        {
            var unit = annexB.Slice(nal.Offset, nal.Length);
            if (unit.Length <= maxPayload)
            {
                packets.Add(new RtpPayload(unit.ToArray(), false));
                continue;
            }
            // FU-A：指示字节（F、NRI 照抄，类型 28）+ 头字节（起始位 / 结束位 + 原来的类型）+ 一段 NAL 内容（不含 NAL 头）
            var indicator = (byte)((unit[0] & 0xE0) | FuA);
            var type = unit[0] & 0x1F;
            var body = unit[1..];
            var chunk = maxPayload - 2;
            for (var offset = 0; offset < body.Length; offset += chunk)
            {
                var size = Math.Min(chunk, body.Length - offset);
                var data = new byte[size + 2];
                data[0] = indicator;
                data[1] = (byte)(type | (offset == 0 ? 0x80 : 0) | (offset + size == body.Length ? 0x40 : 0));
                body.Slice(offset, size).CopyTo(data.AsSpan(2));
                packets.Add(new RtpPayload(data, false));
            }
        }
        if (packets.Count > 0) packets[^1] = packets[^1] with { Marker = true };
        return packets;
    }
}

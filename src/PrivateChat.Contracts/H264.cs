namespace PrivateChat.Contracts.Remote;

/// <summary>NAL 单元在 Annex B 字节流里的位置（不含起始码）。</summary>
public readonly record struct NalUnit(int Offset, int Length)
{
    public int Type(ReadOnlySpan<byte> data) => data[Offset] & 0x1F;
}

/// <summary>H.264 Annex B 字节流的最小解析：切 NAL、认关键帧、取参数集。编码器输出与浏览器 WebCodecs 输入都是 Annex B。</summary>
public static class H264
{
    public const int Idr = 5;
    public const int Sps = 7;
    public const int Pps = 8;

    private static readonly byte[] StartCode = [0, 0, 0, 1];

    public static List<NalUnit> Split(ReadOnlySpan<byte> data)
    {
        var units = new List<NalUnit>();
        var start = -1;
        var i = 0;
        while (i + 2 < data.Length)
        {
            if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1)
            {
                if (start >= 0) Add(units, data, start, i);
                i += 3;
                start = i;
                continue;
            }
            i++;
        }
        if (start >= 0) Add(units, data, start, data.Length);
        return units;
    }

    // 四字节起始码前面那个 0 属于起始码，不属于上一个 NAL（NAL 以停止位 1 结尾，末尾不会有合法的 0 字节）
    private static void Add(List<NalUnit> units, ReadOnlySpan<byte> data, int start, int end)
    {
        while (end > start && data[end - 1] == 0) end--;
        if (end > start) units.Add(new NalUnit(start, end - start));
    }

    public static bool HasType(ReadOnlySpan<byte> data, int type)
    {
        foreach (var nal in Split(data))
            if (nal.Type(data) == type) return true;
        return false;
    }

    public static bool IsKeyFrame(ReadOnlySpan<byte> data) => HasType(data, Idr);

    /// <summary>取出 SPS + PPS（带起始码），没有则返回 null。</summary>
    public static byte[]? ParameterSets(ReadOnlySpan<byte> data)
    {
        var output = new List<byte>();
        bool sps = false, pps = false;
        foreach (var nal in Split(data))
        {
            var type = nal.Type(data);
            if (type is not (Sps or Pps)) continue;
            sps |= type == Sps;
            pps |= type == Pps;
            output.AddRange(StartCode);
            output.AddRange(data.Slice(nal.Offset, nal.Length).ToArray());
        }
        return sps && pps ? [.. output] : null;
    }

    /// <summary>WebCodecs 的 codec 字符串（avc1.PPCCLL），取自 SPS 的 profile_idc、约束标志、level_idc。</summary>
    public static string? CodecString(ReadOnlySpan<byte> data)
    {
        foreach (var nal in Split(data))
        {
            if (nal.Type(data) != Sps || nal.Length < 4) continue;
            return $"avc1.{data[nal.Offset + 1]:X2}{data[nal.Offset + 2]:X2}{data[nal.Offset + 3]:X2}";
        }
        return null;
    }
}

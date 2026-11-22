using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using PrivateChat.Contracts.Remote;

namespace PrivateChat.Agent.Video;

// 选编码器（含"哪个尺寸建不起来"的记账）、硬件编码器逐帧定质量、认"这一帧的输出"里不碰 Windows 的那部分：纯计算，测试项目把这个文件直接编进去测
// （VideoEncodersTests）。用到它的是 VideoEncoders、HardwareH264Encoder、Telemetry/EncoderProbe。

/// <summary>硬件编码器怎么控制每帧的大小。</summary>
public enum HardwareRateControl
{
    /// <summary>码率控制设成"按质量"（eAVEncCommonRateControlMode_Quality），逐帧改 AVEncCommonQuality（0–100，和软件编码器一样）。</summary>
    Quality,
    /// <summary>码率控制设成"按质量"，逐帧改 AVEncVideoEncodeQP（固定 QP；质量按 <see cref="EncoderChoice.QpForQuality"/> 换算）。</summary>
    Qp,
    /// <summary>固定码率（不能逐帧定质量：采集循环按码率走）。</summary>
    Cbr,
}

/// <summary>PRIVATECHAT_AGENT_ENCODER：auto（默认）| hardware | software。</summary>
public enum EncoderSwitch { Auto, Hardware, Software }

/// <summary>要试的一个硬件编码器：按名字（MFT 友好名）认；Modes 按顺序试，第一个编码器接受的就用；Validated = 最近一次探测验证过。</summary>
public sealed record EncoderAttempt(string Name, int VendorId, IReadOnlyList<HardwareRateControl> Modes, bool Validated);

public static class EncoderChoice
{
    /// <summary>硬件编码器名字（日志、遥测里）的后缀："Intel® Quick Sync Video H.264 Encoder MFT（硬件）"。</summary>
    public const string HardwareSuffix = "（硬件）";

    /// <summary>Microsoft 的 PCI 厂商号："Microsoft AVC DX12 Encoder"这种通用的硬件编码器报这个，排在各家显卡自己的后面。</summary>
    public const int MicrosoftVendorId = 0x1414;

    /// <summary>
    /// 探测没验证过的编码器（hardware 模式）照这个顺序试：逐帧 QP、按质量、最后固定码率。
    /// 逐帧 QP 在前：Intel Quick Sync（这台笔记本上实测）接受按质量、但中途改 AVEncCommonQuality 没有效果，改 QP 有效；
    /// 而且 QP 是按软件编码器的刻度换算的（画质规划照那个刻度学的），各家"按质量"的 0–100 怎么对应 QP 说不准。
    /// </summary>
    public static readonly IReadOnlyList<HardwareRateControl> UnverifiedModes = [HardwareRateControl.Qp, HardwareRateControl.Quality, HardwareRateControl.Cbr];

    public static EncoderSwitch ParseSwitch(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "hardware" or "hw" => EncoderSwitch.Hardware,
        "software" or "sw" => EncoderSwitch.Software,
        _ => EncoderSwitch.Auto,
    };

    public static string DisplayName(string friendlyName) => friendlyName + HardwareSuffix;

    /// <summary>"X（硬件）" → "X"（别的原样）：探测结果、MarkFailed 传进来的名字两种写法都认。</summary>
    public static string BaseName(string name) => name.EndsWith(HardwareSuffix, StringComparison.Ordinal) ? name[..^HardwareSuffix.Length] : name;

    /// <summary>
    /// 质量（0–100，软件编码器的刻度）→ 量化参数：qp = round(44 − q / 3.5)，限在 10–51。
    /// 软件编码器实测每 3.5 个质量差一个 QP（0 ≈ QP 44，70 ≈ QP 24，100 ≈ QP 15～16），画质规划（QualityPlanner）是照这个刻度学的。
    /// </summary>
    public static int QpForQuality(int quality)
    {
        var q = Math.Clamp(quality, 0, 100);
        return Math.Clamp((int)Math.Round(44 - q / 3.5, MidpointRounding.AwayFromZero), 10, 51);
    }

    /// <summary>
    /// AVEncVideoEncodeQP 的值（VT_UI8）：四个 16 位——0–15 位默认 QP、16–31 位 I 帧、32–47 位 P 帧、48–63 位 B 帧。
    /// 文档说编码器可能只看默认的那个；四个都填同一个 QP，看 I / P 那几位的编码器也不会拿到 0。
    /// </summary>
    public static long PackQp(int qp)
    {
        var v = (long)(ushort)Math.Clamp(qp, 0, 51);
        return v | v << 16 | v << 32 | v << 48;
    }

    /// <summary>等这一帧的输出最多等多久：max(100 ms, 3 帧的时间)。</summary>
    public static TimeSpan FrameTimeout(int fps) => TimeSpan.FromMilliseconds(Math.Max(100, 3000.0 / Math.Max(1, fps)));

    /// <summary>
    /// 逐帧改质量真的有效：同一幅画面质量低的关键帧之后再编一帧（same，几乎是空的），调高质量再编同一幅（refined），
    /// refined 应该明显更大（"补清晰"）——和 --selftest 一样要 4 倍以上，而且至少 1000 字节。null = 那一帧没有输出。
    /// </summary>
    public static bool RefineWorks(int? same, int? refined) => same is { } s && refined is { } r && r >= 1000 && r > s * 4;

    /// <summary>
    /// 这次要按顺序试哪些硬件编码器（空 = 直接用软件编码器）。
    /// - software：一个都不试。
    /// - auto：只试 validated 里能逐帧定质量的（探测时按质量或逐帧 QP 验证有效的那种方式，两种都有效先按质量）。
    /// - hardware：先 validated（同上），再试列出来的所有硬件编码器（enumerated，没验证过的：按 <see cref="UnverifiedModes"/> 的顺序试）。
    /// 名字重复的只留第一个；excluded（本进程里出过错的、这个尺寸建不起来的）跳过。
    /// 排序：和抓屏那块显示器同一个厂商的（captureVendorId，0 = 不知道）在前，Microsoft 通用的那种在最后，其余保持原来的顺序；
    /// hardware 模式下验证过的整体排在没验证过的前面。
    /// </summary>
    public static List<EncoderAttempt> Plan(EncoderSwitch mode, IReadOnlyList<ValidatedEncoder> validated, IReadOnlyList<(string Name, int VendorId)> enumerated,
        int captureVendorId, Func<string, bool> excluded)
    {
        if (mode == EncoderSwitch.Software) return [];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var proven = new List<EncoderAttempt>();
        foreach (var v in validated)
        {
            var name = BaseName(v.Name);
            List<HardwareRateControl> modes = [];
            if (v.QualityMode) modes.Add(HardwareRateControl.Quality);
            if (v.PerFrameQp) modes.Add(HardwareRateControl.Qp);
            if (modes.Count == 0 || name.Length == 0 || excluded(name) || !seen.Add(name)) continue;
            proven.Add(new EncoderAttempt(name, v.VendorId, modes, true));
        }
        var result = Sorted(proven, captureVendorId);
        if (mode != EncoderSwitch.Hardware) return result;

        var rest = new List<EncoderAttempt>();
        foreach (var (raw, vendor) in enumerated)
        {
            var name = BaseName(raw);
            if (name.Length == 0 || excluded(name) || !seen.Add(name)) continue;
            rest.Add(new EncoderAttempt(name, vendor, UnverifiedModes, false));
        }
        result.AddRange(Sorted(rest, captureVendorId));
        return result;
    }

    private static List<EncoderAttempt> Sorted(List<EncoderAttempt> attempts, int captureVendorId)
    {
        int Rank(EncoderAttempt a) => captureVendorId != 0 && a.VendorId == captureVendorId ? 0 : a.VendorId == MicrosoftVendorId ? 2 : 1;
        return [.. attempts.Select((a, i) => (a, i)).OrderBy(x => Rank(x.a)).ThenBy(x => x.i).Select(x => x.a)];
    }

    /// <summary>这段 Annex B 里有画面（片 NAL：类型 1–5）；只有 SPS / PPS / SEI / AUD 的是 false。</summary>
    public static bool HasPicture(ReadOnlySpan<byte> annexB)
    {
        foreach (var nal in H264.Split(annexB))
            if (nal.Type(annexB) is >= 1 and <= 5) return true;
        return false;
    }

    /// <summary>SPS 里的 profile_idc（66 Baseline、77 Main、100 High……）；没有 SPS 是 null。</summary>
    public static int? Profile(ReadOnlySpan<byte> annexB)
    {
        foreach (var nal in H264.Split(annexB))
            if (nal.Type(annexB) == H264.Sps && nal.Length >= 2) return annexB[nal.Offset + 1];
        return null;
    }

    /// <summary>快速线路的 SDP 写死了 Main（profile-level-id 4d001f）：Main 和 Baseline（Main 的子集）可以，High 之类不行。</summary>
    public static bool MainCompatible(int profile) => profile is 66 or 77;
}

/// <summary>
/// 本进程里哪个硬件编码器（友好名）在哪个尺寸上建不起来：VideoEncoders 记下来，之后这个名字在这个尺寸上不再试（别的尺寸照试）。
/// MFTEnumEx 给每个适配器列一个实例、名字一样（这台笔记本上 Intel 的列了两遍；同厂商的核显 + 独显也是这样），所以按"这一次同名的都试过"算：
/// 同名的每个实例在这个尺寸上都起不来才记；有一个起来了就把以前记的那条去掉——一个实例起不来不连累同名的另一个。
/// </summary>
public sealed class EncoderSizeFailures
{
    private readonly ConcurrentDictionary<(string Name, int Width, int Height), string> _failed = new();

    /// <summary>这个名字在这个尺寸上记过建不起来（why = 当时的原因）。</summary>
    public bool TryGet(string name, int width, int height, [MaybeNullWhen(false)] out string why) => _failed.TryGetValue((name, width, height), out why);

    /// <summary>
    /// 按顺序试同名的几个实例（start 建一个，建不起来抛异常）：第一个起来的就返回，以前记过的这一条去掉；
    /// 每个起不来的原因（explain 换成文字）交给 failed。一个都没起来（至少试了一个）才记下来（原因用最后那个的），返回 null。
    /// </summary>
    public T? StartFirst<TInstance, T>(string name, int width, int height, IEnumerable<TInstance> instances, Func<TInstance, T> start,
        Func<Exception, string> explain, Action<string> failed) where T : class
    {
        string? last = null;
        foreach (var instance in instances)
        {
            T started;
            try
            {
                started = start(instance);
            }
            catch (Exception ex)
            {
                last = explain(ex);
                failed(last);
                continue;
            }
            _failed.TryRemove((name, width, height), out _);
            return started;
        }
        if (last is not null) _failed[(name, width, height)] = last;
        return null;
    }
}

/// <summary>
/// 送进硬件编码器、还没出画面的帧（送入时的样本时间，按送的顺序）：编码器吐出一段输出时认它是哪一帧的。
/// 没有 B 帧，编码器按送的顺序出帧：某一帧出了画面，比它早的就不会再出了。
/// </summary>
public sealed class PendingFrames
{
    /// <summary>编码器把时间取整（比如到毫秒）也认：差这么多以内算同一帧（100 ns 为单位，1 ms）。</summary>
    public const long Tolerance = 10_000;
    private const int MaxPending = 8;
    private readonly List<long> _times = [];

    public int Count => _times.Count;

    public void Add(long time)
    {
        _times.Add(time);
        if (_times.Count > MaxPending) _times.RemoveAt(0);
    }

    /// <summary>
    /// 输出（样本时间 outputTime；编码器没带时间是 null）是哪一帧的：时间一样的（或差 1 ms 以内最近的那个）；
    /// 没带时间的按先进先出算最早那帧；带了时间但对不上任何一帧（早就出过画面的、不认识的）是 null。
    /// </summary>
    public long? Owner(long? outputTime)
    {
        if (_times.Count == 0) return null;
        if (outputTime is not { } t) return _times[0];
        long? best = null;
        foreach (var time in _times)
        {
            var d = Math.Abs(time - t);
            if (d <= Tolerance && (best is not { } b || d < Math.Abs(b - t))) best = time;
        }
        return best;
    }

    /// <summary>这一帧出了画面：它和比它早送进去的都不再等了。</summary>
    public void RemoveThrough(long time)
    {
        var index = _times.IndexOf(time);
        if (index >= 0) _times.RemoveRange(0, index + 1);
    }

    public void Clear() => _times.Clear();
}

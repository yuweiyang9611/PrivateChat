using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using PrivateChat.Agent.Telemetry;

namespace PrivateChat.Agent.Video;

/// <summary>
/// 选编码器（docs/DESIGN.md"截屏和编码器怎么选"）。PRIVATECHAT_AGENT_ENCODER：
/// - auto（默认）：只用最近一次探测（--probe-hardware，子进程里用 <see cref="HardwareH264Encoder"/> 实际试编过）验证过的硬件编码器，
///   和抓屏那块显示器同一个厂商的优先；按验证有效的那种方式逐帧定质量。一个都没有、都起不来，用软件编码器（<see cref="H264Encoder"/>）。
/// - hardware：探测没验证过也试——先验证过的，再试 MFTEnumEx 列出来的所有硬件编码器；还是都起不来才用软件编码器。
/// - software：只用软件编码器。
/// 硬件编码器"起来"= 建出来、接受这个尺寸和一种码率控制方式、热身那帧 500 ms 内出来（见 HardwareH264Encoder），否则试下一个。
/// 中途出错的（采集循环调 <see cref="MarkFailed"/>）本进程里不再选；某个尺寸建不起来的（同名的每个实例都起不来，见 <see cref="EncoderSizeFailures"/>），
/// 本进程里这个尺寸不再试（别的尺寸照试）。
/// 软件编码器建不起来（Windows N 版没装媒体功能包）照旧抛异常。只在采集线程上调（MTA）。
/// </summary>
public static class VideoEncoders
{
    public const string SwitchVariable = "PRIVATECHAT_AGENT_ENCODER";
    /// <summary>新建编码器时第一帧的质量（和以前远程时一样）。</summary>
    private const int InitialQuality = 30;

    /// <summary>本进程里出过错的硬件编码器（友好名 → 原因）。</summary>
    private static readonly ConcurrentDictionary<string, string> Failed = new(StringComparer.Ordinal);
    /// <summary>本进程里在某个尺寸上建不起来的（友好名、宽、高；同名的每个实例都起不来才算）。</summary>
    private static readonly EncoderSizeFailures FailedSizes = new();

    /// <summary>
    /// validated：最近一次探测里试编成功的硬件编码器（按优先顺序）；captureVendorId：抓屏那块显示器所在显卡的厂商（0 = 不知道），同厂商的优先。
    /// note：没用硬件时的原因（给日志），用了硬件时 null。
    /// </summary>
    public static IVideoEncoder Create(int width, int height, int fps, int maxKbps, IReadOnlyList<ValidatedEncoder> validated, int captureVendorId, out string? note)
    {
        var mode = EncoderChoice.ParseSwitch(Environment.GetEnvironmentVariable(SwitchVariable));
        if (mode == EncoderSwitch.Software)
        {
            note = $"{SwitchVariable}=software：用软件编码器";
            return Software(width, height, fps, maxKbps);
        }

        var reasons = new List<string>();
        bool Excluded(string name)
        {
            if (Failed.TryGetValue(name, out var why))
            {
                reasons.Add($"硬件编码器 {name} 这次运行里出过错，不再用（{why}）");
                return true;
            }
            if (FailedSizes.TryGet(name, width, height, out why))
            {
                reasons.Add($"硬件编码器 {name} 编不了 {width}x{height}（{why}）");
                return true;
            }
            return false;
        }

        List<IMFActivate> enumerated = [];
        try
        {
            List<EncoderAttempt> plan;
            if (mode == EncoderSwitch.Auto)
            {
                plan = EncoderChoice.Plan(mode, validated, [], captureVendorId, Excluded);
                if (plan.Count == 0)
                {
                    note = reasons.Count > 0 ? Join(reasons) : validated.Count == 0 ? "没有探测验证过的硬件编码器" : "探测验证过的硬件编码器都不能逐帧定质量";
                    return Software(width, height, fps, maxKbps);
                }
                enumerated = Mft.H264Encoders(Mft.EnumHardware | Mft.EnumAsyncMft | Mft.EnumSortAndFilter);
            }
            else
            {
                enumerated = Mft.H264Encoders(Mft.EnumHardware | Mft.EnumAsyncMft | Mft.EnumSortAndFilter);
                plan = EncoderChoice.Plan(mode, validated, [.. enumerated.Where(Mft.IsHardware).Select(a => (Mft.Name(a) ?? "", Mft.VendorId(a)))], captureVendorId, Excluded);
            }

            foreach (var attempt in plan)
            {
                var matching = enumerated.Where(a => Mft.Name(a) == attempt.Name).ToList();
                if (matching.Count == 0)
                {
                    reasons.Add($"硬件编码器 {attempt.Name} 现在列不出来了（换了驱动？）");
                    continue;
                }
                // 同名的几个实例（每个适配器一个）挨个试：都起不来才记"这个尺寸建不起来"，有一个起来了就去掉以前记的
                var encoder = FailedSizes.StartFirst(attempt.Name, width, height, matching,
                    activate =>
                    {
                        var started = new HardwareH264Encoder(activate, attempt.Name, attempt.VendorId != 0 ? attempt.VendorId : Mft.VendorId(activate),
                            width, height, fps, maxKbps, attempt.Modes, InitialQuality, ownsActivate: true);
                        enumerated.Remove(activate); // 归编码器了
                        return started;
                    },
                    ex => HResults.Clip(HResults.Explain(ex), 160),
                    why => reasons.Add($"硬件编码器 {attempt.Name} 起不来：{why}"));
                if (encoder is not null)
                {
                    note = null;
                    return encoder;
                }
            }
            if (mode == EncoderSwitch.Hardware && plan.Count == 0 && reasons.Count == 0) reasons.Add("没有找到硬件编码器（MFTEnumEx 一个都没列出来）");
        }
        catch (Exception ex)
        {
            reasons.Add($"列不出硬件编码器：{HResults.Clip(HResults.Explain(ex), 160)}");
        }
        finally
        {
            foreach (var activate in enumerated)
            {
                try
                {
                    Marshal.ReleaseComObject(activate);
                }
                catch (Exception) { }
            }
        }
        note = Join(reasons);
        return Software(width, height, fps, maxKbps);
    }

    /// <summary>这个硬件编码器在本进程里出过错：之后不再选它（name 写"X（硬件）"或"X"都行）。</summary>
    public static void MarkFailed(string name, string reason)
    {
        var key = EncoderChoice.BaseName(name);
        if (key.Length == 0 || key == H264Encoder.SoftwareName) return;
        Failed[key] = HResults.Clip(reason, 160);
    }

    private static H264Encoder Software(int width, int height, int fps, int maxKbps) => new(width, height, fps, maxKbps, quality: InitialQuality);

    private static string Join(List<string> reasons) => HResults.Clip(string.Join("；", reasons.Distinct()), 300);
}

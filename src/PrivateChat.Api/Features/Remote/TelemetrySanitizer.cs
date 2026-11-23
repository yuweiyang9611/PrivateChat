using System.Text;
using PrivateChat.Contracts.Remote;

namespace PrivateChat.Api.Features.Remote;

/// <summary>
/// 遥测进库之前清理一遍（被控端、浏览器发来的都不可信，管理员看到的就是清理完的这一份）：
/// - 字符串：控制字符换成空格（jsonb 不收 \u0000）、去掉落单的半个代理项、截短——说明（问题、出错）300 字，名字 200 字，版本号 32 字，
///   浏览器 / 系统 64 字（只该是"Chrome 131"这种短名字，整串 UA 带进来也截掉大半）。缺了的（null）补成空字符串。
/// - 列表：缺了补空列表、去掉空项；一般最多 32 项，问题 / 出错 / 线路问题最多 20 条。
/// - 数字：小数不是有限值的当 0，都不小于 0；尺寸、帧率、比例这些有明确范围的夹到范围内；小数留 3 位。
/// - 统计：Samples 为 0 时其余都是 0（合同里就这么约定的）。枚举不认识的值当 Auto。
/// 纯函数，单元测试直接调。
/// </summary>
public static class TelemetrySanitizer
{
    public const int MaxText = 300;
    public const int MaxName = 200;
    public const int MaxVersion = 32;
    public const int MaxBrowser = 64;
    public const int MaxItems = 32;
    public const int MaxMessages = 20;
    private const int MaxPixels = 16384;
    private const int MaxFps = 1000;
    private const double MaxValue = 1e9;
    private const long MaxBytes = 1L << 50;

    public static AgentTelemetrySnapshot Snapshot(AgentTelemetrySnapshot s) => new(
        Text(s.AgentVersion, MaxVersion),
        s.CollectedAt.ToUniversalTime(),
        Text(s.Fingerprint, MaxName),
        Os(s.Os),
        Cpu(s.Cpu),
        Bytes(s.MemoryBytes),
        Items(s.Gpus, Gpu),
        Items(s.Displays, Display),
        Items(s.Encoders, Encoder),
        Capture(s.Capture),
        s.RemoteDesktopSession,
        Texts(s.Problems, MaxMessages, MaxText));

    public static AgentSessionTelemetry AgentSession(AgentSessionTelemetry t) => new(
        Text(t.AgentVersion, MaxVersion), t.Final,
        Count(t.VideoSeconds), Count(t.RelaySeconds), Count(t.RealtimeSeconds),
        Text(t.Encoder, MaxName), t.QualityMode, Text(t.Capture, MaxName),
        Items(t.Settings, Setting),
        Items(t.Sizes, Size),
        Stat(t.SentFps), Stat(t.SentKbps), Stat(t.TargetKbps),
        Stat(t.CaptureMs), Stat(t.EncodeMs), Stat(t.CpuPercent), Stat(t.RttMs),
        Count(t.CpuBoundSeconds), Count(t.SecureDesktopSeconds),
        Count(t.AutoNetworkSteps), Count(t.AutoCpuSteps), Count(t.AutoCpuUndos),
        Count(t.KeyFramesRequested), Count(t.Stalls), Count(t.RelaySendDrops),
        Count(t.MaxFpsCaps), Range(t.MinMaxFps, MaxFps),
        Count(t.LineConnects), Count(t.LineFailures), Texts(t.LineProblems, MaxMessages, MaxText),
        StatOrNull(t.LossPermille), StatOrNull(t.ReceivedKbps), StatOrNull(t.JitterBufferMs), StatOrNull(t.ViewerFps),
        Texts(t.Errors, MaxMessages, MaxText),
        t.PublisherAttempts?.Where(a => a is { Attempt: > 0, Outcome: "connected" or "failed" or "cancelled" or "pending" })
            .TakeLast(8).Select(a => new PublisherAttemptTelemetry(
                a.Attempt, a.Outcome, a.Strategy is "all" or "relay" ? a.Strategy : "unknown", Duration(a.TotalMs),
                DurationOrNull(a.IceConfigMs), DurationOrNull(a.GatherMs), DurationOrNull(a.PublishMs), DurationOrNull(a.HandshakeMs),
                Candidate(a.LocalCandidateType), Candidate(a.RemoteCandidateType))).ToArray());

    public static ViewerSessionTelemetry Viewer(ViewerSessionTelemetry v) => new(
        v.Final, Text(v.Browser, MaxBrowser), Text(v.Platform, MaxBrowser),
        Range(v.ScreenWidth, MaxPixels), Range(v.ScreenHeight, MaxPixels), Number(v.DevicePixelRatio, 16), Range(v.HardwareConcurrency, 4096),
        Count(v.Seconds), Count(v.RelaySeconds), Count(v.RealtimeSeconds),
        Stat(v.DecodedFps),
        Count(v.Freezes), Count(v.LongestFreezeMs), Count(v.FreezeMsTotal),
        Count(v.KeyFrameRequests), Count(v.MaxFpsCaps), Range(v.MinMaxFps, MaxFps),
        Count(v.RealtimeReconnects), Texts(v.LineProblems, MaxMessages, MaxText),
        StatOrNull(v.JitterBufferMs), StatOrNull(v.LossPermille),
        Texts(v.Errors, MaxMessages, MaxText),
        v.Startup is { } start ? new ViewerStartupTelemetry(
            DurationOrNull(start.RequestToFirstFrameMs), DurationOrNull(start.ConnectToFirstFrameMs),
            start.FirstFrameLine is "relay" or "realtime" ? start.FirstFrameLine : null,
            (start.Attempts ?? []).Where(a => a is { Attempt: > 0, Outcome: "firstFrame" or "failed" or "cancelled" or "pending" })
                .TakeLast(8).Select(a => new ViewerFastAttemptTelemetry(a.Attempt, a.Outcome, Duration(a.TotalMs),
                    DurationOrNull(a.PublisherWaitMs), DurationOrNull(a.PullToFirstFrameMs))).ToArray()) : null,
        v.Playback is { } p ? new ViewerPlaybackTelemetry(
            NonnegativeOrNull(p.FastFramesDecoded), NonnegativeOrNull(p.FastFramesDropped), NonnegativeOrNull(p.FastNackRequests),
            NonnegativeOrNull(p.FastRetransmittedPackets), p.FastDecodeMs is { Samples: > 0 } ? Stat(p.FastDecodeMs) : null,
            p.RelayDecodeQueue is { Samples: > 0 } ? Stat(p.RelayDecodeQueue) : null, NonnegativeOrNull(p.RelaySkippedUntilKey)) : null);

    private static string? Candidate(string? kind) => kind is "host" or "srflx" or "prflx" or "relay" ? kind : null;
    private static int Duration(int milliseconds) => Math.Clamp(milliseconds, 0, 86_400_000);
    private static int? DurationOrNull(int? milliseconds) => milliseconds is >= 0 ? Duration(milliseconds.Value) : null;
    private static int? NonnegativeOrNull(int? count) => count is >= 0 ? count : null;

    // ---- 各个部分 ----

    private static TelemetryOs Os(TelemetryOs? o) => o is null
        ? new TelemetryOs("", "", 0, 0, "", false)
        : new TelemetryOs(Text(o.ProductName, MaxName), Text(o.DisplayVersion, MaxVersion), Count(o.Build), Count(o.Revision), Text(o.Edition, MaxName), o.Is64Bit);

    private static TelemetryCpu Cpu(TelemetryCpu? c) => c is null
        ? new TelemetryCpu("", 0, 0, 0)
        : new TelemetryCpu(Text(c.Name, MaxName), Range(c.Cores, 4096), Range(c.Threads, 4096), Range(c.BaseMhz, 100_000));

    private static TelemetryGpu Gpu(TelemetryGpu g) => new(
        Text(g.Name, MaxName), Count(g.VendorId), Count(g.DeviceId), Text(g.DriverVersion, MaxName), Bytes(g.DedicatedMemoryBytes), g.Software,
        Texts(g.Outputs, MaxItems, MaxName));

    private static TelemetryDisplay Display(TelemetryDisplay d) => new(
        Text(d.DeviceName, MaxName), Range(d.Width, MaxPixels), Range(d.Height, MaxPixels), Range(d.RefreshHz, MaxFps), Range(d.DpiScalePercent, 1000), d.Primary,
        TextOrNull(d.Adapter, MaxName));

    private static TelemetryEncoder Encoder(TelemetryEncoder e) => new(
        Text(e.Name, MaxName), e.Hardware, Count(e.VendorId), e.Activated, e.Async,
        Texts(e.RateControlModes, MaxItems, MaxName),
        e.QualityMode, e.PerFrameQp, e.LowLatency, e.Accepts1080p, e.Accepts4k,
        e.Test is { } t ? EncodeTest(t) : null,
        TextOrNull(e.Error, MaxText));

    private static TelemetryEncodeTest EncodeTest(TelemetryEncodeTest t) => new(
        Range(t.Width, MaxPixels), Range(t.Height, MaxPixels), Range(t.Frames, 100_000), Range(t.FramesOut, 100_000),
        Number(t.FirstFrameMs), Number(t.AvgMs), Number(t.P90Ms), Count(t.Bytes), TextOrNull(t.Error, MaxText));

    private static TelemetryCapture Capture(TelemetryCapture? c) => c is null
        ? new TelemetryCapture([], null, null, null)
        : new TelemetryCapture(Items(c.Duplication, Duplication), NumberOrNull(c.GdiNativeMs), NumberOrNull(c.GdiScaledMs), TextOrNull(c.Error, MaxText));

    private static TelemetryDuplication Duplication(TelemetryDuplication d) => new(
        Text(d.Output, MaxName), TextOrNull(d.Adapter, MaxName), d.Ok, NumberOrNull(d.FirstFrameMs), TextOrNull(d.Error, MaxText),
        NumberOrNull(d.FrameMs), NumberOrNull(d.FrameP90Ms), TextOrNull(d.FrameSize, MaxVersion));

    private static TelemetrySettingsUse Setting(TelemetrySettingsUse s) => new(
        Enum.IsDefined(s.Mode) ? s.Mode : RemoteVideoMode.Auto,
        Range(s.MaxWidth, MaxPixels), Range(s.MaxHeight, MaxPixels), Range(s.Fps, MaxFps), Range(s.MaxKbps, 1_000_000), Count(s.Seconds));

    private static TelemetrySizeUse Size(TelemetrySizeUse s) => new(Range(s.Width, MaxPixels), Range(s.Height, MaxPixels), Count(s.Seconds));

    private static readonly TelemetryStat EmptyStat = new(0, 0, 0, 0, 0, 0, 0);

    private static TelemetryStat Stat(TelemetryStat? s) => s is null || s.Samples <= 0
        ? EmptyStat
        : new TelemetryStat(s.Samples, Number(s.Mean), Number(s.Min), Number(s.P10), Number(s.P50), Number(s.P90), Number(s.Max));

    private static TelemetryStat? StatOrNull(TelemetryStat? s) => s is null ? null : Stat(s);

    // ---- 基本类型 ----

    /// <summary>列表：缺了当空的，去掉 null 项，最多 MaxItems 项，每项再清理。</summary>
    private static List<TOut> Items<TIn, TOut>(IReadOnlyList<TIn>? items, Func<TIn, TOut> clean) where TIn : class
        => [.. (items ?? []).Where(x => x is not null).Take(MaxItems).Select(clean)];

    /// <summary>字符串列表：每条清理、去掉清理完是空的，最多 maxItems 条。</summary>
    public static List<string> Texts(IReadOnlyList<string>? items, int maxItems, int maxLength)
        => [.. (items ?? []).Select(x => Text(x, maxLength)).Where(x => x.Length > 0).Take(maxItems)];

    public static string? TextOrNull(string? s, int max) => s is null ? null : Text(s, max);

    /// <summary>控制字符换成空格、去掉落单的半个代理项、截到 max 个字符（不把一对代理项截开）、去掉首尾空白。</summary>
    public static string Text(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(Math.Min(s.Length, max));
        for (var i = 0; i < s.Length && sb.Length < max; i++)
        {
            var c = s[i];
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                if (sb.Length + 2 > max) break;
                sb.Append(c).Append(s[++i]);
            }
            else if (char.IsSurrogate(c)) continue;
            else sb.Append(char.IsControl(c) ? ' ' : c);
        }
        return sb.ToString().Trim();
    }

    /// <summary>计数、秒数、毫秒数、编号：不小于 0。</summary>
    public static int Count(int v) => Math.Max(0, v);

    public static int Range(int v, int max) => Math.Clamp(v, 0, max);

    public static long Bytes(long v) => Math.Clamp(v, 0, MaxBytes);

    /// <summary>小数：不是有限值的当 0，夹到 0..max，留 3 位。</summary>
    public static double Number(double v, double max = MaxValue) => double.IsFinite(v) ? Math.Round(Math.Clamp(v, 0, max), 3) : 0;

    public static double? NumberOrNull(double? v) => v is { } x ? Number(x) : null;
}

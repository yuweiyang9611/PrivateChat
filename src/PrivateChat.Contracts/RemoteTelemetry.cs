namespace PrivateChat.Contracts.Remote;

/// <summary>一台被控设备的最近一次更新检查。同次检查复用 ID 和起始时间，状态、阶段和错误仅使用固定类别。</summary>
public sealed record AgentUpdateStatusReport(
    Guid CheckId, DateTimeOffset CheckedAt, string Source, string CurrentVersion, string? TargetVersion,
    string State, int ElapsedMs, string? FailureStage, string? ErrorCode);

// 远程协助遥测（docs/DESIGN.md"远程协助遥测"）：被控端和控制端浏览器把性能数字、硬件型号报到我们自己的服务器上，管理员在设备页的"诊断"里看。
// 只有数字、型号和出错的说明：不含屏幕内容、键盘输入、剪贴板、窗口标题、文件名。被控端设 PRIVATECHAT_AGENT_TELEMETRY=off 就全都不报。
// 网页那份类型定义是照着这里手写的（web/src/features/remote/telemetry.ts）。

/// <summary>被控端这台电脑的硬件和能力：连上服务器后上报（PUT /api/remote/agent/telemetry）。</summary>
/// <param name="Fingerprint">程序版本、显卡和驱动、显示器算出来的指纹：没变就不重新探测编码器 / 抓屏（用上次的结果）。</param>
/// <param name="RemoteDesktopSession">程序跑在远程桌面（RDP）会话里。</param>
/// <param name="Problems">探测时出的问题（每条截到 300 字）。</param>
public sealed record AgentTelemetrySnapshot(
    string AgentVersion,
    DateTimeOffset CollectedAt,
    string Fingerprint,
    TelemetryOs Os,
    TelemetryCpu Cpu,
    long MemoryBytes,
    IReadOnlyList<TelemetryGpu> Gpus,
    IReadOnlyList<TelemetryDisplay> Displays,
    IReadOnlyList<TelemetryEncoder> Encoders,
    TelemetryCapture Capture,
    bool RemoteDesktopSession,
    IReadOnlyList<string> Problems);

public sealed record TelemetryOs(string ProductName, string DisplayVersion, int Build, int Revision, string Edition, bool Is64Bit);

public sealed record TelemetryCpu(string Name, int Cores, int Threads, int BaseMhz);

/// <param name="Outputs">接在这张卡上的显示器（\\.\DISPLAY1 这种名字）。</param>
public sealed record TelemetryGpu(string Name, int VendorId, int DeviceId, string DriverVersion, long DedicatedMemoryBytes, bool Software, IReadOnlyList<string> Outputs);

/// <param name="Adapter">这块显示器接在哪张显卡上（显卡名）；查不到是 null。</param>
public sealed record TelemetryDisplay(string DeviceName, int Width, int Height, int RefreshHz, int DpiScalePercent, bool Primary, string? Adapter);

/// <summary>一个 H.264 编码器（Media Foundation）：软件的那个和各家显卡的硬件编码器。</summary>
/// <param name="RateControlModes">它接受的码率控制方式：CBR / VBR / Quality / LowDelayVBR / ……（ICodecAPI 问到的）。</param>
/// <param name="QualityMode">能"按质量"编（AVEncCommonQuality 可设）。</param>
/// <param name="PerFrameQp">能逐帧给量化参数（AVEncVideoEncodeQP 可设）。</param>
public sealed record TelemetryEncoder(
    string Name, bool Hardware, int VendorId, bool Activated, bool Async,
    IReadOnlyList<string> RateControlModes,
    bool QualityMode,
    bool PerFrameQp,
    bool LowLatency,
    bool Accepts1080p, bool Accepts4k,
    TelemetryEncodeTest? Test,
    string? Error);

/// <summary>实际编一小段测试画面（系统内存里的 NV12，1920×1080，30 帧）。</summary>
public sealed record TelemetryEncodeTest(int Width, int Height, int Frames, int FramesOut, double FirstFrameMs, double AvgMs, double P90Ms, int Bytes, string? Error);

/// <param name="GdiNativeMs">GDI 按原尺寸截主显示器一帧平均多久（BitBlt）。</param>
/// <param name="GdiScaledMs">GDI 缩到 1280×720 截一帧平均多久（StretchBlt HALFTONE）。</param>
public sealed record TelemetryCapture(
    IReadOnlyList<TelemetryDuplication> Duplication,
    double? GdiNativeMs, double? GdiScaledMs,
    string? Error);

/// <summary>Desktop Duplication 在这块显示器上能不能用（在接着它的那张卡上建 D3D11 设备、DuplicateOutput、取一帧）。</summary>
/// <param name="FrameMs">能用时：画面在动时每来一幅新画面要多久（显卡上缩放转 NV12 + 读回内存），按自动档最大的尺寸（FrameSize）连做 20 次的平均；
/// 和 <see cref="TelemetryCapture.GdiNativeMs"/> 比就知道快了多少。0.8.7 起，老版本没有这几项。</param>
public sealed record TelemetryDuplication(string Output, string? Adapter, bool Ok, double? FirstFrameMs, string? Error,
    double? FrameMs = null, double? FrameP90Ms = null, string? FrameSize = null);

/// <summary>
/// 一次远程里被控端这边的汇总：每 60 秒一次（Final=false），结束时一次（Final=true）。PUT /api/remote/sessions/{id}/telemetry
/// </summary>
/// <param name="Encoder">现在用的编码器（"Microsoft H.264（软件）"）。</param>
/// <param name="Capture">怎么截屏（"GDI"）。</param>
/// <param name="LossPermille">快速线路上控制端每秒的回报：丢包千分比（没走快速线路时 null，下面三个同）。</param>
public sealed record AgentSessionTelemetry(
    string AgentVersion, bool Final,
    int VideoSeconds, int RelaySeconds, int RealtimeSeconds,
    string Encoder, bool QualityMode, string Capture,
    IReadOnlyList<TelemetrySettingsUse> Settings,
    IReadOnlyList<TelemetrySizeUse> Sizes,
    TelemetryStat SentFps, TelemetryStat SentKbps, TelemetryStat TargetKbps,
    TelemetryStat CaptureMs, TelemetryStat EncodeMs, TelemetryStat CpuPercent, TelemetryStat RttMs,
    int CpuBoundSeconds, int SecureDesktopSeconds,
    int AutoNetworkSteps, int AutoCpuSteps, int AutoCpuUndos,
    int KeyFramesRequested, int Stalls, int RelaySendDrops,
    int MaxFpsCaps, int MinMaxFps,
    int LineConnects, int LineFailures, IReadOnlyList<string> LineProblems,
    TelemetryStat? LossPermille, TelemetryStat? ReceivedKbps, TelemetryStat? JitterBufferMs, TelemetryStat? ViewerFps,
    IReadOnlyList<string> Errors, IReadOnlyList<PublisherAttemptTelemetry>? PublisherAttempts = null);

public sealed record PublisherAttemptTelemetry(
    int Attempt, string Outcome, string Strategy, int TotalMs, int? IceConfigMs, int? GatherMs,
    int? PublishMs, int? HandshakeMs, string? LocalCandidateType, string? RemoteCandidateType);

/// <summary>每秒一个样本的统计（Samples=0 时其余都是 0）。</summary>
public sealed record TelemetryStat(int Samples, double Mean, double Min, double P10, double P50, double P90, double Max);

public sealed record TelemetrySettingsUse(RemoteVideoMode Mode, int MaxWidth, int MaxHeight, int Fps, int MaxKbps, int Seconds);

public sealed record TelemetrySizeUse(int Width, int Height, int Seconds);

/// <summary>
/// 一次远程里控制端浏览器这边的汇总（看到的才是用户觉得卡不卡）。POST /api/remote/sessions/{id}/viewer-telemetry
/// （每 60 秒一次，结束 / 关页面时用 sendBeacon 再发一次）。
/// </summary>
/// <param name="Freezes">该有画面却超过 500 ms 没出新帧算一次卡住：次数（最长一次、加起来多久见后两个）。</param>
public sealed record ViewerSessionTelemetry(
    bool Final, string Browser, string Platform,
    int ScreenWidth, int ScreenHeight, double DevicePixelRatio, int HardwareConcurrency,
    int Seconds, int RelaySeconds, int RealtimeSeconds,
    TelemetryStat DecodedFps,
    int Freezes, int LongestFreezeMs, int FreezeMsTotal,
    int KeyFrameRequests, int MaxFpsCaps, int MinMaxFps,
    int RealtimeReconnects, IReadOnlyList<string> LineProblems,
    TelemetryStat? JitterBufferMs, TelemetryStat? LossPermille,
    IReadOnlyList<string> Errors, ViewerStartupTelemetry? Startup = null, ViewerPlaybackTelemetry? Playback = null);

public sealed record ViewerStartupTelemetry(int? RequestToFirstFrameMs, int? ConnectToFirstFrameMs,
    string? FirstFrameLine, IReadOnlyList<ViewerFastAttemptTelemetry> Attempts);

public sealed record ViewerFastAttemptTelemetry(int Attempt, string Outcome, int TotalMs,
    int? PublisherWaitMs, int? PullToFirstFrameMs);

public sealed record ViewerPlaybackTelemetry(int? FastFramesDecoded, int? FastFramesDropped, int? FastNackRequests,
    int? FastRetransmittedPackets, TelemetryStat? FastDecodeMs, TelemetryStat? RelayDecodeQueue, int? RelaySkippedUntilKey);

namespace PrivateChat.Api.Domain;

// 远程协助遥测（docs/DESIGN.md 4.8"远程协助遥测"）：只有性能数字、硬件和驱动型号、出错的说明，管理员在设备页的"诊断"里看。
// 内容是 PrivateChat.Contracts.Remote 里那几个记录的 JSON（服务器清理过：字符串截短、列表限长、数字夹到范围内）。

/// <summary>
/// 被控端电脑的硬件和能力（<c>AgentTelemetrySnapshot</c>）：每台设备一行，被控端每次连上服务器后上报一次、覆盖上一次。
/// 单独一张表：devices 那一行每个设备令牌请求都要整行读一遍，几十 KB 的 JSON 不放那里。跟着设备行走（不按时间清理）。
/// </summary>
public class DeviceTelemetry
{
    public Guid DeviceId { get; set; }
    /// <summary>服务器收到的时间（被控电脑自己的时钟可能不准，探测时间在 Data 里）。</summary>
    public DateTimeOffset ReportedAt { get; set; } = DateTimeOffset.UtcNow;
    public string AgentVersion { get; set; } = "";
    /// <summary>AgentTelemetrySnapshot 的 JSON。</summary>
    public string Data { get; set; } = "{}";
}

/// <summary>
/// 一次远程的汇总：被控端那边（<c>AgentSessionTelemetry</c>）和控制端浏览器那边（<c>ViewerSessionTelemetry</c>）各一列，
/// 两边各自每 60 秒覆盖一次、结束时再覆盖一次（Final）；结束汇总之后晚到、秒数又不比它多的中途汇总不覆盖（<c>RemoteTelemetryService</c>）。
/// UpdatedAt 超过 90 天的整行删掉（<c>RemoteTelemetryRetentionWorker</c>）。
/// </summary>
public class RemoteSessionTelemetry
{
    public Guid SessionId { get; set; }
    /// <summary>被控的设备（冗余一份，按设备查最近几次时不用先连 remote_sessions）。</summary>
    public Guid DeviceId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>AgentSessionTelemetry 的 JSON，被控端还没报过时为空。</summary>
    public string? Agent { get; set; }
    /// <summary>ViewerSessionTelemetry 的 JSON，控制端还没报过时为空。</summary>
    public string? Viewer { get; set; }
}

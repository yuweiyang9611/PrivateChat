using PrivateChat.Api.Domain;
using PrivateChat.Contracts.Remote;

namespace PrivateChat.Api.Features.Remote;

public sealed record ScreenDto(int Width, int Height, bool Primary);

/// <summary>
/// 一台装了 Agent 的电脑。canControl：我能发起远程；canManage：我能改策略、吊销（主人或管理员）。
/// allowedControllers、trustedControllers（免同意名单）只给能管理的人看。trusted：我在免同意名单里，点了就直接连上。
/// session：这台设备上还没结束的会话（谁在用），没有为空。
/// </summary>
public sealed record DeviceDto(
    Guid Id,
    string Name,
    string Platform,
    Guid OwnerId,
    bool Online,
    DateTimeOffset? LastSeenAt,
    DateTimeOffset CreatedAt,
    string? AgentVersion,
    string? Os,
    List<ScreenDto> Screens,
    bool CanControl,
    bool CanManage,
    List<Guid>? AllowedControllers,
    bool HasPin,
    DateTimeOffset? PinLockedUntil,
    RemoteSessionDto? Session,
    bool Trusted,
    List<Guid>? TrustedControllers);

public sealed record RemoteSessionDto(
    Guid Id,
    Guid ControllerUserId,
    Guid TargetDeviceId,
    string DeviceName,
    Guid DeviceOwnerId,
    RemoteSessionMode Mode,
    RemoteConsentType Consent,
    RemoteSessionState State,
    DateTimeOffset StartedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? AcceptedAt,
    DateTimeOffset? EndedAt,
    string? EndReason,
    long BytesRelayed)
{
    public static RemoteSessionDto From(RemoteSession s, Device d) => new(
        s.Id, s.ControllerUserId, s.TargetDeviceId, d.Name, d.UserId, s.Mode, s.Consent, s.State,
        s.StartedAt, s.ExpiresAt, s.AcceptedAt, s.EndedAt, s.EndReason, s.BytesRelayed);
}

public sealed record RemoteSessionPageDto(List<RemoteSessionDto> Items, DateTimeOffset? NextBefore);

/// <summary>pin：无人值守时直接输入设备 PIN，不用等对方点同意。在免同意名单里的人不用带 PIN 也直接开始。</summary>
public sealed record CreateRemoteSessionRequest(Guid TargetDeviceId, RemoteSessionMode Mode, string? Pin);

public sealed record RemoteConsentRequest(bool Accept);

/// <summary>
/// 只改给了的字段。pin 设新 PIN（至少 6 位数字），clearPin 清掉。
/// trustedControllers：免同意名单，只能是电脑的主人或（这次改完之后的）远程名单里的人。
/// </summary>
public sealed record UpdateDeviceRequest(string? Name, List<Guid>? AllowedControllers, string? Pin, bool? ClearPin, List<Guid>? TrustedControllers = null);

/// <summary>"添加电脑"向导：Agent 下载地址和配置串（服务器地址 + Cloudflare Access 服务令牌）。</summary>
public sealed record AgentSetupDto(string ServerUrl, string? DownloadUrl, string SetupCode, bool AccessTokenConfigured);

/// <summary>SignalR DeviceChanged：设备上下线、改名、改策略、吊销，客户端重新拉列表。</summary>
public sealed record DeviceChangedDto(Guid DeviceId);

/// <summary>
/// 管理员的"诊断"（GET /api/telemetry/devices/{id}）：这台电脑的硬件和能力（被控端最近一次上报的，没报过为空），
/// 和最近几次有遥测的远程（新的在前）。
/// </summary>
public sealed record DeviceTelemetryDto(Guid DeviceId, string DeviceName, DateTimeOffset? ReportedAt, AgentTelemetrySnapshot? Snapshot, IReadOnlyList<SessionTelemetryDto> Sessions);

/// <summary>一次远程：会话本身的记录（谁、何时、怎么结束的），加上被控端、控制端各自报的汇总（哪边没报过就是空）。</summary>
public sealed record SessionTelemetryDto(Guid SessionId, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, string? EndReason, RemoteSessionMode Mode, string ControllerName,
    DateTimeOffset UpdatedAt, AgentSessionTelemetry? Agent, ViewerSessionTelemetry? Viewer);

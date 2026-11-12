using PrivateChat.Api.Domain;
using PrivateChat.Contracts.Remote;

namespace PrivateChat.Api.Features.Remote;

public sealed record ScreenDto(int Width, int Height, bool Primary);

/// <summary>
/// 一台装了 Agent 的电脑。canControl：我能发起远程；canManage：我能改策略、吊销（主人或管理员）。
/// allowedControllers 只给能管理的人看。session：这台设备上还没结束的会话（谁在用），没有为空。
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
    RemoteSessionDto? Session);

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

/// <summary>pin：无人值守时直接输入设备 PIN，不用等对方点同意。</summary>
public sealed record CreateRemoteSessionRequest(Guid TargetDeviceId, RemoteSessionMode Mode, string? Pin);

public sealed record RemoteConsentRequest(bool Accept);

/// <summary>只改给了的字段。pin 设新 PIN（至少 6 位数字），clearPin 清掉。</summary>
public sealed record UpdateDeviceRequest(string? Name, List<Guid>? AllowedControllers, string? Pin, bool? ClearPin);

/// <summary>"添加电脑"向导：Agent 下载地址和配置串（服务器地址 + Cloudflare Access 服务令牌）。</summary>
public sealed record AgentSetupDto(string ServerUrl, string? DownloadUrl, string SetupCode, bool AccessTokenConfigured);

/// <summary>SignalR DeviceChanged：设备上下线、改名、改策略、吊销，客户端重新拉列表。</summary>
public sealed record DeviceChangedDto(Guid DeviceId);

using Microsoft.AspNetCore.SignalR;
using PrivateChat.Api.Features.Chat;
using PrivateChat.Contracts.Remote;

namespace PrivateChat.Api.Features.Remote;

/// <summary>
/// 远程控制的实时通知：
/// - 网页（/hubs/chat）：RemoteSessionChanged 发给控制的人和设备主人；DeviceChanged 发给所有人（只带设备 Id，各自重新拉列表，列表按权限过滤）。
/// - Agent（/hubs/agent）：只发给这台设备当前那条连接。
/// </summary>
public sealed class RemoteNotifier(IHubContext<ChatHub> chat, IHubContext<AgentHub> agentHub, AgentRegistry agents)
{
    public Task SessionChangedAsync(RemoteSessionDto session)
    {
        var groups = new[] { session.ControllerUserId, session.DeviceOwnerId }.Distinct().Select(ChatEvents.UserGroup).ToList();
        return chat.Clients.Groups(groups).SendAsync(ChatEvents.RemoteSessionChanged, session);
    }

    public Task DeviceChangedAsync(Guid deviceId)
        => chat.Clients.All.SendAsync(ChatEvents.DeviceChanged, new DeviceChangedDto(deviceId));

    public Task SessionRequestedAsync(Guid deviceId, AgentSessionRequest request)
        => ToAgent(deviceId, AgentHubEvents.SessionRequested, request);

    public Task SessionEndedAsync(Guid deviceId, AgentSessionEnded ended)
        => ToAgent(deviceId, AgentHubEvents.SessionEnded, ended);

    public Task RevokedAsync(Guid deviceId) => ToAgent(deviceId, AgentHubEvents.Revoked, null);

    private Task ToAgent(Guid deviceId, string method, object? payload)
        => agents.Get(deviceId) is { } entry
            ? payload is null
                ? agentHub.Clients.Client(entry.ConnectionId).SendAsync(method)
                : agentHub.Clients.Client(entry.ConnectionId).SendAsync(method, payload)
            : Task.CompletedTask;
}

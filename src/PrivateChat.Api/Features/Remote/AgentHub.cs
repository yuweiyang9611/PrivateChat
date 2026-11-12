using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using PrivateChat.Api.Features.Auth;
using PrivateChat.Api.Infrastructure;
using PrivateChat.Contracts.Remote;

namespace PrivateChat.Api.Features.Remote;

/// <summary>
/// Agent 的控制面：/hubs/agent，只认设备令牌。和网页用的 /hubs/chat 分开——
/// Agent 连着不算主人"在线"（否则主人永远收不到推送），也收不到聊天消息。
/// </summary>
[Authorize(AuthenticationSchemes = AuthSchemes.DeviceToken)]
public sealed class AgentHub(AgentRegistry agents, RemoteService remote, RemoteNotifier notifier, AppDbContext db, ILogger<AgentHub> logger) : Hub
{
    private Guid DeviceId => Context.User?.DeviceId() ?? throw new HubException("需要设备令牌");

    public override async Task OnConnectedAsync()
    {
        var deviceId = DeviceId;
        // 同一台设备的旧连接（网络切换后旧连接还没超时）：断掉
        agents.Connected(deviceId, Context)?.Context.Abort();
        try
        {
            logger.LogInformation("Agent 上线：设备 {Device}", deviceId);
            await notifier.DeviceChangedAsync(deviceId);
            foreach (var request in await remote.OpenRequestsAsync(deviceId, Context.ConnectionAborted))
                await Clients.Caller.SendAsync(AgentHubEvents.SessionRequested, request);
            await base.OnConnectedAsync();
        }
        catch
        {
            // OnConnectedAsync 抛异常时 SignalR 不会再调 OnDisconnectedAsync：自己把登记撤掉，免得一直显示在线
            if (agents.Disconnected(deviceId, Context.ConnectionId)) await notifier.DeviceChangedAsync(deviceId);
            throw;
        }
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.User?.DeviceId() is { } deviceId && agents.Disconnected(deviceId, Context.ConnectionId))
        {
            logger.LogInformation("Agent 下线：设备 {Device}", deviceId);
            await remote.TouchAsync(deviceId);
            await notifier.DeviceChangedAsync(deviceId);
        }
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>Agent 上报版本、系统、显示器（连上后、显示器变化时）。</summary>
    public async Task Report(AgentReport report)
    {
        var deviceId = DeviceId;
        var version = Clip(report.Version, 32);
        var os = Clip(report.Os, 128);
        var screens = (report.Screens ?? []).Take(16)
            .Select(s => new AgentScreen(Math.Clamp(s.Width, 0, 16384), Math.Clamp(s.Height, 0, 16384), s.Primary)).ToList();
        agents.Reported(deviceId, Context.ConnectionId, new AgentReport(version, os, screens));
        await db.Devices.Where(d => d.Id == deviceId).ExecuteUpdateAsync(set => set
            .SetProperty(d => d.AgentVersion, version)
            .SetProperty(d => d.Os, os)
            .SetProperty(d => d.LastSeenAt, DateTimeOffset.UtcNow));
        await notifier.DeviceChangedAsync(deviceId);
    }

    private static string Clip(string? value, int max) => (value ?? "").Length > max ? value![..max] : value ?? "";
}

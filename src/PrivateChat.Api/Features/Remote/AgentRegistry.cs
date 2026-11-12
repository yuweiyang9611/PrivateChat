using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using PrivateChat.Contracts.Remote;

namespace PrivateChat.Api.Features.Remote;

/// <summary>
/// 在线的 Agent（单实例内存版）：每台设备至多一条 /hubs/agent 连接，新的顶掉旧的。
/// 显示器信息只在这里（随上报更新），不落库。
/// </summary>
public sealed class AgentRegistry
{
    public sealed record Entry(string ConnectionId, HubCallerContext Context, DateTimeOffset Since, AgentReport? Report);

    private readonly ConcurrentDictionary<Guid, Entry> _agents = new();

    /// <summary>登记连接，返回被顶掉的旧连接（调用方负责断开它）。</summary>
    public Entry? Connected(Guid deviceId, HubCallerContext context)
    {
        Entry? previous = null;
        _agents.AddOrUpdate(deviceId,
            _ => new Entry(context.ConnectionId, context, DateTimeOffset.UtcNow, null),
            (_, old) =>
            {
                previous = old;
                return new Entry(context.ConnectionId, context, DateTimeOffset.UtcNow, null);
            });
        return previous;
    }

    /// <summary>只在断开的就是当前登记的那条时才算下线（被顶掉的旧连接断开不影响）。</summary>
    public bool Disconnected(Guid deviceId, string connectionId)
        => _agents.TryGetValue(deviceId, out var e) && e.ConnectionId == connectionId
           && _agents.TryRemove(new KeyValuePair<Guid, Entry>(deviceId, e));

    public void Reported(Guid deviceId, string connectionId, AgentReport report)
    {
        while (_agents.TryGetValue(deviceId, out var e) && e.ConnectionId == connectionId)
            if (_agents.TryUpdate(deviceId, e with { Report = report }, e)) return;
    }

    public bool IsOnline(Guid deviceId) => _agents.ContainsKey(deviceId);

    public Entry? Get(Guid deviceId) => _agents.GetValueOrDefault(deviceId);

    /// <summary>吊销：断开这台设备的连接。</summary>
    public void Abort(Guid deviceId)
    {
        if (_agents.TryRemove(deviceId, out var e)) e.Context.Abort();
    }
}

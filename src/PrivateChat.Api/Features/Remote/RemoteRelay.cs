using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using PrivateChat.Contracts.Remote;

namespace PrivateChat.Api.Features.Remote;

public enum RelayRole { Agent, Controller }

/// <summary>
/// 远程控制数据面中继：/ws/remote/{sessionId}/{agent|controller} 两条 WebSocket 配对，按消息原样转发（不解码、不落盘）。
/// - 唯一看内容的地方：只看模式下，控制端发来的输入帧（键鼠、文字、剪贴板）在这里丢掉，不信任控制端页面。
/// - 背压：转发是"收一条、发一条"，对端发不动时这边就不再收，TCP 背压一路传回发送方（Agent 另有按帧确认的流控）。
/// - 对端连上 / 断开时给另一端发一条文本消息 {"type":"peer","connected":…}，Agent 据此开始 / 暂停采集。
/// - 同一角色重连会顶掉旧连接（关闭码 4000）；会话结束时两端都以 4001 关闭。
/// - 断开的一方 30 秒内没回来，会话由 RemoteMaintenanceWorker 结束（见 <see cref="Track"/>）。
/// </summary>
public sealed class RemoteRelay(ILogger<RemoteRelay> logger)
{
    /// <summary>单条消息上限：4K 关键帧也就几百 KB，8 MB 很宽裕。</summary>
    public const int MaxMessageBytes = 8 << 20;
    private const int InitialBuffer = 64 * 1024;
    /// <summary>往一端发一条消息最多等这么久，超时说明那端卡死了，直接断开它。</summary>
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(30);

    private static readonly byte[] PeerConnected = JsonSerializer.SerializeToUtf8Bytes(new RelayNotice("peer", true), JsonSerializerOptions.Web);
    private static readonly byte[] PeerDisconnected = JsonSerializer.SerializeToUtf8Bytes(new RelayNotice("peer", false), JsonSerializerOptions.Web);

    private sealed class Peer(WebSocket socket)
    {
        public WebSocket Socket { get; } = socket;
        public SemaphoreSlim SendLock { get; } = new(1, 1);
    }

    private sealed class Pair(RemoteSessionMode mode)
    {
        public readonly Lock Gate = new();
        public readonly RemoteSessionMode Mode = mode;
        public readonly DateTimeOffset Created = DateTimeOffset.UtcNow;
        public Peer? Agent;
        public Peer? Controller;
        public DateTimeOffset? AgentLeft;
        public DateTimeOffset? ControllerLeft;
        public long Bytes;
        public bool Closed;

        public Peer? Get(RelayRole role) => role == RelayRole.Agent ? Agent : Controller;

        public void Set(RelayRole role, Peer? peer, DateTimeOffset? left)
        {
            if (role == RelayRole.Agent) (Agent, AgentLeft) = (peer, left);
            else (Controller, ControllerLeft) = (peer, left);
        }
    }

    private readonly ConcurrentDictionary<Guid, Pair> _pairs = new();

    private static RelayRole Opposite(RelayRole role) => role == RelayRole.Agent ? RelayRole.Controller : RelayRole.Agent;

    /// <summary>接管一条已接受的 WebSocket，直到它断开或会话结束。</summary>
    public async Task RunAsync(Guid sessionId, RelayRole role, RemoteSessionMode mode, WebSocket socket, CancellationToken ct)
    {
        var pair = _pairs.GetOrAdd(sessionId, _ => new Pair(mode));
        var me = new Peer(socket);
        Peer? replaced = null, other = null;
        bool closed;
        lock (pair.Gate)
        {
            closed = pair.Closed;
            if (!closed)
            {
                replaced = pair.Get(role);
                pair.Set(role, me, null);
                other = pair.Get(Opposite(role));
            }
        }
        if (closed)
        {
            await CloseAsync(me, RelayCloseCodes.Ended, "ended");
            return;
        }
        if (replaced is not null) await CloseAsync(replaced, RelayCloseCodes.Replaced, "replaced");
        await NoticeAsync(me, other is not null);
        if (other is not null) await NoticeAsync(other, true);

        try
        {
            await PumpAsync(pair, role, me, ct);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            // 对端掉线 / 请求取消：正常结束
        }
        finally
        {
            Peer? remaining = null;
            bool wasCurrent;
            lock (pair.Gate)
            {
                wasCurrent = pair.Get(role) == me;
                if (wasCurrent)
                {
                    pair.Set(role, null, DateTimeOffset.UtcNow);
                    if (!pair.Closed) remaining = pair.Get(Opposite(role));
                }
            }
            if (remaining is not null) await NoticeAsync(remaining, false);
        }
    }

    private async Task PumpAsync(Pair pair, RelayRole role, Peer me, CancellationToken ct)
    {
        var socket = me.Socket;
        var buffer = new byte[InitialBuffer];
        while (socket.State == WebSocketState.Open)
        {
            var count = 0;
            ValueWebSocketReceiveResult result;
            do
            {
                if (count == buffer.Length)
                {
                    if (buffer.Length >= MaxMessageBytes)
                    {
                        await CloseAsync(me, WebSocketCloseStatus.MessageTooBig, "message too big");
                        return;
                    }
                    Array.Resize(ref buffer, Math.Min(buffer.Length * 2, MaxMessageBytes));
                }
                result = await socket.ReceiveAsync(buffer.AsMemory(count), ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await CloseAsync(me, WebSocketCloseStatus.NormalClosure, "bye");
                    return;
                }
                count += result.Count;
            } while (!result.EndOfMessage);

            // 两端只发二进制帧；文本消息是中继自己发的通知，客户端发来的忽略
            if (result.MessageType != WebSocketMessageType.Binary || count == 0) continue;
            if (role == RelayRole.Controller && pair.Mode == RemoteSessionMode.View && RemoteFrames.IsInput((RemoteFrameType)buffer[0])) continue;

            Peer? target;
            lock (pair.Gate) target = pair.Get(Opposite(role));
            if (target is not null)
            {
                Interlocked.Add(ref pair.Bytes, count);
                await SendAsync(target, buffer.AsMemory(0, count), WebSocketMessageType.Binary);
            }
            // 大关键帧把缓冲撑大后缩回去，不让每个会话常驻几 MB
            if (buffer.Length > 1 << 20) buffer = new byte[InitialBuffer];
        }
    }

    private async Task SendAsync(Peer peer, ReadOnlyMemory<byte> data, WebSocketMessageType type)
    {
        // 不用来源连接的取消令牌：来源断开时不能顺带把对端的连接也取消（WebSocket 发送被取消会直接中止连接）
        using var timeout = new CancellationTokenSource(SendTimeout);
        try
        {
            await peer.SendLock.WaitAsync(timeout.Token);
            try
            {
                if (peer.Socket.State == WebSocketState.Open) await peer.Socket.SendAsync(data, type, true, timeout.Token);
            }
            finally
            {
                peer.SendLock.Release();
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            // 对端自己的接收循环会发现断开并清理；发不动超时的直接中止
            if (timeout.IsCancellationRequested)
            {
                logger.LogInformation("远程控制中继：一端 {Seconds} 秒发不出去，断开它", SendTimeout.TotalSeconds);
                peer.Socket.Abort();
            }
        }
    }

    private Task NoticeAsync(Peer peer, bool connected)
        => SendAsync(peer, connected ? PeerConnected : PeerDisconnected, WebSocketMessageType.Text);

    private static async Task CloseAsync(Peer peer, WebSocketCloseStatus status, string reason)
        => await CloseAsync(peer, (int)status, reason);

    private static async Task CloseAsync(Peer peer, int status, string reason)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await peer.SendLock.WaitAsync(timeout.Token);
            try
            {
                if (peer.Socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    await peer.Socket.CloseOutputAsync((WebSocketCloseStatus)status, reason, timeout.Token);
            }
            finally
            {
                peer.SendLock.Release();
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            peer.Socket.Abort();
        }
        // 对方不回关闭帧的话，几秒后直接断掉，接收循环随之退出
        _ = Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ =>
        {
            try { if (peer.Socket.State != WebSocketState.Closed) peer.Socket.Abort(); }
            catch (ObjectDisposedException) { }
        }, TaskScheduler.Default);
    }

    /// <summary>会话结束：两端都关掉，返回累计转发的字节数。</summary>
    public async Task<long> CloseAsync(Guid sessionId)
    {
        if (!_pairs.TryRemove(sessionId, out var pair)) return 0;
        Peer? agent, controller;
        lock (pair.Gate)
        {
            pair.Closed = true;
            agent = pair.Agent;
            controller = pair.Controller;
        }
        await Task.WhenAll(
            agent is null ? Task.CompletedTask : CloseAsync(agent, RelayCloseCodes.Ended, "ended"),
            controller is null ? Task.CompletedTask : CloseAsync(controller, RelayCloseCodes.Ended, "ended"));
        return Interlocked.Read(ref pair.Bytes);
    }

    /// <summary>两端在不在、从什么时候开始不在（从没连上过的从开始跟踪算起）。</summary>
    public readonly record struct Presence(bool Agent, DateTimeOffset AgentAbsentSince, bool Controller, DateTimeOffset ControllerAbsentSince);

    /// <summary>
    /// 维护任务对每个进行中的会话调用：没有记录就从现在开始跟踪（刚同意的会话、或服务重启后两端还没重连上来，都给足等待时间）。
    /// </summary>
    public Presence Track(Guid sessionId, RemoteSessionMode mode)
    {
        var pair = _pairs.GetOrAdd(sessionId, _ => new Pair(mode));
        lock (pair.Gate)
            return new Presence(pair.Agent is not null, pair.AgentLeft ?? pair.Created, pair.Controller is not null, pair.ControllerLeft ?? pair.Created);
    }

    public IReadOnlyCollection<Guid> Sessions => [.. _pairs.Keys];
}

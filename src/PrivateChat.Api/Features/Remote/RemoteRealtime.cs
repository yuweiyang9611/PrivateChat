using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using PrivateChat.Api.Features.Auth;
using PrivateChat.Api.Features.Calls;
using PrivateChat.Api.Infrastructure;
using PrivateChat.Contracts.Remote;

namespace PrivateChat.Api.Features.Remote;

/// <summary>
/// 快速线路在 Cloudflare 那边的东西：每个远程会话里，被控端发布画面和声音用的 Cloudflare 会话、控制端拉流用的 Cloudflare 会话。
/// 只放在内存里：服务重启后两边的数据通道会断开重连，快速线路跟着重新建。
/// 换掉或用完的轨道让 Cloudflare 停止转发是尽力而为的事，交给 <see cref="CallCleanupQueue"/> 在后台做。
/// </summary>
public sealed class RemoteRealtimeStore(CallCleanupQueue cleanup)
{
    public sealed record PublishedTrack(string Name, string Mid, string Kind);
    public sealed record Published(string SessionId, IReadOnlyList<PublishedTrack> Tracks);
    public sealed record Pulled(string SessionId, IReadOnlyList<string> Mids);

    private sealed class Entry
    {
        public Published? Publish;
        public Pulled? Pull;
    }

    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();

    public Published? PublishedOf(Guid session) => _entries.TryGetValue(session, out var e) ? Volatile.Read(ref e.Publish) : null;
    public Pulled? PulledOf(Guid session) => _entries.TryGetValue(session, out var e) ? Volatile.Read(ref e.Pull) : null;

    /// <summary>被控端（重新）发布了：以前发布的和以前拉的那份都作废。</summary>
    public void SetPublished(Guid session, Published published)
    {
        var entry = _entries.GetOrAdd(session, _ => new Entry());
        lock (entry)
        {
            Release(entry.Publish, entry.Pull);
            entry.Publish = published;
            entry.Pull = null;
        }
    }

    /// <summary>控制端（重新）拉流了：以前拉的那份作废（换了标签页、掉线重连）。</summary>
    public void SetPulled(Guid session, Pulled pulled)
    {
        var entry = _entries.GetOrAdd(session, _ => new Entry());
        lock (entry)
        {
            Release(null, entry.Pull);
            entry.Pull = pulled;
        }
    }

    /// <summary>远程会话结束：两边在 Cloudflare 的轨道都关掉。</summary>
    public void Close(Guid session)
    {
        if (!_entries.TryRemove(session, out var entry)) return;
        lock (entry)
        {
            Release(entry.Publish, entry.Pull);
            entry.Publish = null;
            entry.Pull = null;
        }
    }

    public int Count => _entries.Count;

    private void Release(Published? published, Pulled? pulled)
    {
        var items = new List<CallCleanup>();
        if (pulled is not null) items.Add(new CallCleanup(pulled.SessionId, [.. pulled.Mids]));
        if (published is not null) items.Add(new CallCleanup(published.SessionId, [.. published.Tracks.Select(t => t.Mid)]));
        cleanup.Enqueue(items);
    }
}

/// <summary>
/// 快速线路的信令：被控端把画面和声音发布到 Cloudflare Realtime，控制端从那里拉。
/// 两边都只和我们的接口打交道，App Secret、Cloudflare 的会话 ID 都不出服务器（和通话一样）。
/// 键鼠、剪贴板这些控制帧不走这里，仍走 <see cref="RemoteRelay"/>。
/// </summary>
public sealed class RemoteRealtimeService(AppDbContext db, CloudflareRealtimeClient realtime, RemoteRealtimeStore store, ILogger<RemoteRealtimeService> logger)
{
    private const int MaxSdpLength = 200_000;
    private static readonly string[] Kinds = ["video", "audio"];

    private enum Party { Agent, Controller }

    /// <summary>这台服务器能不能走快速线路，以及连 Cloudflare 用的 ICE 服务器。被控端和控制端都要先问一下。</summary>
    public async Task<RemoteRealtimeInfo> InfoAsync(ClaimsPrincipal user, Guid id, CancellationToken ct)
    {
        await ActiveAsync(user, id, null, ct);
        if (!realtime.IsConfigured) return new RemoteRealtimeInfo(false, []);
        var servers = await realtime.GenerateIceServersAsync(ct);
        return new RemoteRealtimeInfo(true, [.. servers.Select(s => new RemoteIceServer(s.Urls, s.Username, s.Credential))]);
    }

    /// <summary>被控端发布画面和声音：每次都用一个新的 Cloudflare 会话（重新发布 = 换一条连接）。</summary>
    public async Task<RemoteRealtimePublishResponse> PublishAsync(ClaimsPrincipal user, Guid id, RemoteRealtimePublishRequest request, CancellationToken ct)
    {
        await ActiveAsync(user, id, Party.Agent, ct);
        RequireConfigured();
        ValidateSdp(request.Offer, "offer");
        var tracks = request.Tracks ?? [];
        if (tracks.Count is < 1 or > 2 || tracks.Any(t => !Kinds.Contains(t.Kind) || string.IsNullOrWhiteSpace(t.Mid) || t.Mid.Length > 16)
            || tracks.Select(t => t.Kind).Distinct().Count() != tracks.Count || tracks.Select(t => t.Mid).Distinct().Count() != tracks.Count)
            throw ApiException.BadRequest("要发布一条画面轨道，可以再带一条声音轨道");

        var named = tracks.Select(t => new RemoteRealtimeStore.PublishedTrack($"{t.Kind}-{Guid.NewGuid():N}"[..20], t.Mid, t.Kind)).ToList();
        var session = await CloudflareAsync("建立快速线路", () => realtime.NewSessionAsync(ct));
        var response = await CloudflareAsync("发布画面", () => realtime.AddTracksAsync(session,
            new TracksRequest([.. named.Select(t => new TrackObject("local", t.Name, t.Mid))], new SessionDescription("offer", request.Offer)), ct));
        if (response.Tracks?.FirstOrDefault(t => t.ErrorCode is not null) is { } failed)
        {
            logger.LogWarning("远程会话 {Session} 发布到 Cloudflare Realtime 被拒绝：{Code} {Description}", id, failed.ErrorCode, failed.ErrorDescription);
            throw new ApiException(502, "发布画面失败", failed.ErrorCode);
        }
        if (response.SessionDescription is not { Type: "answer" } answer) throw new ApiException(502, "发布画面失败", "Cloudflare Realtime 没有返回 answer");

        store.SetPublished(id, new RemoteRealtimeStore.Published(session, named));
        await CloseIfEndedAsync(id);
        logger.LogInformation("远程会话 {Session}：被控端已发布到快速线路（{Tracks}）", id, string.Join("、", named.Select(t => t.Kind)));
        return new RemoteRealtimePublishResponse(answer.Sdp);
    }

    /// <summary>控制端拉流：新建一个 Cloudflare 会话去拉被控端发布的轨道，返回 Cloudflare 给的 offer。</summary>
    public async Task<RemoteRealtimePullResponse> PullAsync(ClaimsPrincipal user, Guid id, CancellationToken ct)
    {
        await ActiveAsync(user, id, Party.Controller, ct);
        RequireConfigured();
        var published = store.PublishedOf(id) ?? throw ApiException.Conflict("对方还没有把画面发到快速线路上");

        var session = await CloudflareAsync("建立快速线路", () => realtime.NewSessionAsync(ct));
        var response = await CloudflareAsync("接收画面", () => realtime.AddTracksAsync(session,
            new TracksRequest([.. published.Tracks.Select(t => new TrackObject("remote", t.Name, SessionId: published.SessionId))]), ct));
        var results = response.Tracks ?? [];
        if (results.FirstOrDefault(t => t.ErrorCode is not null) is { } failed)
        {
            logger.LogWarning("远程会话 {Session} 从 Cloudflare Realtime 拉流被拒绝：{Code} {Description}", id, failed.ErrorCode, failed.ErrorDescription);
            // 被控端那条连接已经没了（它会重新发布，再通知控制端来拉）
            throw ApiException.Conflict("对方的画面暂时拉不到");
        }
        if (response.SessionDescription is not { Type: "offer" } offer) throw new ApiException(502, "接收画面失败", "Cloudflare Realtime 没有返回 offer");

        var tracks = new List<RemoteRealtimeTrack>();
        foreach (var result in results)
        {
            var source = published.Tracks.FirstOrDefault(t => t.Name == result.TrackName);
            if (source is null || string.IsNullOrEmpty(result.Mid)) throw new ApiException(502, "接收画面失败", "Cloudflare Realtime 返回的轨道对不上");
            tracks.Add(new RemoteRealtimeTrack(result.Mid, source.Kind));
        }
        store.SetPulled(id, new RemoteRealtimeStore.Pulled(session, [.. tracks.Select(t => t.Mid)]));
        await CloseIfEndedAsync(id);
        return new RemoteRealtimePullResponse(offer.Sdp, tracks);
    }

    /// <summary>控制端把自己的 answer 交回来，Cloudflare 才开始往它那边发。</summary>
    public async Task RenegotiateAsync(ClaimsPrincipal user, Guid id, RemoteRealtimeAnswerRequest request, CancellationToken ct)
    {
        await ActiveAsync(user, id, Party.Controller, ct);
        RequireConfigured();
        ValidateSdp(request.Answer, "answer");
        var pulled = store.PulledOf(id) ?? throw ApiException.Conflict("还没有开始接收画面");
        await CloudflareAsync("接收画面", async () =>
        {
            await realtime.RenegotiateAsync(pulled.SessionId, new SessionDescription("answer", request.Answer), ct);
            return true;
        });
    }

    /// <summary>会话要在进行中，来的人要是这次会话的被控端或控制端（别人一律当作没有这个会话）。</summary>
    private async Task ActiveAsync(ClaimsPrincipal user, Guid id, Party? only, CancellationToken ct)
    {
        var session = await db.RemoteSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw ApiException.NotFound("会话");
        var deviceId = user.DeviceId();
        Party party;
        if (deviceId is not null && deviceId == session.TargetDeviceId) party = Party.Agent;
        else if (deviceId is null && user.UserId() == session.ControllerUserId) party = Party.Controller;
        else throw ApiException.NotFound("会话");
        if (only is not null && only != party)
            throw ApiException.Forbidden(only == Party.Agent ? "只有被控电脑上的远程协助程序可以发布画面" : "只有发起远程的人可以接收画面");
        if (session.State != RemoteSessionState.Active || session.EndedAt is not null) throw ApiException.Conflict("这次远程不在进行中");
    }

    /// <summary>登记完再看一眼：会话恰好在这期间结束的话，刚登记的这份没人会来收，自己收掉。</summary>
    private async Task CloseIfEndedAsync(Guid id)
    {
        var active = await db.RemoteSessions.AsNoTracking().AnyAsync(s => s.Id == id && s.State == RemoteSessionState.Active && s.EndedAt == null);
        if (!active) store.Close(id);
    }

    private void RequireConfigured()
    {
        if (!realtime.IsConfigured) throw new ApiException(503, "快速线路未配置", "管理员需要在部署配置里填写 Cloudflare Realtime 的 App ID 与 App Secret");
    }

    private static void ValidateSdp(string? sdp, string type)
    {
        if (string.IsNullOrWhiteSpace(sdp)) throw ApiException.BadRequest($"需要 SDP {type}");
        if (sdp.Length > MaxSdpLength) throw ApiException.BadRequest("SDP 过大");
    }

    private async Task<T> CloudflareAsync<T>(string what, Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (CloudflareRealtimeException ex)
        {
            logger.LogWarning("Cloudflare Realtime {What}失败：{Status} {Code} {Description}", what, ex.Status, ex.ErrorCode, ex.ErrorDescription);
            // 那边的会话没了：重新发布 / 重新拉流才能恢复
            if (ex.SessionGone) throw new ApiException(410, "快速线路的连接已失效");
            throw new ApiException(502, $"{what}失败", ex.ErrorCode);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning("Cloudflare Realtime {What}失败：{Message}", what, ex.Message);
            throw new ApiException(502, $"{what}失败", "连不上 Cloudflare Realtime");
        }
    }
}

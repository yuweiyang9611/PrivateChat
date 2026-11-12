using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using PrivateChat.Api.Domain;
using PrivateChat.Api.Features.Chat;
using PrivateChat.Api.Features.Notifications;
using PrivateChat.Api.Infrastructure;

namespace PrivateChat.Api.Features.Calls;

/// <summary>
/// 同一通通话的状态变更（加入、离开、结束、超时清理）串行化。单实例部署，进程内分段锁即可；
/// 分段而不是每通通话一把锁，免得通话多了锁对象只增不减。
/// </summary>
public sealed class CallLocks
{
    private readonly SemaphoreSlim[] _stripes = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    public async Task<IDisposable> AcquireAsync(Guid key, CancellationToken ct)
    {
        var stripe = _stripes[(key.GetHashCode() & int.MaxValue) % _stripes.Length];
        await stripe.WaitAsync(ct);
        return new Releaser(stripe);
    }

    private sealed class Releaser(SemaphoreSlim stripe) : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) stripe.Release();
        }
    }
}

/// <summary>
/// 通话编排（docs/DESIGN.md 4.5）。Cloudflare Realtime 只有会话和轨道，没有房间：
/// 谁在通话里、谁发布了什么、什么时候算接通 / 未接 / 结束，都在这里维护并经 SignalR 广播。
/// 每个参与者两条 Cloudflare 会话：一条只发布，一条只订阅，各自一条协商队列（官方 video-room 示例的做法）。
/// </summary>
public sealed class CallService(
    AppDbContext db,
    CloudflareRealtimeClient realtime,
    IHubContext<ChatHub> hub,
    MessageService messages,
    PushService push,
    CallLocks locks,
    CallCleanupQueue cleanupQueue,
    ILogger<CallService> logger)
{
    public const string LivingRoom = "living-room";
    /// <summary>响铃多久没人接算未接。</summary>
    public static readonly TimeSpan RingTimeout = TimeSpan.FromSeconds(60);
    /// <summary>客户端每 10 秒心跳一次；这么久没心跳视为掉线。</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(35);
    public const int HeartbeatSeconds = 10;
    private const int MaxSdpLength = 200_000;
    /// <summary>410 的 detail：同一个人在别的设备上还在这通通话里。客户端应就地收场，不要重新加入把那台设备顶掉。</summary>
    public const string ReplacedDetail = "replaced";

    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public bool Enabled => realtime.IsConfigured;

    // ---------------------------------------------------------------- 查询

    public async Task<CallDto> GetAsync(Guid me, Guid callId, CancellationToken ct)
    {
        var call = await Query().AsNoTracking().FirstOrDefaultAsync(c => c.Id == callId, ct) ?? throw ApiException.NotFound("通话");
        await EnsureAccessAsync(call, me, ct);
        return CallDto.From(call);
    }

    /// <summary>我所在会话里进行中的通话（响铃中或已接通），不含会议室。重新打开页面 / 重连后用它恢复来电与"加入通话"入口。</summary>
    public async Task<List<CallDto>> ActiveAsync(Guid me, CancellationToken ct)
    {
        var mine = db.ConversationMembers.Where(m => m.UserId == me).Select(m => m.ConversationId);
        var list = await Query().AsNoTracking()
            .Where(c => c.EndedAt == null && c.RoomSlug == null && c.ConversationId != null && mine.Contains(c.ConversationId.Value))
            .OrderBy(c => c.StartedAt).ToListAsync(ct);
        return list.Select(CallDto.From).ToList();
    }

    public async Task<List<CallDto>> HistoryAsync(Guid me, Guid? conversationId, int limit, CancellationToken ct)
    {
        var mine = db.ConversationMembers.Where(m => m.UserId == me).Select(m => m.ConversationId);
        var q = Query().AsNoTracking().Where(c => c.RoomSlug == null && c.ConversationId != null && mine.Contains(c.ConversationId.Value));
        if (conversationId is { } cid) q = q.Where(c => c.ConversationId == cid);
        var list = await q.OrderByDescending(c => c.StartedAt).Take(Math.Clamp(limit, 1, 100)).ToListAsync(ct);
        return list.Select(CallDto.From).ToList();
    }

    /// <summary>常驻会议室：永不结束，随时可进；第一次访问时创建。</summary>
    public async Task<List<CallDto>> RoomsAsync(Guid me, CancellationToken ct)
    {
        var room = await Query().AsNoTracking().FirstOrDefaultAsync(c => c.RoomSlug == LivingRoom, ct);
        if (room is null)
        {
            db.CallSessions.Add(new CallSession { Type = CallType.Meeting, RoomSlug = LivingRoom, StartedBy = me });
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // 两个人同时第一次打开：另一边已经建好了
                db.ChangeTracker.Clear();
            }
            room = await Query().AsNoTracking().FirstAsync(c => c.RoomSlug == LivingRoom, ct);
        }
        return [CallDto.From(room)];
    }

    /// <summary>本月通话用量估算（管理员看，别超过 Cloudflare 每月 1 TB 的免费额度）。</summary>
    public async Task<CallUsageDto> UsageAsync(DateTimeOffset now, CancellationToken ct)
    {
        var since = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var rows = await db.CallSessions.AsNoTracking()
            .Where(c => c.StartedAt >= since || c.RoomSlug != null)
            .Select(c => new { c.RoomSlug, c.AnsweredAt, c.EndedAt, c.EgressBytesEstimate })
            .ToListAsync(ct);
        var calls = rows.Where(r => r.RoomSlug == null).ToList();
        var seconds = calls.Where(r => r.AnsweredAt != null).Sum(r => (long)((r.EndedAt ?? now) - r.AnsweredAt!.Value).TotalSeconds);
        // 会议室的估算是累计值，没有按月拆分：算进来宁可偏大
        return new CallUsageDto(since, calls.Count, seconds, rows.Sum(r => r.EgressBytesEstimate));
    }

    // ---------------------------------------------------------------- 发起 / 加入 / 拒接 / 离开 / 结束

    /// <summary>在会话里发起通话并让其他成员响铃；会话里已有进行中的通话就直接返回它（调用方接着加入）。</summary>
    public async Task<(CallDto Call, bool Created)> StartAsync(Guid me, StartCallRequest req, CancellationToken ct)
    {
        if (req.Type == CallType.Meeting) throw ApiException.BadRequest("多人会议请进客厅");
        if (!realtime.IsConfigured) throw NotConfigured();
        var conv = await db.Conversations.Include(c => c.Members).AsNoTracking().FirstOrDefaultAsync(c => c.Id == req.ConversationId, ct)
                   ?? throw ApiException.NotFound("会话");
        if (conv.Members.All(m => m.UserId != me)) throw ApiException.Forbidden("不是该会话成员");

        using var _ = await locks.AcquireAsync(conv.Id, ct);
        var existing = await Query().AsNoTracking().FirstOrDefaultAsync(c => c.ConversationId == conv.Id && c.EndedAt == null, ct);
        if (existing is not null) return (CallDto.From(existing), false);

        var call = new CallSession { ConversationId = conv.Id, Type = req.Type, StartedBy = me };
        db.CallSessions.Add(call);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // 唯一索引兜底（一个会话只有一通进行中的通话）
            db.ChangeTracker.Clear();
            existing = await Query().AsNoTracking().FirstOrDefaultAsync(c => c.ConversationId == conv.Id && c.EndedAt == null, ct);
            if (existing is null) throw;
            return (CallDto.From(existing), false);
        }

        var dto = CallDto.From(call);
        var others = conv.Members.Where(m => m.UserId != me).ToList();
        if (others.Count > 0)
            await hub.Clients.Groups(others.Select(m => ChatEvents.UserGroup(m.UserId)).ToList()).SendAsync(ChatEvents.IncomingCall, dto, ct);
        // 发起人的其它设备也知道这通通话
        await hub.Clients.Group(ChatEvents.UserGroup(me)).SendAsync(ChatEvents.CallUpdated, dto, ct);

        var now = DateTimeOffset.UtcNow;
        var ring = others.Where(m => m.MutedUntil == null || m.MutedUntil < now).Select(m => m.UserId).ToList();
        if (ring.Count > 0)
        {
            var caller = await db.Users.AsNoTracking().FirstAsync(u => u.Id == me, ct);
            push.SendToUsersInBackground(ring, CallPush.Ringing(call, conv, caller.DisplayName), CallPush.RingTtlSeconds);
        }
        return (dto, true);
    }

    /// <summary>
    /// 加入通话。同一个人在别的设备 / 标签页上已经在通话里的，那一边被顶掉（网络断了重进也走这里）。
    /// 第二个人进来的那一刻算接通。
    /// </summary>
    /// <remarks>replacing：客户端重连时带上自己原来的参与者 Id。那一行已经不在、而这个人在别的设备上还在通话里时，说明是被那台设备顶掉了，返回 410 replaced，不再反过来把它顶掉。</remarks>
    public async Task<JoinResponse> JoinAsync(Guid me, Guid callId, CancellationToken ct, Guid? replacing = null)
    {
        if (!realtime.IsConfigured) throw NotConfigured();
        var iceServers = await realtime.GenerateIceServersAsync(ct);

        using var _ = await locks.AcquireAsync(callId, ct);
        var call = await Query().FirstOrDefaultAsync(c => c.Id == callId, ct) ?? throw ApiException.NotFound("通话");
        if (call.EndedAt is not null) throw Gone("通话已结束");
        await EnsureAccessAsync(call, me, ct);
        if (replacing is { } rid)
        {
            var old = call.Participants.FirstOrDefault(p => p.Id == rid && p.UserId == me);
            // 旧身份已经被清理掉了（客厅里离开超过一小时）：睡了很久的设备不要悄悄回到通话里
            if (old is null) throw Gone("已不在通话中");
            if (ElsewhereSince(call.Participants, me, old)) throw ReplacedElsewhere();
        }

        var now = DateTimeOffset.UtcNow;
        var cleanup = new List<CallCleanup>();
        var firstTime = call.Participants.All(p => p.UserId != me);
        var replaced = call.Participants.Where(p => p.UserId == me && p.LeftAt == null).ToList();
        foreach (var old in replaced) MarkLeft(call, old, now, cleanup);

        // 主键是客户端生成的：必须显式 Add，只挂到导航属性上会被 EF 当成已存在的行去 UPDATE
        var participant = new CallParticipant { CallId = call.Id, UserId = me, JoinedAt = now, LastSeenAt = now, ReplacesId = replacing };
        db.CallParticipants.Add(participant);
        if (!call.Participants.Contains(participant)) call.Participants.Add(participant);
        var answeredNow = call.RoomSlug is null && call.AnsweredAt is null && ActiveUsers(call) >= 2;
        if (answeredNow) call.AnsweredAt = now;
        await db.SaveChangesAsync(ct);

        var audience = await AudienceAsync(call, ct);
        foreach (var old in replaced)
            await audience.SendAsync(ChatEvents.ParticipantLeft, new ParticipantLeftDto(call.Id, old.Id, old.UserId, "replaced"), ct);
        await audience.SendAsync(ChatEvents.ParticipantJoined, new ParticipantEventDto(call.Id, ParticipantDto.From(participant)), ct);
        if (answeredNow) await audience.SendAsync(ChatEvents.CallUpdated, CallDto.From(call), ct);

        // 这个人的其它设备还挂着响铃通知（会一直挂着，直到被替换）：收掉。只在真的响过铃时（会话没静音）
        if (firstTime && call.RoomSlug is null && call.StartedBy != me && call.ConversationId is { } cid
            && await db.ConversationMembers.AnyAsync(m => m.ConversationId == cid && m.UserId == me && (m.MutedUntil == null || m.MutedUntil < now), ct))
            push.SendToUsersInBackground([me], CallPush.Answered(call, cid), CallPush.RingTtlSeconds);

        Cleanup(cleanup);
        return new JoinResponse(participant.Id, iceServers, CallDto.From(call));
    }

    /// <summary>拒接。单聊里拒接直接结束通话；群聊里只是告诉大家"我不接"，通话继续。</summary>
    public async Task DeclineAsync(Guid me, Guid callId, CancellationToken ct)
    {
        using var _ = await locks.AcquireAsync(callId, ct);
        var call = await Query().FirstOrDefaultAsync(c => c.Id == callId, ct) ?? throw ApiException.NotFound("通话");
        await EnsureAccessAsync(call, me, ct);
        if (call.EndedAt is not null || call.RoomSlug is not null) return;
        // 发起人取消用挂断；已经在通话里的人也用挂断，都不是拒接
        if (call.StartedBy == me || call.Participants.Any(p => p.UserId == me && p.LeftAt == null)) return;

        if (call.AnsweredAt is null && call.StartedBy != me && await IsDirectAsync(call, ct))
        {
            await EndCoreAsync(call, CallEndReason.Declined, DateTimeOffset.UtcNow, ct);
            return;
        }
        await (await AudienceAsync(call, ct)).SendAsync(ChatEvents.CallDeclined, new CallDeclinedDto(call.Id, me), ct);
        // 群聊里拒接：自己其它设备上还在响的通知收掉
        if (call.ConversationId is { } cid)
            push.SendToUsersInBackground([me], CallPush.Closed(call, cid, "已拒绝"), CallPush.RingTtlSeconds);
    }

    /// <summary>离开。带 participantId 时只离开这台设备（别的设备上的同一个人不受影响）；不带时离开这个人的所有设备（旧客户端）。</summary>
    public async Task LeaveAsync(Guid me, Guid callId, CancellationToken ct, Guid? participantId = null)
    {
        using var _ = await locks.AcquireAsync(callId, ct);
        var call = await Query().FirstOrDefaultAsync(c => c.Id == callId, ct) ?? throw ApiException.NotFound("通话");
        if (call.EndedAt is not null) return;
        var mine = call.Participants.Where(p => p.UserId == me && p.LeftAt == null && (participantId == null || p.Id == participantId)).ToList();
        if (mine.Count == 0) return;
        await LeaveCoreAsync(call, mine, "left", DateTimeOffset.UtcNow, ct);
    }

    /// <summary>结束整通通话：发起人或管理员。会议室不会结束，管理员可以清场。</summary>
    /// <remarks>
    /// ifUnanswered：发起人自己没能进去（没有麦克风、加入失败）时客户端用它取消刚建的通话。
    /// 判断在通话锁里做：已经有人接通了（群聊里别人接了正在聊）、或我在别的设备上进去了，就不结束。
    /// </remarks>
    public async Task EndAsync(Guid me, UserRole role, Guid callId, CancellationToken ct, bool ifUnanswered = false)
    {
        using var _ = await locks.AcquireAsync(callId, ct);
        var call = await Query().FirstOrDefaultAsync(c => c.Id == callId, ct) ?? throw ApiException.NotFound("通话");
        await EnsureAccessAsync(call, me, ct);
        if (call.EndedAt is not null) return;
        var now = DateTimeOffset.UtcNow;
        if (call.RoomSlug is not null)
        {
            if (role != UserRole.Admin) throw ApiException.Forbidden("只有管理员可以清空会议室");
            var everyone = call.Participants.Where(p => p.LeftAt == null).ToList();
            if (everyone.Count > 0) await LeaveCoreAsync(call, everyone, "ended", now, ct);
            return;
        }
        if (call.StartedBy != me && role != UserRole.Admin) throw ApiException.Forbidden("只有发起人或管理员可以结束通话");
        if (ifUnanswered && (call.AnsweredAt is not null || call.Participants.Any(p => p.LeftAt == null && p.UserId == me))) return;
        await EndCoreAsync(call, call.AnsweredAt is null ? CallEndReason.Cancelled : CallEndReason.Completed, now, ct);
    }

    // ---------------------------------------------------------------- 媒体：发布 / 取消发布 / 订阅

    /// <summary>发布本端轨道：浏览器的 offer → Cloudflare tracks/new → answer。轨道名由服务端生成。</summary>
    public async Task<PublishResponse> PublishAsync(Guid me, Guid callId, PublishRequest req, CancellationToken ct, Guid? participantId = null)
    {
        ValidateSdp(req.Offer, "offer");
        if (req.Tracks is not { Count: >= 1 and <= 3 }) throw ApiException.BadRequest("一次发布 1–3 条轨道");
        if (req.Tracks.Any(t => string.IsNullOrWhiteSpace(t.Mid) || t.Mid.Length > 16)) throw ApiException.BadRequest("mid 无效");
        if (req.Tracks.Select(t => t.Kind).Distinct().Count() != req.Tracks.Count) throw ApiException.BadRequest("同一种轨道只能发布一条");

        var p = await MyParticipantAsync(me, callId, participantId, ct);
        if (p.Tracks.Any(t => t.UnpublishedAt == null && req.Tracks.Any(r => r.Kind == t.Kind)))
            throw ApiException.Conflict("这种轨道已经在发布，先取消发布");
        var sessionId = await EnsureSessionAsync(p, pull: false, ct);

        var named = req.Tracks.Select(t => (Input: t, Name: $"{t.Kind.ToString().ToLowerInvariant()}-{Guid.NewGuid():N}"[..20])).ToList();
        var cf = await CfAsync("发布", () => realtime.AddTracksAsync(sessionId,
            new TracksRequest(named.Select(t => new TrackObject("local", t.Name, Mid: t.Input.Mid)).ToList(), req.Offer), ct));
        if (cf.SessionDescription is null) throw new ApiException(502, "发布失败", "Cloudflare 没有返回 answer");

        var failed = (cf.Tracks ?? []).Where(t => t.ErrorCode is not null).ToList();
        var ok = named.Where(t => failed.All(f => f.TrackName != t.Name && f.Mid != t.Input.Mid)).ToList();
        if (failed.Count > 0) logger.LogWarning("发布轨道部分失败：{Errors}", string.Join("; ", failed.Select(f => $"{f.Mid} {f.ErrorCode} {f.ErrorDescription}")));

        using var _ = await locks.AcquireAsync(callId, ct);
        db.ChangeTracker.Clear();
        var call = await Query().FirstOrDefaultAsync(c => c.Id == callId, ct);
        var current = call?.Participants.FirstOrDefault(x => x.Id == p.Id);
        if (call is null || call.EndedAt is not null || current is null || current.LeftAt is not null)
        {
            // 协商期间已经离开 / 通话结束：别留下没人管的轨道
            Cleanup([new CallCleanup(sessionId, ok.Select(t => t.Input.Mid).ToList())]);
            throw Gone("已不在通话中");
        }
        var now = DateTimeOffset.UtcNow;
        foreach (var t in ok)
        {
            var track = new CallTrack { ParticipantId = current.Id, TrackName = t.Name, Kind = t.Input.Kind, Mid = t.Input.Mid, PublishedAt = now };
            db.CallTracks.Add(track);
            if (!current.Tracks.Contains(track)) current.Tracks.Add(track);
        }
        var camChanged = ok.Any(t => t.Input.Kind == TrackKind.Video) && !current.CamOn;
        if (camChanged) current.CamOn = true;
        await db.SaveChangesAsync(ct);

        var audience = await AudienceAsync(call, ct);
        foreach (var t in ok)
            await audience.SendAsync(ChatEvents.TrackPublished, new TrackEventDto(call.Id, current.Id, t.Name, t.Input.Kind), ct);
        // 发布视频即视为开了摄像头：别人在加入快照里看到的可能还是旧状态，要单独广播
        if (camChanged) await audience.SendAsync(ChatEvents.ParticipantUpdated, new ParticipantEventDto(call.Id, ParticipantDto.From(current)), ct);
        // 部分失败也把 answer 交回去：浏览器要靠它回到 stable，没成功的轨道由前端决定重试
        return new PublishResponse(cf.SessionDescription, ok.Select(t => new PublishedTrackDto(t.Input.Mid, t.Name, t.Input.Kind)).ToList());
    }

    /// <summary>取消发布（停止共享屏幕等）。带 offer 走协商式关闭，返回 answer；不带则强制关闭。</summary>
    public async Task<UnpublishResponse> UnpublishAsync(Guid me, Guid callId, UnpublishRequest req, CancellationToken ct, Guid? participantId = null)
    {
        if (req.Offer is not null) ValidateSdp(req.Offer, "offer");
        var p = await MyParticipantAsync(me, callId, participantId, ct);
        var names = (req.TrackNames ?? []).ToHashSet();
        var targets = p.Tracks.Where(t => t.UnpublishedAt == null && names.Contains(t.TrackName)).ToList();
        if (targets.Count == 0 || p.CfSessionId is not { } sessionId) return new UnpublishResponse(null);

        var cf = await CfAsync("取消发布", () => realtime.CloseTracksAsync(sessionId, targets.Select(t => t.Mid), req.Offer, ct));
        if (cf.ErrorCode is not null) logger.LogWarning("取消发布返回错误：{Code} {Description}", cf.ErrorCode, cf.ErrorDescription);
        if (req.Offer is not null && cf.SessionDescription is null) throw new ApiException(502, "取消发布失败", cf.ErrorCode);

        using var _ = await locks.AcquireAsync(callId, ct);
        db.ChangeTracker.Clear();
        var call = await Query().FirstOrDefaultAsync(c => c.Id == callId, ct);
        var current = call?.Participants.FirstOrDefault(x => x.Id == p.Id);
        if (call is not null && current is not null)
        {
            var now = DateTimeOffset.UtcNow;
            var closed = current.Tracks.Where(t => t.UnpublishedAt == null && names.Contains(t.TrackName)).ToList();
            foreach (var t in closed)
            {
                AccrueEgress(call, current, t, now);
                t.UnpublishedAt = now;
            }
            var camChanged = closed.Any(t => t.Kind == TrackKind.Video) && current.CamOn;
            if (camChanged) current.CamOn = false;
            await db.SaveChangesAsync(ct);
            if (call.EndedAt is null && current.LeftAt is null)
            {
                var audience = await AudienceAsync(call, ct);
                foreach (var t in closed)
                    await audience.SendAsync(ChatEvents.TrackUnpublished, new TrackEventDto(call.Id, current.Id, t.TrackName, t.Kind), ct);
                if (camChanged) await audience.SendAsync(ChatEvents.ParticipantUpdated, new ParticipantEventDto(call.Id, ParticipantDto.From(current)), ct);
            }
        }
        return new UnpublishResponse(cf.SessionDescription);
    }

    /// <summary>
    /// 订阅别人的轨道。客户端给参与者 Id + 轨道名，这里换成对方的 Cloudflare 会话：只能拉同一通通话里、还在发布的轨道。
    /// Cloudflare 返回带新轨道的 offer，浏览器回 answer 后调 renegotiate 才算完成。
    /// </summary>
    public async Task<PullResponse> PullAsync(Guid me, Guid callId, PullRequest req, CancellationToken ct, Guid? participantId = null)
    {
        if (req.Tracks is not { Count: >= 1 and <= 32 }) throw ApiException.BadRequest("一次订阅 1–32 条轨道");
        var p = await MyParticipantAsync(me, callId, participantId, ct);
        var others = await db.CallParticipants.AsNoTracking().Include(x => x.Tracks)
            .Where(x => x.CallId == callId && x.LeftAt == null && x.Id != p.Id).ToListAsync(ct);

        var results = new PulledTrackDto?[req.Tracks.Count];
        var remote = new List<(int Index, TrackObject Track, TrackKind Kind)>();
        for (var i = 0; i < req.Tracks.Count; i++)
        {
            var input = req.Tracks[i];
            var owner = others.FirstOrDefault(x => x.Id == input.ParticipantId);
            var track = owner?.Tracks.FirstOrDefault(t => t.UnpublishedAt == null && t.TrackName == input.TrackName);
            if (owner?.CfSessionId is null || track is null)
                results[i] = new PulledTrackDto(input.ParticipantId, input.TrackName, track?.Kind ?? TrackKind.Audio, null, "not_found");
            else
                remote.Add((i, new TrackObject("remote", track.TrackName, SessionId: owner.CfSessionId), track.Kind));
        }
        if (remote.Count == 0) return new PullResponse(false, null, results.Select(r => r!).ToList());

        var sessionId = await EnsureSessionAsync(p, pull: true, ct);
        var cf = await CfAsync("订阅", () => realtime.AddTracksAsync(sessionId, new TracksRequest(remote.Select(r => r.Track).ToList()), ct));
        var cfTracks = cf.Tracks ?? [];
        for (var n = 0; n < remote.Count; n++)
        {
            var (index, track, kind) = remote[n];
            var input = req.Tracks[index];
            // 结果里带发布者会话 + 轨道名；个别实现不回显时按顺序对
            var hit = cfTracks.FirstOrDefault(t => t.SessionId == track.SessionId && t.TrackName == track.TrackName)
                      ?? (cfTracks.Count == remote.Count ? cfTracks[n] : null);
            results[index] = hit is { ErrorCode: null, Mid: not null }
                ? new PulledTrackDto(input.ParticipantId, input.TrackName, kind, hit.Mid, null)
                : new PulledTrackDto(input.ParticipantId, input.TrackName, kind, null, hit?.ErrorCode ?? "no_result");
        }
        // 记下拉到的 mid：离开时要让 Cloudflare 停止往这个订阅会话转发
        var mids = results.Where(r => r?.Mid is not null).Select(r => r!.Mid!).ToArray();
        if (mids.Length > 0)
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE call_participants SET pulled_mids = array_cat(pulled_mids, {mids}) WHERE id = {p.Id}", ct);
        return new PullResponse(cf.RequiresImmediateRenegotiation, cf.SessionDescription, results.Select(r => r!).ToList());
    }

    /// <summary>订阅会话的 answer 交给 Cloudflare，完成一次订阅协商。</summary>
    public async Task RenegotiateAsync(Guid me, Guid callId, SessionDescription answer, CancellationToken ct, Guid? participantId = null)
    {
        ValidateSdp(answer, "answer");
        var p = await MyParticipantAsync(me, callId, participantId, ct);
        if (p.CfPullSessionId is not { } sessionId) throw ApiException.Conflict("还没有订阅会话");
        await CfAsync("协商", async () => { await realtime.RenegotiateAsync(sessionId, answer, ct); return true; });
    }

    /// <summary>不再接收某些轨道（对方取消发布 / 离开后）：强制关闭，不需要协商。</summary>
    public async Task ClosePulledAsync(Guid me, Guid callId, ClosePulledRequest req, CancellationToken ct, Guid? participantId = null)
    {
        if (req.Mids is not { Count: >= 1 and <= 64 }) return;
        var p = await MyParticipantAsync(me, callId, participantId, ct);
        if (p.CfPullSessionId is null) return;
        // 浏览器自己要求不再接收：这条订阅会话还在用，关闭要排在它之后的订阅变更前面（Cloudflare 要求同一会话上的变更串行），
        // 所以这里同步关——但不在通话锁里，超时也短
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(CallCleanupWorker.CloseTimeout);
            await realtime.CloseTracksAsync(p.CfPullSessionId, req.Mids, null, timeout.Token);
        }
        catch (Exception ex) when (ex is CloudflareRealtimeException or HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogInformation("关闭订阅的轨道没有成功（忽略，Cloudflare 会自行回收）：{Message}", ex.Message);
        }
        var mids = req.Mids.ToArray();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE call_participants SET pulled_mids = ARRAY(SELECT unnest(pulled_mids) EXCEPT SELECT unnest({mids})) WHERE id = {p.Id}", ct);
    }

    public async Task SetStateAsync(Guid me, Guid callId, ParticipantStateRequest req, CancellationToken ct, Guid? participantId = null)
    {
        var p = await MyParticipantAsync(me, callId, participantId, ct);
        if (p.MicOn == req.MicOn && p.CamOn == req.CamOn) return;
        p.MicOn = req.MicOn;
        p.CamOn = req.CamOn;
        await db.SaveChangesAsync(ct);
        var call = await db.CallSessions.AsNoTracking().FirstAsync(c => c.Id == callId, ct);
        await (await AudienceAsync(call, ct)).SendAsync(ChatEvents.ParticipantUpdated, new ParticipantEventDto(callId, ParticipantDto.From(p)), ct);
    }

    /// <summary>
    /// 心跳。带 participantId 时只认这台设备：已经不在通话里返回 410——被别的设备顶掉时 detail 为 replaced（客户端收场），
    /// 否则（超时被清理）客户端重新加入。不带时按人算（旧客户端）。
    /// </summary>
    public async Task HeartbeatAsync(Guid me, Guid callId, CancellationToken ct, Guid? participantId = null)
    {
        var now = DateTimeOffset.UtcNow;
        var rows = await db.CallParticipants
            .Where(p => p.CallId == callId && p.UserId == me && p.LeftAt == null && (participantId == null || p.Id == participantId))
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.LastSeenAt, now), ct);
        if (rows == 0) throw await NotInCallAsync(me, callId, participantId, ct);
    }

    // ---------------------------------------------------------------- 超时清理

    /// <summary>
    /// 由后台任务每几秒调用：响铃超时记未接；心跳断了的参与者按离开处理。返回处理了几通通话。
    /// healthySince：服务从什么时候起一直正常（刚启动、刚从数据库故障或主机休眠里恢复时）。在那之前心跳本来就送不进来，
    /// 不能拿它判掉线——浏览器和 Cloudflare 之间的媒体并不依赖这里，通话其实好好的。
    /// </summary>
    public async Task<int> SweepAsync(DateTimeOffset now, CancellationToken ct, DateTimeOffset? healthySince = null)
    {
        var ringCutoff = now - RingTimeout;
        var staleCutoff = now - StaleAfter;
        var checkStale = healthySince is not { } since || now - since >= StaleAfter;
        await PruneRoomHistoryAsync(now, ct);
        var ids = await db.CallSessions.AsNoTracking()
            .Where(c => c.EndedAt == null && (
                (c.RoomSlug == null && c.AnsweredAt == null && c.StartedAt < ringCutoff) ||
                (checkStale && c.Participants.Any(p => p.LeftAt == null && p.LastSeenAt < staleCutoff))))
            .Select(c => c.Id).ToListAsync(ct);

        var handled = 0;
        foreach (var id in ids)
        {
            try
            {
                using var _ = await locks.AcquireAsync(id, ct);
                db.ChangeTracker.Clear();
                var call = await Query().FirstOrDefaultAsync(c => c.Id == id, ct);
                if (call is null || call.EndedAt is not null) continue;
                if (call.RoomSlug is null && call.AnsweredAt is null && call.StartedAt < ringCutoff)
                {
                    await EndCoreAsync(call, CallEndReason.Missed, now, ct);
                    handled++;
                    continue;
                }
                if (!checkStale) continue;
                var stale = call.Participants.Where(p => p.LeftAt == null && p.LastSeenAt < staleCutoff).ToList();
                if (stale.Count == 0) continue;
                await LeaveCoreAsync(call, stale, "timeout", now, ct);
                handled++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "清理通话 {CallId} 失败", id);
            }
        }
        return handled;
    }

    /// <summary>
    /// 会议室永不结束，每次进出都留一行参与者：离开超过一小时的删掉（轨道随之级联删除），
    /// 免得每次进出客厅都要把几个月的历史加载进来。
    /// </summary>
    private Task PruneRoomHistoryAsync(DateTimeOffset now, CancellationToken ct)
    {
        var cutoff = now - TimeSpan.FromHours(1);
        return db.CallParticipants
            .Where(p => p.LeftAt != null && p.LeftAt < cutoff && db.CallSessions.Any(c => c.Id == p.CallId && c.RoomSlug != null))
            .ExecuteDeleteAsync(ct);
    }

    // ---------------------------------------------------------------- 内部

    private IQueryable<CallSession> Query() => db.CallSessions.Include(c => c.Participants).ThenInclude(p => p.Tracks).AsSplitQuery();

    private static ApiException Gone(string title) => new(410, title);
    private static ApiException NotConfigured() => new(503, "通话服务未配置", "管理员需要在部署配置里填写 Cloudflare Realtime 的 App ID 与 App Secret");

    private static int ActiveUsers(CallSession call) => call.Participants.Where(p => p.LeftAt == null).Select(p => p.UserId).Distinct().Count();

    private static void ValidateSdp(SessionDescription? sd, string type)
    {
        if (sd is null || sd.Type != type || string.IsNullOrWhiteSpace(sd.Sdp)) throw ApiException.BadRequest($"需要 SDP {type}");
        if (sd.Sdp.Length > MaxSdpLength) throw ApiException.BadRequest("SDP 过大");
    }

    private async Task EnsureAccessAsync(CallSession call, Guid me, CancellationToken ct)
    {
        // 会议室全家可进；会话里的通话只有会话成员能看、能进
        if (call.ConversationId is { } cid && !await db.ConversationMembers.AnyAsync(m => m.ConversationId == cid && m.UserId == me, ct))
            throw ApiException.Forbidden("不是该会话成员");
    }

    private async Task<bool> IsDirectAsync(CallSession call, CancellationToken ct)
        => call.ConversationId is { } cid && await db.Conversations.AnyAsync(c => c.Id == cid && c.Type == ConversationType.Direct, ct);

    /// <summary>事件发给谁：会话里的通话发给全体会话成员（没进通话的也要看到"通话进行中"）；会议室发给全家。</summary>
    private async Task<IClientProxy> AudienceAsync(CallSession call, CancellationToken ct)
    {
        if (call.ConversationId is not { } cid) return hub.Clients.All;
        var members = await db.ConversationMembers.Where(m => m.ConversationId == cid).Select(m => m.UserId).ToListAsync(ct);
        return hub.Clients.Groups(members.Select(ChatEvents.UserGroup).ToList());
    }

    /// <summary>
    /// 这台设备在通话里的身份。带 participantId 时严格按它找（旧设备的迟到请求不会落到新设备的会话上）；
    /// 不带时取这个人最新的一个（旧客户端）。
    /// </summary>
    private async Task<CallParticipant> MyParticipantAsync(Guid me, Guid callId, Guid? participantId, CancellationToken ct)
    {
        var q = db.CallParticipants.Include(p => p.Tracks).Where(p => p.CallId == callId && p.UserId == me && p.LeftAt == null);
        var p = participantId is { } pid
            ? await q.FirstOrDefaultAsync(x => x.Id == pid, ct)
            : await q.OrderByDescending(x => x.JoinedAt).FirstOrDefaultAsync(ct);
        return p ?? throw await NotInCallAsync(me, callId, participantId, ct);
    }

    private async Task<ApiException> NotInCallAsync(Guid me, Guid callId, Guid? participantId, CancellationToken ct)
    {
        if (participantId is not { } pid) return Gone("已不在通话中");
        var mine = await db.CallParticipants.AsNoTracking().Where(x => x.CallId == callId && x.UserId == me).ToListAsync(ct);
        var old = mine.FirstOrDefault(x => x.Id == pid);
        return old is not null && ElsewhereSince(mine, me, old) ? ReplacedElsewhere() : Gone("已不在通话中");
    }

    /// <summary>
    /// 同一个人在别的设备上：还在通话里，或者在 old 这一份掉线之后进来过（换到那台设备上、又在那边挂了）。
    /// 这两种情况下 old 那台设备都不能自己回到通话里。例外：这台设备上一次重连（响应丢了）留下的那一份——它的 ReplacesId 就是 old。
    /// 顶替时新一份的 JoinedAt 与旧一份的 LeftAt 是同一个时刻，所以用 &gt;=。
    /// </summary>
    private static bool ElsewhereSince(IEnumerable<CallParticipant> participants, Guid me, CallParticipant old)
        => participants.Any(p => p.UserId == me && p.Id != old.Id && p.ReplacesId != old.Id
            && (p.LeftAt == null || (old.LeftAt is { } left && p.JoinedAt >= left)));

    private static ApiException ReplacedElsewhere() => new(410, "已在其他设备上加入通话", ReplacedDetail);

    /// <summary>Cloudflare 的会话要等浏览器准备好协商时再建（没用过的会话会过期），所以是第一次发布 / 订阅时才建。</summary>
    private async Task<string> EnsureSessionAsync(CallParticipant p, bool pull, CancellationToken ct)
    {
        var existing = pull ? p.CfPullSessionId : p.CfSessionId;
        if (existing is not null) return existing;
        var id = await CfAsync("建立会话", () => realtime.NewSessionAsync(ct));
        if (pull) p.CfPullSessionId = id;
        else p.CfSessionId = id;
        await db.SaveChangesAsync(ct);
        return id;
    }

    private async Task<T> CfAsync<T>(string what, Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (CloudflareRealtimeException ex)
        {
            logger.LogWarning("Cloudflare Realtime {What}失败：{Status} {Code} {Description}", what, ex.Status, ex.ErrorCode, ex.ErrorDescription);
            // 会话没了：客户端重新加入（换新会话）才能恢复
            if (ex.SessionGone) throw Gone("通话连接已失效");
            throw new ApiException(502, $"{what}失败", ex.ErrorCode);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning("Cloudflare Realtime {What}失败：{Message}", what, ex.Message);
            throw new ApiException(502, $"{what}失败", "连不上 Cloudflare Realtime");
        }
    }

    /// <summary>
    /// 让 Cloudflare 停止转发这些轨道：只入队，后台去关（见 CallCleanupQueue）。不能在通话锁里同步等——
    /// 关闭刚断开的连接上的轨道有时要等好几秒，期间同一通通话里别人的加入、离开都会被拖住。
    /// </summary>
    private void Cleanup(List<CallCleanup> items) => cleanupQueue.Enqueue(items);

    /// <summary>标记离开：轨道一并下线，用量记账，Cloudflare 侧待关闭的轨道记进 cleanup。</summary>
    private static void MarkLeft(CallSession call, CallParticipant p, DateTimeOffset now, List<CallCleanup> cleanup)
    {
        if (p.LeftAt is not null) return;
        var open = p.Tracks.Where(t => t.UnpublishedAt == null).ToList();
        foreach (var t in open)
        {
            AccrueEgress(call, p, t, now);
            t.UnpublishedAt = now;
        }
        p.LeftAt = now;
        if (p.CfSessionId is not null && open.Count > 0) cleanup.Add(new CallCleanup(p.CfSessionId, open.Select(t => t.Mid).ToList()));
        // 订阅会话里拉着的别人的轨道也要关：被顶掉 / 超时的设备如果还连着，否则会一直收到大家的音视频
        if (p.CfPullSessionId is not null && p.PulledMids.Count > 0) cleanup.Add(new CallCleanup(p.CfPullSessionId, p.PulledMids.Distinct().ToList()));
    }

    /// <summary>
    /// 出站流量估算：一条轨道下线时，按"时长 × 码率 × 当时在场的其他人数"累加。
    /// 只是量级估计（Cloudflare 按出站字节计费，前 1 TB 免费），实际以 Cloudflare 控制台为准。
    /// </summary>
    private static void AccrueEgress(CallSession call, CallParticipant owner, CallTrack track, DateTimeOffset now)
    {
        // 同一次操作里一起离开的人（LeftAt == now）在这条轨道的整个时长里都在接收
        var receivers = call.Participants.Where(p => (p.LeftAt == null || p.LeftAt >= now) && p.UserId != owner.UserId).Select(p => p.UserId).Distinct().Count();
        var seconds = Math.Max(0, (now - track.PublishedAt).TotalSeconds);
        var bitsPerSecond = track.Kind switch { TrackKind.Audio => 32_000, TrackKind.Video => 1_000_000, _ => 1_200_000 };
        call.EgressBytesEstimate += (long)(seconds * bitsPerSecond / 8 * receivers);
    }

    private async Task LeaveCoreAsync(CallSession call, List<CallParticipant> leaving, string reason, DateTimeOffset now, CancellationToken ct)
    {
        var cleanup = new List<CallCleanup>();
        foreach (var p in leaving) MarkLeft(call, p, now, cleanup);

        if (call.RoomSlug is null)
        {
            var active = ActiveUsers(call);
            CallEndReason? end = null;
            if (call.AnsweredAt is null) end = active == 0 ? CallEndReason.Cancelled : null;
            // 单聊里一方挂断就结束；群聊等最后一个人离开
            else if (await IsDirectAsync(call, ct) ? active < 2 : active == 0) end = CallEndReason.Completed;
            if (end is { } r)
            {
                await EndCoreAsync(call, r, now, ct, cleanup);
                return;
            }
        }

        await db.SaveChangesAsync(ct);
        var audience = await AudienceAsync(call, ct);
        foreach (var p in leaving)
            await audience.SendAsync(ChatEvents.ParticipantLeft, new ParticipantLeftDto(call.Id, p.Id, p.UserId, reason), ct);
        Cleanup(cleanup);
    }

    /// <summary>结束通话：所有人离开、写通话记录消息、广播、未接时给没接的人补一条"未接来电"推送。</summary>
    private async Task EndCoreAsync(CallSession call, CallEndReason reason, DateTimeOffset now, CancellationToken ct, List<CallCleanup>? cleanup = null)
    {
        cleanup ??= [];
        foreach (var p in call.Participants.Where(p => p.LeftAt == null).ToList()) MarkLeft(call, p, now, cleanup);
        call.EndedAt = now;
        call.EndReason = reason;
        await db.SaveChangesAsync(ct);

        var audience = await AudienceAsync(call, ct);
        var dto = CallDto.From(call);
        await audience.SendAsync(ChatEvents.CallUpdated, dto, ct);

        if (call.ConversationId is { } cid)
        {
            var joined = dto.JoinedUserIds;
            var duration = call.AnsweredAt is { } answered ? Math.Max(0, (int)(now - answered).TotalSeconds) : 0;
            var payload = JsonSerializer.Serialize(new CallMessagePayload(call.Id, call.Type, reason, duration, joined), PayloadJson);
            var (message, created) = await messages.AddServerMessageAsync(cid, call.StartedBy, call.Id, MessageType.Call, CallPush.Summary(call.Type, reason, duration), payload, ct);
            if (created)
            {
                // 按用户组发（不走会话组）：刚建的单聊里，对方的连接可能还没进会话组
                await audience.SendAsync(ChatEvents.MessageReceived, MessageDto.From(message), ct);
                // 参加了通话的人不算未读：之前都读完了的，已读位置顺延到这条记录
                var caughtUp = await db.ConversationMembers
                    .Where(m => m.ConversationId == cid && joined.Contains(m.UserId) && m.LastReadSeq == message.Seq - 1)
                    .Select(m => m.UserId).ToListAsync(ct);
                if (caughtUp.Count > 0)
                {
                    await db.ConversationMembers
                        .Where(m => m.ConversationId == cid && caughtUp.Contains(m.UserId) && m.LastReadSeq == message.Seq - 1)
                        .ExecuteUpdateAsync(s => s.SetProperty(m => m.LastReadSeq, message.Seq), ct);
                    foreach (var userId in caughtUp)
                        await hub.Clients.Group(ChatEvents.UserGroup(userId)).SendAsync(ChatEvents.ReadReceipt, new ReadReceiptDto(cid, userId, message.Seq), ct);
                }
            }

            // 响过铃却没进来的人：未接 / 取消换成"未接来电"；接通后结束、被拒接时把还挂着的响铃通知收掉
            var rung = await db.ConversationMembers
                .Where(m => m.ConversationId == cid && m.UserId != call.StartedBy && !joined.Contains(m.UserId) && (m.MutedUntil == null || m.MutedUntil < now))
                .Select(m => m.UserId).ToListAsync(ct);
            if (rung.Count > 0)
            {
                if (reason is CallEndReason.Missed or CallEndReason.Cancelled)
                {
                    var conv = await db.Conversations.AsNoTracking().FirstAsync(c => c.Id == cid, ct);
                    var caller = await db.Users.AsNoTracking().FirstAsync(u => u.Id == call.StartedBy, ct);
                    push.SendToUsersInBackground(rung, CallPush.Missed(call, conv, caller.DisplayName), PushSender.DefaultTtlSeconds);
                }
                else
                {
                    push.SendToUsersInBackground(rung, CallPush.Closed(call, cid, "通话已结束"), CallPush.RingTtlSeconds);
                }
            }
        }
        Cleanup(cleanup);
    }
}

/// <summary>来电相关推送与通话记录的文案。</summary>
public static class CallPush
{
    /// <summary>响铃推送只保留这么久：过了这个时间再送到，电话早没人等了。</summary>
    public const int RingTtlSeconds = 45;

    public static string Tag(Guid callId) => $"call:{callId}";

    private static string Kind(CallType type) => type == CallType.Audio ? "语音" : "视频";

    private static string Title(Conversation conv, string callerName)
        => conv.Type == ConversationType.Direct || string.IsNullOrWhiteSpace(conv.Title) ? callerName : $"{callerName} · {conv.Title}";

    public static PushPayload Ringing(CallSession call, Conversation conv, string callerName)
        => new(Title(conv, callerName), $"邀请你{Kind(call.Type)}通话", $"/messages/{conv.Id}?call={call.Id}", Tag(call.Id), Kind: PushKinds.Call, CallId: call.Id);

    public static PushPayload Missed(CallSession call, Conversation conv, string callerName)
        => new(Title(conv, callerName), $"未接{Kind(call.Type)}来电", $"/messages/{conv.Id}", Tag(call.Id), Kind: PushKinds.CallMissed, CallId: call.Id);

    public static PushPayload Answered(CallSession call, Guid conversationId) => Closed(call, conversationId, "已在其他设备接听");

    /// <summary>把这通通话还挂着的响铃通知收掉（同一个 tag 原地替换后自动关闭）。</summary>
    public static PushPayload Closed(CallSession call, Guid conversationId, string body)
        => new("PrivateChat", body, $"/messages/{conversationId}", Tag(call.Id), Kind: PushKinds.CallAnswered, CallId: call.Id);

    /// <summary>通话记录消息的正文（不认识 payload 的客户端、会话列表摘要以外的地方显示它）。</summary>
    public static string Summary(CallType type, CallEndReason reason, int durationSeconds) => reason switch
    {
        CallEndReason.Completed => $"{Kind(type)}通话 {Duration(durationSeconds)}",
        CallEndReason.Missed => $"未接听的{Kind(type)}通话",
        CallEndReason.Declined => $"已拒绝的{Kind(type)}通话",
        _ => $"已取消的{Kind(type)}通话",
    };

    public static string Duration(int seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";
    }
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using PrivateChat.Api.Domain;
using PrivateChat.Api.Features.Auth;
using PrivateChat.Api.Features.Chat;
using PrivateChat.Api.Infrastructure;

namespace PrivateChat.Api.Features.Calls;

public sealed record CreateCallRequest(Guid? ConversationId, CallType Type, string? RoomSlug = null);
public sealed record CallDto(Guid Id, Guid? ConversationId, CallType Type, string? RoomSlug, Guid StartedBy, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, List<ParticipantDto> Participants)
{
    public static CallDto From(CallSession c) => new(c.Id, c.ConversationId, c.Type, c.RoomSlug, c.StartedBy, c.StartedAt, c.EndedAt,
        c.Participants.Where(p => p.LeftAt == null).Select(ParticipantDto.From).ToList());
}
public sealed record ParticipantDto(Guid Id, Guid UserId, string? CfSessionId, List<TrackDto> Tracks)
{
    public static ParticipantDto From(CallParticipant p) => new(p.Id, p.UserId, p.CfSessionId,
        p.Tracks.Where(t => t.UnpublishedAt == null).Select(t => new TrackDto(t.TrackName, t.Kind)).ToList());
}
public sealed record TrackDto(string TrackName, TrackKind Kind);
public sealed record JoinResponse(Guid ParticipantId, string CfSessionId, IceServers IceServers, CallDto Call);
public sealed record PublishTracksRequest(SessionDescription Offer, List<PublishTrackInput> Tracks);
public sealed record PublishTrackInput(string Mid, string TrackName, TrackKind Kind);
public sealed record PullTracksRequest(List<PullTrackInput> Tracks);
public sealed record PullTrackInput(string CfSessionId, string TrackName);
public sealed record RenegotiateApiRequest(SessionDescription Answer);
public sealed record TrackEventDto(Guid CallId, Guid ParticipantId, string CfSessionId, string TrackName, TrackKind Kind);

/// <summary>
/// 通话编排：Cloudflare Realtime 没有房间概念，这里维护 call_sessions / call_participants / call_tracks 并经 SignalR 广播。
/// 浏览器永远不拿 App Secret，所有 Cloudflare 调用在此转发。
/// </summary>
[ApiController]
[Route("api/calls")]
[Authorize]
public sealed class CallsController(AppDbContext db, CloudflareRealtimeClient realtime, IHubContext<ChatHub> hub) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<CallDto>>> List([FromQuery] Guid? conversationId, CancellationToken ct)
    {
        var q = db.CallSessions.Include(c => c.Participants).ThenInclude(p => p.Tracks).AsQueryable();
        if (conversationId is { } cid) q = q.Where(c => c.ConversationId == cid);
        var list = await q.OrderByDescending(c => c.StartedAt).Take(50).ToListAsync(ct);
        return list.Select(CallDto.From).ToList();
    }

    /// <summary>发起通话（或进入常驻会议室：带 RoomSlug 且已存在则直接返回）。</summary>
    [HttpPost]
    [Authorize(Policy = AuthPolicies.Member)]
    public async Task<ActionResult<CallDto>> Create([FromBody] CreateCallRequest req, CancellationToken ct)
    {
        var me = User.UserId();
        if (req.RoomSlug is { } slug)
        {
            var room = await db.CallSessions.Include(c => c.Participants).ThenInclude(p => p.Tracks)
                .FirstOrDefaultAsync(c => c.RoomSlug == slug && c.EndedAt == null, ct);
            if (room is not null) return CallDto.From(room);
        }
        if (req.ConversationId is { } convId && !await db.ConversationMembers.AnyAsync(m => m.ConversationId == convId && m.UserId == me, ct))
            throw ApiException.Forbidden("不是该会话成员");

        var call = new CallSession { ConversationId = req.ConversationId, Type = req.Type, RoomSlug = req.RoomSlug, StartedBy = me };
        db.CallSessions.Add(call);
        await db.SaveChangesAsync(ct);

        var dto = CallDto.From(call);
        if (req.ConversationId is { } cid2)
            await hub.Clients.Group(ChatEvents.ConversationGroup(cid2)).SendAsync(ChatEvents.IncomingCall, dto, ct);
        // TODO: 离线成员 Web Push（带接听 / 拒绝 actions），60 s 无人接听标记 Missed
        return dto;
    }

    /// <summary>加入：为参与者建一个 Cloudflare 会话，并返回 TURN 凭据。</summary>
    [HttpPost("{id:guid}/join")]
    public async Task<ActionResult<JoinResponse>> Join(Guid id, CancellationToken ct)
    {
        var call = await LoadActiveAsync(id, ct);
        if (!realtime.IsConfigured) return StatusCode(501, new ProblemDetails { Title = "未配置 Cloudflare Realtime" });

        var cfSessionId = await realtime.NewSessionAsync(ct);
        var turn = await realtime.GenerateTurnCredentialsAsync(ct);

        var participant = new CallParticipant { CallId = call.Id, UserId = User.UserId(), CfSessionId = cfSessionId };
        db.CallParticipants.Add(participant);
        await db.SaveChangesAsync(ct);
        call.Participants.Add(participant);

        await BroadcastAsync(call, ChatEvents.ParticipantJoined, new { callId = call.Id, participant = ParticipantDto.From(participant) }, ct);
        return new JoinResponse(participant.Id, cfSessionId, turn.IceServers, CallDto.From(call));
    }

    /// <summary>发布本端轨道：offer → Cloudflare → answer，写 call_tracks 并广播 TrackPublished。</summary>
    [HttpPost("{id:guid}/tracks")]
    public async Task<ActionResult<SessionDescription>> Publish(Guid id, [FromBody] PublishTracksRequest req, CancellationToken ct)
    {
        var (call, me) = await LoadMineAsync(id, ct);
        var cf = await realtime.AddTracksAsync(me.CfSessionId!, new TracksRequest(
            req.Tracks.Select(t => new TrackObject("local", t.TrackName, Mid: t.Mid)).ToList(), req.Offer), ct);
        if (cf.ErrorCode is not null) throw new ApiException(502, "发布轨道失败", cf.ErrorDescription);

        foreach (var t in req.Tracks)
        {
            var track = new CallTrack { ParticipantId = me.Id, TrackName = t.TrackName, Kind = t.Kind };
            db.CallTracks.Add(track);
            me.Tracks.Add(track);
        }
        await db.SaveChangesAsync(ct);
        foreach (var t in req.Tracks)
            await BroadcastAsync(call, ChatEvents.TrackPublished, new TrackEventDto(call.Id, me.Id, me.CfSessionId!, t.TrackName, t.Kind), ct);

        return cf.SessionDescription ?? throw new ApiException(502, "Cloudflare 未返回 answer");
    }

    /// <summary>订阅他人轨道：Cloudflare 返回带新轨道的 offer，前端回 answer 后调 renegotiate。</summary>
    [HttpPost("{id:guid}/tracks/pull")]
    public async Task<ActionResult<TracksResponse>> Pull(Guid id, [FromBody] PullTracksRequest req, CancellationToken ct)
    {
        var (_, me) = await LoadMineAsync(id, ct);
        var cf = await realtime.AddTracksAsync(me.CfSessionId!, new TracksRequest(
            req.Tracks.Select(t => new TrackObject("remote", t.TrackName, SessionId: t.CfSessionId)).ToList()), ct);
        if (cf.ErrorCode is not null) throw new ApiException(502, "订阅轨道失败", cf.ErrorDescription);
        return cf;
    }

    [HttpPost("{id:guid}/renegotiate")]
    public async Task<IActionResult> Renegotiate(Guid id, [FromBody] RenegotiateApiRequest req, CancellationToken ct)
    {
        var (_, me) = await LoadMineAsync(id, ct);
        await realtime.RenegotiateAsync(me.CfSessionId!, req.Answer, ct);
        return NoContent();
    }

    /// <summary>离开；最后一人离开则结束通话（常驻会议室除外）。</summary>
    [HttpPost("{id:guid}/leave")]
    public async Task<IActionResult> Leave(Guid id, CancellationToken ct)
    {
        var (call, me) = await LoadMineAsync(id, ct);
        me.LeftAt = DateTimeOffset.UtcNow;
        foreach (var t in me.Tracks.Where(t => t.UnpublishedAt == null)) t.UnpublishedAt = me.LeftAt;
        if (call.RoomSlug is null && call.Participants.All(p => p.LeftAt != null)) call.EndedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await BroadcastAsync(call, ChatEvents.ParticipantLeft, new { callId = call.Id, participantId = me.Id, userId = me.UserId }, ct);
        if (call.EndedAt is not null) await BroadcastAsync(call, ChatEvents.CallUpdated, CallDto.From(call), ct);
        // TODO: 通话结束时在会话里插入一条 Call 类型消息
        return NoContent();
    }

    [HttpPost("{id:guid}/end")]
    [Authorize(Policy = AuthPolicies.Member)]
    public async Task<IActionResult> End(Guid id, CancellationToken ct)
    {
        var call = await LoadActiveAsync(id, ct);
        if (call.StartedBy != User.UserId() && User.Role() != UserRole.Admin) throw ApiException.Forbidden();
        call.EndedAt = DateTimeOffset.UtcNow;
        foreach (var p in call.Participants.Where(p => p.LeftAt == null)) p.LeftAt = call.EndedAt;
        await db.SaveChangesAsync(ct);
        await BroadcastAsync(call, ChatEvents.CallUpdated, CallDto.From(call), ct);
        return NoContent();
    }

    private async Task<CallSession> LoadActiveAsync(Guid id, CancellationToken ct)
    {
        var call = await db.CallSessions.Include(c => c.Participants).ThenInclude(p => p.Tracks)
            .FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw ApiException.NotFound("通话");
        if (call.EndedAt is not null) throw ApiException.Conflict("通话已结束");
        if (call.ConversationId is { } cid && !await db.ConversationMembers.AnyAsync(m => m.ConversationId == cid && m.UserId == User.UserId(), ct))
            throw ApiException.Forbidden("不是该会话成员");
        return call;
    }

    private async Task<(CallSession Call, CallParticipant Me)> LoadMineAsync(Guid id, CancellationToken ct)
    {
        var call = await LoadActiveAsync(id, ct);
        var me = call.Participants.Where(p => p.UserId == User.UserId() && p.LeftAt == null).OrderByDescending(p => p.JoinedAt).FirstOrDefault()
                 ?? throw ApiException.Conflict("尚未加入该通话");
        return (call, me);
    }

    private async Task BroadcastAsync(CallSession call, string evt, object payload, CancellationToken ct)
    {
        var userIds = call.ConversationId is { } cid
            ? await db.ConversationMembers.Where(m => m.ConversationId == cid).Select(m => m.UserId).ToListAsync(ct)
            : call.Participants.Select(p => p.UserId).Distinct().ToList();
        foreach (var uid in userIds)
            await hub.Clients.Group(ChatEvents.UserGroup(uid)).SendAsync(evt, payload, ct);
    }
}

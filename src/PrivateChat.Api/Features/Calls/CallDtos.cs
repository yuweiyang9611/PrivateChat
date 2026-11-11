using PrivateChat.Api.Domain;

namespace PrivateChat.Api.Features.Calls;

public sealed record CallConfigDto(bool Enabled, int RingSeconds, int HeartbeatSeconds);

public sealed record StartCallRequest(Guid ConversationId, CallType Type);

public sealed record TrackDto(string TrackName, TrackKind Kind);

public sealed record ParticipantDto(Guid Id, Guid UserId, bool MicOn, bool CamOn, DateTimeOffset JoinedAt, List<TrackDto> Tracks)
{
    public static ParticipantDto From(CallParticipant p) => new(p.Id, p.UserId, p.MicOn, p.CamOn, p.JoinedAt,
        p.Tracks.Where(t => t.UnpublishedAt == null).OrderBy(t => t.PublishedAt).ThenBy(t => t.Kind).Select(t => new TrackDto(t.TrackName, t.Kind)).ToList());
}

/// <summary>
/// 一通通话的快照。Participants 只含还在通话里的人；JoinedUserIds 是所有进来过的人（通话记录用）。
/// AnsweredAt 为空且未结束 = 响铃中。Cloudflare 会话 Id 不下发，订阅时用参与者 Id + 轨道名。
/// </summary>
public sealed record CallDto(
    Guid Id, Guid? ConversationId, CallType Type, string? RoomSlug, Guid StartedBy,
    DateTimeOffset StartedAt, DateTimeOffset? AnsweredAt, DateTimeOffset? EndedAt, CallEndReason? EndReason,
    List<ParticipantDto> Participants, List<Guid> JoinedUserIds)
{
    public static CallDto From(CallSession c) => new(
        c.Id, c.ConversationId, c.Type, c.RoomSlug, c.StartedBy, c.StartedAt, c.AnsweredAt, c.EndedAt, c.EndReason,
        c.Participants.Where(p => p.LeftAt == null).OrderBy(p => p.JoinedAt).Select(ParticipantDto.From).ToList(),
        c.Participants.OrderBy(p => p.JoinedAt).Select(p => p.UserId).Distinct().ToList());
}

public sealed record JoinResponse(Guid ParticipantId, List<IceServer> IceServers, CallDto Call);

public sealed record PublishTrackInput(string Mid, TrackKind Kind);
public sealed record PublishRequest(SessionDescription Offer, List<PublishTrackInput> Tracks);
public sealed record PublishedTrackDto(string Mid, string TrackName, TrackKind Kind);
public sealed record PublishResponse(SessionDescription Answer, List<PublishedTrackDto> Tracks);

/// <summary>取消发布。带 Offer 走协商式关闭（返回 answer）；不带则强制关闭。</summary>
public sealed record UnpublishRequest(List<string> TrackNames, SessionDescription? Offer = null);
public sealed record UnpublishResponse(SessionDescription? Answer);

public sealed record PullTrackInput(Guid ParticipantId, string TrackName);
public sealed record PullRequest(List<PullTrackInput> Tracks);
/// <summary>Mid 为空表示这一条没拉到（ErrorCode 说明原因，例如对方还没开始发媒体），可以稍后重试。</summary>
public sealed record PulledTrackDto(Guid ParticipantId, string TrackName, TrackKind Kind, string? Mid, string? ErrorCode);
public sealed record PullResponse(bool RequiresImmediateRenegotiation, SessionDescription? Offer, List<PulledTrackDto> Tracks);

public sealed record RenegotiateApiRequest(SessionDescription Answer);
public sealed record ClosePulledRequest(List<string> Mids);
public sealed record ParticipantStateRequest(bool MicOn, bool CamOn);

// ---- SignalR 事件载荷 ----
public sealed record ParticipantEventDto(Guid CallId, ParticipantDto Participant);
public sealed record ParticipantLeftDto(Guid CallId, Guid ParticipantId, Guid UserId, string Reason);
public sealed record TrackEventDto(Guid CallId, Guid ParticipantId, string TrackName, TrackKind Kind);
public sealed record CallDeclinedDto(Guid CallId, Guid UserId);

/// <summary>通话记录消息（messages.payload）的结构，前端按它渲染。</summary>
public sealed record CallMessagePayload(Guid CallId, CallType CallType, CallEndReason Reason, int DurationSeconds, List<Guid> ParticipantIds);

public sealed record CallUsageDto(DateTimeOffset Since, int Calls, long Seconds, long EgressBytesEstimate);

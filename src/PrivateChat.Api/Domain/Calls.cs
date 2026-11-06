namespace PrivateChat.Api.Domain;

public enum CallType { Audio, Video, Meeting }
public enum TrackKind { Audio, Video, Screen }

/// <summary>一次通话或一个常驻会议室。Cloudflare Realtime 没有房间概念，房间状态由这三张表维护。</summary>
public class CallSession
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid? ConversationId { get; set; }
    public CallType Type { get; set; }
    /// <summary>常驻会议室的 slug（如 living-room）；普通通话为空。</summary>
    public string? RoomSlug { get; set; }
    public Guid StartedBy { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? EndedAt { get; set; }
    public DateTimeOffset? ScheduledAt { get; set; }
    public long EgressBytesEstimate { get; set; }
    public List<CallParticipant> Participants { get; set; } = [];
}

public class CallParticipant
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid CallId { get; set; }
    public Guid UserId { get; set; }
    /// <summary>Cloudflare Realtime 会话 Id（一个 PeerConnection）。</summary>
    public string? CfSessionId { get; set; }
    public DateTimeOffset JoinedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LeftAt { get; set; }
    public List<CallTrack> Tracks { get; set; } = [];
}

public class CallTrack
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid ParticipantId { get; set; }
    public string TrackName { get; set; } = "";
    public TrackKind Kind { get; set; }
    public DateTimeOffset PublishedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UnpublishedAt { get; set; }
}

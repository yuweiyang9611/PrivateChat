namespace PrivateChat.Api.Domain;

public enum CallType { Audio, Video, Meeting }
public enum TrackKind { Audio, Video, Screen }

/// <summary>通话怎么结束的；写进通话记录消息。</summary>
public enum CallEndReason
{
    /// <summary>接通后正常挂断。</summary>
    Completed,
    /// <summary>响铃超时无人接听。</summary>
    Missed,
    /// <summary>单聊里对方拒接。</summary>
    Declined,
    /// <summary>接通前发起人自己挂断。</summary>
    Cancelled,
}

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
    /// <summary>第二个人加入的时刻；为空表示还在响铃。</summary>
    public DateTimeOffset? AnsweredAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public CallEndReason? EndReason { get; set; }
    public DateTimeOffset? ScheduledAt { get; set; }
    public long EgressBytesEstimate { get; set; }
    public List<CallParticipant> Participants { get; set; } = [];
}

public class CallParticipant
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid CallId { get; set; }
    public Guid UserId { get; set; }
    /// <summary>发布用的 Cloudflare Realtime 会话 Id（一个 PeerConnection）；第一次发布时才创建。</summary>
    public string? CfSessionId { get; set; }
    /// <summary>订阅用的 Cloudflare Realtime 会话 Id；第一次订阅时才创建。</summary>
    public string? CfPullSessionId { get; set; }
    /// <summary>订阅会话里正在接收的 mid：离开 / 被顶掉 / 超时时据此让 Cloudflare 停止转发（光从状态里删掉并不会停）。</summary>
    public List<string> PulledMids { get; set; } = [];
    /// <summary>这一份是重连时为顶替哪个旧身份而建的（join?replacing=）。重连响应丢了再重试时，据此认出"这是我自己刚才那次"，不当成别的设备。</summary>
    public Guid? ReplacesId { get; set; }
    public bool MicOn { get; set; } = true;
    public bool CamOn { get; set; }
    public DateTimeOffset JoinedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>最近一次心跳；超过阈值没有心跳视为掉线。</summary>
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LeftAt { get; set; }
    public List<CallTrack> Tracks { get; set; } = [];
}

public class CallTrack
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid ParticipantId { get; set; }
    public string TrackName { get; set; } = "";
    public TrackKind Kind { get; set; }
    /// <summary>发布会话里这条轨道的 mid，强制关闭时要用。</summary>
    public string Mid { get; set; } = "";
    public DateTimeOffset PublishedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UnpublishedAt { get; set; }
}

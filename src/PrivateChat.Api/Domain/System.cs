using PrivateChat.Contracts.Remote;

namespace PrivateChat.Api.Domain;

public class RemoteSession
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid ControllerUserId { get; set; }
    public Guid TargetDeviceId { get; set; }
    public RemoteSessionMode Mode { get; set; }
    public RemoteConsentType Consent { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? EndedAt { get; set; }
    public string? EndReason { get; set; }
    public long BytesRelayed { get; set; }
}

public class Notification
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public string Type { get; set; } = "";
    public string? Payload { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ReadAt { get; set; }
}

public enum JobStatus { Pending, Running, Done, Failed }

/// <summary>持久化任务队列：缩略图、转码、推送、GC 等。HostedService 用 FOR UPDATE SKIP LOCKED 领取。</summary>
public class Job
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string Type { get; set; } = "";
    public string? Payload { get; set; }
    public JobStatus Status { get; set; } = JobStatus.Pending;
    public int Attempts { get; set; }
    public DateTimeOffset RunAfter { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LockedUntil { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

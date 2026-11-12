namespace PrivateChat.Api.Domain;

public enum UserRole { Admin, Member, Child }
public enum DeviceKind { Browser, Agent }

public class User
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    /// <summary>Cloudflare Access 身份映射键。</summary>
    public string Email { get; set; } = "";
    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public Guid? AvatarBlobId { get; set; }
    /// <summary>仅局域网备用登录使用。</summary>
    public string? PasswordHash { get; set; }
    public UserRole Role { get; set; } = UserRole.Member;
    public bool ElderMode { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DisabledAt { get; set; }
}

public class Family
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string Name { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class Invite
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    /// <summary>允许通过 Access 自动建号的家人邮箱。</summary>
    public string? Email { get; set; }
    /// <summary>局域网模式下使用的邀请码。</summary>
    public string? Code { get; set; }
    public Guid CreatedBy { get; set; }
    public UserRole Role { get; set; } = UserRole.Member;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ExpiresAt { get; set; }
    public Guid? UsedBy { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
}

public class Device
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public DeviceKind Kind { get; set; }
    public string Name { get; set; } = "";
    public string Platform { get; set; } = "";
    /// <summary>Web Push 订阅 JSON。</summary>
    public string? PushSubscription { get; set; }
    /// <summary>设备令牌的 SHA-256，用于吊销校验。</summary>
    public string? TokenHash { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
    public bool RemoteCapable { get; set; }
    public string? UnattendedPinHash { get; set; }
    /// <summary>无人值守 PIN 连续输错的次数；到 5 次锁 15 分钟并清零。</summary>
    public int PinFailures { get; set; }
    public DateTimeOffset? PinLockedUntil { get; set; }
    /// <summary>允许远程控制本设备的用户 Id 列表（JSON）。设备主人总是可以，不用列。</summary>
    public string AllowedControllers { get; set; } = "[]";
    /// <summary>Agent 上报：版本、系统（显示器信息只在内存里，见 AgentRegistry）。</summary>
    public string? AgentVersion { get; set; }
    public string? Os { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? RevokedAt { get; set; }
}

public class RefreshToken
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public Guid? DeviceId { get; set; }
    public string Hash { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

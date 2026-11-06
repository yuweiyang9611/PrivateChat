namespace PrivateChat.Api.Domain;

public enum DerivedStatus { Pending, Ready, Failed }

/// <summary>内容寻址的原始文件：聊天附件、网盘文件、动态图片三处共用。</summary>
public class Blob
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
    public string Mime { get; set; } = "application/octet-stream";
    public string StorageKey { get; set; } = "";
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>缩略图 / 封面 / 转码产物。</summary>
public class DerivedBlob
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid BlobId { get; set; }
    /// <summary>thumb-320 / thumb-1280 / poster / play-720p / audio-m4a</summary>
    public string Variant { get; set; } = "";
    public string StorageKey { get; set; } = "";
    public string? Meta { get; set; }
    public DerivedStatus Status { get; set; } = DerivedStatus.Pending;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum SpaceType { Personal, Family }
public enum SharePermission { Read, Write }

public class Space
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public SpaceType Type { get; set; }
    public Guid? OwnerUserId { get; set; }
    public long? QuotaBytes { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class DriveNode
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid SpaceId { get; set; }
    public Guid? ParentId { get; set; }
    public string Name { get; set; } = "";
    public bool IsFolder { get; set; }
    public Guid? BlobId { get; set; }
    public long Size { get; set; }
    public string? Mime { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DeletedAt { get; set; }
}

public class NodeShare
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid NodeId { get; set; }
    public Guid? GranteeUserId { get; set; }
    public string? LinkToken { get; set; }
    public SharePermission Permission { get; set; } = SharePermission.Read;
    public string? PasswordHash { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

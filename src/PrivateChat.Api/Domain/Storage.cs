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

    // ---- 媒体信息（后台探测，图片读 EXIF、视频用 ffprobe）；相册时间线按 TakenAt 排序 ----
    /// <summary>拍摄时间；没有元数据时为空，时间线退回 CreatedAt。</summary>
    public DateTimeOffset? TakenAt { get; set; }
    /// <summary>显示宽高（已按 EXIF / 旋转元数据纠正方向）。</summary>
    public int? Width { get; set; }
    public int? Height { get; set; }
    public double? DurationSeconds { get; set; }
    /// <summary>探测过就写时间（成功失败都写），后台据此补做存量数据。</summary>
    public DateTimeOffset? MediaProbedAt { get; set; }
}

/// <summary>
/// "某人拥有这份内容"的凭证：上传完成或秒传命中时登记。内容去重后 blob 只有一个创建者，
/// 后来上传同一文件的人靠这条记录获得引用 / 读取权限；也作为垃圾回收的"近期还有人要"的依据。
/// </summary>
public class BlobClaim
{
    public Guid BlobId { get; set; }
    public Guid UserId { get; set; }
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
    /// <summary>家庭空间：孩子账号只读（设计稿 4.6"Child 可配置为只读"）。</summary>
    public bool ChildReadOnly { get; set; }
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
    /// <summary>在回收站里：删除一个文件夹时整棵子树同时标记，便于所有查询只看这一列。</summary>
    public DateTimeOffset? DeletedAt { get; set; }
    /// <summary>这次删除操作的根节点 id（回收站只列根；还原 / 彻底删除按它整批处理）。</summary>
    public Guid? TrashRootId { get; set; }
    public Guid? DeletedBy { get; set; }
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

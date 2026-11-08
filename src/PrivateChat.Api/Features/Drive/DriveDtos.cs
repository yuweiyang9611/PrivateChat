using PrivateChat.Api.Domain;

namespace PrivateChat.Api.Features.Drive;

public sealed record SpaceDto(Guid Id, SpaceType Type, string Name, Guid? OwnerUserId, long UsedBytes, long? QuotaBytes, DrivePermission Permission, bool ChildReadOnly);
public sealed record DriveOverviewDto(List<SpaceDto> Spaces, long DiskTotalBytes, long DiskFreeBytes);

public sealed record NodeDto(
    Guid Id, Guid SpaceId, Guid? ParentId, string Name, bool IsFolder, Guid? BlobId, long Size, string? Mime,
    Guid CreatedBy, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, bool HasShares,
    int? Width, int? Height, double? DurationSeconds, DateTimeOffset? TakenAt);

public sealed record ListingDto(SpaceDto Space, NodeDto? Folder, List<NodeDto> Path, DrivePermission Permission, List<NodeDto> Items);
public sealed record FolderRefDto(Guid Id, Guid? ParentId, string Name);

public enum NameConflict { Rename, Replace }
public sealed record CreateFolderRequest(Guid SpaceId, Guid? ParentId, string Name);
public sealed record CreateFileRequest(Guid SpaceId, Guid? ParentId, Guid BlobId, string Name, NameConflict OnConflict = NameConflict.Rename);
public sealed record MoveTarget(Guid SpaceId, Guid? ParentId);
public sealed record PatchNodeRequest(string? Name, MoveTarget? MoveTo);
public sealed record PatchSpaceRequest(long? QuotaBytes, bool? ClearQuota, bool? ChildReadOnly);

public sealed record TrashItemDto(NodeDto Node, DateTimeOffset DeletedAt, Guid? DeletedBy, string SpaceName, long TotalBytes, int ItemCount);

public sealed record SharedNodeDto(NodeDto Node, Guid OwnerUserId, string OwnerName, SharePermission Permission, DateTimeOffset? ExpiresAt);

public sealed record CreateShareRequest(Guid? GranteeUserId, SharePermission Permission = SharePermission.Read, bool Link = false, string? Password = null, int? ExpiresInDays = null);
public sealed record ShareDto(Guid Id, Guid NodeId, Guid? GranteeUserId, string? GranteeName, SharePermission Permission, bool IsLink, string? Url, bool HasPassword, DateTimeOffset? ExpiresAt, DateTimeOffset CreatedAt);

public sealed record SendToChatRequest(Guid ConversationId, Guid ClientMessageId);

public sealed record GalleryItemDto(
    Guid BlobId, string Mime, DateTimeOffset SortAt, DateTimeOffset? TakenAt, int? Width, int? Height, double? DurationSeconds,
    string Source, Guid SourceId, Guid ContextId, string? Name);
public sealed record GalleryPageDto(List<GalleryItemDto> Items, DateTimeOffset? NextBefore, Guid? NextBeforeId);

/// <summary>DriveChanged 事件：某个文件夹（parentId 为空即空间根）的内容变了。</summary>
public sealed record DriveChangedDto(Guid SpaceId, Guid? ParentId);

public static class DriveNames
{
    private const string Forbidden = "\\/:*?\"<>|";
    public const int MaxLength = 200;

    /// <summary>用户输入的名字：不合法就拒绝（让用户自己改），不悄悄替换。</summary>
    public static string Validate(string? name)
    {
        var s = (name ?? "").Trim();
        if (s.Length == 0) throw Infrastructure.ApiException.BadRequest("名称不能为空");
        if (s.Length > MaxLength) throw Infrastructure.ApiException.BadRequest($"名称不能超过 {MaxLength} 个字符");
        if (s is "." or "..") throw Infrastructure.ApiException.BadRequest("名称不合法");
        if (s.Any(c => char.IsControl(c) || Forbidden.Contains(c))) throw Infrastructure.ApiException.BadRequest("名称不能包含 \\ / : * ? \" < > |");
        return s;
    }

    /// <summary>上传文件的原名：把不合法字符替换掉，空了就用默认名。</summary>
    public static string Sanitize(string? name, string fallback = "未命名")
    {
        var chars = (name ?? "").Trim().Select(c => char.IsControl(c) || Forbidden.Contains(c) ? '_' : c).ToArray();
        var s = new string(chars).Trim();
        if (s.Length == 0 || s is "." or "..") s = fallback;
        if (s.Length > MaxLength)
        {
            var ext = Path.GetExtension(s);
            s = s[..(MaxLength - ext.Length)] + ext;
        }
        return s;
    }

    /// <summary>"照片.jpg" 冲突时依次试 "照片 (1).jpg"、"照片 (2).jpg"……</summary>
    public static string NextFree(string name, bool isFolder, ISet<string> taken)
    {
        if (!taken.Contains(name)) return name;
        var ext = isFolder ? "" : Path.GetExtension(name);
        var stem = isFolder ? name : name[..^ext.Length];
        for (var i = 1; i < 10_000; i++)
        {
            var candidate = $"{stem} ({i}){ext}";
            if (candidate.Length > MaxLength) candidate = $"{stem[..Math.Max(1, stem.Length - (candidate.Length - MaxLength))]} ({i}){ext}";
            if (!taken.Contains(candidate)) return candidate;
        }
        return $"{stem} ({Guid.NewGuid():N}){ext}";
    }
}

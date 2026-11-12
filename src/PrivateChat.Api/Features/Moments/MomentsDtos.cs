using PrivateChat.Api.Domain;
using PrivateChat.Api.Features.Drive;

namespace PrivateChat.Api.Features.Moments;

public sealed record PostMediaDto(Guid BlobId, string Mime, int? Width, int? Height, double? DurationSeconds, DateTimeOffset? TakenAt);

public sealed record ReactionSummaryDto(Guid UserId, string Emoji);

/// <summary>ReplyToUserId：被回复的那条评论的作者（"爸 回复 妈：…"），被回复的评论删了也保留。</summary>
public sealed record CommentDto(Guid Id, Guid PostId, Guid AuthorId, Guid? ReplyToCommentId, Guid? ReplyToUserId, string Text, DateTimeOffset CreatedAt, List<ReactionSummaryDto> Reactions);

public sealed record PostDto(
    Guid Id, Guid AuthorId, string? Text, PostVisibility Visibility, DateTimeOffset CreatedAt,
    List<PostMediaDto> Media, List<ReactionSummaryDto> Reactions, List<CommentDto> Comments);

public sealed record PostPageDto(List<PostDto> Items, DateTimeOffset? NextBefore, Guid? NextBeforeId);

public sealed record CreatePostRequest(string? Text, PostVisibility Visibility = PostVisibility.Family, List<Guid>? BlobIds = null);

public sealed record CreateCommentRequest(string Text, Guid? ReplyToCommentId = null);

public sealed record ToggleReactionRequest(string Emoji);

/// <summary>那年今天：往年同月同日的照片（每年最多 9 张，另给总数）和动态。</summary>
public sealed record OnThisDayYearDto(int Year, int YearsAgo, List<GalleryItemDto> Photos, int PhotoCount, List<PostDto> Posts);
public sealed record OnThisDayDto(DateOnly Date, List<OnThisDayYearDto> Years);

public sealed record AlbumDto(Guid Id, string Title, Guid CreatedBy, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, Guid? CoverBlobId, int ItemCount);

public sealed record AlbumsOverviewDto(List<AlbumDto> Albums, List<GalleryMonthDto> Months);

public sealed record CreateAlbumRequest(string Title, List<Guid>? BlobIds = null);

/// <summary>CoverBlobId 设成相册里的某一项；ClearCover 恢复为"最近加入的一项"。</summary>
public sealed record PatchAlbumRequest(string? Title = null, Guid? CoverBlobId = null, bool? ClearCover = null);

public sealed record AddAlbumItemsRequest(List<Guid> BlobIds);

/// <summary>相册里的一项：与时间线的条目同样的字段，另带是谁加进来的（加的人、相册创建人、管理员可以移出）。</summary>
public sealed record AlbumItemDto(Guid BlobId, string Mime, DateTimeOffset SortAt, DateTimeOffset? TakenAt, int? Width, int? Height, double? DurationSeconds, Guid AddedBy);

public sealed record AlbumItemsPageDto(AlbumDto Album, List<AlbumItemDto> Items, DateTimeOffset? NextBefore, Guid? NextBeforeId);

/// <summary>Added：这次真正新加入的张数（已在相册里的不重复加）。</summary>
public sealed record AddAlbumItemsResult(int Added, AlbumDto Album);

/// <summary>SignalR PostChanged：Change = created / updated / deleted。</summary>
public sealed record PostChangedDto(Guid PostId, Guid AuthorId, string Change);

/// <summary>SignalR AlbumChanged：Deleted 为 true 时相册已删除。</summary>
public sealed record AlbumChangedDto(Guid AlbumId, bool Deleted);

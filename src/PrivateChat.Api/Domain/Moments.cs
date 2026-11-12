namespace PrivateChat.Api.Domain;

public enum PostVisibility { Family, Private }
public enum ReactionTargetType { Post, Comment }

public class Post
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid AuthorId { get; set; }
    public string? Text { get; set; }
    public PostVisibility Visibility { get; set; } = PostVisibility.Family;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DeletedAt { get; set; }
    public List<PostMedia> Media { get; set; } = [];
}

public class PostMedia
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid PostId { get; set; }
    public Guid BlobId { get; set; }
    public int Sort { get; set; }
    public string? Meta { get; set; }
}

public class Comment
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid PostId { get; set; }
    public Guid AuthorId { get; set; }
    public Guid? ReplyToCommentId { get; set; }
    public string Text { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DeletedAt { get; set; }
}

public class Reaction
{
    public ReactionTargetType TargetType { get; set; }
    public Guid TargetId { get; set; }
    public Guid UserId { get; set; }
    public string Emoji { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class Album
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string Title { get; set; } = "";
    /// <summary>手动指定的封面（必须是相册里的一项）；为空时用最近加入的一项。</summary>
    public Guid? CoverBlobId { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>最后一次改名 / 加减照片的时间：相册列表按它排序。</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class AlbumItem
{
    public Guid AlbumId { get; set; }
    public Guid BlobId { get; set; }
    public Guid AddedBy { get; set; }
    public DateTimeOffset? TakenAt { get; set; }
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;
}

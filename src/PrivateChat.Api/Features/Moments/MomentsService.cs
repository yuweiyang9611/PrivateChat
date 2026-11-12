using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PrivateChat.Api.Domain;
using PrivateChat.Api.Features.Blobs;
using PrivateChat.Api.Features.Drive;
using PrivateChat.Api.Features.Notifications;
using PrivateChat.Api.Infrastructure;
using PrivateChat.Api.Infrastructure.Media;

namespace PrivateChat.Api.Features.Moments;

/// <summary>
/// 家庭动态（docs/DESIGN.md 4.7）：文本 + 至多 9 个照片 / 视频；可见范围"家人"（全家可见、可评论点赞）或"仅自己"。
/// 评论可回复；动态和评论都能表情点赞。删除：动态由作者或管理员；评论由评论者、动态作者或管理员。
/// 每次改动后返回整条动态（前端直接替换），并经 SignalR PostChanged 通知能看到它的人。
/// </summary>
public sealed class MomentsService(AppDbContext db, BlobAccessService blobAccess, GalleryService gallery, MomentsNotifier notify, IOptions<AppOptions> app)
{
    public const int MaxMedia = 9;
    public const int MaxPostText = 5000;
    public const int MaxCommentText = 2000;
    public const int MaxPageSize = 50;
    private const int OnThisDayPhotosPerYear = 9;

    private string TimeZoneId => app.Value.TimeZone;

    private static IQueryable<Post> VisibleTo(IQueryable<Post> q, Guid me)
        => q.Where(p => p.DeletedAt == null && (p.Visibility == PostVisibility.Family || p.AuthorId == me));

    // ---------------- 读 ----------------

    /// <summary>时间倒序，游标为上一页最后一条的 (createdAt, id)；authorId 只看某个人的。</summary>
    public async Task<PostPageDto> FeedAsync(Guid me, DateTimeOffset? before, Guid? beforeId, Guid? authorId, int limit, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, MaxPageSize);
        var q = before is { } b
            ? db.Posts.FromSqlInterpolated($"SELECT * FROM posts WHERE (created_at, id) < ({b.ToUniversalTime()}, {beforeId ?? Guid.Empty})")
            : db.Posts;
        q = VisibleTo(q, me);
        if (authorId is { } aid) q = q.Where(p => p.AuthorId == aid);
        var posts = await q.OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.Id).Take(limit).ToListAsync(ct);
        var items = await BuildAsync(posts, ct);
        var last = posts.Count == limit ? posts[^1] : null;
        return new PostPageDto(items, last?.CreatedAt, last?.Id);
    }

    public async Task<PostDto> GetAsync(Guid me, Guid id, CancellationToken ct)
        => (await BuildAsync([await VisiblePostAsync(me, id, ct)], ct))[0];

    private async Task<Post> VisiblePostAsync(Guid me, Guid id, CancellationToken ct)
        => await VisibleTo(db.Posts, me).FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw ApiException.NotFound("动态");

    /// <summary>批量组装：媒体、点赞、评论（含被回复人）、评论的点赞，各一次查询。</summary>
    private async Task<List<PostDto>> BuildAsync(List<Post> posts, CancellationToken ct)
    {
        if (posts.Count == 0) return [];
        var ids = posts.Select(p => p.Id).ToList();

        var media = await (
            from pm in db.PostMedia
            where ids.Contains(pm.PostId)
            join b in db.Blobs on pm.BlobId equals b.Id
            orderby pm.Sort
            select new { pm.PostId, Dto = new PostMediaDto(b.Id, b.Mime, b.Width, b.Height, b.DurationSeconds, b.TakenAt) }
        ).ToListAsync(ct);

        var comments = await db.Comments.Where(c => ids.Contains(c.PostId) && c.DeletedAt == null)
            .OrderBy(c => c.CreatedAt).ThenBy(c => c.Id).ToListAsync(ct);
        var replyIds = comments.Where(c => c.ReplyToCommentId != null).Select(c => c.ReplyToCommentId!.Value).Distinct().ToList();
        var replyAuthors = replyIds.Count == 0
            ? new Dictionary<Guid, Guid>()
            : await db.Comments.Where(c => replyIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.AuthorId, ct);

        var targetIds = ids.Concat(comments.Select(c => c.Id)).ToList();
        var reactions = await db.Reactions.Where(r => targetIds.Contains(r.TargetId))
            .OrderBy(r => r.CreatedAt).ToListAsync(ct);
        List<ReactionSummaryDto> ReactionsOf(ReactionTargetType type, Guid id) =>
            reactions.Where(r => r.TargetType == type && r.TargetId == id).Select(r => new ReactionSummaryDto(r.UserId, r.Emoji)).ToList();

        return posts.Select(p => new PostDto(
            p.Id, p.AuthorId, p.Text, p.Visibility, p.CreatedAt,
            media.Where(m => m.PostId == p.Id).Select(m => m.Dto).ToList(),
            ReactionsOf(ReactionTargetType.Post, p.Id),
            comments.Where(c => c.PostId == p.Id).Select(c => new CommentDto(
                c.Id, c.PostId, c.AuthorId, c.ReplyToCommentId,
                c.ReplyToCommentId is { } r && replyAuthors.TryGetValue(r, out var ra) ? ra : null,
                c.Text, c.CreatedAt, ReactionsOf(ReactionTargetType.Comment, c.Id))).ToList()
        )).ToList();
    }

    // ---------------- 发布 / 删除 ----------------

    public async Task<PostDto> CreateAsync(Guid me, CreatePostRequest req, CancellationToken ct)
    {
        if (!Enum.IsDefined(req.Visibility)) throw ApiException.BadRequest("可见范围无效");
        var text = string.IsNullOrWhiteSpace(req.Text) ? null : req.Text.Trim();
        if (text is { Length: > MaxPostText }) throw ApiException.BadRequest($"正文最多 {MaxPostText} 字");
        var blobIds = (req.BlobIds ?? []).Distinct().ToList();
        if (blobIds.Count > MaxMedia) throw ApiException.BadRequest($"最多 {MaxMedia} 张照片或视频");
        if (text is null && blobIds.Count == 0) throw ApiException.BadRequest("写点什么或者选几张照片");
        await RequireMediaAsync(me, blobIds, ct);

        var post = new Post { AuthorId = me, Text = text, Visibility = req.Visibility };
        for (var i = 0; i < blobIds.Count; i++) post.Media.Add(new PostMedia { PostId = post.Id, BlobId = blobIds[i], Sort = i });
        db.Posts.Add(post);
        await db.SaveChangesAsync(ct);
        await notify.PostChangedAsync(post, "created", ct);
        return await GetAsync(me, post.Id, ct);
    }

    /// <summary>照片 / 视频都要存在、是图片或视频，并且发布人本来就持有或能看到（不能借动态读别人的文件）。</summary>
    private async Task RequireMediaAsync(Guid me, List<Guid> blobIds, CancellationToken ct)
    {
        if (blobIds.Count == 0) return;
        var blobs = await db.Blobs.Where(b => blobIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, ct);
        foreach (var id in blobIds)
        {
            if (!blobs.TryGetValue(id, out var blob)) throw ApiException.NotFound("照片");
            if (!IsVisualMedia(blob.Mime)) throw ApiException.BadRequest("动态只能放照片和视频");
            if (!await blobAccess.CanReferenceAsync(id, me, ct)) throw ApiException.Forbidden("无权使用该文件");
        }
    }

    public static bool IsVisualMedia(string mime) => MimeRules.IsImage(mime) && !mime.Equals("image/svg+xml", StringComparison.OrdinalIgnoreCase) || MimeRules.IsVideo(mime);

    /// <summary>删动态：作者或管理员。媒体引用一并去掉（别处没人引用的内容由垃圾回收清理）。</summary>
    public async Task DeleteAsync(Guid me, UserRole role, Guid id, CancellationToken ct)
    {
        var post = await VisiblePostAsync(me, id, ct);
        if (post.AuthorId != me && role != UserRole.Admin) throw ApiException.Forbidden("只能删除自己的动态");
        post.DeletedAt = DateTimeOffset.UtcNow;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        await db.PostMedia.Where(m => m.PostId == id).ExecuteDeleteAsync(ct);
        await db.Reactions.Where(r => r.TargetType == ReactionTargetType.Post && r.TargetId == id).ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);
        await notify.PostChangedAsync(post, "deleted", ct);
    }

    // ---------------- 评论 ----------------

    public async Task<PostDto> CommentAsync(Guid me, Guid postId, CreateCommentRequest req, CancellationToken ct)
    {
        var post = await VisiblePostAsync(me, postId, ct);
        var text = (req.Text ?? "").Trim();
        if (text.Length == 0) throw ApiException.BadRequest("评论不能为空");
        if (text.Length > MaxCommentText) throw ApiException.BadRequest($"评论最多 {MaxCommentText} 字");
        Comment? replyTo = null;
        if (req.ReplyToCommentId is { } rid)
            replyTo = await db.Comments.FirstOrDefaultAsync(c => c.Id == rid && c.PostId == postId && c.DeletedAt == null, ct)
                      ?? throw ApiException.BadRequest("要回复的评论不存在");

        db.Comments.Add(new Comment { PostId = postId, AuthorId = me, ReplyToCommentId = replyTo?.Id, Text = text });
        await db.SaveChangesAsync(ct);
        await notify.PostChangedAsync(post, "updated", ct);

        var snippet = Snippet(text);
        if (replyTo is not null && replyTo.AuthorId != post.AuthorId)
            await notify.PushAsync([replyTo.AuthorId], me, "{0} 回复了你", snippet, post.Id, ct);
        await notify.PushAsync([post.AuthorId], me, replyTo?.AuthorId == post.AuthorId ? "{0} 回复了你" : "{0} 评论了你的动态", snippet, post.Id, ct);
        return await GetAsync(me, postId, ct);
    }

    /// <summary>删评论：评论者、动态作者或管理员。</summary>
    public async Task<PostDto> DeleteCommentAsync(Guid me, UserRole role, Guid commentId, CancellationToken ct)
    {
        var comment = await db.Comments.FirstOrDefaultAsync(c => c.Id == commentId && c.DeletedAt == null, ct) ?? throw ApiException.NotFound("评论");
        var post = await VisiblePostAsync(me, comment.PostId, ct);
        if (comment.AuthorId != me && post.AuthorId != me && role != UserRole.Admin) throw ApiException.Forbidden("只能删除自己的评论");
        comment.DeletedAt = DateTimeOffset.UtcNow;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        await db.Reactions.Where(r => r.TargetType == ReactionTargetType.Comment && r.TargetId == commentId).ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);
        await notify.PostChangedAsync(post, "updated", ct);
        return await GetAsync(me, post.Id, ct);
    }

    // ---------------- 点赞 ----------------

    public async Task<PostDto> TogglePostReactionAsync(Guid me, Guid postId, string emoji, CancellationToken ct)
    {
        var post = await VisiblePostAsync(me, postId, ct);
        if (await ToggleAsync(ReactionTargetType.Post, postId, me, emoji, ct) is { } added)
            await notify.PushAsync([post.AuthorId], me, "{0} 赞了你的动态", $"{added} {Snippet(post.Text ?? "")}".Trim(), post.Id, ct);
        await notify.PostChangedAsync(post, "updated", ct);
        return await GetAsync(me, postId, ct);
    }

    public async Task<PostDto> ToggleCommentReactionAsync(Guid me, Guid commentId, string emoji, CancellationToken ct)
    {
        var comment = await db.Comments.FirstOrDefaultAsync(c => c.Id == commentId && c.DeletedAt == null, ct) ?? throw ApiException.NotFound("评论");
        var post = await VisiblePostAsync(me, comment.PostId, ct);
        if (await ToggleAsync(ReactionTargetType.Comment, commentId, me, emoji, ct) is { } added)
            await notify.PushAsync([comment.AuthorId], me, "{0} 赞了你的评论", $"{added} {Snippet(comment.Text)}", post.Id, ct);
        await notify.PostChangedAsync(post, "updated", ct);
        return await GetAsync(me, post.Id, ct);
    }

    /// <summary>有就取消、没有就加上；返回新加上的表情（取消时为 null）。并发重复点同一个表情时以已存在为准。</summary>
    private async Task<string?> ToggleAsync(ReactionTargetType type, Guid targetId, Guid me, string emoji, CancellationToken ct)
    {
        emoji = NormalizeEmoji(emoji);
        var existing = await db.Reactions.FirstOrDefaultAsync(r => r.TargetType == type && r.TargetId == targetId && r.UserId == me && r.Emoji == emoji, ct);
        if (existing is not null)
        {
            db.Reactions.Remove(existing);
            await db.SaveChangesAsync(ct);
            return null;
        }
        var reaction = new Reaction { TargetType = type, TargetId = targetId, UserId = me, Emoji = emoji };
        db.Reactions.Add(reaction);
        try
        {
            await db.SaveChangesAsync(ct);
            return emoji;
        }
        catch (DbUpdateException ex) when (DriveService.IsUniqueViolation(ex))
        {
            db.Entry(reaction).State = EntityState.Detached;
            return null;
        }
    }

    /// <summary>与消息表情回应同样的规则：非空、不超过 8 个码点、不含空白和字母数字。</summary>
    public static string NormalizeEmoji(string? emoji)
    {
        emoji = (emoji ?? "").Trim();
        if (emoji.Length == 0 || emoji.EnumerateRunes().Count() > 8 || emoji.Any(char.IsWhiteSpace) || emoji.Any(char.IsAsciiLetterOrDigit))
            throw ApiException.BadRequest("表情无效");
        return emoji;
    }

    private static string Snippet(string text)
    {
        text = text.ReplaceLineEndings(" ").Trim();
        return text.Length > 60 ? text[..60] + "…" : text;
    }

    // ---------------- 那年今天 ----------------

    /// <summary>
    /// 往年同月同日（家庭时区）的照片 / 视频与动态，按年份从近到远。家庭规模的数据量下实时计算即可，不预先生成。
    /// date 只用于测试与"看看别的日子"，默认今天。
    /// </summary>
    public async Task<OnThisDayDto> OnThisDayAsync(Guid me, DateOnly? date, CancellationToken ct)
    {
        var zone = MediaProbe.Zone(TimeZoneId);
        var today = date ?? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).DateTime);
        var md = today.ToString("MM-dd");

        var photos = await gallery.SameDayAsync(me, TimeZoneId, today, 500, ct);
        var posts = await VisibleTo(db.Posts.FromSqlInterpolated($"""
                SELECT * FROM posts
                WHERE to_char(created_at AT TIME ZONE {TimeZoneId}, 'MM-DD') = {md}
                  AND (created_at AT TIME ZONE {TimeZoneId}) < make_timestamp({today.Year}, 1, 1, 0, 0, 0)
                """), me)
            .OrderByDescending(p => p.CreatedAt).Take(50).ToListAsync(ct);
        var postDtos = await BuildAsync(posts, ct);

        int YearOf(DateTimeOffset t) => TimeZoneInfo.ConvertTime(t, zone).Year;
        var years = photos.Select(p => YearOf(p.SortAt)).Concat(postDtos.Select(p => YearOf(p.CreatedAt))).Distinct().OrderByDescending(y => y);
        return new OnThisDayDto(today, years.Select(y =>
        {
            var ofYear = photos.Where(p => YearOf(p.SortAt) == y).ToList();
            return new OnThisDayYearDto(y, today.Year - y, ofYear.Take(OnThisDayPhotosPerYear).ToList(), ofYear.Count,
                postDtos.Where(p => YearOf(p.CreatedAt) == y).ToList());
        }).ToList());
    }
}

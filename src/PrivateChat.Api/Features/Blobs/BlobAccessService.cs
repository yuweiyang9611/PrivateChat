using Microsoft.EntityFrameworkCore;
using PrivateChat.Api.Domain;
using PrivateChat.Api.Features.Drive;
using PrivateChat.Api.Infrastructure;

namespace PrivateChat.Api.Features.Blobs;

/// <summary>
/// 读权限（docs/DESIGN.md 4.4）：持有该内容的人（上传过 / 秒传命中，见 blob_claims），
/// 或对任一引用该 blob 的消息 / 网盘节点 / 动态有读权限。
/// </summary>
public sealed class BlobAccessService(AppDbContext db, DriveAccess drive)
{
    public async Task<bool> CanReadAsync(Guid blobId, Guid userId, CancellationToken ct)
    {
        if (await db.BlobClaims.AnyAsync(c => c.BlobId == blobId && c.UserId == userId, ct)) return true;

        // 聊天附件：所在会话的成员
        var viaMessage = await db.MessageAttachments
            .Where(a => a.BlobId == blobId)
            .Join(db.Messages, a => a.MessageId, m => m.Id, (a, m) => m)
            .AnyAsync(m => m.DeletedAt == null && db.ConversationMembers.Any(cm => cm.ConversationId == m.ConversationId && cm.UserId == userId), ct);
        if (viaMessage) return true;

        // 头像：任何成员可见
        if (await db.Users.AnyAsync(u => u.AvatarBlobId == blobId, ct)) return true;
        if (await db.Conversations.AnyAsync(c => c.AvatarBlobId == blobId && db.ConversationMembers.Any(cm => cm.ConversationId == c.Id && cm.UserId == userId), ct)) return true;

        // 动态：家人可见的动态全家都能看，"仅自己"的只有作者；删掉的动态不再算
        if (await db.PostMedia.AnyAsync(pm => pm.BlobId == blobId
                && db.Posts.Any(p => p.Id == pm.PostId && p.DeletedAt == null && (p.Visibility == PostVisibility.Family || p.AuthorId == userId)), ct))
            return true;

        // 相册：全家共享
        if (await db.AlbumItems.AnyAsync(i => i.BlobId == blobId, ct)) return true;

        // 网盘：个人空间主人、家庭空间成员、被共享的人（读权限与角色无关）
        return await drive.CanReadBlobViaDriveAsync(blobId, new DriveActor(userId, UserRole.Member), ct);
    }

    /// <summary>发消息 / 存网盘时引用 blob：必须持有或本来就能读到（转发）。</summary>
    public Task<bool> CanReferenceAsync(Guid blobId, Guid userId, CancellationToken ct) => CanReadAsync(blobId, userId, ct);

    /// <summary>登记"此人持有这份内容"；已有就刷新时间（垃圾回收据此判断近期是否还有人要）。</summary>
    public static Task ClaimAsync(AppDbContext db, Guid blobId, Guid userId, CancellationToken ct)
        => db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO blob_claims (blob_id, user_id, created_at) VALUES ({blobId}, {userId}, now()) ON CONFLICT (blob_id, user_id) DO UPDATE SET created_at = EXCLUDED.created_at",
            ct);
}

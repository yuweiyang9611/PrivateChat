using Microsoft.EntityFrameworkCore;
using PrivateChat.Api.Infrastructure;

namespace PrivateChat.Api.Features.Blobs;

/// <summary>读权限：上传者本人，或对任一引用该 blob 的消息 / 网盘节点 / 动态有读权限（docs/DESIGN.md 3.3）。</summary>
public sealed class BlobAccessService(AppDbContext db)
{
    public async Task<bool> CanReadAsync(Guid blobId, Guid userId, CancellationToken ct)
    {
        if (await db.Blobs.AnyAsync(b => b.Id == blobId && b.CreatedBy == userId, ct)) return true;

        // 聊天附件：所在会话的成员
        var viaMessage = await db.MessageAttachments
            .Where(a => a.BlobId == blobId)
            .Join(db.Messages, a => a.MessageId, m => m.Id, (a, m) => m)
            .AnyAsync(m => m.DeletedAt == null && db.ConversationMembers.Any(cm => cm.ConversationId == m.ConversationId && cm.UserId == userId), ct);
        if (viaMessage) return true;

        // 头像：任何成员可见
        if (await db.Users.AnyAsync(u => u.AvatarBlobId == blobId, ct)) return true;
        if (await db.Conversations.AnyAsync(c => c.AvatarBlobId == blobId && db.ConversationMembers.Any(cm => cm.ConversationId == c.Id && cm.UserId == userId), ct)) return true;

        // 动态：家庭内全员可见（M4 再细化可见范围）
        if (await db.PostMedia.AnyAsync(p => p.BlobId == blobId, ct)) return true;

        // 网盘：M2 接共享规则，这里先按节点创建者
        if (await db.DriveNodes.AnyAsync(n => n.BlobId == blobId && n.CreatedBy == userId && n.DeletedAt == null, ct)) return true;

        return false;
    }

    /// <summary>发消息时引用 blob：必须是自己上传的，或自己本来就能读到的（转发）。</summary>
    public Task<bool> CanReferenceAsync(Guid blobId, Guid userId, CancellationToken ct) => CanReadAsync(blobId, userId, ct);
}

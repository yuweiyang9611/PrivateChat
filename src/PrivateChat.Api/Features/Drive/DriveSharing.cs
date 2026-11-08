using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PrivateChat.Api.Domain;
using PrivateChat.Api.Features.Chat;
using PrivateChat.Api.Features.Notifications;
using PrivateChat.Api.Infrastructure;
using PrivateChat.Api.Infrastructure.Keys;

namespace PrivateChat.Api.Features.Drive;

/// <summary>
/// 共享（docs/DESIGN.md 4.6 / 4.9）：
/// - 对成员共享（Read / Write），作用于节点及其整棵子树；
/// - 外链 /s/{token}：256 bit 随机 token，可选密码与过期时间，只读；孩子账号不能创建；
/// - 发送到聊天：建一条引用同一 blob 的消息，零拷贝。
/// 管理共享要对节点所在空间空间级可写（个人空间主人 / 家庭成员），被共享的人不能再转共享。
/// </summary>
public sealed class DriveSharing(
    AppDbContext db, DriveAccess access, DriveService drive, MessageService messages,
    IHubContext<ChatHub> hub, PushService push, IOptions<AppOptions> app)
{
    public const int MaxLinkDays = 365;
    private static readonly PasswordHasher<NodeShare> Hasher = new();

    private async Task<(DriveNode Node, Space Space)> ManageableAsync(Guid nodeId, DriveActor a, CancellationToken ct)
    {
        var node = await db.DriveNodes.FirstOrDefaultAsync(n => n.Id == nodeId && n.DeletedAt == null, ct) ?? throw ApiException.NotFound("文件");
        var space = await drive.GetSpaceAsync(node.SpaceId, ct);
        if (DriveAccess.ForSpace(space, a) < DrivePermission.Write) throw ApiException.Forbidden("只有主人能管理共享");
        return (node, space);
    }

    public string LinkUrl(string token) => $"{app.Value.PublicUrl.TrimEnd('/')}/s/{token}";

    private async Task<ShareDto> ToDtoAsync(NodeShare s, CancellationToken ct)
    {
        string? grantee = null;
        if (s.GranteeUserId is { } gid) grantee = await db.Users.Where(u => u.Id == gid).Select(u => u.DisplayName).FirstOrDefaultAsync(ct);
        return new ShareDto(s.Id, s.NodeId, s.GranteeUserId, grantee, s.Permission, s.LinkToken != null,
            s.LinkToken is null ? null : LinkUrl(s.LinkToken), s.PasswordHash != null, s.ExpiresAt, s.CreatedAt);
    }

    public async Task<List<ShareDto>> ListAsync(Guid nodeId, DriveActor a, CancellationToken ct)
    {
        await ManageableAsync(nodeId, a, ct);
        var shares = await db.NodeShares.Where(s => s.NodeId == nodeId).OrderBy(s => s.CreatedAt).ToListAsync(ct);
        var result = new List<ShareDto>();
        foreach (var s in shares) result.Add(await ToDtoAsync(s, ct));
        return result;
    }

    public async Task<ShareDto> CreateAsync(Guid nodeId, CreateShareRequest req, DriveActor a, CancellationToken ct)
    {
        var (node, space) = await ManageableAsync(nodeId, a, ct);
        DateTimeOffset? expires = null;
        if (req.ExpiresInDays is { } days)
        {
            if (days is < 1 or > MaxLinkDays) throw ApiException.BadRequest($"有效期为 1–{MaxLinkDays} 天");
            expires = DateTimeOffset.UtcNow.AddDays(days);
        }

        NodeShare share;
        if (req.Link)
        {
            if (a.Role == UserRole.Child) throw ApiException.Forbidden("孩子账号不能创建外链");
            if (req.Password is { Length: > 128 }) throw ApiException.BadRequest("密码过长");
            share = new NodeShare
            {
                NodeId = node.Id,
                LinkToken = Base64Url.Encode(RandomNumberGenerator.GetBytes(32)),
                Permission = SharePermission.Read,
                ExpiresAt = expires,
                CreatedBy = a.UserId,
            };
            if (!string.IsNullOrEmpty(req.Password)) share.PasswordHash = Hasher.HashPassword(share, req.Password);
            db.NodeShares.Add(share);
        }
        else
        {
            var grantee = req.GranteeUserId ?? throw ApiException.BadRequest("请选择要共享给谁");
            if (grantee == a.UserId) throw ApiException.BadRequest("不用共享给自己");
            if (!await db.Users.AnyAsync(u => u.Id == grantee && u.DisabledAt == null, ct)) throw ApiException.NotFound("家庭成员");
            share = await db.NodeShares.FirstOrDefaultAsync(s => s.NodeId == node.Id && s.GranteeUserId == grantee, ct)
                    ?? db.NodeShares.Add(new NodeShare { NodeId = node.Id, GranteeUserId = grantee, CreatedBy = a.UserId }).Entity;
            share.Permission = req.Permission;
            share.ExpiresAt = expires;
        }
        await db.SaveChangesAsync(ct);
        if (share.GranteeUserId is { } g)
            await hub.Clients.Group(ChatEvents.UserGroup(g)).SendAsync(ChatEvents.DriveChanged, new DriveChangedDto(space.Id, node.ParentId), ct);
        await drive.NotifyAsync(space, node.ParentId, ct);
        return await ToDtoAsync(share, ct);
    }

    public async Task RevokeAsync(Guid shareId, DriveActor a, CancellationToken ct)
    {
        var share = await db.NodeShares.FirstOrDefaultAsync(s => s.Id == shareId, ct) ?? throw ApiException.NotFound("共享");
        var (node, space) = await ManageableAsync(share.NodeId, a, ct);
        db.NodeShares.Remove(share);
        await db.SaveChangesAsync(ct);
        if (share.GranteeUserId is { } g)
            await hub.Clients.Group(ChatEvents.UserGroup(g)).SendAsync(ChatEvents.DriveChanged, new DriveChangedDto(space.Id, node.ParentId), ct);
        await drive.NotifyAsync(space, node.ParentId, ct);
    }

    /// <summary>"与我共享"：直接共享给我的节点（未过期、未删除）。</summary>
    public async Task<List<SharedNodeDto>> SharedWithMeAsync(DriveActor a, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var grants = await (
            from s in db.NodeShares
            join n in db.DriveNodes on s.NodeId equals n.Id
            join sp in db.Spaces on n.SpaceId equals sp.Id
            where s.GranteeUserId == a.UserId && n.DeletedAt == null && (s.ExpiresAt == null || s.ExpiresAt > now)
            select new { s.NodeId, s.Permission, s.ExpiresAt, Owner = sp.OwnerUserId ?? s.CreatedBy, s.CreatedAt })
            .OrderByDescending(x => x.CreatedAt).ToListAsync(ct);
        if (grants.Count == 0) return [];
        var ownerIds = grants.Select(g => g.Owner).Distinct().ToList();
        var owners = await db.Users.Where(u => ownerIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        var result = new List<SharedNodeDto>();
        foreach (var g in grants)
            result.Add(new SharedNodeDto(await drive.NodeDtoAsync(g.NodeId, ct), g.Owner, owners.GetValueOrDefault(g.Owner, "家人"), g.Permission, g.ExpiresAt));
        return result;
    }

    // ---------------- 发送到聊天 ----------------

    public async Task<MessageDto> SendToChatAsync(Guid nodeId, SendToChatRequest req, DriveActor a, CancellationToken ct)
    {
        var node = await db.DriveNodes.FirstOrDefaultAsync(n => n.Id == nodeId && n.DeletedAt == null, ct) ?? throw ApiException.NotFound("文件");
        if (node.IsFolder || node.BlobId is null) throw ApiException.BadRequest("只能发送文件");
        var space = await drive.GetSpaceAsync(node.SpaceId, ct);
        if ((await access.ForNodeAsync(node, space, a, ct)).Permission < DrivePermission.Read) throw ApiException.Forbidden("无权读取该文件");
        var blob = await db.Blobs.FirstAsync(b => b.Id == node.BlobId, ct);

        var mime = blob.Mime;
        var (type, kind) = mime switch
        {
            _ when mime.StartsWith("image/", StringComparison.Ordinal) && mime != "image/svg+xml" => (MessageType.Image, AttachmentKind.Image),
            _ when mime.StartsWith("video/", StringComparison.Ordinal) => (MessageType.Video, AttachmentKind.Video),
            _ when mime.StartsWith("audio/", StringComparison.Ordinal) => (MessageType.Audio, AttachmentKind.Audio),
            _ => (MessageType.File, AttachmentKind.File),
        };
        var meta = JsonSerializer.Serialize(new { name = node.Name, size = blob.Size, mime, width = blob.Width, height = blob.Height, duration = blob.DurationSeconds },
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
                // 与前端写入的 meta 一致：文件名里的中文不转义
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All),
            });

        var (message, created) = await messages.SendAsync(a.UserId,
            new SendMessageRequest(req.ConversationId, req.ClientMessageId, type, null, null, null, [new AttachmentInput(blob.Id, kind, meta)]), ct);
        var dto = MessageDto.From(message);
        if (created)
        {
            await hub.Clients.Group(ChatEvents.ConversationGroup(req.ConversationId)).SendAsync(ChatEvents.MessageReceived, dto, ct);
            await push.QueueMessageAsync(message, ct);
        }
        return dto;
    }
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using PrivateChat.Api.Domain;
using PrivateChat.Api.Features.Auth;
using PrivateChat.Api.Infrastructure;

namespace PrivateChat.Api.Features.Chat;

[ApiController]
[Route("api/conversations")]
[Authorize]
public sealed class ConversationsController(AppDbContext db, IHubContext<ChatHub> hub, PresenceTracker presence) : ControllerBase
{
    /// <summary>当前用户的会话列表，含未读数。</summary>
    [HttpGet]
    public async Task<ActionResult<List<ConversationDto>>> List(CancellationToken ct)
    {
        var userId = User.UserId();
        var rows = await db.ConversationMembers
            .Where(m => m.UserId == userId)
            .Join(db.Conversations, m => m.ConversationId, c => c.Id, (m, c) => new { m, c })
            .OrderByDescending(x => x.c.LastSeq)
            .ToListAsync(ct);
        var ids = rows.Select(x => x.c.Id).ToList();
        var members = await db.ConversationMembers.Where(m => ids.Contains(m.ConversationId))
            .GroupBy(m => m.ConversationId)
            .ToDictionaryAsync(g => g.Key, g => g.Select(m => m.UserId).ToList(), ct);

        // 每个会话的最后一条消息（seq == last_seq），用于列表预览
        var lastMessages = await db.Messages
            .Where(m => ids.Contains(m.ConversationId) && m.Seq == db.Conversations.Where(c => c.Id == m.ConversationId).Select(c => c.LastSeq).First())
            .ToDictionaryAsync(m => m.ConversationId, ct);

        return rows.Select(x =>
        {
            var last = lastMessages.GetValueOrDefault(x.c.Id);
            return new ConversationDto(
                x.c.Id, x.c.Type, x.c.Title, x.c.AvatarBlobId, x.c.LastSeq, x.m.LastReadSeq,
                Math.Max(0, x.c.LastSeq - x.m.LastReadSeq), x.m.MutedUntil, members.GetValueOrDefault(x.c.Id, []),
                last is null ? null : Preview(last), last?.CreatedAt);
        }).ToList();
    }

    public static string Preview(Message m) => m.DeletedAt is not null ? "[已撤回]" : m.Type switch
    {
        MessageType.Text => m.Body ?? "",
        MessageType.Image => "[图片]",
        MessageType.Video => "[视频]",
        MessageType.Audio => "[语音]",
        MessageType.File => "[文件]",
        MessageType.Sticker => "[表情]",
        MessageType.Call => "[通话]",
        _ => m.Body ?? "",
    };


    /// <summary>建群 / 发起单聊（单聊若已存在则直接返回）。孩子账号可以和家人单聊（打电话也靠它），但不能建群。</summary>
    [HttpPost]
    public async Task<ActionResult<ConversationDto>> Create([FromBody] CreateConversationRequest req, CancellationToken ct)
    {
        var me = User.UserId();
        if (req.Type != ConversationType.Direct && User.Role() == UserRole.Child) throw ApiException.Forbidden("孩子账号不能建群");
        var memberIds = req.MemberIds.Append(me).Distinct().ToList();
        if (req.Type == ConversationType.Family) throw ApiException.BadRequest("全家群由系统维护");
        if (req.Type == ConversationType.Direct && memberIds.Count != 2) throw ApiException.BadRequest("单聊必须恰好两个人");

        var known = await db.Users.Where(u => memberIds.Contains(u.Id) && u.DisabledAt == null).CountAsync(ct);
        if (known != memberIds.Count) throw ApiException.BadRequest("成员不存在");

        if (req.Type == ConversationType.Direct)
        {
            var other = memberIds.First(id => id != me);
            var existing = await db.Conversations
                .Where(c => c.Type == ConversationType.Direct && c.Members.Any(m => m.UserId == me) && c.Members.Any(m => m.UserId == other))
                .Include(c => c.Members).FirstOrDefaultAsync(ct);
            if (existing is not null) return ToDto(existing, me);
        }

        var conv = new Conversation
        {
            Type = req.Type,
            Title = req.Type == ConversationType.Group ? req.Title?.Trim() : null,
            Members = memberIds.Select(id => new ConversationMember { UserId = id, Role = id == me ? "owner" : "member" }).ToList(),
        };
        db.Conversations.Add(conv);
        await db.SaveChangesAsync(ct);

        var dto = ToDto(conv, me);
        // 在线成员的连接是在连上那一刻按当时的会话入组的：新会话要补进去，否则要等重连才收得到这里的消息
        foreach (var id in memberIds)
            foreach (var connectionId in presence.ConnectionsOf(id))
                await hub.Groups.AddToGroupAsync(connectionId, ChatEvents.ConversationGroup(conv.Id), ct);
        foreach (var id in memberIds)
            await hub.Clients.Group(ChatEvents.UserGroup(id)).SendAsync(ChatEvents.ConversationUpdated, ToDto(conv, id), ct);
        return dto;
    }

    private static ConversationDto ToDto(Conversation c, Guid me)
    {
        var mine = c.Members.First(m => m.UserId == me);
        return new ConversationDto(c.Id, c.Type, c.Title, c.AvatarBlobId, c.LastSeq, mine.LastReadSeq,
            Math.Max(0, c.LastSeq - mine.LastReadSeq), mine.MutedUntil, c.Members.Select(m => m.UserId).ToList());
    }
}

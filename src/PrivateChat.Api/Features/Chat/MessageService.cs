using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore;
using PrivateChat.Api.Domain;
using PrivateChat.Api.Infrastructure;

namespace PrivateChat.Api.Features.Chat;

/// <summary>消息写入：幂等（sender + clientMessageId）、事务内 last_seq 自增、严格递增 seq。</summary>
public sealed class MessageService(AppDbContext db)
{
    public const int SyncLimitPerConversation = 500;

    public async Task<bool> IsMemberAsync(Guid conversationId, Guid userId, CancellationToken ct)
        => await db.ConversationMembers.AnyAsync(m => m.ConversationId == conversationId && m.UserId == userId, ct);

    public async Task<(Message Message, bool Created)> SendAsync(Guid senderId, SendMessageRequest req, CancellationToken ct)
    {
        if (!await IsMemberAsync(req.ConversationId, senderId, ct)) throw ApiException.Forbidden("不是该会话成员");

        var existing = await db.Messages.Include(m => m.Attachments).Include(m => m.Reactions)
            .FirstOrDefaultAsync(m => m.SenderId == senderId && m.ClientMessageId == req.ClientMessageId, ct);
        if (existing is not null) return (existing, false);

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // 同一事务里原子自增，保证 seq 严格递增且不重复。
        // UPDATE … RETURNING 不是可组合 SQL，EF 的 SqlQuery 会拒绝，这里直接走 ADO 命令并挂在当前事务上。
        var seq = await NextSeqAsync(req.ConversationId, ct);

        var message = new Message
        {
            ConversationId = req.ConversationId,
            Seq = seq,
            SenderId = senderId,
            ClientMessageId = req.ClientMessageId,
            Type = req.Type,
            Body = req.Body,
            Payload = req.Payload,
            ReplyToId = req.ReplyToId,
            Attachments = (req.Attachments ?? []).Select(a => new MessageAttachment { BlobId = a.BlobId, Kind = a.Kind, Meta = a.Meta }).ToList(),
        };
        db.Messages.Add(message);

        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException)
        {
            // 并发重发命中唯一约束：回滚并返回已有记录
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            var dup = await db.Messages.Include(m => m.Attachments).Include(m => m.Reactions)
                .FirstOrDefaultAsync(m => m.SenderId == senderId && m.ClientMessageId == req.ClientMessageId, ct);
            if (dup is null) throw;
            return (dup, false);
        }

        // 发送者自己的已读推进到本条
        await db.ConversationMembers
            .Where(m => m.ConversationId == req.ConversationId && m.UserId == senderId && m.LastReadSeq < seq)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.LastReadSeq, seq), ct);

        return (message, true);
    }

    private async Task<long> NextSeqAsync(Guid conversationId, CancellationToken ct)
    {
        var conn = db.Database.GetDbConnection();
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        cmd.CommandText = "UPDATE conversations SET last_seq = last_seq + 1 WHERE id = @id RETURNING last_seq";
        var p = cmd.CreateParameter();
        p.ParameterName = "id";
        p.Value = conversationId;
        cmd.Parameters.Add(p);
        var result = await cmd.ExecuteScalarAsync(ct);
        if (result is null || result is DBNull) throw ApiException.NotFound("会话");
        return (long)result;
    }

    public async Task<List<Message>> ListAsync(Guid conversationId, Guid userId, long? beforeSeq, int limit, CancellationToken ct)
    {
        if (!await IsMemberAsync(conversationId, userId, ct)) throw ApiException.Forbidden("不是该会话成员");
        limit = Math.Clamp(limit, 1, 200);
        var q = db.Messages.Include(m => m.Attachments).Include(m => m.Reactions)
            .Where(m => m.ConversationId == conversationId);
        if (beforeSeq is { } b) q = q.Where(m => m.Seq < b);
        return await q.OrderByDescending(m => m.Seq).Take(limit).ToListAsync(ct);
    }

    public async Task<SyncResultDto> SyncAsync(Guid userId, Dictionary<Guid, long> lastSeqs, CancellationToken ct)
    {
        var result = new SyncResultDto([], []);
        var memberOf = await db.ConversationMembers.Where(m => m.UserId == userId).Select(m => m.ConversationId).ToListAsync(ct);
        foreach (var convId in memberOf)
        {
            var since = lastSeqs.GetValueOrDefault(convId, 0);
            var msgs = await db.Messages.Include(m => m.Attachments).Include(m => m.Reactions)
                .Where(m => m.ConversationId == convId && m.Seq > since)
                .OrderBy(m => m.Seq).Take(SyncLimitPerConversation + 1).ToListAsync(ct);
            if (msgs.Count > SyncLimitPerConversation) { result.NeedFullReload.Add(convId); continue; }
            if (msgs.Count > 0) result.Messages[convId] = msgs.Select(MessageDto.From).ToList();
        }
        return result;
    }

    public async Task<long> MarkReadAsync(Guid conversationId, Guid userId, long seq, CancellationToken ct)
    {
        var member = await db.ConversationMembers.FirstOrDefaultAsync(m => m.ConversationId == conversationId && m.UserId == userId, ct)
                     ?? throw ApiException.Forbidden("不是该会话成员");
        if (seq > member.LastReadSeq)
        {
            member.LastReadSeq = seq;
            await db.SaveChangesAsync(ct);
        }
        return member.LastReadSeq;
    }

    /// <summary>撤回：发送者本人 2 分钟内，或 Admin 任意时间。</summary>
    public async Task<Message> RecallAsync(Guid messageId, Guid userId, UserRole role, CancellationToken ct)
    {
        var m = await db.Messages.Include(x => x.Attachments).Include(x => x.Reactions)
            .FirstOrDefaultAsync(x => x.Id == messageId, ct) ?? throw ApiException.NotFound("消息");
        var own = m.SenderId == userId && DateTimeOffset.UtcNow - m.CreatedAt <= TimeSpan.FromMinutes(2);
        if (!own && role != UserRole.Admin) throw ApiException.Forbidden("只能撤回 2 分钟内自己发的消息");
        m.DeletedAt ??= DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return m;
    }
}

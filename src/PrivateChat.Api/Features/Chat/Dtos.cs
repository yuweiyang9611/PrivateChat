using PrivateChat.Api.Domain;

namespace PrivateChat.Api.Features.Chat;

public sealed record ConversationDto(Guid Id, ConversationType Type, string? Title, Guid? AvatarBlobId, long LastSeq, long LastReadSeq, long Unread, DateTimeOffset? MutedUntil, List<Guid> MemberIds, string? LastMessagePreview = null, DateTimeOffset? LastMessageAt = null);
public sealed record CreateConversationRequest(ConversationType Type, string? Title, List<Guid> MemberIds);

public sealed record AttachmentInput(Guid BlobId, AttachmentKind Kind, string? Meta);
public sealed record AttachmentDto(Guid Id, Guid BlobId, AttachmentKind Kind, string? Meta);
public sealed record ReactionDto(Guid UserId, string Emoji);

public sealed record SendMessageRequest(
    Guid ConversationId,
    Guid ClientMessageId,
    MessageType Type,
    string? Body,
    string? Payload = null,
    Guid? ReplyToId = null,
    List<AttachmentInput>? Attachments = null);

public sealed record MessageDto(
    Guid Id, Guid ConversationId, long Seq, Guid SenderId, Guid ClientMessageId, MessageType Type,
    string? Body, string? Payload, Guid? ReplyToId, DateTimeOffset CreatedAt, DateTimeOffset? EditedAt, bool Deleted,
    List<AttachmentDto> Attachments, List<ReactionDto> Reactions)
{
    public static MessageDto From(Message m) => new(
        m.Id, m.ConversationId, m.Seq, m.SenderId, m.ClientMessageId, m.Type,
        m.DeletedAt is null ? m.Body : null, m.DeletedAt is null ? m.Payload : null, m.ReplyToId, m.CreatedAt, m.EditedAt, m.DeletedAt is not null,
        m.DeletedAt is null ? m.Attachments.Select(a => new AttachmentDto(a.Id, a.BlobId, a.Kind, a.Meta)).ToList() : [],
        m.Reactions.Select(r => new ReactionDto(r.UserId, r.Emoji)).ToList());
}

public sealed record ReactRequest(string Emoji);
/// <summary>ReactionChanged 事件与 POST reactions 的返回：该消息当前全部回应。</summary>
public sealed record ReactionsDto(Guid MessageId, Guid ConversationId, long Seq, List<ReactionDto> Reactions);

public sealed record ReadReceiptDto(Guid ConversationId, Guid UserId, long Seq);
public sealed record TypingDto(Guid ConversationId, Guid UserId);
public sealed record SyncResultDto(Dictionary<Guid, List<MessageDto>> Messages, List<Guid> NeedFullReload);

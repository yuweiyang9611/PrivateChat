namespace PrivateChat.Api.Domain;

public enum ConversationType { Direct, Group, Family }
public enum MessageType { Text, Image, Video, Audio, File, Sticker, Call, System }
public enum AttachmentKind { Image, Video, Audio, File }

public class Conversation
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public ConversationType Type { get; set; }
    public string? Title { get; set; }
    public Guid? AvatarBlobId { get; set; }
    /// <summary>会话内严格递增的消息序号，事务内 UPDATE ... RETURNING 自增。</summary>
    public long LastSeq { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<ConversationMember> Members { get; set; } = [];
}

public class ConversationMember
{
    public Guid ConversationId { get; set; }
    public Guid UserId { get; set; }
    public string Role { get; set; } = "member";
    public long LastReadSeq { get; set; }
    public DateTimeOffset? MutedUntil { get; set; }
    public DateTimeOffset JoinedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class Message
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid ConversationId { get; set; }
    public long Seq { get; set; }
    public Guid SenderId { get; set; }
    /// <summary>客户端生成的幂等键（GUID v7）。</summary>
    public Guid ClientMessageId { get; set; }
    public MessageType Type { get; set; }
    public string? Body { get; set; }
    /// <summary>按消息类型变化的载荷（jsonb）。</summary>
    public string? Payload { get; set; }
    public Guid? ReplyToId { get; set; }
    public Guid? ForwardedFromId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? EditedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public List<MessageAttachment> Attachments { get; set; } = [];
    public List<MessageReaction> Reactions { get; set; } = [];
}

public class MessageAttachment
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid MessageId { get; set; }
    public Guid BlobId { get; set; }
    public AttachmentKind Kind { get; set; }
    /// <summary>width/height/duration/name/size/变体（jsonb）。</summary>
    public string? Meta { get; set; }
}

public class MessageReaction
{
    public Guid MessageId { get; set; }
    public Guid UserId { get; set; }
    public string Emoji { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

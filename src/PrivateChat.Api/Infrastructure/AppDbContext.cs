using Microsoft.EntityFrameworkCore;
using PrivateChat.Api.Domain;

namespace PrivateChat.Api.Infrastructure;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Family> Families => Set<Family>();
    public DbSet<Invite> Invites => Set<Invite>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<ConversationMember> ConversationMembers => Set<ConversationMember>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<MessageAttachment> MessageAttachments => Set<MessageAttachment>();
    public DbSet<MessageReaction> MessageReactions => Set<MessageReaction>();

    public DbSet<Blob> Blobs => Set<Blob>();
    public DbSet<BlobClaim> BlobClaims => Set<BlobClaim>();
    public DbSet<DerivedBlob> DerivedBlobs => Set<DerivedBlob>();
    public DbSet<Space> Spaces => Set<Space>();
    public DbSet<DriveNode> DriveNodes => Set<DriveNode>();
    public DbSet<NodeShare> NodeShares => Set<NodeShare>();

    public DbSet<Post> Posts => Set<Post>();
    public DbSet<PostMedia> PostMedia => Set<PostMedia>();
    public DbSet<Comment> Comments => Set<Comment>();
    public DbSet<Reaction> Reactions => Set<Reaction>();
    public DbSet<Album> Albums => Set<Album>();
    public DbSet<AlbumItem> AlbumItems => Set<AlbumItem>();

    public DbSet<CallSession> CallSessions => Set<CallSession>();
    public DbSet<CallParticipant> CallParticipants => Set<CallParticipant>();
    public DbSet<CallTrack> CallTracks => Set<CallTrack>();

    public DbSet<RemoteSession> RemoteSessions => Set<RemoteSession>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<Job> Jobs => Set<Job>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        // 枚举统一存字符串，便于直接查库；jsonb 列显式标注
        foreach (var et in b.Model.GetEntityTypes())
            foreach (var p in et.GetProperties())
                if ((Nullable.GetUnderlyingType(p.ClrType) ?? p.ClrType).IsEnum) p.SetProviderClrType(typeof(string));

        b.Entity<User>(e =>
        {
            e.HasIndex(x => x.Email).IsUnique();
            e.HasIndex(x => x.Username).IsUnique();
        });
        b.Entity<Invite>(e => e.HasIndex(x => x.Email));
        b.Entity<Device>(e =>
        {
            e.HasIndex(x => x.UserId);
            e.Property(x => x.PushSubscription).HasColumnType("jsonb");
            e.Property(x => x.AllowedControllers).HasColumnType("jsonb");
        });
        b.Entity<RefreshToken>(e => e.HasIndex(x => x.Hash).IsUnique());

        b.Entity<Conversation>(e => e.HasMany(x => x.Members).WithOne().HasForeignKey(x => x.ConversationId));
        b.Entity<ConversationMember>(e =>
        {
            e.HasKey(x => new { x.ConversationId, x.UserId });
            e.HasIndex(x => x.UserId);
        });
        b.Entity<Message>(e =>
        {
            e.HasIndex(x => new { x.ConversationId, x.Seq }).IsUnique();
            e.HasIndex(x => new { x.SenderId, x.ClientMessageId }).IsUnique();
            e.HasIndex(x => x.ConversationId);
            e.Property(x => x.Payload).HasColumnType("jsonb");
            e.HasMany(x => x.Attachments).WithOne().HasForeignKey(x => x.MessageId);
            e.HasMany(x => x.Reactions).WithOne().HasForeignKey(x => x.MessageId);
            // TODO: 迁移里追加 pg_trgm 扩展与 body 的 GIN 索引（gin_trgm_ops）
        });
        b.Entity<MessageAttachment>(e =>
        {
            e.Property(x => x.Meta).HasColumnType("jsonb");
            e.HasIndex(x => x.BlobId);
        });
        b.Entity<MessageReaction>(e => e.HasKey(x => new { x.MessageId, x.UserId, x.Emoji }));

        b.Entity<Blob>(e =>
        {
            e.HasIndex(x => x.Sha256).IsUnique();
            e.HasIndex(x => x.MediaProbedAt);
        });
        b.Entity<BlobClaim>(e =>
        {
            e.HasKey(x => new { x.BlobId, x.UserId });
            e.HasIndex(x => x.UserId);
        });
        // 每人一个个人空间、全家一个家庭空间（家庭空间 owner 为空，NULLS NOT DISTINCT 保证只有一个）
        b.Entity<Space>(e => e.HasIndex(x => new { x.Type, x.OwnerUserId }).IsUnique().AreNullsDistinct(false));
        b.Entity<DerivedBlob>(e =>
        {
            e.HasIndex(x => new { x.BlobId, x.Variant }).IsUnique();
            e.Property(x => x.Meta).HasColumnType("jsonb");
        });
        b.Entity<DriveNode>(e =>
        {
            e.HasIndex(x => new { x.SpaceId, x.ParentId });
            // 同一目录下（含空间根目录，parent 为空）不能重名；回收站里的不算
            e.HasIndex(x => new { x.SpaceId, x.ParentId, x.Name }).IsUnique().HasFilter("deleted_at IS NULL").AreNullsDistinct(false);
            e.HasIndex(x => x.BlobId);
            e.HasIndex(x => x.TrashRootId);
        });
        b.Entity<NodeShare>(e =>
        {
            e.HasIndex(x => x.NodeId);
            e.HasIndex(x => x.LinkToken).IsUnique();
        });

        b.Entity<Post>(e =>
        {
            e.HasIndex(x => x.CreatedAt).IsDescending();
            e.HasIndex(x => x.AuthorId);
            e.HasMany(x => x.Media).WithOne().HasForeignKey(x => x.PostId);
        });
        b.Entity<PostMedia>(e =>
        {
            e.Property(x => x.Meta).HasColumnType("jsonb");
            e.HasIndex(x => x.BlobId); // 读权限判断、垃圾回收
        });
        b.Entity<Comment>(e => e.HasIndex(x => new { x.PostId, x.CreatedAt }));
        b.Entity<Reaction>(e => e.HasKey(x => new { x.TargetType, x.TargetId, x.UserId, x.Emoji }));
        b.Entity<AlbumItem>(e =>
        {
            e.HasKey(x => new { x.AlbumId, x.BlobId });
            e.HasIndex(x => x.BlobId);
            // 删相册时条目一起删；相册删掉之后也插不进孤儿条目（孤儿条目会让照片一直对全家可见）
            e.HasOne<Album>().WithMany().HasForeignKey(x => x.AlbumId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<CallSession>(e =>
        {
            e.HasIndex(x => x.ConversationId);
            // 一个会话同一时间只有一通进行中的通话
            e.HasIndex(x => x.ConversationId, "ActiveConversation").HasDatabaseName("ix_call_sessions_active_conversation").IsUnique().HasFilter("ended_at IS NULL AND conversation_id IS NOT NULL");
            e.HasIndex(x => x.RoomSlug).IsUnique();
            e.HasIndex(x => x.StartedAt);
            e.HasMany(x => x.Participants).WithOne().HasForeignKey(x => x.CallId);
        });
        b.Entity<CallParticipant>(e =>
        {
            e.HasIndex(x => new { x.CallId, x.UserId });
            e.HasMany(x => x.Tracks).WithOne().HasForeignKey(x => x.ParticipantId);
        });

        b.Entity<RemoteSession>(e => e.HasIndex(x => x.TargetDeviceId));
        b.Entity<Notification>(e =>
        {
            e.HasIndex(x => new { x.UserId, x.ReadAt });
            e.Property(x => x.Payload).HasColumnType("jsonb");
        });
        b.Entity<Job>(e =>
        {
            e.HasIndex(x => new { x.Status, x.RunAfter });
            e.Property(x => x.Payload).HasColumnType("jsonb");
        });
    }
}

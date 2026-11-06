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
                if (p.ClrType.IsEnum) p.SetProviderClrType(typeof(string));

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
        b.Entity<MessageAttachment>(e => e.Property(x => x.Meta).HasColumnType("jsonb"));
        b.Entity<MessageReaction>(e => e.HasKey(x => new { x.MessageId, x.UserId, x.Emoji }));

        b.Entity<Blob>(e => e.HasIndex(x => x.Sha256).IsUnique());
        b.Entity<DerivedBlob>(e =>
        {
            e.HasIndex(x => new { x.BlobId, x.Variant }).IsUnique();
            e.Property(x => x.Meta).HasColumnType("jsonb");
        });
        b.Entity<DriveNode>(e =>
        {
            e.HasIndex(x => new { x.SpaceId, x.ParentId });
            e.HasIndex(x => new { x.ParentId, x.Name }).IsUnique().HasFilter("deleted_at IS NULL");
        });
        b.Entity<NodeShare>(e =>
        {
            e.HasIndex(x => x.NodeId);
            e.HasIndex(x => x.LinkToken).IsUnique();
        });

        b.Entity<Post>(e =>
        {
            e.HasIndex(x => x.CreatedAt).IsDescending();
            e.HasMany(x => x.Media).WithOne().HasForeignKey(x => x.PostId);
        });
        b.Entity<PostMedia>(e => e.Property(x => x.Meta).HasColumnType("jsonb"));
        b.Entity<Comment>(e => e.HasIndex(x => x.PostId));
        b.Entity<Reaction>(e => e.HasKey(x => new { x.TargetType, x.TargetId, x.UserId, x.Emoji }));
        b.Entity<AlbumItem>(e => e.HasKey(x => new { x.AlbumId, x.BlobId }));

        b.Entity<CallSession>(e =>
        {
            e.HasIndex(x => x.ConversationId);
            e.HasIndex(x => x.RoomSlug).IsUnique();
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

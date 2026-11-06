using System.Text.Json;
using System.Threading.Channels;
using ImageMagick;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using PrivateChat.Api.Domain;
using PrivateChat.Api.Features.Chat;
using PrivateChat.Api.Infrastructure;
using PrivateChat.Api.Infrastructure.Blobs;

namespace PrivateChat.Api.Features.Blobs;

/// <summary>进程内队列；持久状态在 derived_blobs（Pending），重启后由 Worker 扫表补做。</summary>
public sealed class ThumbnailQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });
    public void Enqueue(Guid blobId) => _channel.Writer.TryWrite(blobId);
    public ChannelReader<Guid> Reader => _channel.Reader;
}

/// <summary>
/// 图片缩略图：Magick.NET 读原图（含 HEIC、EXIF 方向），生成 320 / 1280 两档 WebP，完成后向引用该 blob 的会话广播 MessageUpdated。
/// 并发度 1：家庭规模够用，也不会和将来的 ffmpeg 转码抢 CPU。
/// </summary>
public sealed class ThumbnailWorker(ThumbnailQueue queue, IServiceScopeFactory scopes, IBlobStore blobs, DerivedStore derived, IHubContext<ChatHub> hub, ILogger<ThumbnailWorker> logger) : BackgroundService
{
    private static readonly Dictionary<string, uint> Sizes = new() { [Variants.Thumb320] = 320, [Variants.Thumb1280] = 1280 };

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await RequeuePendingAsync(ct);
        await foreach (var blobId in queue.Reader.ReadAllAsync(ct))
        {
            try { await ProcessAsync(blobId, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { logger.LogError(ex, "缩略图生成失败 {BlobId}", blobId); }
        }
    }

    private async Task RequeuePendingAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var pending = await db.DerivedBlobs.Where(d => d.Status == DerivedStatus.Pending).Select(d => d.BlobId).Distinct().ToListAsync(ct);
        foreach (var id in pending) queue.Enqueue(id);
        if (pending.Count > 0) logger.LogInformation("重新入队 {Count} 个待生成缩略图的 blob", pending.Count);
    }

    private async Task ProcessAsync(Guid blobId, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var blob = await db.Blobs.FirstOrDefaultAsync(b => b.Id == blobId, ct);
        if (blob is null) return;
        var rows = await db.DerivedBlobs.Where(d => d.BlobId == blobId && d.Status == DerivedStatus.Pending && Sizes.Keys.Contains(d.Variant)).ToListAsync(ct);
        if (rows.Count == 0) return;

        var source = blobs is FileSystemBlobStore fs ? fs.PathFor(blob.StorageKey) : null;
        try
        {
            using var image = source is not null ? new MagickImage(source) : new MagickImage(await blobs.OpenReadAsync(blob.StorageKey, ct));
            image.AutoOrient();
            image.Strip(); // 缩略图不带 EXIF / GPS，原件保留
            var origW = image.Width; var origH = image.Height;

            foreach (var row in rows)
            {
                var max = Sizes[row.Variant];
                using var clone = image.Clone();
                if (clone.Width > max || clone.Height > max)
                    clone.Resize(new MagickGeometry(max, max) { IgnoreAspectRatio = false });
                clone.Format = MagickFormat.WebP;
                clone.Quality = 80;
                var key = derived.KeyFor(blob.Sha256, row.Variant, "webp");
                // 临时文件没有扩展名，必须显式指定格式，否则 Magick 报 no encode delegate
                await derived.WriteAsync(key, path => clone.WriteAsync(path, MagickFormat.WebP, ct), ct);
                row.StorageKey = key;
                row.Meta = JsonSerializer.Serialize(new { width = clone.Width, height = clone.Height, origWidth = origW, origHeight = origH, mime = "image/webp" });
                row.Status = DerivedStatus.Ready;
            }
        }
        catch (Exception ex) when (ex is MagickException or IOException)
        {
            logger.LogWarning(ex, "无法解码图片 {BlobId} {Mime}", blobId, blob.Mime);
            foreach (var row in rows) { row.Status = DerivedStatus.Failed; row.Meta = JsonSerializer.Serialize(new { error = ex.GetType().Name }); }
        }
        await db.SaveChangesAsync(ct);
        await BroadcastAsync(db, blobId, ct);
    }

    /// <summary>引用该 blob 的每条消息向其会话广播 MessageUpdated，前端据此刷新缩略图。</summary>
    private async Task BroadcastAsync(AppDbContext db, Guid blobId, CancellationToken ct)
    {
        var messageIds = await db.MessageAttachments.Where(a => a.BlobId == blobId).Select(a => a.MessageId).ToListAsync(ct);
        if (messageIds.Count == 0) return;
        var messages = await db.Messages.Include(m => m.Attachments).Include(m => m.Reactions)
            .Where(m => messageIds.Contains(m.Id) && m.DeletedAt == null).ToListAsync(ct);
        foreach (var m in messages)
            await hub.Clients.Group(ChatEvents.ConversationGroup(m.ConversationId)).SendAsync(ChatEvents.MessageUpdated, MessageDto.From(m), ct);
    }
}

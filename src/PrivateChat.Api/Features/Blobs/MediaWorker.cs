using System.Text.Json;
using System.Threading.Channels;
using ImageMagick;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using PrivateChat.Api.Domain;
using PrivateChat.Api.Features.Chat;
using PrivateChat.Api.Infrastructure;
using PrivateChat.Api.Infrastructure.Blobs;
using PrivateChat.Api.Infrastructure.Media;

namespace PrivateChat.Api.Features.Blobs;

/// <summary>进程内队列；持久状态在 derived_blobs（Pending），重启后由 Worker 扫表补做。</summary>
public sealed class MediaQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });
    public void Enqueue(Guid blobId) => _channel.Writer.TryWrite(blobId);
    public ChannelReader<Guid> Reader => _channel.Reader;
}

/// <summary>
/// 派生产物生成（docs/DESIGN.md 3.3）：
/// - 图片：Magick.NET 读原图（含 HEIC、EXIF 方向），生成 320 / 1280 两档 WebP；
/// - 音频：ffmpeg 转 AAC m4a，保证语音消息在 Safari / 安卓 / 桌面都能播。
/// 完成后向引用该 blob 的会话广播 MessageUpdated。并发度 1：家庭规模够用，也不会抢 CPU。
/// </summary>
public sealed class MediaWorker(MediaQueue queue, IServiceScopeFactory scopes, IBlobStore blobs, DerivedStore derived, Ffmpeg ffmpeg, IHubContext<ChatHub> hub, ILogger<MediaWorker> logger) : BackgroundService
{
    private static readonly Dictionary<string, uint> ThumbSizes = new() { [Variants.Thumb320] = 320, [Variants.Thumb1280] = 1280 };

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await RequeuePendingAsync(ct);
        await foreach (var blobId in queue.Reader.ReadAllAsync(ct))
        {
            try { await ProcessAsync(blobId, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { logger.LogError(ex, "派生产物生成失败 {BlobId}", blobId); }
        }
    }

    private async Task RequeuePendingAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var pending = await db.DerivedBlobs.Where(d => d.Status == DerivedStatus.Pending).Select(d => d.BlobId).Distinct().ToListAsync(ct);
        foreach (var id in pending) queue.Enqueue(id);
        if (pending.Count > 0) logger.LogInformation("重新入队 {Count} 个待处理的 blob", pending.Count);
    }

    private async Task ProcessAsync(Guid blobId, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var blob = await db.Blobs.FirstOrDefaultAsync(b => b.Id == blobId, ct);
        if (blob is null) return;
        var rows = await db.DerivedBlobs.Where(d => d.BlobId == blobId && d.Status == DerivedStatus.Pending).ToListAsync(ct);
        if (rows.Count == 0) return;

        // 本地磁盘实现直接用路径；别的实现先落临时文件
        var sourcePath = blobs is FileSystemBlobStore fs ? fs.PathFor(blob.StorageKey) : await CopyToTempAsync(blob, ct);
        try
        {
            var thumbs = rows.Where(r => ThumbSizes.ContainsKey(r.Variant)).ToList();
            if (thumbs.Count > 0) Thumbnails(blob, sourcePath, thumbs);

            var audio = rows.FirstOrDefault(r => r.Variant == Variants.AudioM4a);
            if (audio is not null) await TranscodeAudioAsync(blob, sourcePath, audio, ct);

            foreach (var r in rows.Where(r => r.Status == DerivedStatus.Pending))
                Fail(r, "unsupported variant");
        }
        finally
        {
            if (blobs is not FileSystemBlobStore && File.Exists(sourcePath)) File.Delete(sourcePath);
        }

        await db.SaveChangesAsync(ct);
        await BroadcastAsync(db, blobId, ct);
    }

    private void Thumbnails(Blob blob, string sourcePath, List<DerivedBlob> rows)
    {
        try
        {
            using var image = new MagickImage(sourcePath);
            image.AutoOrient();
            image.Strip(); // 缩略图不带 EXIF / GPS，原件保留
            var origW = image.Width; var origH = image.Height;

            foreach (var row in rows)
            {
                var max = ThumbSizes[row.Variant];
                using var clone = image.Clone();
                if (clone.Width > max || clone.Height > max)
                    clone.Resize(new MagickGeometry(max, max) { IgnoreAspectRatio = false });
                clone.Quality = 80;
                var key = derived.KeyFor(blob.Sha256, row.Variant, "webp");
                // 临时文件没有扩展名，必须显式指定格式，否则 Magick 报 no encode delegate
                derived.Write(key, path => clone.Write(path, MagickFormat.WebP));
                Ready(row, key, new { width = clone.Width, height = clone.Height, origWidth = origW, origHeight = origH, mime = "image/webp" });
            }
        }
        catch (Exception ex) when (ex is MagickException or IOException)
        {
            logger.LogWarning(ex, "无法解码图片 {BlobId} {Mime}", blob.Id, blob.Mime);
            foreach (var row in rows) Fail(row, ex.GetType().Name);
        }
    }

    private async Task TranscodeAudioAsync(Blob blob, string sourcePath, DerivedBlob row, CancellationToken ct)
    {
        if (!ffmpeg.IsAvailable()) { Fail(row, "ffmpeg not installed"); return; }
        try
        {
            var key = derived.KeyFor(blob.Sha256, Variants.AudioM4a, "m4a");
            await derived.WriteAsync(key, path => ffmpeg.TranscodeAudioToM4aAsync(sourcePath, path, ct), ct);
            var duration = await ffmpeg.ProbeDurationAsync(derived.PathFor(key), ct);
            Ready(row, key, new { mime = "audio/mp4", duration });
        }
        catch (FfmpegException ex)
        {
            logger.LogWarning(ex, "音频转码失败 {BlobId} {Mime}", blob.Id, blob.Mime);
            Fail(row, "FfmpegException");
        }
    }

    private static void Ready(DerivedBlob row, string key, object meta)
    {
        row.StorageKey = key;
        row.Meta = JsonSerializer.Serialize(meta);
        row.Status = DerivedStatus.Ready;
    }

    private static void Fail(DerivedBlob row, string error)
    {
        row.Status = DerivedStatus.Failed;
        row.Meta = JsonSerializer.Serialize(new { error });
    }

    private async Task<string> CopyToTempAsync(Blob blob, CancellationToken ct)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "pc-" + blob.Sha256);
        await using var src = await blobs.OpenReadAsync(blob.StorageKey, ct);
        await using var dst = File.Create(tmp);
        await src.CopyToAsync(dst, ct);
        return tmp;
    }

    /// <summary>引用该 blob 的每条消息向其会话广播 MessageUpdated，前端据此刷新缩略图 / 换成转码后的音频。</summary>
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

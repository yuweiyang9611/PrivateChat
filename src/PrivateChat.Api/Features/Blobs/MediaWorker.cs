using System.Text.Json;
using System.Threading.Channels;
using ImageMagick;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PrivateChat.Api.Domain;
using PrivateChat.Api.Features.Chat;
using PrivateChat.Api.Features.Drive;
using PrivateChat.Api.Infrastructure;
using PrivateChat.Api.Infrastructure.Blobs;
using PrivateChat.Api.Infrastructure.Media;

namespace PrivateChat.Api.Features.Blobs;

/// <summary>
/// 进程内队列；持久状态在 derived_blobs（Pending）与 blobs.media_probed_at，重启后由 Worker 扫表补做。
/// 分两条：快队列（探测、缩略图、视频封面、音频转码，秒级）与转码队列（视频转 720p，可能几分钟到几十分钟），
/// 一段长视频在转码时，后面的照片缩略图和语音消息不用等它。
/// </summary>
public sealed class MediaQueue
{
    private readonly Channel<Guid> _fast = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Channel<Guid> _transcode = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });
    public void Enqueue(Guid blobId) => _fast.Writer.TryWrite(blobId);
    public void EnqueueTranscode(Guid blobId) => _transcode.Writer.TryWrite(blobId);
    public ChannelReader<Guid> Reader => _fast.Reader;
    public ChannelReader<Guid> TranscodeReader => _transcode.Reader;
}

/// <summary>
/// 媒体后台处理（docs/DESIGN.md 4.4）：
/// - 媒体信息：图片读 EXIF（拍摄时间、方向）、视频用 ffprobe（宽高、旋转、时长、拍摄时间），相册时间线靠它排序；
/// - 图片：Magick.NET 读原图（含 HEIC），生成 320 / 1280 两档 WebP；
/// - 视频：抽帧做同样两档封面缩略图；不是 H.264 / 太大（iPhone 的 HEVC、4K）时转成 H.264 720p；
/// - 音频：ffmpeg 转 AAC m4a，保证语音消息在 Safari / 安卓 / 桌面都能播。
/// 完成后向引用该 blob 的会话广播 MessageUpdated、向网盘所在文件夹广播 DriveChanged。
/// 快队列与转码队列各一个循环、各自串行，不抢 CPU；快的那部分做完先保存并广播（封面、时长马上可见），转码完成再广播一次。
/// </summary>
public sealed class MediaWorker(
    MediaQueue queue, IServiceScopeFactory scopes, IBlobStore blobs, DerivedStore derived, Ffmpeg ffmpeg,
    IHubContext<ChatHub> hub, IOptions<AppOptions> app, ILogger<MediaWorker> logger) : BackgroundService
{
    private static readonly Dictionary<string, uint> ThumbSizes = new() { [Variants.Thumb320] = 320, [Variants.Thumb1280] = 1280 };
    private readonly TimeZoneInfo _zone = MediaProbe.Zone(app.Value.TimeZone);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await BackfillAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogError(ex, "存量媒体补做入队失败"); }
        await Task.WhenAll(
            LoopAsync(queue.Reader, ProcessAsync, "媒体处理失败", ct),
            LoopAsync(queue.TranscodeReader, TranscodeAsync, "视频转码失败", ct));
    }

    private async Task LoopAsync(ChannelReader<Guid> reader, Func<Guid, CancellationToken, Task> handle, string error, CancellationToken ct)
    {
        await foreach (var blobId in reader.ReadAllAsync(ct))
        {
            try { await handle(blobId, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { logger.LogError(ex, "{Error} {BlobId}", error, blobId); }
        }
    }

    /// <summary>启动时补做：未完成的派生、还没探测过的图片 / 视频、M1 时期没有任何派生登记的视频。</summary>
    private async Task BackfillAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var bareVideos = await db.Blobs
            .Where(b => b.Mime.StartsWith("video/") && !db.DerivedBlobs.Any(d => d.BlobId == b.Id))
            .Select(b => new { b.Id, b.Mime }).ToListAsync(ct);
        foreach (var v in bareVideos)
            db.DerivedBlobs.AddRange(DerivedPlan.For(v.Mime).Select(x => new DerivedBlob { BlobId = v.Id, Variant = x }));
        if (bareVideos.Count > 0) await db.SaveChangesAsync(ct);

        var pending = await db.DerivedBlobs.Where(d => d.Status == DerivedStatus.Pending).Select(d => d.BlobId).Distinct().ToListAsync(ct);
        var unprobed = await db.Blobs
            .Where(b => b.MediaProbedAt == null && (b.Mime.StartsWith("image/") || b.Mime.StartsWith("video/")))
            .Select(b => b.Id).ToListAsync(ct);
        var all = pending.Union(unprobed).ToList();
        foreach (var id in all) queue.Enqueue(id);
        if (all.Count > 0) logger.LogInformation("补做 {Count} 个媒体（其中 {Unprobed} 个未探测）", all.Count, unprobed.Count);
    }

    /// <summary>快队列：探测、缩略图 / 封面、音频转码；需要转 720p 的视频保存后交给转码队列。</summary>
    private async Task ProcessAsync(Guid blobId, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var blob = await db.Blobs.FirstOrDefaultAsync(b => b.Id == blobId, ct);
        if (blob is null) return;
        var rows = await db.DerivedBlobs.Where(d => d.BlobId == blobId && d.Status == DerivedStatus.Pending).ToListAsync(ct);
        var needProbe = blob.MediaProbedAt is null && DerivedPlan.NeedsProbe(blob.Mime);
        if (rows.Count == 0 && !needProbe) return;

        var play = rows.FirstOrDefault(r => r.Variant == Variants.Play720);
        var transcodeLater = false;
        var sourcePath = await SourcePathAsync(blob, ct);
        try
        {
            var isVideo = MimeRules.IsVideo(blob.Mime);
            VideoProbe? video = isVideo ? await ffmpeg.ProbeVideoAsync(sourcePath, ct) : null;

            if (needProbe) Probe(blob, sourcePath, video);

            var thumbs = rows.Where(r => ThumbSizes.ContainsKey(r.Variant)).ToList();
            if (thumbs.Count > 0)
            {
                if (isVideo) await VideoThumbnailsAsync(blob, sourcePath, video, thumbs, ct);
                else Thumbnails(blob, sourcePath, thumbs);
            }

            if (play is not null) transcodeLater = PlanTranscode(db, blob, video, play);

            var audio = rows.FirstOrDefault(r => r.Variant == Variants.AudioM4a);
            if (audio is not null) await TranscodeAudioAsync(blob, sourcePath, audio, ct);

            foreach (var r in rows.Where(r => r != play && r.Status == DerivedStatus.Pending && db.Entry(r).State != EntityState.Deleted))
                Fail(r, "unsupported variant");
        }
        finally
        {
            DeleteTemp(sourcePath);
        }

        await db.SaveChangesAsync(ct);
        // 只探测了媒体信息时消息里看不到任何变化，不发 MessageUpdated（上线补做存量会给每张老照片探测一遍）；
        // 网盘列表里有宽高 / 时长 / 拍摄时间，仍然通知。
        var messageChanged = rows.Any(r => !(r == play && transcodeLater));
        if (messageChanged || needProbe) await BroadcastAsync(scope.ServiceProvider, db, blobId, messageChanged, ct);
        if (transcodeLater) queue.EnqueueTranscode(blobId);
    }

    /// <summary>转码队列：一次一个视频转 H.264 720p，完成后再广播一次（前端换成能播的版本）。</summary>
    private async Task TranscodeAsync(Guid blobId, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var blob = await db.Blobs.FirstOrDefaultAsync(b => b.Id == blobId, ct);
        var row = await db.DerivedBlobs.FirstOrDefaultAsync(d => d.BlobId == blobId && d.Variant == Variants.Play720 && d.Status == DerivedStatus.Pending, ct);
        if (blob is null || row is null) return; // 重复入队：已经转完或失败了

        var sourcePath = await SourcePathAsync(blob, ct);
        try
        {
            var video = await ffmpeg.ProbeVideoAsync(sourcePath, ct);
            if (PlanTranscode(db, blob, video, row)) await TranscodeVideoAsync(blob, sourcePath, video!, row, ct);
        }
        finally
        {
            DeleteTemp(sourcePath);
        }
        await db.SaveChangesAsync(ct);
        await BroadcastAsync(scope.ServiceProvider, db, blobId, notifyMessages: true, ct);
    }

    /// <summary>本地磁盘实现直接用路径；别的实现先落临时文件（每次一个独立文件名，两个队列可能同时处理同一个 blob）。</summary>
    private async Task<string> SourcePathAsync(Blob blob, CancellationToken ct)
        => blobs is FileSystemBlobStore fs ? fs.PathFor(blob.StorageKey) : await CopyToTempAsync(blob, ct);

    private void DeleteTemp(string sourcePath)
    {
        if (blobs is not FileSystemBlobStore && File.Exists(sourcePath)) File.Delete(sourcePath);
    }

    private void Probe(Blob blob, string sourcePath, VideoProbe? video)
    {
        try
        {
            if (MimeRules.IsImage(blob.Mime))
            {
                var info = MediaProbe.ProbeImage(sourcePath, _zone);
                // timestamptz 只接受 UTC 偏移：拍摄时间带着家庭时区（+08:00）要先转
                (blob.Width, blob.Height, blob.TakenAt) = (info.Width, info.Height, info.TakenAt?.ToUniversalTime());
            }
            else if (video is not null)
            {
                (blob.Width, blob.Height, blob.DurationSeconds, blob.TakenAt) = (video.Width, video.Height, video.DurationSeconds, video.CreatedAt?.ToUniversalTime());
            }
        }
        catch (Exception ex) when (ex is MagickException or IOException or ArgumentException or FormatException)
        {
            logger.LogWarning(ex, "读取媒体信息失败 {BlobId} {Mime}", blob.Id, blob.Mime);
        }
        // 成功失败都记下，避免每次启动反复探测同一个坏文件
        blob.MediaProbedAt = DateTimeOffset.UtcNow;
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

    /// <summary>视频封面：在 1 秒处（短视频取中间）抽一帧，再按图片的方式出两档缩略图。</summary>
    private async Task VideoThumbnailsAsync(Blob blob, string sourcePath, VideoProbe? video, List<DerivedBlob> rows, CancellationToken ct)
    {
        if (!ffmpeg.IsAvailable())
        {
            foreach (var row in rows) Fail(row, "ffmpeg not installed");
            return;
        }
        var frame = Path.Combine(Path.GetTempPath(), $"pc-frame-{blob.Id:N}.png");
        try
        {
            var at = video?.DurationSeconds is { } d && d < 2 ? d / 2 : 1;
            await ffmpeg.ExtractFrameAsync(sourcePath, frame, at, ct);
            Thumbnails(blob, frame, rows);
        }
        catch (FfmpegException ex)
        {
            logger.LogWarning(ex, "视频抽帧失败 {BlobId}", blob.Id);
            foreach (var row in rows) Fail(row, "FfmpegException");
        }
        finally
        {
            if (File.Exists(frame)) File.Delete(frame);
        }
    }

    /// <summary>
    /// 决定 play-720p 怎么办：本来就能直接播的删掉这条登记（前端请求 play-720p 时服务端回退原件，并可长期缓存）；
    /// 没法处理的标记失败；真要转码的返回 true，留在 Pending。
    /// </summary>
    private bool PlanTranscode(AppDbContext db, Blob blob, VideoProbe? video, DerivedBlob row)
    {
        if (!ffmpeg.IsAvailable()) { Fail(row, "ffmpeg not installed"); return false; }
        if (video is null) { Fail(row, "probe failed"); return false; }
        if (!MediaProbe.NeedsTranscode(video, blob.Mime))
        {
            db.DerivedBlobs.Remove(row);
            return false;
        }
        return true;
    }

    private async Task TranscodeVideoAsync(Blob blob, string sourcePath, VideoProbe video, DerivedBlob row, CancellationToken ct)
    {
        try
        {
            var key = derived.KeyFor(blob.Sha256, Variants.Play720, "mp4");
            await derived.WriteAsync(key, path => ffmpeg.TranscodeVideo720Async(sourcePath, path, ct), ct);
            var probed = await ffmpeg.ProbeVideoAsync(derived.PathFor(key), ct);
            Ready(row, key, new { mime = "video/mp4", width = probed?.Width, height = probed?.Height, duration = probed?.DurationSeconds });
            logger.LogInformation("视频转码完成 {BlobId} {Codec} {W}x{H}", blob.Id, video.Codec, video.Width, video.Height);
        }
        catch (FfmpegException ex)
        {
            logger.LogWarning(ex, "视频转码失败 {BlobId} {Mime}", blob.Id, blob.Mime);
            Fail(row, "FfmpegException");
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
        var tmp = Path.Combine(Path.GetTempPath(), $"pc-{blob.Sha256}-{Guid.NewGuid():N}");
        await using var src = await blobs.OpenReadAsync(blob.StorageKey, ct);
        await using var dst = File.Create(tmp);
        await src.CopyToAsync(dst, ct);
        return tmp;
    }

    /// <summary>
    /// 引用该 blob 的每条消息向其会话广播 MessageUpdated（前端刷新缩略图 / 换成转码后的音视频）；
    /// 引用它的网盘文件所在文件夹广播 DriveChanged（网格里的缩略图与尺寸）。
    /// </summary>
    private async Task BroadcastAsync(IServiceProvider services, AppDbContext db, Guid blobId, bool notifyMessages, CancellationToken ct)
    {
        var messageIds = notifyMessages ? await db.MessageAttachments.Where(a => a.BlobId == blobId).Select(a => a.MessageId).ToListAsync(ct) : [];
        if (messageIds.Count > 0)
        {
            var messages = await db.Messages.Include(m => m.Attachments).Include(m => m.Reactions)
                .Where(m => messageIds.Contains(m.Id) && m.DeletedAt == null).ToListAsync(ct);
            foreach (var m in messages)
                await hub.Clients.Group(ChatEvents.ConversationGroup(m.ConversationId)).SendAsync(ChatEvents.MessageUpdated, MessageDto.From(m), ct);
        }

        var folders = await db.DriveNodes.Where(n => n.BlobId == blobId && n.DeletedAt == null)
            .Select(n => new { n.SpaceId, n.ParentId }).Distinct().ToListAsync(ct);
        if (folders.Count == 0) return;
        var drive = services.GetRequiredService<DriveService>();
        foreach (var f in folders)
        {
            var space = await db.Spaces.FirstOrDefaultAsync(s => s.Id == f.SpaceId, ct);
            if (space is not null) await drive.NotifyAsync(space, f.ParentId, ct);
        }
    }
}

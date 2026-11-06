using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using PrivateChat.Api.Domain;
using PrivateChat.Api.Infrastructure;
using PrivateChat.Api.Infrastructure.Blobs;

namespace PrivateChat.Api.Features.Blobs;

/// <summary>
/// 上传完成后的入库：校验 / 计算 SHA-256，按内容去重，移入 Blob 存储，写 blobs 行，入队派生任务。
/// </summary>
public sealed class BlobIngestService(AppDbContext db, IBlobStore store, MediaQueue media, ILogger<BlobIngestService> logger)
{
    public sealed record IngestResult(Blob Blob, bool Created);

    /// <param name="tempPath">已落盘的完整文件（tus 临时区）。成功后文件会被移走或删除。</param>
    /// <param name="expectedSha256">客户端声明的哈希，可空；不一致则拒绝。</param>
    public async Task<IngestResult> IngestFileAsync(string tempPath, string? mime, string? expectedSha256, Guid userId, CancellationToken ct)
    {
        string sha;
        long size;
        await using (var fs = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true))
        {
            size = fs.Length;
            sha = await Sha256HexAsync(fs, ct);
        }

        if (!string.IsNullOrWhiteSpace(expectedSha256) && !sha.Equals(expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(tempPath);
            throw ApiException.BadRequest("文件内容与声明的 SHA-256 不一致，请重新上传");
        }

        var existing = await db.Blobs.FirstOrDefaultAsync(b => b.Sha256 == sha, ct);
        if (existing is not null)
        {
            File.Delete(tempPath);
            return new IngestResult(existing, false);
        }

        var key = store.KeyFor(sha);
        await store.MoveInAsync(key, tempPath, ct);

        var blob = new Blob
        {
            Sha256 = sha,
            Size = size,
            Mime = MimeRules.Normalize(mime),
            StorageKey = key,
            CreatedBy = userId,
        };
        db.Blobs.Add(blob);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // 两个人同时上传同一文件：唯一索引冲突，用先落库的那条
            db.ChangeTracker.Clear();
            var winner = await db.Blobs.FirstAsync(b => b.Sha256 == sha, ct);
            return new IngestResult(winner, false);
        }

        await EnqueueDerivedAsync(blob, ct);
        logger.LogInformation("Blob 入库 {BlobId} {Mime} {Size}B by {UserId}", blob.Id, blob.Mime, blob.Size, userId);
        return new IngestResult(blob, true);
    }

    /// <summary>图片：两档缩略图；音频（除已是 AAC / MP3 的）：转 m4a。视频转码留到 M2。</summary>
    private async Task EnqueueDerivedAsync(Blob blob, CancellationToken ct)
    {
        var variants = new List<string>();
        if (MimeRules.IsImage(blob.Mime)) variants.AddRange([Variants.Thumb320, Variants.Thumb1280]);
        if (MimeRules.NeedsAudioTranscode(blob.Mime)) variants.Add(Variants.AudioM4a);
        if (variants.Count == 0) return;
        db.DerivedBlobs.AddRange(variants.Select(v => new DerivedBlob { BlobId = blob.Id, Variant = v }));
        await db.SaveChangesAsync(ct);
        media.Enqueue(blob.Id);
    }

    public static async Task<string> Sha256HexAsync(Stream stream, CancellationToken ct)
    {
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexStringLower(hash);
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using PrivateChat.Api.Features.Blobs;
using PrivateChat.Api.Infrastructure;
using PrivateChat.Api.Infrastructure.Blobs;

namespace PrivateChat.Api.Features.Drive;

/// <summary>
/// 存储维护（docs/DESIGN.md 4.4 / 4.6），每小时一次：
/// 1. 回收站里超过 30 天的整批彻底删除；
/// 2. 垃圾回收：没有任何消息 / 网盘节点 / 动态 / 相册 / 头像引用、创建超过 7 天、7 天内也没人登记持有的 blob
///    （宽限期留给离线发件箱：传完还没发出去的消息，几天后联网再发也还能找到内容），
///    连同缩略图 / 转码产物一起删掉。删除语句里再判断一遍引用，避免与并发的引用竞争。
/// </summary>
public sealed class StorageMaintenanceWorker(IServiceScopeFactory scopes, IBlobStore blobs, DerivedStore derived, ILogger<StorageMaintenanceWorker> logger) : BackgroundService
{
    public static readonly TimeSpan TrashRetention = TimeSpan.FromDays(30);
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    private const int GcBatch = 200;
    private const string CandidatesSql = "SELECT b.id AS \"Value\" FROM blobs b WHERE " + Unreferenced + " LIMIT 200";

    /// <summary>"没人要了"的判定，选候选与真正删除时都用同一段条件。</summary>
    private const string Unreferenced = """
        b.created_at < now() - interval '7 days'
        AND NOT EXISTS (SELECT 1 FROM blob_claims c WHERE c.blob_id = b.id AND c.created_at > now() - interval '7 days')
        AND NOT EXISTS (SELECT 1 FROM message_attachments a WHERE a.blob_id = b.id)
        AND NOT EXISTS (SELECT 1 FROM drive_nodes n WHERE n.blob_id = b.id)
        AND NOT EXISTS (SELECT 1 FROM post_media p WHERE p.blob_id = b.id)
        AND NOT EXISTS (SELECT 1 FROM album_items ai WHERE ai.blob_id = b.id)
        AND NOT EXISTS (SELECT 1 FROM users u WHERE u.avatar_blob_id = b.id)
        AND NOT EXISTS (SELECT 1 FROM conversations cv WHERE cv.avatar_blob_id = b.id)
        """;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // 启动后稍等再跑，别和迁移、补做媒体抢资源
        try { await Task.Delay(TimeSpan.FromMinutes(2), ct); } catch (OperationCanceledException) { return; }
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var purged = await PurgeExpiredTrashAsync(db, ct);
                var collected = await CollectGarbageAsync(db, ct);
                if (purged + collected > 0) logger.LogInformation("存储维护：清理回收站 {Purged} 批，回收 {Collected} 个文件", purged, collected);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "存储维护失败");
            }
        } while (await timer.WaitForNextTickAsync(ct));
    }

    public static async Task<int> PurgeExpiredTrashAsync(AppDbContext db, CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow - TrashRetention;
        var roots = await db.DriveNodes.Where(n => n.TrashRootId == n.Id && n.DeletedAt < cutoff).Select(n => new { n.Id, n.SpaceId }).ToListAsync(ct);
        foreach (var r in roots) await DriveService.PurgeRootAsync(db, r.SpaceId, r.Id, ct);
        return roots.Count;
    }

    public async Task<int> CollectGarbageAsync(AppDbContext db, CancellationToken ct)
    {
        var total = 0;
        while (!ct.IsCancellationRequested)
        {
            var candidates = await db.Database.SqlQueryRaw<Guid>(CandidatesSql).ToListAsync(ct);
            if (candidates.Count == 0) break;
            foreach (var id in candidates)
                if (await DeleteIfUnreferencedAsync(db, id, ct)) total++;
            if (candidates.Count < GcBatch) break;
        }
        return total;
    }

    private async Task<bool> DeleteIfUnreferencedAsync(AppDbContext db, Guid id, CancellationToken ct)
    {
        string? storageKey;
        List<string> derivedKeys;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            var conn = db.Database.GetDbConnection();
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx.GetDbTransaction();
            cmd.CommandText = $"DELETE FROM blobs b WHERE b.id = @id AND {Unreferenced} RETURNING b.storage_key";
            var p = cmd.CreateParameter(); p.ParameterName = "id"; p.Value = id; cmd.Parameters.Add(p);
            storageKey = await cmd.ExecuteScalarAsync(ct) as string;
            if (storageKey is null)
            {
                await tx.RollbackAsync(ct);
                return false;
            }
            derivedKeys = await db.DerivedBlobs.Where(d => d.BlobId == id && d.StorageKey != "").Select(d => d.StorageKey).ToListAsync(ct);
            await db.DerivedBlobs.Where(d => d.BlobId == id).ExecuteDeleteAsync(ct);
            await db.BlobClaims.Where(c => c.BlobId == id).ExecuteDeleteAsync(ct);
            await tx.CommitAsync(ct);
        }
        // 数据库先提交再删文件：删文件失败只会留下孤儿文件，不会出现"记录在、文件没了"
        try
        {
            await blobs.DeleteAsync(storageKey, ct);
            foreach (var key in derivedKeys) await derived.DeleteAsync(key, ct);
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "删除文件失败 {BlobId}", id);
        }
        return true;
    }
}

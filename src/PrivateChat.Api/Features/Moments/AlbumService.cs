using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NpgsqlTypes;
using PrivateChat.Api.Domain;
using PrivateChat.Api.Features.Blobs;
using PrivateChat.Api.Features.Drive;
using PrivateChat.Api.Infrastructure;

namespace PrivateChat.Api.Features.Moments;

/// <summary>
/// 相册（docs/DESIGN.md 4.7）：
/// - 手动相册是全家共享的：每个人都能看、都能往里加自己能看到的照片 / 视频（加进来就等于分享给了全家）；
///   改名、设封面、删相册由创建人或管理员；移出某一项由加它的人、创建人或管理员。
/// - 自动按月相册不落库，就是时间线按月份分组（GalleryService.MonthsAsync）。
/// 相册只引用内容，不复制数据；删相册不删照片。
/// </summary>
public sealed class AlbumService(AppDbContext db, BlobAccessService blobAccess, GalleryService gallery, MomentsNotifier notify, IOptions<AppOptions> app)
{
    public const int MaxTitle = 60;
    public const int MaxAddPerRequest = 500;
    public const int MaxPageSize = 200;

    public async Task<AlbumsOverviewDto> OverviewAsync(Guid me, CancellationToken ct)
    {
        var albums = await db.Albums.OrderByDescending(a => a.UpdatedAt).ToListAsync(ct);
        return new AlbumsOverviewDto(await DtosAsync(albums, ct), await gallery.MonthsAsync(me, app.Value.TimeZone, ct));
    }

    private async Task<List<AlbumDto>> DtosAsync(List<Album> albums, CancellationToken ct)
    {
        if (albums.Count == 0) return [];
        var ids = albums.Select(a => a.Id).ToList();
        var stats = await db.AlbumItems.Where(i => ids.Contains(i.AlbumId))
            .GroupBy(i => i.AlbumId)
            .Select(g => new { AlbumId = g.Key, Count = g.Count(), Latest = g.OrderByDescending(i => i.AddedAt).ThenByDescending(i => i.BlobId).Select(i => i.BlobId).First() })
            .ToDictionaryAsync(x => x.AlbumId, ct);
        return albums.Select(a =>
        {
            var s = stats.GetValueOrDefault(a.Id);
            return new AlbumDto(a.Id, a.Title, a.CreatedBy, a.CreatedAt, a.UpdatedAt, a.CoverBlobId ?? s?.Latest, s?.Count ?? 0);
        }).ToList();
    }

    private async Task<AlbumDto> DtoAsync(Album album, CancellationToken ct) => (await DtosAsync([album], ct))[0];

    private async Task<Album> FindAsync(Guid id, CancellationToken ct)
        => await db.Albums.FirstOrDefaultAsync(a => a.Id == id, ct) ?? throw ApiException.NotFound("相册");

    private static void RequireManage(Album album, Guid me, UserRole role)
    {
        if (album.CreatedBy != me && role != UserRole.Admin) throw ApiException.Forbidden("只有创建相册的人或管理员能修改它");
    }

    private static string ValidTitle(string? title)
    {
        title = (title ?? "").Trim();
        if (title.Length == 0) throw ApiException.BadRequest("相册名不能为空");
        if (title.Length > MaxTitle) throw ApiException.BadRequest($"相册名最多 {MaxTitle} 字");
        return title;
    }

    public async Task<AlbumDto> CreateAsync(Guid me, CreateAlbumRequest req, CancellationToken ct)
    {
        var album = new Album { Title = ValidTitle(req.Title), CreatedBy = me };
        var blobIds = (req.BlobIds ?? []).Distinct().ToList();
        var blobs = await ValidBlobsAsync(me, blobIds, ct);
        db.Albums.Add(album);
        foreach (var b in blobs) db.AlbumItems.Add(new AlbumItem { AlbumId = album.Id, BlobId = b.Id, AddedBy = me, TakenAt = b.TakenAt });
        await db.SaveChangesAsync(ct);
        await notify.AlbumChangedAsync(album.Id, false, ct);
        return await DtoAsync(album, ct);
    }

    public async Task<AlbumDto> PatchAsync(Guid me, UserRole role, Guid id, PatchAlbumRequest req, CancellationToken ct)
    {
        var album = await FindAsync(id, ct);
        RequireManage(album, me, role);
        if (req.Title is not null) album.Title = ValidTitle(req.Title);
        if (req.ClearCover == true) album.CoverBlobId = null;
        else if (req.CoverBlobId is { } cover)
        {
            if (!await db.AlbumItems.AnyAsync(i => i.AlbumId == id && i.BlobId == cover, ct)) throw ApiException.BadRequest("封面必须是相册里的照片");
            album.CoverBlobId = cover;
        }
        album.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await notify.AlbumChangedAsync(id, false, ct);
        return await DtoAsync(album, ct);
    }

    public async Task DeleteAsync(Guid me, UserRole role, Guid id, CancellationToken ct)
    {
        var album = await FindAsync(id, ct);
        RequireManage(album, me, role);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.AlbumItems.Where(i => i.AlbumId == id).ExecuteDeleteAsync(ct);
        db.Albums.Remove(album);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await notify.AlbumChangedAsync(id, true, ct);
    }

    private const string ItemsSql = """
        SELECT ai.blob_id, b.mime, COALESCE(b.taken_at, b.created_at) AS sort_at, b.taken_at, b.width, b.height, b.duration_seconds, ai.added_by
        FROM album_items ai JOIN blobs b ON b.id = ai.blob_id
        WHERE ai.album_id = @album
          AND (@before IS NULL OR (COALESCE(b.taken_at, b.created_at), ai.blob_id) < (@before, @beforeId))
        ORDER BY sort_at DESC, ai.blob_id DESC
        LIMIT @limit
        """;

    /// <summary>相册内容：按拍摄时间倒序，游标为上一页最后一项的 (sortAt, blobId)。</summary>
    public async Task<AlbumItemsPageDto> ItemsAsync(Guid id, DateTimeOffset? before, Guid? beforeId, int limit, CancellationToken ct)
    {
        var album = await FindAsync(id, ct);
        limit = Math.Clamp(limit, 1, MaxPageSize);
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await db.Database.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = ItemsSql;
        void Add(string name, object? value, NpgsqlDbType type) => cmd.Parameters.Add(new Npgsql.NpgsqlParameter(name, type) { Value = value ?? DBNull.Value });
        Add("album", id, NpgsqlDbType.Uuid);
        Add("before", before?.ToUniversalTime(), NpgsqlDbType.TimestampTz);
        Add("beforeId", beforeId ?? Guid.Empty, NpgsqlDbType.Uuid);
        Add("limit", limit, NpgsqlDbType.Integer);
        var items = new List<AlbumItemDto>();
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
                items.Add(new AlbumItemDto(r.GetGuid(0), r.GetString(1), r.GetFieldValue<DateTimeOffset>(2),
                    r.IsDBNull(3) ? null : r.GetFieldValue<DateTimeOffset>(3), r.IsDBNull(4) ? null : r.GetInt32(4),
                    r.IsDBNull(5) ? null : r.GetInt32(5), r.IsDBNull(6) ? null : r.GetDouble(6), r.GetGuid(7)));
        }
        var last = items.Count == limit ? items[^1] : null;
        return new AlbumItemsPageDto(await DtoAsync(album, ct), items, last?.SortAt, last?.BlobId);
    }

    /// <summary>加照片：自己能看到的照片 / 视频；已在相册里的跳过。</summary>
    public async Task<AddAlbumItemsResult> AddItemsAsync(Guid me, Guid id, AddAlbumItemsRequest req, CancellationToken ct)
    {
        await FindAsync(id, ct);
        var blobIds = (req.BlobIds ?? []).Distinct().ToList();
        if (blobIds.Count == 0) throw ApiException.BadRequest("没有选择照片");
        // 校验（每张都要判读权限，可能较慢）放在加锁之前，锁只覆盖写入
        var blobs = await ValidBlobsAsync(me, blobIds, ct);
        var added = 0;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            // 锁住相册这一行（同时确认它还在）：与删除相册互斥，不会往刚删掉的相册里插条目
            if (await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE albums SET updated_at = updated_at WHERE id = {id}", ct) == 0)
                throw ApiException.NotFound("相册");
            foreach (var b in blobs)
            {
                // 已在相册里的、并发加同一张的：ON CONFLICT 跳过，不计数
                added += await db.Database.ExecuteSqlInterpolatedAsync(
                    $"INSERT INTO album_items (album_id, blob_id, added_by, taken_at, added_at) VALUES ({id}, {b.Id}, {me}, {b.TakenAt}, now()) ON CONFLICT DO NOTHING", ct);
            }
            if (added > 0) await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE albums SET updated_at = now() WHERE id = {id}", ct);
            await tx.CommitAsync(ct);
        }
        if (added > 0) await notify.AlbumChangedAsync(id, false, ct);
        db.ChangeTracker.Clear();
        return new AddAlbumItemsResult(added, await DtoAsync(await FindAsync(id, ct), ct));
    }

    public async Task<AlbumDto> RemoveItemAsync(Guid me, UserRole role, Guid id, Guid blobId, CancellationToken ct)
    {
        var album = await FindAsync(id, ct);
        var item = await db.AlbumItems.FirstOrDefaultAsync(i => i.AlbumId == id && i.BlobId == blobId, ct) ?? throw ApiException.NotFound("相册里的这张照片");
        if (item.AddedBy != me && album.CreatedBy != me && role != UserRole.Admin) throw ApiException.Forbidden("只能移出自己加的照片");
        db.AlbumItems.Remove(item);
        if (album.CoverBlobId == blobId) album.CoverBlobId = null;
        album.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await notify.AlbumChangedAsync(id, false, ct);
        return await DtoAsync(album, ct);
    }

    private async Task<List<Blob>> ValidBlobsAsync(Guid me, List<Guid> blobIds, CancellationToken ct)
    {
        if (blobIds.Count > MaxAddPerRequest) throw ApiException.BadRequest($"一次最多加 {MaxAddPerRequest} 张");
        if (blobIds.Count == 0) return [];
        var blobs = await db.Blobs.Where(b => blobIds.Contains(b.Id)).ToListAsync(ct);
        if (blobs.Count != blobIds.Count) throw ApiException.NotFound("照片");
        foreach (var b in blobs)
        {
            if (!MomentsService.IsVisualMedia(b.Mime)) throw ApiException.BadRequest("相册只能放照片和视频");
            if (!await blobAccess.CanReadAsync(b.Id, me, ct)) throw ApiException.Forbidden("无权使用该文件");
        }
        return blobs;
    }
}

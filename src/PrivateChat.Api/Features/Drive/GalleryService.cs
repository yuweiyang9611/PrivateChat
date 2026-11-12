using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using NpgsqlTypes;
using PrivateChat.Api.Infrastructure;
using PrivateChat.Api.Infrastructure.Media;

namespace PrivateChat.Api.Features.Drive;

/// <summary>某个月里我能看到的照片和视频：按月自动相册。Since / Before 是这个月在家庭时区下的起止，查这个月的时间线用。</summary>
public sealed record GalleryMonthDto(string Month, int Count, Guid CoverBlobId, DateTimeOffset Since, DateTimeOffset Before);

/// <summary>
/// 相册时间线（docs/DESIGN.md 4.6）：我能看到的所有照片和视频，不分来源——
/// 我所在会话里发过的、我的个人空间、家庭空间、共享给我的文件夹里的、家人的动态和我自己的私密动态、家庭相册里的。
/// 同一份内容只出现一次。按拍摄时间（没有则用上传时间）倒序，用 (时间, blobId) 做游标分页。
/// 按月相册、那年今天也基于同一个"可见集合"。
/// </summary>
public sealed class GalleryService(AppDbContext db)
{
    public const int MaxLimit = 200;

    /// <summary>可见集合：dedup 里每份内容一行，sort_at 为排序用的时间。</summary>
    private const string Visible = """
        WITH RECURSIVE shared AS (
            SELECT n.id FROM drive_nodes n
            JOIN node_shares s ON s.node_id = n.id
            WHERE s.grantee_user_id = @me AND (s.expires_at IS NULL OR s.expires_at > now()) AND n.deleted_at IS NULL
            UNION
            SELECT c.id FROM drive_nodes c JOIN shared p ON c.parent_id = p.id WHERE c.deleted_at IS NULL
        ),
        visible AS (
            SELECT a.blob_id, 'message' AS source, m.id AS source_id, m.conversation_id AS context_id, m.created_at AS added_at,
                   NULLIF(a.meta ->> 'name', '') AS name
            FROM message_attachments a
            JOIN messages m ON m.id = a.message_id
            JOIN conversation_members cm ON cm.conversation_id = m.conversation_id AND cm.user_id = @me
            WHERE m.deleted_at IS NULL AND a.kind IN ('Image', 'Video')
            UNION ALL
            SELECT n.blob_id, 'drive', n.id, n.space_id, n.created_at, n.name
            FROM drive_nodes n
            JOIN spaces sp ON sp.id = n.space_id
            WHERE n.deleted_at IS NULL AND n.blob_id IS NOT NULL
              AND ((sp.type = 'Personal' AND sp.owner_user_id = @me) OR sp.type = 'Family' OR n.id IN (SELECT id FROM shared))
            UNION ALL
            SELECT pm.blob_id, 'post', p.id, p.id, p.created_at, NULL
            FROM post_media pm
            JOIN posts p ON p.id = pm.post_id
            WHERE p.deleted_at IS NULL AND (p.visibility = 'Family' OR p.author_id = @me)
            UNION ALL
            SELECT ai.blob_id, 'album', ai.album_id, ai.album_id, ai.added_at, NULL
            FROM album_items ai
        ),
        dedup AS (
            SELECT DISTINCT ON (v.blob_id)
                   v.blob_id, v.source, v.source_id, v.context_id, v.name,
                   b.mime, b.taken_at, b.width, b.height, b.duration_seconds,
                   COALESCE(b.taken_at, b.created_at) AS sort_at
            FROM visible v JOIN blobs b ON b.id = v.blob_id
            WHERE (b.mime LIKE 'image/%' AND b.mime <> 'image/svg+xml') OR b.mime LIKE 'video/%'
            ORDER BY v.blob_id, v.added_at
        )
        """;

    private const string Columns = "blob_id, mime, sort_at, taken_at, width, height, duration_seconds, source, source_id, context_id, name";

    private const string PageSql = Visible + $"""
        SELECT {Columns}
        FROM dedup
        WHERE (@before IS NULL OR (sort_at, blob_id) < (@before, @beforeId))
          AND (@since IS NULL OR sort_at >= @since)
        ORDER BY sort_at DESC, blob_id DESC
        LIMIT @limit
        """;

    /// <summary>每个月一行：张数 + 当月最新一张做封面。月份按家庭时区划分。</summary>
    private const string MonthsSql = Visible + """
        SELECT DISTINCT ON (month) month, count(*) OVER (PARTITION BY month) AS n, blob_id
        FROM (SELECT to_char(sort_at AT TIME ZONE @tz, 'YYYY-MM') AS month, sort_at, blob_id FROM dedup) x
        ORDER BY month DESC, sort_at DESC, blob_id DESC
        """;

    /// <summary>往年的今天（家庭时区的月-日相同、年份早于今年）。</summary>
    private const string SameDaySql = Visible + $"""
        SELECT {Columns}
        FROM dedup
        WHERE to_char(sort_at AT TIME ZONE @tz, 'MM-DD') = @md
          AND (sort_at AT TIME ZONE @tz) < make_timestamp(@year, 1, 1, 0, 0, 0)
        ORDER BY sort_at DESC, blob_id DESC
        LIMIT @limit
        """;

    public async Task<GalleryPageDto> PageAsync(Guid userId, DateTimeOffset? before, Guid? beforeId, int limit, CancellationToken ct, DateTimeOffset? since = null)
    {
        limit = Math.Clamp(limit, 1, MaxLimit);
        await using var cmd = await CommandAsync(PageSql, userId, ct);
        Add(cmd, "before", before?.ToUniversalTime(), NpgsqlDbType.TimestampTz);
        Add(cmd, "beforeId", beforeId ?? Guid.Empty, NpgsqlDbType.Uuid);
        Add(cmd, "since", since?.ToUniversalTime(), NpgsqlDbType.TimestampTz);
        Add(cmd, "limit", limit, NpgsqlDbType.Integer);
        var items = await ReadItemsAsync(cmd, ct);
        var last = items.Count == limit ? items[^1] : null;
        return new GalleryPageDto(items, last?.SortAt, last?.BlobId);
    }

    public async Task<List<GalleryMonthDto>> MonthsAsync(Guid userId, string timeZone, CancellationToken ct)
    {
        await using var cmd = await CommandAsync(MonthsSql, userId, ct);
        Add(cmd, "tz", timeZone, NpgsqlDbType.Text);
        var zone = MediaProbe.Zone(timeZone);
        var result = new List<GalleryMonthDto>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var month = r.GetString(0);
            var start = DateTime.ParseExact(month + "-01", "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            DateTimeOffset Local(DateTime d) => new DateTimeOffset(d, zone.GetUtcOffset(d)).ToUniversalTime();
            result.Add(new GalleryMonthDto(month, (int)r.GetInt64(1), r.GetGuid(2), Local(start), Local(start.AddMonths(1))));
        }
        return result;
    }

    /// <summary>往年同月同日的照片和视频（最多 limit 张），新的在前。</summary>
    public async Task<List<GalleryItemDto>> SameDayAsync(Guid userId, string timeZone, DateOnly today, int limit, CancellationToken ct)
    {
        await using var cmd = await CommandAsync(SameDaySql, userId, ct);
        Add(cmd, "tz", timeZone, NpgsqlDbType.Text);
        Add(cmd, "md", today.ToString("MM-dd"), NpgsqlDbType.Text);
        Add(cmd, "year", today.Year, NpgsqlDbType.Integer);
        Add(cmd, "limit", limit, NpgsqlDbType.Integer);
        return await ReadItemsAsync(cmd, ct);
    }

    private async Task<DbCommand> CommandAsync(string sql, Guid userId, CancellationToken ct)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await db.Database.OpenConnectionAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        Add(cmd, "me", userId, NpgsqlDbType.Uuid);
        return cmd;
    }

    private static void Add(DbCommand cmd, string name, object? value, NpgsqlDbType type)
        => cmd.Parameters.Add(new Npgsql.NpgsqlParameter(name, type) { Value = value ?? DBNull.Value });

    private static async Task<List<GalleryItemDto>> ReadItemsAsync(DbCommand cmd, CancellationToken ct)
    {
        var items = new List<GalleryItemDto>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) items.Add(Read(r));
        return items;
    }

    /// <summary>按 Columns 的列顺序读一行。</summary>
    public static GalleryItemDto Read(DbDataReader r) => new(
        r.GetGuid(0),
        r.GetString(1),
        r.GetFieldValue<DateTimeOffset>(2),
        r.IsDBNull(3) ? null : r.GetFieldValue<DateTimeOffset>(3),
        r.IsDBNull(4) ? null : r.GetInt32(4),
        r.IsDBNull(5) ? null : r.GetInt32(5),
        r.IsDBNull(6) ? null : r.GetDouble(6),
        r.GetString(7),
        r.GetGuid(8),
        r.GetGuid(9),
        r.IsDBNull(10) ? null : r.GetString(10));
}

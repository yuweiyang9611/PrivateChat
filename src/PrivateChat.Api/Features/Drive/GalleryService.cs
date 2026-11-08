using Microsoft.EntityFrameworkCore;
using PrivateChat.Api.Infrastructure;

namespace PrivateChat.Api.Features.Drive;

/// <summary>
/// 相册时间线（docs/DESIGN.md 4.6）：我能看到的所有照片和视频，不分来源——
/// 我所在会话里发过的、我的个人空间、家庭空间、共享给我的文件夹里的。同一份内容只出现一次。
/// 按拍摄时间（没有则用上传时间）倒序，用 (时间, blobId) 做游标分页。
/// </summary>
public sealed class GalleryService(AppDbContext db)
{
    public const int MaxLimit = 200;

    private const string Sql = """
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
        SELECT blob_id, mime, sort_at, taken_at, width, height, duration_seconds, source, source_id, context_id, name
        FROM dedup
        WHERE @before IS NULL OR (sort_at, blob_id) < (@before, @beforeId)
        ORDER BY sort_at DESC, blob_id DESC
        LIMIT @limit
        """;

    public async Task<GalleryPageDto> PageAsync(Guid userId, DateTimeOffset? before, Guid? beforeId, int limit, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, MaxLimit);
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await db.Database.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = Sql;
        void Add(string name, object? value, NpgsqlTypes.NpgsqlDbType type)
        {
            var p = new Npgsql.NpgsqlParameter(name, type) { Value = value ?? DBNull.Value };
            cmd.Parameters.Add(p);
        }
        Add("me", userId, NpgsqlTypes.NpgsqlDbType.Uuid);
        Add("before", before?.ToUniversalTime(), NpgsqlTypes.NpgsqlDbType.TimestampTz);
        Add("beforeId", beforeId ?? Guid.Empty, NpgsqlTypes.NpgsqlDbType.Uuid);
        Add("limit", limit, NpgsqlTypes.NpgsqlDbType.Integer);

        var items = new List<GalleryItemDto>();
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                items.Add(new GalleryItemDto(
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
                    r.IsDBNull(10) ? null : r.GetString(10)));
            }
        }
        var last = items.Count == limit ? items[^1] : null;
        return new GalleryPageDto(items, last?.SortAt, last?.BlobId);
    }
}

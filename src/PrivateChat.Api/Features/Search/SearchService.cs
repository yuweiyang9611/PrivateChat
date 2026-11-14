using System.Data.Common;
using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NpgsqlTypes;
using PrivateChat.Api.Features.Auth;
using PrivateChat.Api.Infrastructure;

namespace PrivateChat.Api.Features.Search;

public enum SearchType { All, Messages, Files, Moments }

/// <summary>聊天里的一条命中：文字消息（kind = text，snippet 是命中附近的原文）或带附件的消息（kind = file，命中的是文件名）。</summary>
public sealed record MessageHitDto(
    Guid MessageId, Guid ConversationId, long Seq, Guid SenderId, DateTimeOffset CreatedAt,
    string Kind, string Snippet, Guid? BlobId, string? FileName, string? Mime, string? AttachmentKind);

/// <summary>
/// 网盘里的一个文件 / 文件夹。space：Personal（我的）、Family（家庭）、Shared（别人共享给我的）。
/// parentReadable：能不能打开它所在的文件夹（直接共享给我的那一项，上一级我是看不到的）。
/// </summary>
public sealed record FileHitDto(
    Guid NodeId, Guid SpaceId, string Space, Guid? ParentId, bool ParentReadable,
    string Name, bool IsFolder, Guid? BlobId, string? Mime, long Size, DateTimeOffset UpdatedAt, DateTimeOffset CreatedAt);

/// <summary>时光里的一条命中：动态正文（kind = post）或评论（kind = comment，postId 是它所在的动态）。</summary>
public sealed record MomentHitDto(string Kind, Guid Id, Guid PostId, Guid AuthorId, DateTimeOffset CreatedAt, string Snippet);

public sealed record AlbumHitDto(Guid AlbumId, string Title, Guid? CoverBlobId, int ItemCount, DateTimeOffset UpdatedAt);

/// <summary>
/// 每一类各自分页：*Next 是下一页的游标（为空表示没有更多），带上 type 和 cursor 再请求一次。
/// type = All 时每类只给前几条，"查看全部"再按类型翻页；相册只在第一页给。
/// </summary>
public sealed record SearchResultDto(
    string Query, List<string> Terms,
    List<MessageHitDto> Messages, string? MessagesNext,
    List<FileHitDto> Files, string? FilesNext,
    List<MomentHitDto> Moments, string? MomentsNext,
    List<AlbumHitDto> Albums, bool AlbumsMore);

/// <summary>
/// 搜索（docs/DESIGN.md 4.11）：聊天消息（正文、附件文件名）、网盘文件名、动态与评论、相册名。
/// - 可见性和各模块完全一致：我所在会话里没撤回的消息；我的个人空间、家庭空间、共享给我的（含子孙）且不在回收站的文件；
///   家人可见的动态和我自己的私密动态，评论跟着动态走；相册全家共享。
/// - 中文子串匹配：ILIKE + pg_trgm GIN 索引（三个字以上的词能用上索引；一两个字时退化为扫描，家庭规模够快）。
/// - 空格分词，每个词都要出现（AND）；% _ \ 按字面匹配。
/// - 新的在前，按 (时间, Id) 做游标分页。
/// </summary>
public sealed class SearchService(AppDbContext db)
{
    public const int MaxQueryLength = 100;
    public const int MaxTerms = 5;
    public const int PreviewCount = 5;
    public const int MaxLimit = 50;

    public async Task<SearchResultDto> SearchAsync(Guid me, string? q, SearchType type, string? cursor, int? limit, CancellationToken ct)
    {
        var terms = Terms(q);
        if (terms.Count == 0) throw ApiException.BadRequest("请输入要搜索的内容");
        var (before, beforeId) = ParseCursor(cursor);
        if (type == SearchType.All && before is not null) throw ApiException.BadRequest("翻页时请指定类型");
        var take = type == SearchType.All ? PreviewCount : Math.Clamp(limit ?? 20, 1, MaxLimit);
        var patterns = terms.Select(t => "%" + Escape(t) + "%").ToArray();
        // 最长的词最有区分度，单独作为能走索引的条件
        var main = patterns.OrderByDescending(p => p.Length).First();

        List<MessageHitDto> messages = [];
        List<FileHitDto> files = [];
        List<MomentHitDto> moments = [];
        List<AlbumHitDto> albums = [];
        var albumsMore = false;
        string? messagesNext = null, filesNext = null, momentsNext = null;

        if (type is SearchType.All or SearchType.Messages)
            (messages, messagesNext) = await MessagesAsync(me, main, patterns, terms, before, beforeId, take, ct);
        if (type is SearchType.All or SearchType.Files)
            (files, filesNext) = await FilesAsync(me, main, patterns, before, beforeId, take, ct);
        if (type is SearchType.All or SearchType.Moments)
        {
            (moments, momentsNext) = await MomentsAsync(me, main, patterns, terms, before, beforeId, take, ct);
            if (before is null) (albums, albumsMore) = await AlbumsAsync(main, patterns, type == SearchType.All ? PreviewCount : MaxLimit, ct);
        }
        return new SearchResultDto(q!.Trim(), terms, messages, messagesNext, files, filesNext, moments, momentsNext, albums, albumsMore);
    }

    // ---- 查询 ----

    private const string MessagesSql = """
        WITH hits AS (
            SELECT m.id AS message_id, NULL::uuid AS blob_id, NULL::text AS file_name, NULL::text AS mime, NULL::text AS kind
            FROM messages m
            WHERE m.type = 'Text' AND m.deleted_at IS NULL AND m.body ILIKE @main AND m.body ILIKE ALL (@all)
            UNION ALL
            SELECT a.message_id, a.blob_id, a.meta ->> 'name', a.meta ->> 'mime', a.kind
            FROM message_attachments a
            WHERE (a.meta ->> 'name') ILIKE @main AND (a.meta ->> 'name') ILIKE ALL (@all)
        )
        SELECT DISTINCT ON (m.created_at, m.id)
               m.id, m.conversation_id, m.seq, m.sender_id, m.created_at, m.body, h.blob_id, h.file_name, h.mime, h.kind
        FROM hits h
        JOIN messages m ON m.id = h.message_id
        JOIN conversation_members cm ON cm.conversation_id = m.conversation_id AND cm.user_id = @me
        WHERE m.deleted_at IS NULL
          AND (@before IS NULL OR (m.created_at, m.id) < (@before, @beforeId))
        ORDER BY m.created_at DESC, m.id DESC, h.file_name NULLS FIRST
        LIMIT @limit
        """;

    private async Task<(List<MessageHitDto>, string?)> MessagesAsync(
        Guid me, string main, string[] all, List<string> terms, DateTimeOffset? before, Guid beforeId, int take, CancellationToken ct)
    {
        await using var cmd = await CommandAsync(MessagesSql, me, main, all, before, beforeId, take + 1, ct);
        var rows = new List<MessageHitDto>();
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                var fileName = r.IsDBNull(7) ? null : r.GetString(7);
                rows.Add(new MessageHitDto(
                    r.GetGuid(0), r.GetGuid(1), r.GetInt64(2), r.GetGuid(3), r.GetFieldValue<DateTimeOffset>(4),
                    fileName is null ? "text" : "file",
                    fileName ?? Snippet(r.IsDBNull(5) ? "" : r.GetString(5), terms),
                    r.IsDBNull(6) ? null : r.GetGuid(6), fileName, r.IsDBNull(8) ? null : r.GetString(8), r.IsDBNull(9) ? null : r.GetString(9)));
            }
        return Page(rows, take, h => (h.CreatedAt, h.MessageId));
    }

    /// <summary>网盘的可见集合与相册时间线（GalleryService）相同：自己的个人空间、家庭空间、共享给我的节点及其子孙。新建的在前。</summary>
    private const string FilesSql = """
        WITH RECURSIVE shared AS (
            SELECT n.id FROM drive_nodes n
            JOIN node_shares s ON s.node_id = n.id
            WHERE s.grantee_user_id = @me AND (s.expires_at IS NULL OR s.expires_at > now()) AND n.deleted_at IS NULL
            UNION
            SELECT c.id FROM drive_nodes c JOIN shared p ON c.parent_id = p.id WHERE c.deleted_at IS NULL
        )
        SELECT n.id, n.space_id,
               CASE WHEN sp.type = 'Family' THEN 'Family' WHEN sp.owner_user_id = @me THEN 'Personal' ELSE 'Shared' END AS space,
               n.parent_id,
               COALESCE(sp.type = 'Family' OR sp.owner_user_id = @me OR n.parent_id IN (SELECT id FROM shared), false) AS parent_readable,
               n.name, n.is_folder, n.blob_id, n.mime, n.size, n.updated_at, n.created_at
        FROM drive_nodes n
        JOIN spaces sp ON sp.id = n.space_id
        WHERE n.deleted_at IS NULL AND n.name ILIKE @main AND n.name ILIKE ALL (@all)
          AND ((sp.type = 'Personal' AND sp.owner_user_id = @me) OR sp.type = 'Family' OR n.id IN (SELECT id FROM shared))
          AND (@before IS NULL OR (n.created_at, n.id) < (@before, @beforeId))
        ORDER BY n.created_at DESC, n.id DESC
        LIMIT @limit
        """;

    private async Task<(List<FileHitDto>, string?)> FilesAsync(
        Guid me, string main, string[] all, DateTimeOffset? before, Guid beforeId, int take, CancellationToken ct)
    {
        await using var cmd = await CommandAsync(FilesSql, me, main, all, before, beforeId, take + 1, ct);
        var rows = new List<FileHitDto>();
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                rows.Add(new FileHitDto(
                    r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetGuid(3), r.GetBoolean(4),
                    r.GetString(5), r.GetBoolean(6), r.IsDBNull(7) ? null : r.GetGuid(7), r.IsDBNull(8) ? null : r.GetString(8),
                    r.GetInt64(9), r.GetFieldValue<DateTimeOffset>(10), r.GetFieldValue<DateTimeOffset>(11)));
        // 按创建时间翻页：改名、移动会改 updated_at，用它做游标的话翻页时会漏掉 / 重复
        return Page(rows, take, h => (h.CreatedAt, h.NodeId));
    }

    /// <summary>动态和评论放在一起按时间排：评论只看它所在的动态我能不能看到（删动态时评论不会跟着标删除，所以要连上动态判断）。</summary>
    private const string MomentsSql = """
        SELECT kind, id, post_id, author_id, created_at, text FROM (
            SELECT 'post' AS kind, p.id, p.id AS post_id, p.author_id, p.created_at, p.text
            FROM posts p
            WHERE p.deleted_at IS NULL AND (p.visibility = 'Family' OR p.author_id = @me)
              AND p.text ILIKE @main AND p.text ILIKE ALL (@all)
            UNION ALL
            SELECT 'comment', c.id, c.post_id, c.author_id, c.created_at, c.text
            FROM comments c
            JOIN posts p ON p.id = c.post_id
            WHERE c.deleted_at IS NULL AND p.deleted_at IS NULL AND (p.visibility = 'Family' OR p.author_id = @me)
              AND c.text ILIKE @main AND c.text ILIKE ALL (@all)
        ) x
        WHERE (@before IS NULL OR (created_at, id) < (@before, @beforeId))
        ORDER BY created_at DESC, id DESC
        LIMIT @limit
        """;

    private async Task<(List<MomentHitDto>, string?)> MomentsAsync(
        Guid me, string main, string[] all, List<string> terms, DateTimeOffset? before, Guid beforeId, int take, CancellationToken ct)
    {
        await using var cmd = await CommandAsync(MomentsSql, me, main, all, before, beforeId, take + 1, ct);
        var rows = new List<MomentHitDto>();
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                rows.Add(new MomentHitDto(r.GetString(0), r.GetGuid(1), r.GetGuid(2), r.GetGuid(3), r.GetFieldValue<DateTimeOffset>(4), Snippet(r.GetString(5), terms)));
        return Page(rows, take, h => (h.CreatedAt, h.Id));
    }

    /// <summary>相册全家共享，按名字找；没设封面时用最近加进去的一张。</summary>
    private const string AlbumsSql = """
        SELECT a.id, a.title,
               COALESCE(a.cover_blob_id, (SELECT ai.blob_id FROM album_items ai WHERE ai.album_id = a.id ORDER BY ai.added_at DESC LIMIT 1)),
               (SELECT count(*) FROM album_items ai WHERE ai.album_id = a.id)::int,
               a.updated_at
        FROM albums a
        WHERE a.title ILIKE @main AND a.title ILIKE ALL (@all)
        ORDER BY a.updated_at DESC, a.id DESC
        LIMIT @limit
        """;

    /// <summary>相册不翻页（家里的相册不会多到哪去）："全部"给 5 个、"时光"给 50 个，more 表示还有更多。</summary>
    private async Task<(List<AlbumHitDto>, bool)> AlbumsAsync(string main, string[] all, int take, CancellationToken ct)
    {
        await using var cmd = await CommandAsync(AlbumsSql, Guid.Empty, main, all, null, Guid.Empty, take + 1, ct);
        var rows = new List<AlbumHitDto>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            rows.Add(new AlbumHitDto(r.GetGuid(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetGuid(2), r.GetInt32(3), r.GetFieldValue<DateTimeOffset>(4)));
        var more = rows.Count > take;
        if (more) rows.RemoveAt(rows.Count - 1);
        return (rows, more);
    }

    private async Task<DbCommand> CommandAsync(string sql, Guid me, string main, string[] all, DateTimeOffset? before, Guid beforeId, int limit, CancellationToken ct)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await db.Database.OpenConnectionAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 15;
        Add(cmd, "me", me, NpgsqlDbType.Uuid);
        Add(cmd, "main", main, NpgsqlDbType.Text);
        Add(cmd, "all", all, NpgsqlDbType.Array | NpgsqlDbType.Text);
        Add(cmd, "before", before?.ToUniversalTime(), NpgsqlDbType.TimestampTz);
        Add(cmd, "beforeId", beforeId, NpgsqlDbType.Uuid);
        Add(cmd, "limit", limit, NpgsqlDbType.Integer);
        return cmd;
    }

    private static void Add(DbCommand cmd, string name, object? value, NpgsqlDbType type)
        => cmd.Parameters.Add(new Npgsql.NpgsqlParameter(name, type) { Value = value ?? DBNull.Value });

    /// <summary>多取了一条：有第 take+1 条就说明还有下一页，游标指向本页最后一条。</summary>
    private static (List<T>, string?) Page<T>(List<T> rows, int take, Func<T, (DateTimeOffset At, Guid Id)> key)
    {
        if (rows.Count <= take) return (rows, null);
        rows.RemoveRange(take, rows.Count - take);
        var (at, id) = key(rows[^1]);
        return (rows, FormatCursor(at, id));
    }

    // ---- 查询词、游标、摘要 ----

    /// <summary>
    /// 按空白（含全角空格）和控制字符分词、去重；最多 5 个词，整串最多 100 个字。
    /// 不做 Unicode 规范化：库里存的是客户端原样发来的文字（macOS 上的文件名常是 NFD），规范化反而对不上。
    /// </summary>
    public static List<string> Terms(string? q)
    {
        if (string.IsNullOrWhiteSpace(q)) return [];
        q = q.Trim();
        if (q.Length > MaxQueryLength) throw ApiException.BadRequest($"搜索内容最多 {MaxQueryLength} 个字");
        var cleaned = new string([.. q.Select(c => char.IsControl(c) ? ' ' : c)]);
        return [.. cleaned.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxTerms)];
    }

    /// <summary>LIKE 的通配符按字面匹配（默认转义符就是反斜杠）。</summary>
    public static string Escape(string term) => term.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");

    public static string FormatCursor(DateTimeOffset at, Guid id) => $"{at.UtcTicks.ToString(CultureInfo.InvariantCulture)}.{id:N}";

    public static (DateTimeOffset? At, Guid Id) ParseCursor(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor)) return (null, Guid.Empty);
        var dot = cursor.IndexOf('.');
        if (dot <= 0 || !long.TryParse(cursor.AsSpan(0, dot), NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            || ticks < DateTimeOffset.MinValue.UtcTicks || ticks > DateTimeOffset.MaxValue.UtcTicks
            || !Guid.TryParseExact(cursor.AsSpan(dot + 1), "N", out var id))
            throw ApiException.BadRequest("翻页游标无效");
        return (new DateTimeOffset(ticks, TimeSpan.Zero), id);
    }

    /// <summary>命中附近的一段原文（前后各约 30 个字，超出用省略号），换行压成空格；不会切开代理对。</summary>
    public static string Snippet(string text, IReadOnlyList<string> terms, int radius = 30)
    {
        var flat = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (flat.Length <= radius * 3) return flat;
        var at = -1;
        var length = 0;
        foreach (var t in terms)
        {
            var i = flat.IndexOf(t, StringComparison.OrdinalIgnoreCase);
            if (i >= 0 && (at < 0 || i < at))
            {
                at = i;
                length = t.Length;
            }
        }
        if (at < 0) at = 0;
        var start = Math.Max(0, at - radius);
        var end = Math.Min(flat.Length, at + length + radius * 2);
        if (start > 0 && char.IsLowSurrogate(flat[start])) start--;
        if (end < flat.Length && char.IsLowSurrogate(flat[end])) end++;
        return (start > 0 ? "…" : "") + flat[start..end] + (end < flat.Length ? "…" : "");
    }
}

[ApiController]
[Authorize]
[Route("api/search")]
public sealed class SearchController(SearchService search) : ControllerBase
{
    /// <summary>
    /// 搜索聊天消息（正文、附件文件名）、网盘文件、动态与评论、相册。q 按空格分词，每个词都要出现。
    /// type = All 时每类给前 5 条；指定类型时按 cursor 翻页（limit 默认 20，最多 50）。
    /// </summary>
    [HttpGet]
    public Task<SearchResultDto> Search([FromQuery] string? q, [FromQuery] SearchType type = SearchType.All,
        [FromQuery] string? cursor = null, [FromQuery] int? limit = null, CancellationToken ct = default)
        => search.SearchAsync(User.UserId(), q, type, cursor, limit, ct);
}

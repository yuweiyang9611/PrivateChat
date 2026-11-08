using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using PrivateChat.Api.Domain;
using PrivateChat.Api.Infrastructure;

namespace PrivateChat.Api.Features.Drive;

/// <summary>对某个空间 / 节点能做什么。数值可比较：Owner &gt; Write &gt; Read &gt; None。</summary>
public enum DrivePermission { None = 0, Read = 1, Write = 2, Owner = 3 }

public sealed record DriveActor(Guid UserId, UserRole Role);

/// <summary>节点及其到根的祖先链上、给某人的有效共享。</summary>
public sealed record NodeAccess(DrivePermission Permission, List<Guid> Chain, HashSet<Guid> GrantedNodeIds);

/// <summary>
/// 网盘权限（docs/DESIGN.md 4.6）：
/// - 空间级：个人空间主人 Owner；家庭空间成员 Write（孩子在"只读"设置下 Read）；其他人 None。
/// - 节点级：共享给我的节点及其整棵子树 Read / Write（过期的不算）；有效权限取空间级与共享的较大者。
/// - 修改一个文件夹里的内容（新建、上传、改名、移入移出、删除）要对"这个文件夹"有 Write；
///   因此被共享的人能改共享文件夹里面的东西，但不能删除 / 改名 / 移走共享文件夹本身。
/// </summary>
public sealed class DriveAccess(AppDbContext db)
{
    public const int MaxDepth = 64;

    public static DrivePermission ForSpace(Space space, DriveActor actor) => space.Type switch
    {
        SpaceType.Personal => space.OwnerUserId == actor.UserId ? DrivePermission.Owner : DrivePermission.None,
        SpaceType.Family => actor.Role == UserRole.Child && space.ChildReadOnly ? DrivePermission.Read : DrivePermission.Write,
        _ => DrivePermission.None,
    };

    /// <summary>从节点自身往上到根的 id 链（自身在前）。</summary>
    public async Task<List<Guid>> ChainAsync(Guid nodeId, CancellationToken ct)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await db.Database.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        cmd.CommandText = """
            WITH RECURSIVE a AS (
                SELECT id, parent_id, 0 AS depth FROM drive_nodes WHERE id = @id
                UNION ALL
                SELECT n.id, n.parent_id, a.depth + 1 FROM drive_nodes n JOIN a ON n.id = a.parent_id WHERE a.depth < @max
            )
            SELECT id FROM a ORDER BY depth
            """;
        var p = cmd.CreateParameter(); p.ParameterName = "id"; p.Value = nodeId; cmd.Parameters.Add(p);
        var m = cmd.CreateParameter(); m.ParameterName = "max"; m.Value = MaxDepth; cmd.Parameters.Add(m);
        var result = new List<Guid>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(reader.GetGuid(0));
        return result;
    }

    /// <summary>
    /// ancestorId 是否是 nodeId 自身或它的某个祖先。不限深度（UNION 去重保证有环也会结束），
    /// 用于移动时防止把文件夹移进自己的子孙里——这里不能用有深度上限的 ChainAsync。
    /// </summary>
    public async Task<bool> IsSelfOrAncestorAsync(Guid ancestorId, Guid nodeId, CancellationToken ct)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await db.Database.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        cmd.CommandText = """
            WITH RECURSIVE a AS (
                SELECT id, parent_id FROM drive_nodes WHERE id = @node
                UNION
                SELECT n.id, n.parent_id FROM drive_nodes n JOIN a ON n.id = a.parent_id
            )
            SELECT EXISTS (SELECT 1 FROM a WHERE id = @ancestor)
            """;
        var p = cmd.CreateParameter(); p.ParameterName = "node"; p.Value = nodeId; cmd.Parameters.Add(p);
        var q = cmd.CreateParameter(); q.ParameterName = "ancestor"; q.Value = ancestorId; cmd.Parameters.Add(q);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>对节点的有效权限，连同祖先链与给我的共享所在节点（用于裁剪面包屑）。</summary>
    public async Task<NodeAccess> ForNodeAsync(DriveNode node, Space space, DriveActor actor, CancellationToken ct)
    {
        var spacePerm = ForSpace(space, actor);
        var chain = await ChainAsync(node.Id, ct);
        if (spacePerm >= DrivePermission.Write) return new NodeAccess(spacePerm, chain, []);

        var now = DateTimeOffset.UtcNow;
        var grants = await db.NodeShares
            .Where(s => chain.Contains(s.NodeId) && s.GranteeUserId == actor.UserId && (s.ExpiresAt == null || s.ExpiresAt > now))
            .Select(s => new { s.NodeId, s.Permission })
            .ToListAsync(ct);
        var shared = grants.Count == 0
            ? DrivePermission.None
            : grants.Any(g => g.Permission == SharePermission.Write) ? DrivePermission.Write : DrivePermission.Read;
        var perm = (DrivePermission)Math.Max((int)spacePerm, (int)shared);
        return new NodeAccess(perm, chain, grants.Select(g => g.NodeId).ToHashSet());
    }

    /// <summary>对"某个文件夹里的内容"的权限：folderId 为空表示空间根目录，只看空间级。</summary>
    public async Task<DrivePermission> ForContainerAsync(Space space, Guid? folderId, DriveActor actor, CancellationToken ct)
    {
        if (folderId is null) return ForSpace(space, actor);
        var folder = await db.DriveNodes.FirstOrDefaultAsync(n => n.Id == folderId && n.SpaceId == space.Id && n.IsFolder && n.DeletedAt == null, ct);
        if (folder is null) return DrivePermission.None;
        return (await ForNodeAsync(folder, space, actor, ct)).Permission;
    }

    /// <summary>某用户能不能读某个 blob（通过任一引用它、自己可读的网盘节点）。</summary>
    public async Task<bool> CanReadBlobViaDriveAsync(Guid blobId, DriveActor actor, CancellationToken ct)
    {
        var nodes = await db.DriveNodes.Where(n => n.BlobId == blobId && n.DeletedAt == null).ToListAsync(ct);
        if (nodes.Count == 0) return false;
        var spaceIds = nodes.Select(n => n.SpaceId).Distinct().ToList();
        var spaces = await db.Spaces.Where(s => spaceIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct);
        foreach (var n in nodes)
        {
            if (!spaces.TryGetValue(n.SpaceId, out var space)) continue;
            if (ForSpace(space, actor) >= DrivePermission.Read) return true;
        }
        foreach (var n in nodes)
        {
            if (!spaces.TryGetValue(n.SpaceId, out var space)) continue;
            if ((await ForNodeAsync(n, space, actor, ct)).Permission >= DrivePermission.Read) return true;
        }
        return false;
    }
}

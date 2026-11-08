using System.Data.Common;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using PrivateChat.Api.Domain;
using PrivateChat.Api.Features.Blobs;
using PrivateChat.Api.Features.Chat;
using PrivateChat.Api.Infrastructure;

namespace PrivateChat.Api.Features.Drive;

/// <summary>
/// 网盘：空间、目录、文件、改名移动、回收站（docs/DESIGN.md 4.6）。
/// 文件只是对内容寻址 blob 的引用：复制、发到聊天、存到网盘都不复制数据。
/// </summary>
public sealed class DriveService(AppDbContext db, DriveAccess access, BlobAccessService blobAccess, IHubContext<ChatHub> hub, IOptions<AppOptions> app)
{
    public static string SpaceName(Space s) => s.Type == SpaceType.Family ? "家庭共享" : "我的文件";

    // ---------------- 空间 ----------------

    /// <summary>每人一个个人空间、全家一个家庭空间，第一次用到时创建（并发创建靠唯一索引兜底）。</summary>
    public async Task<(Space Personal, Space Family)> EnsureSpacesAsync(Guid userId, CancellationToken ct)
    {
        var personal = await db.Spaces.FirstOrDefaultAsync(s => s.Type == SpaceType.Personal && s.OwnerUserId == userId, ct)
                       ?? await CreateSpaceAsync(SpaceType.Personal, userId, ct);
        var family = await db.Spaces.FirstOrDefaultAsync(s => s.Type == SpaceType.Family, ct)
                     ?? await CreateSpaceAsync(SpaceType.Family, null, ct);
        return (personal, family);
    }

    private async Task<Space> CreateSpaceAsync(SpaceType type, Guid? owner, CancellationToken ct)
    {
        var space = new Space { Type = type, OwnerUserId = owner };
        db.Spaces.Add(space);
        try
        {
            await db.SaveChangesAsync(ct);
            return space;
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            db.Entry(space).State = EntityState.Detached;
            return await db.Spaces.FirstAsync(s => s.Type == type && s.OwnerUserId == owner, ct);
        }
    }

    public async Task<long> UsedBytesAsync(Guid spaceId, CancellationToken ct)
        => await db.DriveNodes.Where(n => n.SpaceId == spaceId && !n.IsFolder).SumAsync(n => (long?)n.Size, ct) ?? 0;

    public async Task<SpaceDto> SpaceDtoAsync(Space s, DriveActor a, CancellationToken ct)
        => new(s.Id, s.Type, SpaceName(s), s.OwnerUserId, await UsedBytesAsync(s.Id, ct), s.QuotaBytes, DriveAccess.ForSpace(s, a), s.ChildReadOnly);

    public async Task<DriveOverviewDto> OverviewAsync(DriveActor a, CancellationToken ct)
    {
        var (personal, family) = await EnsureSpacesAsync(a.UserId, ct);
        var (total, free) = DiskInfo();
        return new DriveOverviewDto([await SpaceDtoAsync(personal, a, ct), await SpaceDtoAsync(family, a, ct)], total, free);
    }

    private (long Total, long Free) DiskInfo()
    {
        try
        {
            var root = Path.GetFullPath(app.Value.DataRoot);
            // 优先用数据卷自己的挂载点（容器里的 /data），否则退回所在盘
            var drive = DriveInfo.GetDrives()
                .Where(d => d.IsReady && root.StartsWith(d.RootDirectory.FullName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(d => d.RootDirectory.FullName.Length)
                .FirstOrDefault();
            return drive is null ? (0, 0) : (drive.TotalSize, drive.AvailableFreeSpace);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (0, 0);
        }
    }

    public async Task<Space> GetSpaceAsync(Guid id, CancellationToken ct)
        => await db.Spaces.FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw ApiException.NotFound("空间");

    public async Task<SpaceDto> PatchSpaceAsync(Guid id, PatchSpaceRequest req, DriveActor a, CancellationToken ct)
    {
        if (a.Role != UserRole.Admin) throw ApiException.Forbidden("只有管理员能修改空间设置");
        var space = await GetSpaceAsync(id, ct);
        if (req.ClearQuota == true) space.QuotaBytes = null;
        else if (req.QuotaBytes is { } q)
        {
            if (q < 0) throw ApiException.BadRequest("配额不能为负");
            space.QuotaBytes = q;
        }
        if (req.ChildReadOnly is { } ro)
        {
            if (space.Type != SpaceType.Family) throw ApiException.BadRequest("只有家庭空间能设置孩子只读");
            space.ChildReadOnly = ro;
        }
        await db.SaveChangesAsync(ct);
        return await SpaceDtoAsync(space, a, ct);
    }

    // ---------------- 浏览 ----------------

    private IQueryable<NodeDto> Project(IQueryable<DriveNode> q) =>
        from n in q
        join b in db.Blobs on n.BlobId equals b.Id into bj
        from b in bj.DefaultIfEmpty()
        select new NodeDto(
            n.Id, n.SpaceId, n.ParentId, n.Name, n.IsFolder, n.BlobId, n.Size, n.Mime, n.CreatedBy, n.CreatedAt, n.UpdatedAt,
            db.NodeShares.Any(s => s.NodeId == n.Id),
            b == null ? null : b.Width, b == null ? null : b.Height, b == null ? null : b.DurationSeconds, b == null ? null : b.TakenAt);

    public async Task<NodeDto> NodeDtoAsync(Guid id, CancellationToken ct)
        => await Project(db.DriveNodes.Where(n => n.Id == id)).FirstAsync(ct);

    public async Task<ListingDto> ListAsync(Guid spaceId, Guid? folderId, DriveActor a, CancellationToken ct)
    {
        var space = await GetSpaceAsync(spaceId, ct);
        DriveNode? folder = null;
        DrivePermission perm;
        var path = new List<NodeDto>();
        if (folderId is null)
        {
            perm = DriveAccess.ForSpace(space, a);
            if (perm < DrivePermission.Read) throw ApiException.Forbidden("无权查看该空间");
        }
        else
        {
            folder = await db.DriveNodes.FirstOrDefaultAsync(n => n.Id == folderId && n.SpaceId == spaceId && n.IsFolder && n.DeletedAt == null, ct)
                     ?? throw ApiException.NotFound("文件夹");
            var acc = await access.ForNodeAsync(folder, space, a, ct);
            perm = acc.Permission;
            if (perm < DrivePermission.Read) throw ApiException.Forbidden("无权查看该文件夹");
            // 面包屑：只通过共享看到的，从最上面那个共享给我的节点开始，不暴露主人更上层的目录
            var top = acc.Chain.Count - 1;
            if (DriveAccess.ForSpace(space, a) < DrivePermission.Read)
                top = acc.Chain.FindLastIndex(id => acc.GrantedNodeIds.Contains(id));
            var ids = acc.Chain.Take(top + 1).ToList();
            var dtos = await Project(db.DriveNodes.Where(n => ids.Contains(n.Id))).ToListAsync(ct);
            path = ids.AsEnumerable().Reverse().Select(id => dtos.First(d => d.Id == id)).ToList();
        }

        // 构造函数投影的记录没法在 SQL 里排序：取出来再排（一个文件夹的条目数在家庭规模下很小）
        var items = (await Project(db.DriveNodes.Where(n => n.SpaceId == spaceId && n.ParentId == folderId && n.DeletedAt == null)).ToListAsync(ct))
            .OrderByDescending(n => n.IsFolder).ThenBy(n => n.Name, StringComparer.CurrentCulture)
            .ToList();
        return new ListingDto(await SpaceDtoAsync(space, a, ct), path.LastOrDefault(), path, perm, items);
    }

    /// <summary>移动对话框用：空间里所有文件夹（空间级可写才给）。</summary>
    public async Task<List<FolderRefDto>> FoldersAsync(Guid spaceId, DriveActor a, CancellationToken ct)
    {
        var space = await GetSpaceAsync(spaceId, ct);
        if (DriveAccess.ForSpace(space, a) < DrivePermission.Write) throw ApiException.Forbidden("无权移动到该空间");
        return await db.DriveNodes.Where(n => n.SpaceId == spaceId && n.IsFolder && n.DeletedAt == null)
            .OrderBy(n => n.Name)
            .Select(n => new FolderRefDto(n.Id, n.ParentId, n.Name))
            .ToListAsync(ct);
    }

    // ---------------- 新建 ----------------

    private async Task<HashSet<string>> SiblingNamesAsync(Guid spaceId, Guid? parentId, Guid? exceptId, CancellationToken ct)
        => (await db.DriveNodes.Where(n => n.SpaceId == spaceId && n.ParentId == parentId && n.DeletedAt == null && n.Id != exceptId)
            .Select(n => n.Name).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);

    private async Task RequireContainerWriteAsync(Space space, Guid? folderId, DriveActor a, CancellationToken ct)
    {
        if (folderId is { } fid && !await db.DriveNodes.AnyAsync(n => n.Id == fid && n.SpaceId == space.Id && n.IsFolder && n.DeletedAt == null, ct))
            throw ApiException.NotFound("目标文件夹");
        if (await access.ForContainerAsync(space, folderId, a, ct) < DrivePermission.Write)
            throw ApiException.Forbidden("没有这个文件夹的写权限");
    }

    /// <summary>插入节点；同名并发冲突时换下一个名字重试。</summary>
    private async Task<DriveNode> InsertWithFreeNameAsync(DriveNode node, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var taken = await SiblingNamesAsync(node.SpaceId, node.ParentId, null, ct);
            node.Name = DriveNames.NextFree(node.Name, node.IsFolder, taken);
            db.DriveNodes.Add(node);
            try
            {
                await db.SaveChangesAsync(ct);
                return node;
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex) && attempt < 3)
            {
                db.Entry(node).State = EntityState.Detached;
            }
        }
    }

    public async Task<NodeDto> CreateFolderAsync(CreateFolderRequest req, DriveActor a, CancellationToken ct)
    {
        var space = await GetSpaceAsync(req.SpaceId, ct);
        var name = DriveNames.Validate(req.Name);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockSpacesAsync(db, ct, space.Id);
        await RequireContainerWriteAsync(space, req.ParentId, a, ct);
        var node = await InsertWithFreeNameAsync(new DriveNode
        {
            SpaceId = space.Id, ParentId = req.ParentId, Name = name, IsFolder = true, CreatedBy = a.UserId,
        }, ct);
        await tx.CommitAsync(ct);
        await NotifyAsync(space, req.ParentId, ct);
        return await NodeDtoAsync(node.Id, ct);
    }

    /// <summary>上传完成（或秒传命中）后登记成网盘文件。</summary>
    public async Task<NodeDto> CreateFileAsync(CreateFileRequest req, DriveActor a, CancellationToken ct)
    {
        var space = await GetSpaceAsync(req.SpaceId, ct);
        var name = DriveNames.Sanitize(req.Name);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockSpacesAsync(db, ct, space.Id);
        await RequireContainerWriteAsync(space, req.ParentId, a, ct);
        var blob = await db.Blobs.FirstOrDefaultAsync(b => b.Id == req.BlobId, ct) ?? throw ApiException.NotFound("文件内容");
        if (!await blobAccess.CanReferenceAsync(blob.Id, a.UserId, ct)) throw ApiException.Forbidden("无权使用该文件内容");
        await EnsureQuotaAsync(space, blob.Size, ct);

        if (req.OnConflict == NameConflict.Replace)
        {
            // 同名覆盖：旧文件进回收站（30 天内可找回），新文件用原名
            var old = await db.DriveNodes.FirstOrDefaultAsync(n => n.SpaceId == space.Id && n.ParentId == req.ParentId && n.Name == name && n.DeletedAt == null, ct);
            if (old is { IsFolder: true }) throw ApiException.Conflict("已有同名文件夹");
            if (old is not null) await TrashSubtreeAsync(old.Id, a.UserId, ct);
        }
        var node = await InsertWithFreeNameAsync(new DriveNode
        {
            SpaceId = space.Id, ParentId = req.ParentId, Name = name, IsFolder = false,
            BlobId = blob.Id, Size = blob.Size, Mime = blob.Mime, CreatedBy = a.UserId,
        }, ct);
        await tx.CommitAsync(ct);
        await NotifyAsync(space, req.ParentId, ct);
        return await NodeDtoAsync(node.Id, ct);
    }

    private async Task EnsureQuotaAsync(Space space, long adding, CancellationToken ct)
    {
        if (space.QuotaBytes is not { } quota) return;
        if (await UsedBytesAsync(space.Id, ct) + adding > quota) throw new ApiException(413, "空间已满", "超出该空间的配额，请先清理回收站或联系管理员");
    }

    // ---------------- 改名 / 移动 ----------------

    public async Task<NodeDto> PatchAsync(Guid id, PatchNodeRequest req, DriveActor a, CancellationToken ct)
    {
        var node = await db.DriveNodes.FirstOrDefaultAsync(n => n.Id == id && n.DeletedAt == null, ct) ?? throw ApiException.NotFound("文件");
        var space = await GetSpaceAsync(node.SpaceId, ct);
        var targetSpace = req.MoveTo is { } m && m.SpaceId != space.Id ? await GetSpaceAsync(m.SpaceId, ct) : space;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockSpacesAsync(db, ct, space.Id, targetSpace.Id);
        await ReloadLiveAsync(node, space.Id, ct);
        // 改名、移走都是在改"它所在的文件夹"
        await RequireContainerWriteAsync(space, node.ParentId, a, ct);
        var oldParent = node.ParentId;

        if (req.MoveTo is { } to)
        {
            if (targetSpace.Id != space.Id
                && (DriveAccess.ForSpace(space, a) < DrivePermission.Write || DriveAccess.ForSpace(targetSpace, a) < DrivePermission.Write))
                throw ApiException.Forbidden("跨空间移动需要两个空间都有写权限");
            await RequireContainerWriteAsync(targetSpace, to.ParentId, a, ct);
            if (to.ParentId is { } targetFolder && node.IsFolder && await access.IsSelfOrAncestorAsync(node.Id, targetFolder, ct))
                throw ApiException.BadRequest("不能把文件夹移到它自己里面");
            if (targetSpace.Id != space.Id) await EnsureQuotaAsync(targetSpace, await SubtreeBytesAsync(node.Id, ct), ct);
        }

        var newName = req.Name is null ? node.Name : DriveNames.Validate(req.Name);
        var newParent = req.MoveTo?.ParentId ?? (req.MoveTo is null ? node.ParentId : null);
        var taken = await SiblingNamesAsync(targetSpace.Id, newParent, node.Id, ct);
        if (taken.Contains(newName))
        {
            // 改名撞名让用户自己决定；移动撞名自动"保留两者"
            if (req.MoveTo is null) throw ApiException.Conflict("同一文件夹里已有同名的文件或文件夹");
            newName = DriveNames.NextFree(newName, node.IsFolder, taken);
        }

        node.Name = newName;
        node.ParentId = newParent;
        node.SpaceId = targetSpace.Id;
        node.UpdatedAt = DateTimeOffset.UtcNow;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            throw ApiException.Conflict("同一文件夹里已有同名的文件或文件夹");
        }
        if (targetSpace.Id != space.Id && node.IsFolder)
        {
            // 整棵（未删除的）子树换空间。之前已经删进回收站的留在原空间：
            // 它们属于原空间的回收站，还原时原文件夹已不在本空间，会回到原空间根目录。
            await ExecAsync("""
                WITH RECURSIVE sub AS (
                    SELECT id FROM drive_nodes WHERE parent_id = @id AND deleted_at IS NULL
                    UNION
                    SELECT n.id FROM drive_nodes n JOIN sub ON n.parent_id = sub.id WHERE n.deleted_at IS NULL
                )
                UPDATE drive_nodes SET space_id = @space WHERE id IN (SELECT id FROM sub)
                """, ct, ("id", node.Id), ("space", targetSpace.Id));
        }
        await tx.CommitAsync(ct);

        await NotifyAsync(space, oldParent, ct);
        if (targetSpace.Id != space.Id || newParent != oldParent) await NotifyAsync(targetSpace, newParent, ct);
        return await NodeDtoAsync(node.Id, ct);
    }

    private async Task<long> SubtreeBytesAsync(Guid id, CancellationToken ct)
    {
        var v = await ScalarAsync("""
            WITH RECURSIVE sub AS (
                SELECT id, size, is_folder FROM drive_nodes WHERE id = @id
                UNION
                SELECT n.id, n.size, n.is_folder FROM drive_nodes n JOIN sub ON n.parent_id = sub.id WHERE n.deleted_at IS NULL
            )
            SELECT COALESCE(SUM(size) FILTER (WHERE NOT is_folder), 0) FROM sub
            """, ct, ("id", id));
        return Convert.ToInt64(v);
    }

    // ---------------- 删除 / 回收站 ----------------

    public async Task DeleteAsync(Guid id, DriveActor a, CancellationToken ct)
    {
        var node = await db.DriveNodes.FirstOrDefaultAsync(n => n.Id == id && n.DeletedAt == null, ct) ?? throw ApiException.NotFound("文件");
        var space = await GetSpaceAsync(node.SpaceId, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockSpacesAsync(db, ct, space.Id);
        await ReloadLiveAsync(node, space.Id, ct);
        await RequireContainerWriteAsync(space, node.ParentId, a, ct);
        await TrashSubtreeAsync(node.Id, a.UserId, ct);
        await tx.CommitAsync(ct);
        await NotifyAsync(space, node.ParentId, ct);
    }

    /// <summary>整棵子树一起进回收站，记下这次删除的根。</summary>
    private Task TrashSubtreeAsync(Guid rootId, Guid userId, CancellationToken ct) => ExecAsync("""
        WITH RECURSIVE sub AS (
            SELECT id FROM drive_nodes WHERE id = @id AND deleted_at IS NULL
            UNION
            SELECT n.id FROM drive_nodes n JOIN sub ON n.parent_id = sub.id WHERE n.deleted_at IS NULL
        )
        UPDATE drive_nodes SET deleted_at = now(), trash_root_id = @id, deleted_by = @user WHERE id IN (SELECT id FROM sub)
        """, ct, ("id", rootId), ("user", userId));

    /// <summary>
    /// 能管理这条回收站记录：所在空间空间级可写（主人 / 家庭成员）；或者是自己删的、并且现在对原来所在的文件夹仍有写权限
    /// （共享被收回、孩子被设为只读、原文件夹已不在或已移到别的空间，就不能再还原 / 彻底删除了）。
    /// </summary>
    private async Task<bool> CanManageTrashAsync(DriveNode root, Space space, DriveActor a, CancellationToken ct)
        => DriveAccess.ForSpace(space, a) >= DrivePermission.Write
           || (root.DeletedBy == a.UserId && await access.ForContainerAsync(space, root.ParentId, a, ct) >= DrivePermission.Write);

    public async Task<List<TrashItemDto>> TrashAsync(DriveActor a, CancellationToken ct)
    {
        var roots = await db.DriveNodes.Where(n => n.TrashRootId == n.Id).ToListAsync(ct);
        if (roots.Count == 0) return [];
        var spaceIds = roots.Select(r => r.SpaceId).Distinct().ToList();
        var spaces = await db.Spaces.Where(s => spaceIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct);
        var mine = new List<DriveNode>();
        foreach (var r in roots)
            if (spaces.TryGetValue(r.SpaceId, out var s) && await CanManageTrashAsync(r, s, a, ct)) mine.Add(r);
        if (mine.Count == 0) return [];
        var ids = mine.Select(r => r.Id).ToList();
        var stats = await db.DriveNodes.Where(n => n.TrashRootId != null && ids.Contains(n.TrashRootId.Value))
            .GroupBy(n => n.TrashRootId!.Value)
            .Select(g => new { Id = g.Key, Bytes = g.Where(x => !x.IsFolder).Sum(x => x.Size), Count = g.Count() })
            .ToDictionaryAsync(x => x.Id, ct);
        var dtos = await Project(db.DriveNodes.Where(n => ids.Contains(n.Id))).ToDictionaryAsync(d => d.Id, ct);
        return mine.OrderByDescending(r => r.DeletedAt)
            .Select(r => new TrashItemDto(dtos[r.Id], r.DeletedAt!.Value, r.DeletedBy, SpaceName(spaces[r.SpaceId]),
                stats.TryGetValue(r.Id, out var st) ? st.Bytes : 0, stats.TryGetValue(r.Id, out var st2) ? st2.Count : 1))
            .ToList();
    }

    private async Task<(DriveNode Root, Space Space)> TrashRootAsync(Guid id, DriveActor a, CancellationToken ct)
    {
        var root = await db.DriveNodes.FirstOrDefaultAsync(n => n.Id == id && n.TrashRootId == id, ct) ?? throw ApiException.NotFound("回收站项目");
        var space = await GetSpaceAsync(root.SpaceId, ct);
        if (!await CanManageTrashAsync(root, space, a, ct)) throw ApiException.Forbidden("无权处理该回收站项目");
        return (root, space);
    }

    /// <summary>还原到原位置；原文件夹已不在（被删 / 被清 / 移到别的空间）就还原到空间根目录；重名自动改名。</summary>
    public async Task<NodeDto> RestoreAsync(Guid id, DriveActor a, CancellationToken ct)
    {
        var (root, space) = await TrashRootAsync(id, a, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockSpacesAsync(db, ct, space.Id);
        await db.Entry(root).ReloadAsync(ct);
        if (db.Entry(root).State == EntityState.Detached || root.TrashRootId != id) throw ApiException.NotFound("回收站项目");
        var parent = root.ParentId;
        if (parent is { } pid && !await db.DriveNodes.AnyAsync(n => n.Id == pid && n.SpaceId == space.Id && n.DeletedAt == null, ct))
            parent = null;
        root.ParentId = parent;
        root.Name = DriveNames.NextFree(root.Name, root.IsFolder, await SiblingNamesAsync(space.Id, parent, root.Id, ct));
        await db.SaveChangesAsync(ct);
        await ExecAsync("UPDATE drive_nodes SET deleted_at = NULL, trash_root_id = NULL, deleted_by = NULL WHERE trash_root_id = @id", ct, ("id", id));
        await tx.CommitAsync(ct);
        await NotifyAsync(space, parent, ct);
        return await NodeDtoAsync(root.Id, ct);
    }

    public async Task PurgeAsync(Guid id, DriveActor a, CancellationToken ct)
    {
        var (_, space) = await TrashRootAsync(id, a, ct);
        await PurgeRootAsync(db, space.Id, id, ct);
    }

    public async Task<int> EmptyTrashAsync(DriveActor a, CancellationToken ct)
    {
        var items = await TrashAsync(a, ct);
        foreach (var item in items) await PurgeRootAsync(db, item.Node.SpaceId, item.Node.Id, ct);
        return items.Count;
    }

    /// <summary>彻底删除一次删除操作的整批节点（及其共享）。blob 由垃圾回收在没人引用后清理。</summary>
    public static async Task PurgeRootAsync(AppDbContext db, Guid spaceId, Guid rootId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockSpacesAsync(db, ct, spaceId);
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM node_shares WHERE node_id IN (SELECT id FROM drive_nodes WHERE trash_root_id = {rootId})", ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM drive_nodes WHERE trash_root_id = {rootId}", ct);
        await tx.CommitAsync(ct);
    }

    // ---------------- 并发 ----------------

    /// <summary>
    /// 同一空间的目录树修改串行化：事务级 advisory lock，提交 / 回滚时自动释放。
    /// 拿到锁之后再做存在性与权限检查，这样"往正在被删除的文件夹里新建 / 移入"不会留下挂在已删除父节点下的活节点，
    /// 配额检查也不会被并发上传绕过。多个空间按 Guid 顺序加锁，避免互相等待。必须在事务里调用。
    /// </summary>
    public static async Task LockSpacesAsync(AppDbContext db, CancellationToken ct, params Guid[] spaceIds)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("LockSpacesAsync 必须在事务中调用");
        foreach (var id in spaceIds.Distinct().Order())
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({id.ToString()}, 0))", ct);
    }

    /// <summary>拿到锁后重新读节点：拿锁前读到的可能已被并发删除或移到别的空间。</summary>
    private async Task ReloadLiveAsync(DriveNode node, Guid spaceId, CancellationToken ct)
    {
        await db.Entry(node).ReloadAsync(ct);
        if (db.Entry(node).State == EntityState.Detached || node.DeletedAt is not null) throw ApiException.NotFound("文件");
        if (node.SpaceId != spaceId) throw ApiException.Conflict("文件刚被移动到别处，请刷新后重试");
    }

    // ---------------- 实时通知 ----------------

    /// <summary>某个文件夹内容变了：家庭空间通知所有人；个人空间通知主人和被共享过该空间节点的人。</summary>
    public async Task NotifyAsync(Space space, Guid? parentId, CancellationToken ct)
    {
        var dto = new DriveChangedDto(space.Id, parentId);
        if (space.Type == SpaceType.Family)
        {
            await hub.Clients.All.SendAsync(ChatEvents.DriveChanged, dto, ct);
            return;
        }
        var users = await db.NodeShares
            .Where(s => s.GranteeUserId != null && db.DriveNodes.Any(n => n.Id == s.NodeId && n.SpaceId == space.Id))
            .Select(s => s.GranteeUserId!.Value).Distinct().ToListAsync(ct);
        if (space.OwnerUserId is { } owner) users.Add(owner);
        foreach (var u in users.Distinct())
            await hub.Clients.Group(ChatEvents.UserGroup(u)).SendAsync(ChatEvents.DriveChanged, dto, ct);
    }

    // ---------------- 工具 ----------------

    public static bool IsUniqueViolation(DbUpdateException ex)
        => ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation };

    private async Task<DbCommand> CommandAsync(string sql, CancellationToken ct, params (string Name, object Value)[] ps)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await db.Database.OpenConnectionAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        cmd.CommandText = sql;
        foreach (var (name, value) in ps)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value;
            cmd.Parameters.Add(p);
        }
        return cmd;
    }

    private async Task ExecAsync(string sql, CancellationToken ct, params (string, object)[] ps)
    {
        await using var cmd = await CommandAsync(sql, ct, ps);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<object?> ScalarAsync(string sql, CancellationToken ct, params (string, object)[] ps)
    {
        await using var cmd = await CommandAsync(sql, ct, ps);
        return await cmd.ExecuteScalarAsync(ct);
    }
}

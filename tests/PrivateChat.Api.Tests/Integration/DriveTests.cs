using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PrivateChat.Api.Domain;
using PrivateChat.Api.Features.Chat;
using PrivateChat.Api.Features.Drive;

namespace PrivateChat.Api.Tests.Integration;

/// <summary>网盘的权限、回收站、共享、外链、发到聊天、相册、垃圾回收：对真实数据库跑完整 HTTP 流程。</summary>
public class DriveTests
{
    private sealed record Ctx(ApiFactory App, HttpClient Admin, Guid AdminId, User Bob, HttpClient BobClient, User Kid, HttpClient KidClient, DriveOverviewDto AdminDrive, DriveOverviewDto BobDrive) : IAsyncDisposable
    {
        public SpaceDto AdminPersonal => AdminDrive.Spaces.Single(s => s.Type == SpaceType.Personal);
        public SpaceDto Family => AdminDrive.Spaces.Single(s => s.Type == SpaceType.Family);
        public SpaceDto BobPersonal => BobDrive.Spaces.Single(s => s.Type == SpaceType.Personal);
        public ValueTask DisposeAsync() => App.DisposeAsync();
    }

    private static async Task<Ctx> SetupAsync()
    {
        var app = new ApiFactory();
        var admin = await app.LoginAsync("admin@example.com");
        var adminId = (await (await admin.GetAsync("/api/auth/me")).ReadAsync<Features.Auth.UserDto>()).Id;
        var bob = await app.AddUserAsync("bob", UserRole.Member);
        var kid = await app.AddUserAsync("kid", UserRole.Child);
        var bobClient = await app.LoginAsync("bob");
        var kidClient = await app.LoginAsync("kid");
        var adminDrive = await (await admin.GetAsync("/api/drive")).ReadAsync<DriveOverviewDto>();
        var bobDrive = await (await bobClient.GetAsync("/api/drive")).ReadAsync<DriveOverviewDto>();
        return new Ctx(app, admin, adminId, bob, bobClient, kid, kidClient, adminDrive, bobDrive);
    }

    private static async Task<NodeDto> FolderAsync(HttpClient c, Guid space, Guid? parent, string name)
        => await (await c.PostJsonAsync("/api/drive/folders", new { spaceId = space, parentId = parent, name })).ReadAsync<NodeDto>();

    private static async Task<NodeDto> FileAsync(HttpClient c, Guid space, Guid? parent, Guid blobId, string name, string onConflict = "Rename")
        => await (await c.PostJsonAsync("/api/drive/files", new { spaceId = space, parentId = parent, blobId, name, onConflict })).ReadAsync<NodeDto>();

    private static async Task<ListingDto> ListAsync(HttpClient c, Guid space, Guid? folder = null)
        => await (await c.GetAsync($"/api/drive/list?spaceId={space}" + (folder is null ? "" : $"&folderId={folder}"))).ReadAsync<ListingDto>();

    [PgFact]
    public async Task Spaces_are_created_once_per_person_and_one_family_space()
    {
        await using var x = await SetupAsync();
        Assert.Equal(x.Family.Id, x.BobDrive.Spaces.Single(s => s.Type == SpaceType.Family).Id);
        Assert.NotEqual(x.AdminPersonal.Id, x.BobPersonal.Id);
        Assert.Equal(DrivePermission.Owner, x.AdminPersonal.Permission);
        Assert.Equal(DrivePermission.Write, x.Family.Permission);
        var again = await (await x.Admin.GetAsync("/api/drive")).ReadAsync<DriveOverviewDto>();
        Assert.Equal(x.AdminPersonal.Id, again.Spaces.Single(s => s.Type == SpaceType.Personal).Id);
        Assert.Equal(1, await x.App.WithDbAsync(db => db.Spaces.CountAsync(s => s.Type == SpaceType.Family)));
    }

    [PgFact]
    public async Task Names_are_unique_per_folder_and_conflicts_get_numbered()
    {
        await using var x = await SetupAsync();
        var blob = await x.App.AddBlobAsync(x.AdminId);
        var a = await FileAsync(x.Admin, x.AdminPersonal.Id, null, blob.Id, "照片.jpg");
        var b = await FileAsync(x.Admin, x.AdminPersonal.Id, null, blob.Id, "照片.jpg");
        var f1 = await FolderAsync(x.Admin, x.AdminPersonal.Id, null, "旅行");
        var f2 = await FolderAsync(x.Admin, x.AdminPersonal.Id, null, "旅行");
        Assert.Equal(("照片.jpg", "照片 (1).jpg", "旅行", "旅行 (1)"), (a.Name, b.Name, f1.Name, f2.Name));

        // 改名撞名由用户决定：409
        var res = await x.Admin.PatchJsonAsync($"/api/drive/nodes/{b.Id}", new { name = "照片.jpg" });
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        // 不合法的名字拒绝
        res = await x.Admin.PostJsonAsync("/api/drive/folders", new { spaceId = x.AdminPersonal.Id, name = "a/b" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);

        // 覆盖：旧文件进回收站，新文件用原名
        var blob2 = await x.App.AddBlobAsync(x.AdminId);
        var replaced = await FileAsync(x.Admin, x.AdminPersonal.Id, null, blob2.Id, "照片.jpg", "Replace");
        Assert.Equal("照片.jpg", replaced.Name);
        var trash = await (await x.Admin.GetAsync("/api/drive/trash")).ReadAsync<List<TrashItemDto>>();
        Assert.Contains(trash, t => t.Node.Id == a.Id);
    }

    [PgFact]
    public async Task Personal_space_is_private_until_shared_and_sharing_covers_the_subtree()
    {
        await using var x = await SetupAsync();
        var trip = await FolderAsync(x.Admin, x.AdminPersonal.Id, null, "旅行");
        var day1 = await FolderAsync(x.Admin, x.AdminPersonal.Id, trip.Id, "第一天");
        var blob = await x.App.AddBlobAsync(x.AdminId);
        var photo = await FileAsync(x.Admin, x.AdminPersonal.Id, day1.Id, blob.Id, "a.png");

        Assert.Equal(HttpStatusCode.Forbidden, (await x.BobClient.GetAsync($"/api/drive/list?spaceId={x.AdminPersonal.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await x.BobClient.GetAsync($"/api/drive/list?spaceId={x.AdminPersonal.Id}&folderId={trip.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await x.BobClient.GetAsync($"/api/blobs/{blob.Id}")).StatusCode);

        // 只读共享"旅行"给 Bob：能看整棵子树、能读文件，不能往里写
        await (await x.Admin.PostJsonAsync($"/api/drive/nodes/{trip.Id}/shares", new { granteeUserId = x.Bob.Id, permission = "Read" })).ReadAsync<ShareDto>();
        var inner = await ListAsync(x.BobClient, x.AdminPersonal.Id, day1.Id);
        Assert.Equal(DrivePermission.Read, inner.Permission);
        Assert.Equal(["旅行", "第一天"], inner.Path.Select(p => p.Name));
        Assert.Equal(HttpStatusCode.OK, (await x.BobClient.GetAsync($"/api/blobs/{blob.Id}")).StatusCode);
        var res = await x.BobClient.PostJsonAsync("/api/drive/folders", new { spaceId = x.AdminPersonal.Id, parentId = day1.Id, name = "x" });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        var shared = await (await x.BobClient.GetAsync("/api/drive/shared-with-me")).ReadAsync<List<SharedNodeDto>>();
        Assert.Equal(trip.Id, Assert.Single(shared).Node.Id);

        // 可写共享：里面可以新建 / 删除，但共享文件夹本身不能删、不能改名
        await (await x.Admin.PostJsonAsync($"/api/drive/nodes/{trip.Id}/shares", new { granteeUserId = x.Bob.Id, permission = "Write" })).ReadAsync<ShareDto>();
        var bobsFolder = await FolderAsync(x.BobClient, x.AdminPersonal.Id, trip.Id, "爸爸补的照片");
        Assert.Equal(HttpStatusCode.NoContent, (await x.BobClient.DeleteAsync($"/api/drive/nodes/{bobsFolder.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await x.BobClient.DeleteAsync($"/api/drive/nodes/{trip.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await x.BobClient.PatchJsonAsync($"/api/drive/nodes/{trip.Id}", new { name = "改了" })).StatusCode);
        // 被共享的人不能把东西移到自己的空间（跨空间需要两边空间级写权限），也不能再转共享
        res = await x.BobClient.PatchJsonAsync($"/api/drive/nodes/{photo.Id}", new { moveTo = new { spaceId = x.BobPersonal.Id, parentId = (Guid?)null } });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        res = await x.BobClient.PostJsonAsync($"/api/drive/nodes/{photo.Id}/shares", new { link = true });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);

        // 撤销后又看不到了
        var shares = await (await x.Admin.GetAsync($"/api/drive/nodes/{trip.Id}/shares")).ReadAsync<List<ShareDto>>();
        Assert.Equal(HttpStatusCode.NoContent, (await x.Admin.DeleteAsync($"/api/drive/shares/{Assert.Single(shares).Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await x.BobClient.GetAsync($"/api/drive/list?spaceId={x.AdminPersonal.Id}&folderId={trip.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await x.BobClient.GetAsync($"/api/blobs/{blob.Id}")).StatusCode);
    }

    [PgFact]
    public async Task Family_space_is_shared_and_child_read_only_is_enforced()
    {
        await using var x = await SetupAsync();
        var docs = await FolderAsync(x.BobClient, x.Family.Id, null, "证件");
        Assert.Contains((await ListAsync(x.Admin, x.Family.Id)).Items, i => i.Id == docs.Id);
        await FolderAsync(x.KidClient, x.Family.Id, null, "我的画");

        // 管理员把家庭空间设为孩子只读
        var forbidden = await x.BobClient.PatchJsonAsync($"/api/drive/spaces/{x.Family.Id}", new { childReadOnly = true });
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        await (await x.Admin.PatchJsonAsync($"/api/drive/spaces/{x.Family.Id}", new { childReadOnly = true })).ReadAsync<SpaceDto>();
        Assert.Equal(DrivePermission.Read, (await ListAsync(x.KidClient, x.Family.Id)).Permission);
        var res = await x.KidClient.PostJsonAsync("/api/drive/folders", new { spaceId = x.Family.Id, name = "不行" });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        // 孩子不能建外链
        var blob = await x.App.AddBlobAsync(x.Kid.Id);
        var kidFile = await FileAsync(x.KidClient, (await (await x.KidClient.GetAsync("/api/drive")).ReadAsync<DriveOverviewDto>()).Spaces.Single(s => s.Type == SpaceType.Personal).Id, null, blob.Id, "画.png");
        res = await x.KidClient.PostJsonAsync($"/api/drive/nodes/{kidFile.Id}/shares", new { link = true });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [PgFact]
    public async Task Move_rejects_cycles_and_moves_whole_subtree_across_spaces()
    {
        await using var x = await SetupAsync();
        var a = await FolderAsync(x.Admin, x.AdminPersonal.Id, null, "A");
        var b = await FolderAsync(x.Admin, x.AdminPersonal.Id, a.Id, "B");
        var blob = await x.App.AddBlobAsync(x.AdminId);
        var file = await FileAsync(x.Admin, x.AdminPersonal.Id, b.Id, blob.Id, "f.png");

        var res = await x.Admin.PatchJsonAsync($"/api/drive/nodes/{a.Id}", new { moveTo = new { spaceId = x.AdminPersonal.Id, parentId = b.Id } });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);

        var moved = await (await x.Admin.PatchJsonAsync($"/api/drive/nodes/{a.Id}", new { moveTo = new { spaceId = x.Family.Id, parentId = (Guid?)null } })).ReadAsync<NodeDto>();
        Assert.Equal(x.Family.Id, moved.SpaceId);
        var spaces = await x.App.WithDbAsync(db => db.DriveNodes.Where(n => n.Id == b.Id || n.Id == file.Id).Select(n => n.SpaceId).ToListAsync());
        Assert.All(spaces, s => Assert.Equal(x.Family.Id, s));
        // 家庭成员现在能读到这个文件
        Assert.Equal(HttpStatusCode.OK, (await x.BobClient.GetAsync($"/api/blobs/{blob.Id}")).StatusCode);
    }

    [PgFact]
    public async Task Move_rejects_cycles_deeper_than_the_breadcrumb_depth_limit()
    {
        await using var x = await SetupAsync();
        var top = await FolderAsync(x.Admin, x.Family.Id, null, "F");
        // 直接在库里建 70 层子文件夹（比祖先链上限 64 深）
        var deepest = await x.App.WithDbAsync(async db =>
        {
            var parent = top.Id;
            for (var i = 0; i < 70; i++)
            {
                var n = new DriveNode { SpaceId = x.Family.Id, ParentId = parent, Name = $"D{i}", IsFolder = true, CreatedBy = x.AdminId };
                db.DriveNodes.Add(n);
                parent = n.Id;
            }
            await db.SaveChangesAsync();
            return parent;
        });
        var res = await x.BobClient.PatchJsonAsync($"/api/drive/nodes/{top.Id}", new { moveTo = new { spaceId = x.Family.Id, parentId = deepest } });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains((await ListAsync(x.Admin, x.Family.Id)).Items, i => i.Id == top.Id);
    }

    [PgFact]
    public async Task Moving_a_folder_to_family_leaves_its_trashed_items_in_the_owners_trash()
    {
        await using var x = await SetupAsync();
        var trip = await FolderAsync(x.Admin, x.AdminPersonal.Id, null, "旅行");
        var blob = await x.App.AddBlobAsync(x.AdminId);
        var secret = await FileAsync(x.Admin, x.AdminPersonal.Id, trip.Id, blob.Id, "私密.png");
        Assert.Equal(HttpStatusCode.NoContent, (await x.Admin.DeleteAsync($"/api/drive/nodes/{secret.Id}")).StatusCode);
        await (await x.Admin.PatchJsonAsync($"/api/drive/nodes/{trip.Id}", new { moveTo = new { spaceId = x.Family.Id, parentId = (Guid?)null } })).ReadAsync<NodeDto>();

        // 删掉的东西没跟着进家庭空间：家人看不到、还原不了、清不掉，也读不到内容
        Assert.Empty(await (await x.BobClient.GetAsync("/api/drive/trash")).ReadAsync<List<TrashItemDto>>());
        Assert.Equal(HttpStatusCode.Forbidden, (await x.BobClient.PostAsync($"/api/drive/trash/{secret.Id}/restore", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await x.BobClient.DeleteAsync($"/api/drive/trash/{secret.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await x.BobClient.GetAsync($"/api/blobs/{blob.Id}")).StatusCode);
        Assert.Equal(x.AdminPersonal.Id, await x.App.WithDbAsync(db => db.DriveNodes.Where(n => n.Id == secret.Id).Select(n => n.SpaceId).SingleAsync()));

        // 主人还原：原文件夹已在家庭空间，回到自己空间的根目录
        var back = await (await x.Admin.PostAsync($"/api/drive/trash/{secret.Id}/restore", null)).ReadAsync<NodeDto>();
        Assert.Equal((x.AdminPersonal.Id, (Guid?)null), (back.SpaceId, back.ParentId));
    }

    [PgFact]
    public async Task Trash_rights_of_a_former_grantee_end_when_the_share_is_revoked()
    {
        await using var x = await SetupAsync();
        var reno = await FolderAsync(x.Admin, x.AdminPersonal.Id, null, "装修");
        var contracts = await FolderAsync(x.Admin, x.AdminPersonal.Id, reno.Id, "合同");
        var share = await (await x.Admin.PostJsonAsync($"/api/drive/nodes/{reno.Id}/shares", new { granteeUserId = x.Bob.Id, permission = "Write" })).ReadAsync<ShareDto>();

        Assert.Equal(HttpStatusCode.NoContent, (await x.BobClient.DeleteAsync($"/api/drive/nodes/{contracts.Id}")).StatusCode);
        Assert.Equal(contracts.Id, Assert.Single(await (await x.BobClient.GetAsync("/api/drive/trash")).ReadAsync<List<TrashItemDto>>()).Node.Id);

        Assert.Equal(HttpStatusCode.NoContent, (await x.Admin.DeleteAsync($"/api/drive/shares/{share.Id}")).StatusCode);
        Assert.Empty(await (await x.BobClient.GetAsync("/api/drive/trash")).ReadAsync<List<TrashItemDto>>());
        Assert.Equal(HttpStatusCode.Forbidden, (await x.BobClient.PostAsync($"/api/drive/trash/{contracts.Id}/restore", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await x.BobClient.DeleteAsync($"/api/drive/trash/{contracts.Id}")).StatusCode);
        Assert.Equal(0, await (await x.BobClient.DeleteAsync("/api/drive/trash")).ReadAsync<int>());
        Assert.True(await x.App.WithDbAsync(db => db.DriveNodes.AnyAsync(n => n.Id == contracts.Id)));

        // 主人照常还原
        var back = await (await x.Admin.PostAsync($"/api/drive/trash/{contracts.Id}/restore", null)).ReadAsync<NodeDto>();
        Assert.Equal(reno.Id, back.ParentId);
    }

    [PgFact]
    public async Task Concurrent_creates_and_delete_of_the_parent_leave_no_live_node_under_a_trashed_folder()
    {
        await using var x = await SetupAsync();
        var blob = await x.App.AddBlobAsync(x.AdminId);
        for (var round = 0; round < 3; round++)
        {
            var p = await FolderAsync(x.Admin, x.Family.Id, null, $"P{round}");
            var creates = Enumerable.Range(0, 12).Select(i => i % 2 == 0
                ? x.Admin.PostJsonAsync("/api/drive/files", new { spaceId = x.Family.Id, parentId = p.Id, blobId = blob.Id, name = $"{i}.png" })
                : x.BobClient.PostJsonAsync("/api/drive/folders", new { spaceId = x.Family.Id, parentId = p.Id, name = $"子{i}" })).ToList();
            var delete = x.BobClient.DeleteAsync($"/api/drive/nodes/{p.Id}");
            var all = await Task.WhenAll(creates.Append(delete));
            // 每个新建要么在删除之前完成（随文件夹一起进回收站），要么在之后、因文件夹已删除返回 404
            Assert.All(all, r => Assert.True(r.IsSuccessStatusCode || r.StatusCode == HttpStatusCode.NotFound, r.StatusCode.ToString()));
        }
        var orphans = await x.App.WithDbAsync(db => db.DriveNodes.CountAsync(n => n.DeletedAt == null && n.ParentId != null
            && db.DriveNodes.Any(p => p.Id == n.ParentId && p.DeletedAt != null)));
        Assert.Equal(0, orphans);
    }

    [PgFact]
    public async Task Trash_restore_and_purge()
    {
        await using var x = await SetupAsync();
        var trip = await FolderAsync(x.Admin, x.Family.Id, null, "旅行");
        var inner = await FolderAsync(x.Admin, x.Family.Id, trip.Id, "里面");
        var blob = await x.App.AddBlobAsync(x.AdminId, content: new byte[1000]);
        await FileAsync(x.Admin, x.Family.Id, inner.Id, blob.Id, "f.png");

        Assert.Equal(HttpStatusCode.NoContent, (await x.Admin.DeleteAsync($"/api/drive/nodes/{trip.Id}")).StatusCode);
        Assert.DoesNotContain((await ListAsync(x.Admin, x.Family.Id)).Items, i => i.Id == trip.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await x.Admin.GetAsync($"/api/drive/list?spaceId={x.Family.Id}&folderId={inner.Id}")).StatusCode);

        // 家庭空间里的删除，其他成员也能在回收站里看到并还原
        var trash = await (await x.BobClient.GetAsync("/api/drive/trash")).ReadAsync<List<TrashItemDto>>();
        var item = Assert.Single(trash);
        Assert.Equal((trip.Id, 3, 1000L), (item.Node.Id, item.ItemCount, item.TotalBytes));

        // 删除后同名新建不冲突；还原时自动改名
        await FolderAsync(x.Admin, x.Family.Id, null, "旅行");
        var restored = await (await x.BobClient.PostAsync($"/api/drive/trash/{trip.Id}/restore", null)).ReadAsync<NodeDto>();
        Assert.Equal("旅行 (1)", restored.Name);
        Assert.Single((await ListAsync(x.Admin, x.Family.Id, inner.Id)).Items);

        // 彻底删除
        await x.Admin.DeleteAsync($"/api/drive/nodes/{trip.Id}");
        Assert.Equal(HttpStatusCode.NoContent, (await x.Admin.DeleteAsync($"/api/drive/trash/{trip.Id}")).StatusCode);
        Assert.Equal(0, await x.App.WithDbAsync(db => db.DriveNodes.CountAsync(n => n.TrashRootId == trip.Id || n.Id == inner.Id)));

        // 原文件夹不在了：还原到空间根目录
        var parent = await FolderAsync(x.Admin, x.Family.Id, null, "父");
        var child = await FolderAsync(x.Admin, x.Family.Id, parent.Id, "子");
        await x.Admin.DeleteAsync($"/api/drive/nodes/{child.Id}");
        await x.Admin.DeleteAsync($"/api/drive/nodes/{parent.Id}");
        var back = await (await x.Admin.PostAsync($"/api/drive/trash/{child.Id}/restore", null)).ReadAsync<NodeDto>();
        Assert.Null(back.ParentId);
    }

    [PgFact]
    public async Task Personal_trash_is_only_visible_to_owner()
    {
        await using var x = await SetupAsync();
        var f = await FolderAsync(x.Admin, x.AdminPersonal.Id, null, "私密");
        await x.Admin.DeleteAsync($"/api/drive/nodes/{f.Id}");
        Assert.Empty(await (await x.BobClient.GetAsync("/api/drive/trash")).ReadAsync<List<TrashItemDto>>());
        Assert.Equal(HttpStatusCode.Forbidden, (await x.BobClient.PostAsync($"/api/drive/trash/{f.Id}/restore", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await x.BobClient.DeleteAsync($"/api/drive/trash/{f.Id}")).StatusCode);
    }

    [PgFact]
    public async Task Quota_is_enforced()
    {
        await using var x = await SetupAsync();
        await (await x.Admin.PatchJsonAsync($"/api/drive/spaces/{x.BobPersonal.Id}", new { quotaBytes = 1500 })).ReadAsync<SpaceDto>();
        var small = await x.App.AddBlobAsync(x.Bob.Id, content: new byte[1000]);
        var big = await x.App.AddBlobAsync(x.Bob.Id, content: new byte[1001]);
        await FileAsync(x.BobClient, x.BobPersonal.Id, null, small.Id, "1.bin");
        var res = await x.BobClient.PostJsonAsync("/api/drive/files", new { spaceId = x.BobPersonal.Id, blobId = big.Id, name = "2.bin" });
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, res.StatusCode);
    }

    [PgFact]
    public async Task Blob_reference_requires_possession_and_instant_upload_counts_as_possession()
    {
        await using var x = await SetupAsync();
        var content = System.Text.Encoding.UTF8.GetBytes("同一份文件 " + Guid.NewGuid());
        var blob = await x.App.AddBlobAsync(x.AdminId, "image/png", content);
        var res = await x.BobClient.PostJsonAsync("/api/drive/files", new { spaceId = x.BobPersonal.Id, blobId = blob.Id, name = "偷来的.png" });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);

        // 秒传命中（说明 Bob 手里有同样的内容）后就能用
        var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content));
        var pre = await (await x.BobClient.PostJsonAsync("/api/blobs/precheck", new { sha256 = sha, size = content.Length })).ReadAsync<Features.Blobs.PrecheckResponse>();
        Assert.True(pre.Exists);
        await FileAsync(x.BobClient, x.BobPersonal.Id, null, blob.Id, "我的.png");
    }

    [PgFact]
    public async Task Share_link_page_password_scope_expiry_and_revocation()
    {
        await using var x = await SetupAsync();
        var trip = await FolderAsync(x.Admin, x.AdminPersonal.Id, null, "国庆旅行");
        var blob = await x.App.AddBlobAsync(x.AdminId, "image/png", System.Text.Encoding.UTF8.GetBytes("photo-bytes"));
        var photo = await FileAsync(x.Admin, x.AdminPersonal.Id, trip.Id, blob.Id, "海边.png");
        var secretBlob = await x.App.AddBlobAsync(x.AdminId);
        var secret = await FileAsync(x.Admin, x.AdminPersonal.Id, null, secretBlob.Id, "不该看到.png");

        var link = await (await x.Admin.PostJsonAsync($"/api/drive/nodes/{trip.Id}/shares", new { link = true, password = "1234", expiresInDays = 7 })).ReadAsync<ShareDto>();
        Assert.True(link.IsLink && link.HasPassword);
        var token = Regex.Match(link.Url!, "/s/([A-Za-z0-9_-]+)$").Groups[1].Value;
        Assert.True(token.Length >= 43, "token 至少 256 bit");

        var anon = x.App.Anonymous();
        var page = await anon.GetAsync($"/s/{token}");
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("需要密码", html);
        Assert.DoesNotContain("海边.png", html);
        Assert.Contains("default-src 'none'", page.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/s/{token}/f/{photo.Id}")).StatusCode);

        var wrong = await anon.PostAsync($"/s/{token}", new FormUrlEncodedContent([new("password", "0000")]));
        Assert.Contains("密码不对", await wrong.Content.ReadAsStringAsync());
        // 并发提交也不能绕过次数限制：另一个带密码的链接被并发试 20 次后，正确密码也要等锁定结束
        var other = await (await x.Admin.PostJsonAsync($"/api/drive/nodes/{photo.Id}/shares", new { link = true, password = "5678" })).ReadAsync<ShareDto>();
        var otherToken = Regex.Match(other.Url!, "/s/([A-Za-z0-9_-]+)$").Groups[1].Value;
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => x.App.Anonymous().PostAsync($"/s/{otherToken}", new FormUrlEncodedContent([new("password", $"bad{i}")]))));
        var locked = await x.App.Anonymous().PostAsync($"/s/{otherToken}", new FormUrlEncodedContent([new("password", "5678")]));
        Assert.Contains("尝试次数过多", await locked.Content.ReadAsStringAsync());
        var right = await anon.PostAsync($"/s/{token}", new FormUrlEncodedContent([new("password", "1234")]));
        Assert.Equal(HttpStatusCode.Redirect, right.StatusCode);

        html = await (await anon.GetAsync($"/s/{token}")).Content.ReadAsStringAsync();
        Assert.Contains("海边.png", html);
        var file = await anon.GetAsync($"/s/{token}/f/{photo.Id}?download=true");
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal("photo-bytes", await file.Content.ReadAsStringAsync());
        // 共享范围之外的文件拿不到
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/s/{token}/f/{secret.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/s/{token}?folder={x.AdminPersonal.Id}")).StatusCode);
        // 名字里的 HTML 特殊字符被转义（< > 已在命名规则里禁止，& 和 ' 允许）
        await FolderAsync(x.Admin, x.AdminPersonal.Id, trip.Id, "汤姆 & 杰瑞's");
        html = await (await anon.GetAsync($"/s/{token}")).Content.ReadAsStringAsync();
        Assert.Contains("汤姆 &amp; 杰瑞&#x27;s", html);

        // 过期 / 撤销后失效
        await x.App.WithDbAsync(async db => { await db.NodeShares.Where(s => s.Id == link.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1))); });
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/s/{token}")).StatusCode);
        await x.App.WithDbAsync(async db => { await db.NodeShares.Where(s => s.Id == link.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.ExpiresAt, (DateTimeOffset?)null)); });
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync($"/s/{token}")).StatusCode);
        await x.Admin.DeleteAsync($"/api/drive/shares/{link.Id}");
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/s/{token}")).StatusCode);
    }

    [PgFact]
    public async Task Send_to_chat_creates_a_message_readable_by_members()
    {
        await using var x = await SetupAsync();
        var blob = await x.App.AddBlobAsync(x.AdminId, "image/jpeg");
        var node = await FileAsync(x.Admin, x.AdminPersonal.Id, null, blob.Id, "全家福.jpg");
        var conv = await (await x.Admin.PostJsonAsync("/api/conversations", new { type = "Direct", memberIds = new[] { x.Bob.Id } })).ReadAsync<ConversationDto>();

        Assert.Equal(HttpStatusCode.Forbidden, (await x.BobClient.GetAsync($"/api/blobs/{blob.Id}")).StatusCode);
        var msg = await (await x.Admin.PostJsonAsync($"/api/drive/nodes/{node.Id}/send", new { conversationId = conv.Id, clientMessageId = Guid.NewGuid() })).ReadAsync<MessageDto>();
        Assert.Equal(MessageType.Image, msg.Type);
        Assert.Contains("全家福.jpg", msg.Attachments.Single().Meta);
        Assert.Equal(HttpStatusCode.OK, (await x.BobClient.GetAsync($"/api/blobs/{blob.Id}")).StatusCode);

        // 不是会话成员不能发进去
        var kidConv = await (await x.Admin.PostJsonAsync("/api/conversations", new { type = "Direct", memberIds = new[] { x.Kid.Id } })).ReadAsync<ConversationDto>();
        var bobsFile = await FileAsync(x.BobClient, x.BobPersonal.Id, null, (await x.App.AddBlobAsync(x.Bob.Id)).Id, "b.png");
        var res = await x.BobClient.PostJsonAsync($"/api/drive/nodes/{bobsFile.Id}/send", new { conversationId = kidConv.Id, clientMessageId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [PgFact]
    public async Task Gallery_merges_chat_and_drive_dedupes_and_respects_visibility()
    {
        await using var x = await SetupAsync();
        var t0 = new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.FromHours(8));
        var family = await x.App.AddBlobAsync(x.AdminId, "image/jpeg", takenAt: t0);
        var privateOne = await x.App.AddBlobAsync(x.AdminId, "image/jpeg", takenAt: t0.AddHours(1));
        var video = await x.App.AddBlobAsync(x.AdminId, "video/mp4", takenAt: t0.AddHours(2));
        var pdf = await x.App.AddBlobAsync(x.AdminId, "application/pdf");
        await FileAsync(x.Admin, x.Family.Id, null, family.Id, "家.jpg");
        await FileAsync(x.Admin, x.Family.Id, null, video.Id, "v.mp4");
        await FileAsync(x.Admin, x.Family.Id, null, pdf.Id, "p.pdf");
        await FileAsync(x.Admin, x.AdminPersonal.Id, null, privateOne.Id, "私.jpg");
        // 同一张图也发到了聊天里：时间线里只出现一次
        var conv = await (await x.Admin.PostJsonAsync("/api/conversations", new { type = "Direct", memberIds = new[] { x.Bob.Id } })).ReadAsync<ConversationDto>();
        var familyNode = (await ListAsync(x.Admin, x.Family.Id)).Items.Single(i => i.Name == "家.jpg");
        await x.Admin.PostJsonAsync($"/api/drive/nodes/{familyNode.Id}/send", new { conversationId = conv.Id, clientMessageId = Guid.NewGuid() });

        var bobs = await (await x.BobClient.GetAsync("/api/drive/gallery")).ReadAsync<GalleryPageDto>();
        Assert.Equal([video.Id, family.Id], bobs.Items.Select(i => i.BlobId));
        var admins = await (await x.Admin.GetAsync("/api/drive/gallery")).ReadAsync<GalleryPageDto>();
        Assert.Equal([video.Id, privateOne.Id, family.Id], admins.Items.Select(i => i.BlobId));

        // 游标分页
        var p1 = await (await x.Admin.GetAsync("/api/drive/gallery?limit=2")).ReadAsync<GalleryPageDto>();
        Assert.Equal(2, p1.Items.Count);
        var p2 = await (await x.Admin.GetAsync($"/api/drive/gallery?limit=2&before={Uri.EscapeDataString(p1.NextBefore!.Value.ToString("O"))}&beforeId={p1.NextBeforeId}")).ReadAsync<GalleryPageDto>();
        Assert.Equal([family.Id], p2.Items.Select(i => i.BlobId));
        Assert.Null(p2.NextBefore);
    }

    [PgFact]
    public async Task Garbage_collection_keeps_referenced_and_recent_blobs()
    {
        await using var x = await SetupAsync();
        var orphan = await x.App.AddBlobAsync(x.AdminId);
        var recent = await x.App.AddBlobAsync(x.AdminId);
        var used = await x.App.AddBlobAsync(x.AdminId);
        var trashed = await x.App.AddBlobAsync(x.AdminId);
        var claimed = await x.App.AddBlobAsync(x.AdminId);
        await FileAsync(x.Admin, x.AdminPersonal.Id, null, used.Id, "用着.png");
        var t = await FileAsync(x.Admin, x.AdminPersonal.Id, null, trashed.Id, "删了.png");
        await x.Admin.DeleteAsync($"/api/drive/nodes/{t.Id}");

        // 除 recent 外都"很久以前"上传、凭证也旧了（超过 7 天宽限期）
        var old = DateTimeOffset.UtcNow.AddDays(-10);
        var oldIds = new[] { orphan.Id, used.Id, trashed.Id, claimed.Id };
        await x.App.WithDbAsync(async db =>
        {
            await db.Blobs.Where(b => oldIds.Contains(b.Id)).ExecuteUpdateAsync(s => s.SetProperty(b => b.CreatedAt, old));
            await db.BlobClaims.Where(c => oldIds.Contains(c.BlobId)).ExecuteUpdateAsync(s => s.SetProperty(c => c.CreatedAt, old));
            // claimed：几天前有人登记过持有（比如离线发件箱里还没发出去的消息），还在宽限期内
            await db.BlobClaims.Where(c => c.BlobId == claimed.Id).ExecuteUpdateAsync(s => s.SetProperty(c => c.CreatedAt, DateTimeOffset.UtcNow.AddDays(-3)));
        });

        var worker = x.App.Services.GetServices<IHostedService>().OfType<StorageMaintenanceWorker>().Single();
        using (var scope = x.App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Infrastructure.AppDbContext>();
            Assert.Equal(1, await worker.CollectGarbageAsync(db, default));
        }
        var remaining = await x.App.WithDbAsync(db => db.Blobs.Select(b => b.Id).ToListAsync());
        Assert.DoesNotContain(orphan.Id, remaining);
        Assert.Contains(recent.Id, remaining);
        Assert.Contains(claimed.Id, remaining);
        Assert.Contains(used.Id, remaining);
        Assert.Contains(trashed.Id, remaining); // 回收站里的还在引用
        Assert.False(File.Exists(Path.Combine(x.App.DataRoot, "blobs", orphan.StorageKey.Replace('/', Path.DirectorySeparatorChar))));

        // 回收站过期清理后，它也会被回收
        await x.App.WithDbAsync(async db => { await db.DriveNodes.Where(n => n.TrashRootId == t.Id).ExecuteUpdateAsync(s => s.SetProperty(n => n.DeletedAt, DateTimeOffset.UtcNow.AddDays(-31))); });
        using (var scope = x.App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Infrastructure.AppDbContext>();
            Assert.Equal(1, await StorageMaintenanceWorker.PurgeExpiredTrashAsync(db, default));
            Assert.Equal(1, await worker.CollectGarbageAsync(db, default));
        }
        Assert.DoesNotContain(trashed.Id, await x.App.WithDbAsync(db => db.Blobs.Select(b => b.Id).ToListAsync()));
    }
}

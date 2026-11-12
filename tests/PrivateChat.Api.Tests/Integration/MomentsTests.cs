using System.Net;
using Microsoft.EntityFrameworkCore;
using PrivateChat.Api.Domain;
using PrivateChat.Api.Features.Drive;
using PrivateChat.Api.Features.Moments;

namespace PrivateChat.Api.Tests.Integration;

/// <summary>动态、评论、点赞、那年今天、相册：对真实数据库跑完整 HTTP 流程。</summary>
public class MomentsTests
{
    private sealed record Ctx(ApiFactory App, HttpClient Admin, Guid AdminId, User Bob, HttpClient BobClient, User Kid, HttpClient KidClient) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => App.DisposeAsync();
    }

    private static async Task<Ctx> SetupAsync()
    {
        var app = new ApiFactory();
        var admin = await app.LoginAsync("admin@example.com");
        var adminId = (await (await admin.GetAsync("/api/auth/me")).ReadAsync<Features.Auth.UserDto>()).Id;
        var bob = await app.AddUserAsync("bob", UserRole.Member);
        var kid = await app.AddUserAsync("kid", UserRole.Child);
        return new Ctx(app, admin, adminId, bob, await app.LoginAsync("bob"), kid, await app.LoginAsync("kid"));
    }

    private static async Task<PostDto> PostAsync(HttpClient c, string? text, string visibility = "Family", params Guid[] blobIds)
        => await (await c.PostJsonAsync("/api/posts", new { text, visibility, blobIds })).ReadAsync<PostDto>();

    private static async Task<PostPageDto> FeedAsync(HttpClient c, string query = "")
        => await (await c.GetAsync("/api/posts" + query)).ReadAsync<PostPageDto>();

    private static async Task EventuallyAsync(Func<bool> condition, string what, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new Xunit.Sdk.XunitException($"等不到：{what}");
            await Task.Delay(25);
        }
    }

    [PgFact]
    public async Task Family_posts_are_shared_private_posts_and_their_media_stay_with_the_author()
    {
        await using var x = await SetupAsync();
        var shared = await x.App.AddBlobAsync(x.AdminId, "image/jpeg");
        var secret = await x.App.AddBlobAsync(x.AdminId, "image/jpeg");
        var family = await PostAsync(x.Admin, "饺子包好了！", "Family", shared.Id);
        var diary = await PostAsync(x.Admin, "今天有点累", "Private", secret.Id);
        Assert.Equal([shared.Id], family.Media.Select(m => m.BlobId));

        Assert.Equal([family.Id], (await FeedAsync(x.BobClient)).Items.Select(p => p.Id));
        Assert.Equal([diary.Id, family.Id], (await FeedAsync(x.Admin)).Items.Select(p => p.Id));
        Assert.Equal(HttpStatusCode.NotFound, (await x.BobClient.GetAsync($"/api/posts/{diary.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await x.BobClient.GetAsync($"/api/blobs/{shared.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await x.BobClient.GetAsync($"/api/blobs/{secret.Id}")).StatusCode);

        // 时间线（相册）：家人可见动态里的照片出现在 Bob 的时间线上，私密的不出现
        var gallery = await (await x.BobClient.GetAsync("/api/drive/gallery")).ReadAsync<GalleryPageDto>();
        Assert.Equal([shared.Id], gallery.Items.Select(i => i.BlobId));
        Assert.Equal("post", gallery.Items[0].Source);

        // 只能删自己的；删掉后家人看不到、照片也读不到了
        Assert.Equal(HttpStatusCode.Forbidden, (await x.BobClient.DeleteAsync($"/api/posts/{family.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await x.Admin.DeleteAsync($"/api/posts/{family.Id}")).StatusCode);
        Assert.Empty((await FeedAsync(x.BobClient)).Items);
        Assert.Equal(HttpStatusCode.Forbidden, (await x.BobClient.GetAsync($"/api/blobs/{shared.Id}")).StatusCode);
        Assert.Equal(0, await x.App.WithDbAsync(db => db.PostMedia.CountAsync(m => m.PostId == family.Id)));
    }

    [PgFact]
    public async Task Admin_can_remove_a_family_members_post()
    {
        await using var x = await SetupAsync();
        var post = await PostAsync(x.BobClient, "不合适的内容");
        Assert.Equal(HttpStatusCode.NoContent, (await x.Admin.DeleteAsync($"/api/posts/{post.Id}")).StatusCode);
        Assert.Empty((await FeedAsync(x.BobClient)).Items);
    }

    [PgFact]
    public async Task Feed_pages_with_a_cursor_and_filters_by_author()
    {
        await using var x = await SetupAsync();
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++) ids.Add((await PostAsync(i % 2 == 0 ? x.Admin : x.BobClient, $"第 {i} 条")).Id);
        ids.Reverse();

        var p1 = await FeedAsync(x.KidClient, "?limit=2");
        var p2 = await FeedAsync(x.KidClient, $"?limit=2&before={Uri.EscapeDataString(p1.NextBefore!.Value.ToString("O"))}&beforeId={p1.NextBeforeId}");
        var p3 = await FeedAsync(x.KidClient, $"?limit=2&before={Uri.EscapeDataString(p2.NextBefore!.Value.ToString("O"))}&beforeId={p2.NextBeforeId}");
        Assert.Equal(ids, p1.Items.Concat(p2.Items).Concat(p3.Items).Select(p => p.Id));
        Assert.Null(p3.NextBefore);

        var bobs = await FeedAsync(x.KidClient, $"?authorId={x.Bob.Id}");
        Assert.Equal(2, bobs.Items.Count);
        Assert.All(bobs.Items, p => Assert.Equal(x.Bob.Id, p.AuthorId));
    }

    [PgFact]
    public async Task Creating_a_post_validates_text_and_media()
    {
        await using var x = await SetupAsync();
        var res = await x.Admin.PostJsonAsync("/api/posts", new { text = "   " });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);

        var ten = new List<Guid>();
        for (var i = 0; i < 10; i++) ten.Add((await x.App.AddBlobAsync(x.AdminId, "image/png")).Id);
        res = await x.Admin.PostJsonAsync("/api/posts", new { text = "太多了", blobIds = ten });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var nine = await PostAsync(x.Admin, null, "Family", [.. ten.Take(9)]);
        Assert.Equal(ten.Take(9), nine.Media.Select(m => m.BlobId));

        var pdf = await x.App.AddBlobAsync(x.AdminId, "application/pdf");
        res = await x.Admin.PostJsonAsync("/api/posts", new { text = "文件", blobIds = new[] { pdf.Id } });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var svg = await x.App.AddBlobAsync(x.AdminId, "image/svg+xml");
        res = await x.Admin.PostJsonAsync("/api/posts", new { blobIds = new[] { svg.Id } });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);

        // 不能借动态"转发"自己看不到的文件；看得到的（家人动态里的）可以
        var adminsPrivate = await x.App.AddBlobAsync(x.AdminId, "image/jpeg");
        res = await x.BobClient.PostJsonAsync("/api/posts", new { text = "偷看", blobIds = new[] { adminsPrivate.Id } });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        var again = await PostAsync(x.BobClient, "转一下", "Family", ten[0]);
        Assert.Single(again.Media);

        res = await x.Admin.PostJsonAsync("/api/posts", new { text = new string('字', MomentsService.MaxPostText + 1) });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [PgFact]
    public async Task Comments_replies_reactions_and_who_may_delete_what()
    {
        await using var x = await SetupAsync();
        var post = await PostAsync(x.Admin, "周末去爬山？");

        post = await (await x.BobClient.PostJsonAsync($"/api/posts/{post.Id}/comments", new { text = "好啊" })).ReadAsync<PostDto>();
        var bobsComment = Assert.Single(post.Comments);
        post = await (await x.Admin.PostJsonAsync($"/api/posts/{post.Id}/comments", new { text = "那就六点出发", replyToCommentId = bobsComment.Id })).ReadAsync<PostDto>();
        var reply = post.Comments[1];
        Assert.Equal((bobsComment.Id, (Guid?)x.Bob.Id), (reply.ReplyToCommentId, reply.ReplyToUserId));

        // 回复别的动态下的评论、空评论都不行
        var other = await PostAsync(x.BobClient, "另一条");
        Assert.Equal(HttpStatusCode.BadRequest, (await x.Admin.PostJsonAsync($"/api/posts/{other.Id}/comments", new { text = "串了", replyToCommentId = bobsComment.Id })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await x.Admin.PostJsonAsync($"/api/posts/{post.Id}/comments", new { text = " " })).StatusCode);

        // 点赞开关
        post = await (await x.KidClient.PostJsonAsync($"/api/posts/{post.Id}/reactions", new { emoji = "❤️" })).ReadAsync<PostDto>();
        Assert.Equal([new ReactionSummaryDto(x.Kid.Id, "❤️")], post.Reactions);
        post = await (await x.KidClient.PostJsonAsync($"/api/posts/{post.Id}/reactions", new { emoji = "❤️" })).ReadAsync<PostDto>();
        Assert.Empty(post.Reactions);
        Assert.Equal(HttpStatusCode.BadRequest, (await x.KidClient.PostJsonAsync($"/api/posts/{post.Id}/reactions", new { emoji = "like" })).StatusCode);
        post = await (await x.BobClient.PostJsonAsync($"/api/comments/{reply.Id}/reactions", new { emoji = "👍" })).ReadAsync<PostDto>();
        Assert.Equal([new ReactionSummaryDto(x.Bob.Id, "👍")], post.Comments.Single(c => c.Id == reply.Id).Reactions);

        // 删除：孩子不能删别人的评论；动态作者能删别人在自己动态下的评论；评论者能删自己的
        Assert.Equal(HttpStatusCode.Forbidden, (await x.KidClient.DeleteAsync($"/api/comments/{bobsComment.Id}")).StatusCode);
        post = await (await x.Admin.DeleteAsync($"/api/comments/{bobsComment.Id}")).ReadAsync<PostDto>();
        Assert.Equal([reply.Id], post.Comments.Select(c => c.Id));
        Assert.Equal(x.Bob.Id, post.Comments[0].ReplyToUserId); // 被回复的评论删了，仍显示回复的是谁
        post = await (await x.Admin.DeleteAsync($"/api/comments/{reply.Id}")).ReadAsync<PostDto>();
        Assert.Empty(post.Comments);
        Assert.Equal(0, await x.App.WithDbAsync(db => db.Reactions.CountAsync(r => r.TargetId == reply.Id)));

        // 私密动态别人既看不到也评论不了
        var diary = await PostAsync(x.Admin, "日记", "Private");
        Assert.Equal(HttpStatusCode.NotFound, (await x.BobClient.PostJsonAsync($"/api/posts/{diary.Id}/comments", new { text = "?" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await x.BobClient.PostJsonAsync($"/api/posts/{diary.Id}/reactions", new { emoji = "👍" })).StatusCode);
    }

    [PgFact]
    public async Task Comments_and_likes_push_to_offline_people_and_refresh_online_ones()
    {
        await using var x = await SetupAsync();
        await x.App.AddPushSubscriptionAsync(x.AdminId, "admin-phone");
        await x.App.AddPushSubscriptionAsync(x.Bob.Id, "bob-phone");
        var post = await PostAsync(x.Admin, "看看我种的花");

        // 作者不在线：评论、点赞都推送给作者，不推给做这件事的人
        await x.BobClient.PostJsonAsync($"/api/posts/{post.Id}/comments", new { text = "真好看" });
        await EventuallyAsync(() => x.App.Push.Requests.Count == 1, "评论推送");
        Assert.EndsWith("/admin-phone", x.App.Push.Requests.Single().Endpoint);
        await x.KidClient.PostJsonAsync($"/api/posts/{post.Id}/reactions", new { emoji = "👍" });
        await EventuallyAsync(() => x.App.Push.Requests.Count == 2, "点赞推送");
        // 取消点赞不推
        await x.KidClient.PostJsonAsync($"/api/posts/{post.Id}/reactions", new { emoji = "👍" });

        // Bob 在线：作者回复他时不推送，改为实时刷新
        await using var bobHub = await x.App.ConnectHubAsync("bob");
        var bobsComment = (await (await x.Admin.GetAsync($"/api/posts/{post.Id}")).ReadAsync<PostDto>()).Comments.Single();
        await x.Admin.PostJsonAsync($"/api/posts/{post.Id}/comments", new { text = "谢谢", replyToCommentId = bobsComment.Id });
        var changed = await bobHub.WaitAsync<PostChangedDto>("PostChanged");
        Assert.Equal((post.Id, "updated"), (changed.PostId, changed.Change));
        await Task.Delay(300);
        Assert.Equal(2, x.App.Push.Requests.Count);

        // 私密动态的变化不会发给别人
        await PostAsync(x.Admin, "私密", "Private");
        await Task.Delay(300);
        Assert.Single(bobHub.All("PostChanged"));
    }

    [PgFact]
    public async Task On_this_day_collects_earlier_years_in_the_family_time_zone()
    {
        await using var x = await SetupAsync();
        var cst = TimeSpan.FromHours(8);
        DateTimeOffset At(int y, int m, int d, int h) => new(y, m, d, h, 0, 0, cst);
        var y2019a = await x.App.AddBlobAsync(x.AdminId, "image/jpeg", takenAt: At(2019, 11, 12, 10));
        var y2019b = await x.App.AddBlobAsync(x.AdminId, "video/mp4", takenAt: new DateTimeOffset(2019, 11, 11, 17, 0, 0, TimeSpan.Zero)); // 北京时间 12 日凌晨 1 点
        var y2024 = await x.App.AddBlobAsync(x.AdminId, "image/jpeg", takenAt: At(2024, 11, 12, 18));
        var otherDay = await x.App.AddBlobAsync(x.AdminId, "image/jpeg", takenAt: At(2019, 11, 13, 10));
        var thisYear = await x.App.AddBlobAsync(x.AdminId, "image/jpeg", takenAt: At(2026, 11, 12, 8));
        var privateOne = await x.App.AddBlobAsync(x.AdminId, "image/jpeg", takenAt: At(2021, 11, 12, 8));
        await PostAsync(x.Admin, "一堆老照片", "Family", y2019a.Id, y2019b.Id, y2024.Id, otherDay.Id, thisYear.Id);
        await PostAsync(x.Admin, "自己留着", "Private", privateOne.Id);

        // 一条三年前今天发的动态、一条私密的
        var old = await PostAsync(x.Admin, "2023 年的今天：外婆生日");
        var oldPrivate = await PostAsync(x.Admin, "2023 年的日记", "Private");
        await x.App.WithDbAsync(async db =>
        {
            await db.Posts.Where(p => p.Id == old.Id || p.Id == oldPrivate.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.CreatedAt, At(2023, 11, 12, 20).ToUniversalTime()));
        });

        var bobs = await (await x.BobClient.GetAsync("/api/posts/on-this-day?date=2026-11-12")).ReadAsync<OnThisDayDto>();
        Assert.Equal([2024, 2023, 2019], bobs.Years.Select(y => y.Year));
        Assert.Equal(3, bobs.Years.Single(y => y.Year == 2023).YearsAgo);
        Assert.Equal([old.Id], bobs.Years.Single(y => y.Year == 2023).Posts.Select(p => p.Id));
        var y2019 = bobs.Years.Single(y => y.Year == 2019);
        Assert.Equal(2, y2019.PhotoCount);
        Assert.Equal(new HashSet<Guid> { y2019a.Id, y2019b.Id }, y2019.Photos.Select(p => p.BlobId).ToHashSet());

        var admins = await (await x.Admin.GetAsync("/api/posts/on-this-day?date=2026-11-12")).ReadAsync<OnThisDayDto>();
        Assert.Equal([2024, 2023, 2021, 2019], admins.Years.Select(y => y.Year));
        Assert.Equal(2, admins.Years.Single(y => y.Year == 2023).Posts.Count);
    }

    [PgFact]
    public async Task Albums_are_shared_with_the_family_with_clear_edit_rights()
    {
        await using var x = await SetupAsync();
        var a = await x.App.AddBlobAsync(x.AdminId, "image/jpeg", takenAt: new DateTimeOffset(2026, 10, 2, 9, 0, 0, TimeSpan.FromHours(8)));
        var b = await x.App.AddBlobAsync(x.Bob.Id, "image/jpeg", takenAt: new DateTimeOffset(2026, 10, 3, 9, 0, 0, TimeSpan.FromHours(8)));
        var adminsPrivate = await x.App.AddBlobAsync(x.AdminId, "image/jpeg");

        var album = await (await x.Admin.PostJsonAsync("/api/albums", new { title = " 国庆旅行 ", blobIds = new[] { a.Id } })).ReadAsync<AlbumDto>();
        Assert.Equal(("国庆旅行", 1, (Guid?)a.Id), (album.Title, album.ItemCount, album.CoverBlobId));

        // 全家都能看到相册和里面的照片
        var overview = await (await x.BobClient.GetAsync("/api/albums")).ReadAsync<AlbumsOverviewDto>();
        Assert.Equal(album.Id, Assert.Single(overview.Albums).Id);
        Assert.Equal(HttpStatusCode.OK, (await x.KidClient.GetAsync($"/api/blobs/{a.Id}")).StatusCode);

        // 谁都能往里加自己能看到的照片；重复加不算；看不到的不能加
        var added = await (await x.BobClient.PostJsonAsync($"/api/albums/{album.Id}/items", new { blobIds = new[] { b.Id, a.Id } })).ReadAsync<AddAlbumItemsResult>();
        Assert.Equal((1, 2), (added.Added, added.Album.ItemCount));
        Assert.Equal(b.Id, added.Album.CoverBlobId); // 没指定封面时用最近加入的
        Assert.Equal(HttpStatusCode.Forbidden, (await x.KidClient.PostJsonAsync($"/api/albums/{album.Id}/items", new { blobIds = new[] { adminsPrivate.Id } })).StatusCode);
        var items = await (await x.KidClient.GetAsync($"/api/albums/{album.Id}/items")).ReadAsync<AlbumItemsPageDto>();
        Assert.Equal([b.Id, a.Id], items.Items.Select(i => i.BlobId)); // 按拍摄时间倒序
        Assert.Equal(x.Bob.Id, items.Items[0].AddedBy);

        // 相册里的照片进了全家的时间线，按月分组
        var months = (await (await x.KidClient.GetAsync("/api/albums")).ReadAsync<AlbumsOverviewDto>()).Months;
        Assert.Equal(("2026-10", 2), (Assert.Single(months).Month, months[0].Count));

        // 改名、设封面、删相册：创建人或管理员
        Assert.Equal(HttpStatusCode.Forbidden, (await x.BobClient.PatchJsonAsync($"/api/albums/{album.Id}", new { title = "改名" })).StatusCode);
        album = await (await x.Admin.PatchJsonAsync($"/api/albums/{album.Id}", new { title = "2026 国庆", coverBlobId = a.Id })).ReadAsync<AlbumDto>();
        Assert.Equal(("2026 国庆", (Guid?)a.Id), (album.Title, album.CoverBlobId));
        Assert.Equal(HttpStatusCode.BadRequest, (await x.Admin.PatchJsonAsync($"/api/albums/{album.Id}", new { coverBlobId = adminsPrivate.Id })).StatusCode);

        // 移出：加的人、创建人、管理员；移出封面后封面恢复为最近加入的一项
        Assert.Equal(HttpStatusCode.Forbidden, (await x.KidClient.DeleteAsync($"/api/albums/{album.Id}/items/{b.Id}")).StatusCode);
        album = await (await x.Admin.DeleteAsync($"/api/albums/{album.Id}/items/{a.Id}")).ReadAsync<AlbumDto>();
        Assert.Equal((1, (Guid?)b.Id), (album.ItemCount, album.CoverBlobId));
        Assert.Equal(HttpStatusCode.OK, (await x.BobClient.DeleteAsync($"/api/albums/{album.Id}/items/{b.Id}")).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await x.BobClient.DeleteAsync($"/api/albums/{album.Id}")).StatusCode);
        await x.BobClient.PostJsonAsync($"/api/albums/{album.Id}/items", new { blobIds = new[] { b.Id } });
        Assert.Equal(HttpStatusCode.NoContent, (await x.Admin.DeleteAsync($"/api/albums/{album.Id}")).StatusCode);
        Assert.Empty((await (await x.BobClient.GetAsync("/api/albums")).ReadAsync<AlbumsOverviewDto>()).Albums);
        // 删相册连同条目一起删；往已删除的相册里加是 404，不会留下孤儿条目
        Assert.Equal(0, await x.App.WithDbAsync(db => db.AlbumItems.CountAsync(i => i.AlbumId == album.Id)));
        Assert.Equal(HttpStatusCode.NotFound, (await x.BobClient.PostJsonAsync($"/api/albums/{album.Id}/items", new { blobIds = new[] { b.Id } })).StatusCode);
    }

    [PgFact]
    public async Task Month_albums_and_gallery_since_filter_use_the_family_time_zone()
    {
        await using var x = await SetupAsync();
        // 北京时间 11 月 1 日 01:00 = UTC 10 月 31 日 17:00：算 11 月
        var early = await x.App.AddBlobAsync(x.AdminId, "image/jpeg", takenAt: new DateTimeOffset(2026, 10, 31, 17, 0, 0, TimeSpan.Zero));
        var oct = await x.App.AddBlobAsync(x.AdminId, "image/jpeg", takenAt: new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.FromHours(8)));
        var album = await (await x.Admin.PostJsonAsync("/api/albums", new { title = "随手", blobIds = new[] { early.Id, oct.Id } })).ReadAsync<AlbumDto>();

        var months = (await (await x.BobClient.GetAsync("/api/albums")).ReadAsync<AlbumsOverviewDto>()).Months;
        Assert.Equal([("2026-11", 1, early.Id), ("2026-10", 1, oct.Id)], months.Select(m => (m.Month, m.Count, m.CoverBlobId)));
        // 每个月的起止按家庭时区：11 月 = 北京时间 11-01 00:00 到 12-01 00:00
        Assert.Equal((new DateTimeOffset(2026, 10, 31, 16, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 30, 16, 0, 0, TimeSpan.Zero)), (months[0].Since, months[0].Before));

        var since = Uri.EscapeDataString(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.FromHours(8)).ToString("O"));
        var november = await (await x.BobClient.GetAsync($"/api/drive/gallery?since={since}")).ReadAsync<GalleryPageDto>();
        Assert.Equal([early.Id], november.Items.Select(i => i.BlobId));
        Assert.Equal(album.Id, november.Items[0].SourceId);
    }
}

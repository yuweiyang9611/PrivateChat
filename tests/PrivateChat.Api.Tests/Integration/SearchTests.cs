using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PrivateChat.Api.Domain;
using PrivateChat.Api.Features.Chat;
using PrivateChat.Api.Features.Drive;
using PrivateChat.Api.Features.Moments;
using PrivateChat.Api.Features.Search;

namespace PrivateChat.Api.Tests.Integration;

/// <summary>搜索：每一类只搜到自己能看到的，撤回、回收站、过期共享、删掉的动态都不出现；分词、通配符、翻页、摘要。</summary>
public class SearchTests
{
    private sealed record Ctx(ApiFactory App, HttpClient Admin, Guid AdminId, User Bob, HttpClient BobClient, User Carol, HttpClient CarolClient, User Kid, HttpClient KidClient)
        : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => App.DisposeAsync();
    }

    private static async Task<Ctx> SetupAsync()
    {
        var app = new ApiFactory();
        var admin = await app.LoginAsync("admin@example.com");
        var adminId = (await (await admin.GetAsync("/api/auth/me")).ReadAsync<Features.Auth.UserDto>()).Id;
        var bob = await app.AddUserAsync("bob", UserRole.Member);
        var carol = await app.AddUserAsync("carol", UserRole.Member);
        var kid = await app.AddUserAsync("kid", UserRole.Child);
        return new Ctx(app, admin, adminId, bob, await app.LoginAsync("bob"), carol, await app.LoginAsync("carol"), kid, await app.LoginAsync("kid"));
    }

    private static async Task<Guid> DirectAsync(HttpClient c, Guid other)
        => (await (await c.PostJsonAsync("/api/conversations", new { type = "Direct", memberIds = new[] { other } })).ReadAsync<ConversationDto>()).Id;

    private static async Task<MessageDto> SayAsync(HttpClient c, Guid conv, string text)
        => await (await c.PostJsonAsync($"/api/conversations/{conv}/messages", new SendMessageRequest(conv, Guid.CreateVersion7(), MessageType.Text, text))).ReadAsync<MessageDto>();

    private static async Task<SearchResultDto> SearchAsync(HttpClient c, string q, string type = "All", string? cursor = null, int? limit = null)
    {
        var url = $"/api/search?q={Uri.EscapeDataString(q)}&type={type}" + (cursor is null ? "" : $"&cursor={cursor}") + (limit is null ? "" : $"&limit={limit}");
        return await (await c.GetAsync(url)).ReadAsync<SearchResultDto>();
    }

    [PgFact]
    public async Task Messages_are_found_only_in_my_conversations_and_not_when_recalled()
    {
        await using var x = await SetupAsync();
        var withBob = await DirectAsync(x.Admin, x.Bob.Id);
        var withCarol = await DirectAsync(x.Admin, x.Carol.Id);
        var withKid = await DirectAsync(x.Admin, x.Kid.Id);

        var park = await SayAsync(x.Admin, withBob, "周末去公园包饺子吧");
        await SayAsync(x.Admin, withBob, "明天买菜：白菜 猪肉 饺子皮");
        await SayAsync(x.BobClient, withBob, "好的饺子我来包");
        await SayAsync(x.Admin, withBob, "今天天气不错");
        await SayAsync(x.Admin, withCarol, "饺子的秘密配方"); // Bob 不在这个会话里
        await SayAsync(x.Admin, withKid, "小宝的饺子");
        var recalled = await SayAsync(x.Admin, withBob, "说错了的饺子");
        Assert.Equal(HttpStatusCode.NoContent, (await x.Admin.DeleteAsync($"/api/messages/{recalled.Id}")).StatusCode);
        // 通话记录（服务端生成的 Call 消息）不算聊天内容
        await x.App.WithDbAsync(async db =>
        {
            db.Messages.Add(new Message { ConversationId = withBob, Seq = 9_999, SenderId = x.AdminId, ClientMessageId = Guid.CreateVersion7(), Type = MessageType.Call, Body = "视频通话 饺子" });
            await db.SaveChangesAsync();
        });
        // 附件按文件名找
        var pdf = await x.App.AddBlobAsync(x.AdminId, "application/pdf");
        var file = await (await x.Admin.PostJsonAsync($"/api/conversations/{withBob}/messages", new SendMessageRequest(withBob, Guid.CreateVersion7(), MessageType.File, null,
            Attachments: [new AttachmentInput(pdf.Id, AttachmentKind.File, JsonSerializer.Serialize(new { name = "饺子做法.pdf", mime = "application/pdf", size = 256 }))]))).ReadAsync<MessageDto>();

        var bob = await SearchAsync(x.BobClient, "饺子", "Messages");
        Assert.Equal(["饺子做法.pdf", "好的饺子我来包", "明天买菜：白菜 猪肉 饺子皮", "周末去公园包饺子吧"], bob.Messages.Select(m => m.Snippet));
        var hit = bob.Messages[0];
        Assert.Equal(("file", file.Id, pdf.Id, "饺子做法.pdf", "File"), (hit.Kind, hit.MessageId, hit.BlobId, hit.FileName, hit.AttachmentKind));
        var text = bob.Messages[^1];
        Assert.Equal(("text", park.Id, withBob, park.Seq, x.AdminId), (text.Kind, text.MessageId, text.ConversationId, text.Seq, text.SenderId));
        Assert.Null(bob.MessagesNext);

        // 孩子只看到自己会话里的；空格分开的每个词都要出现
        Assert.Equal(["小宝的饺子"], (await SearchAsync(x.KidClient, "饺子")).Messages.Select(m => m.Snippet));
        Assert.Equal(["周末去公园包饺子吧"], (await SearchAsync(x.BobClient, "公园　饺子")).Messages.Select(m => m.Snippet)); // 全角空格也算分隔
        Assert.Empty((await SearchAsync(x.BobClient, "秘密配方")).Messages);

        // 翻页
        var first = await SearchAsync(x.BobClient, "饺子", "Messages", limit: 3);
        Assert.Equal(3, first.Messages.Count);
        Assert.NotNull(first.MessagesNext);
        var second = await SearchAsync(x.BobClient, "饺子", "Messages", first.MessagesNext, limit: 3);
        Assert.Equal([park.Id], second.Messages.Select(m => m.MessageId));
        Assert.Null(second.MessagesNext);

        // 全部：每类最多 5 条
        var all = await SearchAsync(x.Admin, "饺子");
        Assert.Equal(5, all.Messages.Count);
        Assert.NotNull(all.MessagesNext);
    }

    [PgFact]
    public async Task Wildcards_are_literal_and_long_text_gets_a_snippet()
    {
        await using var x = await SetupAsync();
        var conv = await DirectAsync(x.Admin, x.Bob.Id);
        await SayAsync(x.Admin, conv, "折扣 100% 真的");
        await SayAsync(x.Admin, conv, "折扣 1000 块");
        await SayAsync(x.Admin, conv, "file_a.txt");
        await SayAsync(x.Admin, conv, "fileXa.txt");
        await SayAsync(x.Admin, conv, @"路径 C:\temp");
        var longText = new string('前', 80) + "关键词在中间" + new string('后', 80);
        await SayAsync(x.Admin, conv, longText);

        Assert.Equal(["折扣 100% 真的"], (await SearchAsync(x.BobClient, "100%")).Messages.Select(m => m.Snippet));
        Assert.Equal(["file_a.txt"], (await SearchAsync(x.BobClient, "e_a")).Messages.Select(m => m.Snippet));
        Assert.Equal([@"路径 C:\temp"], (await SearchAsync(x.BobClient, @":\t")).Messages.Select(m => m.Snippet));

        var snippet = Assert.Single((await SearchAsync(x.BobClient, "关键词")).Messages).Snippet;
        Assert.StartsWith("…", snippet);
        Assert.EndsWith("…", snippet);
        Assert.Contains("关键词在中间", snippet);
        Assert.True(snippet.Length < longText.Length);

        Assert.Equal(HttpStatusCode.BadRequest, (await x.BobClient.GetAsync("/api/search?q=%20%20")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await x.BobClient.GetAsync($"/api/search?q={new string('字', 101)}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await x.BobClient.GetAsync("/api/search?q=a&type=Messages&cursor=garbage")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await x.BobClient.GetAsync($"/api/search?q=a&cursor={SearchService.FormatCursor(DateTimeOffset.UtcNow, Guid.NewGuid())}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await x.App.Anonymous().GetAsync("/api/search?q=a")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await x.BobClient.GetAsync("/api/search?q=%00")).StatusCode); // 只有控制字符：等于没输入
        Assert.Equal(HttpStatusCode.OK, (await x.BobClient.GetAsync("/api/search?q=%E6%8A%98%00%E6%89%A3")).StatusCode); // 中间夹着 NUL 不是 500
    }

    [PgFact]
    public async Task Files_follow_drive_permissions_shares_and_trash()
    {
        await using var x = await SetupAsync();
        var adminDrive = await (await x.Admin.GetAsync("/api/drive")).ReadAsync<DriveOverviewDto>();
        var bobDrive = await (await x.BobClient.GetAsync("/api/drive")).ReadAsync<DriveOverviewDto>();
        var family = adminDrive.Spaces.Single(s => s.Type == SpaceType.Family).Id;
        var adminPersonal = adminDrive.Spaces.Single(s => s.Type == SpaceType.Personal).Id;
        var bobPersonal = bobDrive.Spaces.Single(s => s.Type == SpaceType.Personal).Id;
        async Task<NodeDto> Folder(HttpClient c, Guid space, Guid? parent, string name)
            => await (await c.PostJsonAsync("/api/drive/folders", new { spaceId = space, parentId = parent, name })).ReadAsync<NodeDto>();
        async Task<NodeDto> File(HttpClient c, Guid owner, Guid space, Guid? parent, string name)
        {
            var blob = await x.App.AddBlobAsync(owner, "text/plain");
            return await (await c.PostJsonAsync("/api/drive/files", new { spaceId = space, parentId = parent, blobId = blob.Id, name, onConflict = "Rename" })).ReadAsync<NodeDto>();
        }

        var photos = await Folder(x.Admin, adminPersonal, null, "饺子照片");
        var mine = await File(x.Admin, x.AdminId, adminPersonal, photos.Id, "春节饺子.jpg");
        var shared = await File(x.Admin, x.AdminId, family, null, "饺子馅配方.docx");
        await File(x.BobClient, x.Bob.Id, bobPersonal, null, "鲍勃自己的饺子.txt");
        var trip = await Folder(x.BobClient, bobPersonal, null, "旅行");
        var inTrip = await File(x.BobClient, x.Bob.Id, bobPersonal, trip.Id, "饺子馆合影.jpg");
        var list = await File(x.BobClient, x.Bob.Id, bobPersonal, null, "饺子清单.txt");
        await (await x.BobClient.PostJsonAsync($"/api/drive/nodes/{trip.Id}/shares", new { granteeUserId = x.AdminId, permission = "Read" })).ReadAsync<ShareDto>();
        await (await x.BobClient.PostJsonAsync($"/api/drive/nodes/{list.Id}/shares", new { granteeUserId = x.AdminId, permission = "Read" })).ReadAsync<ShareDto>();

        var admin = (await SearchAsync(x.Admin, "饺子", "Files")).Files;
        Assert.Equal(
            new[] { ("饺子清单.txt", "Shared", false), ("饺子馆合影.jpg", "Shared", true), ("饺子馅配方.docx", "Family", true), ("春节饺子.jpg", "Personal", true), ("饺子照片", "Personal", true) }.OrderBy(t => t.Item1),
            admin.Select(f => (f.Name, f.Space, f.ParentReadable)).OrderBy(t => t.Item1));
        var hit = admin.Single(f => f.NodeId == inTrip.Id);
        Assert.Equal((bobPersonal, trip.Id, false), (hit.SpaceId, hit.ParentId, hit.IsFolder));
        Assert.True(admin.Single(f => f.NodeId == photos.Id).IsFolder);

        // 别人没共享的、家庭空间以外的看不到；孩子能看到家庭空间
        Assert.Equal(["饺子馅配方.docx"], (await SearchAsync(x.CarolClient, "饺子", "Files")).Files.Select(f => f.Name));
        Assert.Equal(["饺子馅配方.docx"], (await SearchAsync(x.KidClient, "饺子", "Files")).Files.Select(f => f.Name));
        Assert.Equal(4, (await SearchAsync(x.BobClient, "饺子", "Files")).Files.Count); // 自己的 3 个 + 家庭的 1 个

        // 回收站里的、过期的共享不出现
        Assert.Equal(HttpStatusCode.NoContent, (await x.Admin.DeleteAsync($"/api/drive/nodes/{photos.Id}")).StatusCode);
        await x.App.WithDbAsync(db => db.NodeShares.Where(s => s.NodeId == trip.Id).ExecuteUpdateAsync(s => s.SetProperty(v => v.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1))));
        Assert.Equal(new[] { "饺子清单.txt", "饺子馅配方.docx" }.Order(StringComparer.Ordinal), (await SearchAsync(x.Admin, "饺子", "Files")).Files.Select(f => f.Name).Order(StringComparer.Ordinal));
        _ = mine;
        _ = shared;
    }

    [PgFact]
    public async Task Moments_follow_post_visibility_and_albums_are_found_by_title()
    {
        await using var x = await SetupAsync();
        async Task<PostDto> Post(HttpClient c, string text, string visibility = "Family")
            => await (await c.PostJsonAsync("/api/posts", new { text, visibility, blobIds = Array.Empty<Guid>() })).ReadAsync<PostDto>();
        async Task<PostDto> Comment(HttpClient c, Guid post, string text)
            => await (await c.PostJsonAsync($"/api/posts/{post}/comments", new { text })).ReadAsync<PostDto>();

        var family = await Post(x.Admin, "今天全家包饺子");
        await Post(x.Admin, "我的私密饺子日记", "Private");
        await Post(x.BobClient, "鲍勃的私密饺子", "Private");
        await Comment(x.BobClient, family.Id, "饺子真香");
        var doomed = await Post(x.Admin, "要删掉的饺子");
        await Comment(x.BobClient, doomed.Id, "这条评论也是饺子");
        Assert.Equal(HttpStatusCode.NoContent, (await x.Admin.DeleteAsync($"/api/posts/{doomed.Id}")).StatusCode);
        var blob = await x.App.AddBlobAsync(x.AdminId, "image/jpeg");
        var album = await (await x.Admin.PostJsonAsync("/api/albums", new { title = "饺子宴", blobIds = new[] { blob.Id } })).ReadAsync<AlbumDto>();

        var admin = await SearchAsync(x.Admin, "饺子", "Moments");
        Assert.Equal(["饺子真香", "我的私密饺子日记", "今天全家包饺子"], admin.Moments.Select(m => m.Snippet));
        var comment = admin.Moments[0];
        Assert.Equal(("comment", family.Id, x.Bob.Id), (comment.Kind, comment.PostId, comment.AuthorId));
        Assert.Equal(("post", family.Id), (admin.Moments[^1].Kind, admin.Moments[^1].PostId));
        Assert.False(admin.AlbumsMore);
        var hit = Assert.Single(admin.Albums);
        Assert.Equal((album.Id, "饺子宴", blob.Id, 1), (hit.AlbumId, hit.Title, hit.CoverBlobId, hit.ItemCount));

        var bob = await SearchAsync(x.BobClient, "饺子");
        Assert.Equal(["饺子真香", "鲍勃的私密饺子", "今天全家包饺子"], bob.Moments.Select(m => m.Snippet));
        Assert.Single(bob.Albums);
    }

    [PgFact]
    public async Task Trigram_indexes_exist()
    {
        await using var x = await SetupAsync();
        var names = await x.App.WithDbAsync(db => db.Database.SqlQueryRaw<string>("SELECT indexname AS \"Value\" FROM pg_indexes WHERE indexname LIKE '%_trgm'").ToListAsync());
        Assert.Equal(
            ["ix_albums_title_trgm", "ix_comments_text_trgm", "ix_drive_nodes_name_trgm", "ix_message_attachments_name_trgm", "ix_messages_body_trgm", "ix_posts_text_trgm"],
            names.Order());
    }

    [Fact]
    public void Terms_cursor_and_snippet_helpers()
    {
        Assert.Equal(["饺子", "公园"], SearchService.Terms("  饺子　公园 饺子 "));
        Assert.Empty(SearchService.Terms("   "));
        Assert.Equal(@"100\%\_\\", SearchService.Escape(@"100%_\"));
        var at = new DateTimeOffset(2026, 11, 14, 8, 0, 0, TimeSpan.Zero).AddTicks(1234);
        var id = Guid.NewGuid();
        Assert.Equal((at, id), SearchService.ParseCursor(SearchService.FormatCursor(at, id)));
        Assert.Equal("短文本 不截", SearchService.Snippet("短文本\n不截", ["截"]));
        Assert.Equal(["a", "b"], SearchService.Terms("a\0b"));
        Assert.Equal(["Cafe\u0301"], SearchService.Terms("Cafe\u0301")); // 不规范化：和库里存的原样对得上
        var emoji = new string('a', 50) + "😀" + "关键" + new string('b', 100);
        var s = SearchService.Snippet(emoji, ["关键"], radius: 1);
        Assert.False(char.IsLowSurrogate(s.TrimStart('…')[0]));
    }

    [PgFact]
    public async Task File_pages_do_not_skip_renamed_items_and_albums_say_when_there_are_more()
    {
        await using var x = await SetupAsync();
        var family = (await (await x.Admin.GetAsync("/api/drive")).ReadAsync<DriveOverviewDto>()).Spaces.Single(s => s.Type == SpaceType.Family).Id;
        var made = new List<NodeDto>();
        for (var i = 0; i < 3; i++)
            made.Add(await (await x.Admin.PostJsonAsync("/api/drive/folders", new { spaceId = family, parentId = (Guid?)null, name = $"相册素材{i}" })).ReadAsync<NodeDto>());
        var first = await SearchAsync(x.Admin, "相册素材", "Files", limit: 2);
        Assert.Equal([made[2].Id, made[1].Id], first.Files.Select(f => f.NodeId));
        // 第二页之前，最早那个被改了名（updated_at 变成最新）：仍然在第二页，不会被跳过
        await (await x.Admin.PatchJsonAsync($"/api/drive/nodes/{made[0].Id}", new { name = "相册素材0（改）" })).ReadAsync<NodeDto>();
        var second = await SearchAsync(x.Admin, "相册素材", "Files", first.FilesNext, limit: 2);
        Assert.Equal([made[0].Id], second.Files.Select(f => f.NodeId));

        var blob = await x.App.AddBlobAsync(x.AdminId, "image/jpeg");
        for (var i = 0; i < 6; i++)
            await (await x.Admin.PostJsonAsync("/api/albums", new { title = $"春游{i}", blobIds = new[] { blob.Id } })).ReadAsync<AlbumDto>();
        var all = await SearchAsync(x.BobClient, "春游");
        Assert.Equal((5, true), (all.Albums.Count, all.AlbumsMore));
        var moments = await SearchAsync(x.BobClient, "春游", "Moments");
        Assert.Equal((6, false), (moments.Albums.Count, moments.AlbumsMore));
    }

    [PgFact]
    public async Task Message_list_pages_forward_after_a_seq_to_fill_gaps()
    {
        await using var x = await SetupAsync();
        var conv = await DirectAsync(x.Admin, x.Bob.Id);
        for (var i = 1; i <= 10; i++) await SayAsync(x.Admin, conv, $"第 {i} 条");
        var after = await (await x.BobClient.GetAsync($"/api/conversations/{conv}/messages?afterSeq=3&limit=4")).ReadAsync<List<MessageDto>>();
        Assert.Equal([7L, 6L, 5L, 4L], after.Select(m => m.Seq)); // 紧挨着 3 之后的 4 条，倒序返回
        var before = await (await x.BobClient.GetAsync($"/api/conversations/{conv}/messages?beforeSeq=3&limit=4")).ReadAsync<List<MessageDto>>();
        Assert.Equal([2L, 1L], before.Select(m => m.Seq));
    }
}

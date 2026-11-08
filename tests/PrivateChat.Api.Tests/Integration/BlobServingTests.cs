using System.Net;
using PrivateChat.Api.Domain;
using PrivateChat.Api.Features.Blobs;

namespace PrivateChat.Api.Tests.Integration;

/// <summary>派生产物还没有 / 不需要时回退原件的规则与缓存头。</summary>
public class BlobServingTests
{
    [PgFact]
    public async Task Play720_fallback_is_cached_only_once_no_transcode_will_ever_replace_it()
    {
        await using var app = new ApiFactory();
        var admin = await app.LoginAsync("admin@example.com");
        var adminId = (await (await admin.GetAsync("/api/auth/me")).ReadAsync<Features.Auth.UserDto>()).Id;

        // 探测过、没有 play-720p 登记：Worker 判定不用转码，回退原件可以长期缓存
        var video = await app.AddBlobAsync(adminId, "video/mp4");
        var res = await admin.GetAsync($"/api/blobs/{video.Id}?v=play-720p&name=clip.mp4");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("immutable", res.Headers.CacheControl!.ToString());
        Assert.Contains("clip.mp4", res.Content.Headers.ContentDisposition!.ToString());

        // 还在排队转码：回退原件但不缓存，转完后同一个地址要能拿到新版本
        await app.WithDbAsync(async db =>
        {
            db.DerivedBlobs.Add(new DerivedBlob { BlobId = video.Id, Variant = Variants.Play720 });
            await db.SaveChangesAsync();
        });
        res = await admin.GetAsync($"/api/blobs/{video.Id}?v=play-720p");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.True(res.Headers.CacheControl!.NoStore);

        // 视频封面没生成：不能拿原视频当图片
        res = await admin.GetAsync($"/api/blobs/{video.Id}?v=thumb-320");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);

        // 本来就是 m4a 的语音：不会有 audio-m4a 这一档，回退原件可缓存
        var voice = await app.AddBlobAsync(adminId, "audio/mp4");
        res = await admin.GetAsync($"/api/blobs/{voice.Id}?v=audio-m4a");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("immutable", res.Headers.CacheControl!.ToString());
    }
}

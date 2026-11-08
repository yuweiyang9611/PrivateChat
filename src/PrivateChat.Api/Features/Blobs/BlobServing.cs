using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;
using PrivateChat.Api.Domain;
using PrivateChat.Api.Infrastructure;
using PrivateChat.Api.Infrastructure.Blobs;

namespace PrivateChat.Api.Features.Blobs;

/// <summary>
/// 把 blob（原件或派生产物）作为 HTTP 响应下发：Range、ETag、immutable 缓存；
/// 非媒体类型强制下载 + nosniff，防止上传的 HTML 在同源下执行。登录接口与外链页共用。
/// 权限由调用方先判定。
/// </summary>
public sealed class BlobServing(AppDbContext db, IBlobStore store, DerivedStore derived)
{
    public async Task<IActionResult> ServeAsync(HttpResponse response, Blob blob, string? variant, string? name, bool download, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(variant))
        {
            if (!Variants.All.Contains(variant)) throw ApiException.BadRequest("未知的变体");
            var d = await db.DerivedBlobs.FirstOrDefaultAsync(x => x.BlobId == blob.Id && x.Variant == variant, ct);
            if (d is { Status: DerivedStatus.Ready })
            {
                response.Headers.CacheControl = "private, max-age=31536000, immutable";
                response.Headers.XContentTypeOptions = "nosniff";
                return new PhysicalFileResult(derived.PathFor(d.StorageKey), ReadMime(d.Meta) ?? "application/octet-stream") { EnableRangeProcessing = true };
            }
            // 还没生成 / 失败 / 不需要（视频本来就是 H.264）：
            // 图片的缩略图、视频的 720p、音频的 m4a 可以用原件顶上（不缓存，生成后 MessageUpdated 会让前端重取）；
            // 视频的缩略图不能拿原视频当图片，给 404
            var canFallBack = variant switch
            {
                Variants.Thumb320 or Variants.Thumb1280 => MimeRules.IsImage(blob.Mime),
                Variants.Play720 or Variants.AudioM4a => true,
                _ => false,
            };
            if (!canFallBack)
            {
                response.Headers.CacheControl = "no-store";
                return new NotFoundResult();
            }
            // 已经确定不会有这一档（视频探测过、不需要转码而删掉了登记；本来就是 m4a 的音频从不登记）：
            // 回退原件的结果以后不会再变，照常长期缓存，重播不用重新下载整段视频。
            // 还在排队 / 失败 / 没处理过的仍然 no-store，免得转码完成后被缓存挡住。
            var settled = d is null && variant switch
            {
                Variants.Play720 => blob.MediaProbedAt is not null,
                Variants.AudioM4a => !DerivedPlan.For(blob.Mime).Contains(Variants.AudioM4a),
                _ => false,
            };
            response.Headers.CacheControl = settled ? "private, max-age=31536000, immutable" : "no-store";
            return await OriginalAsync(response, blob, name, download, ct);
        }

        response.Headers.CacheControl = "private, max-age=31536000, immutable";
        return await OriginalAsync(response, blob, name, download, ct);
    }

    private async Task<IActionResult> OriginalAsync(HttpResponse response, Blob blob, string? name, bool download, CancellationToken ct)
    {
        var inline = !download && MimeRules.IsInlineSafe(blob.Mime);
        var fileName = MimeRules.SafeFileName(name, blob.Sha256[..12]);
        var disposition = new ContentDispositionHeaderValue(inline ? "inline" : "attachment");
        disposition.SetHttpFileName(fileName);
        response.Headers.ContentDisposition = disposition.ToString();
        response.Headers.XContentTypeOptions = "nosniff";
        // 不安全的类型统一按二进制下发，浏览器不会当 HTML / 脚本执行
        var mime = inline ? blob.Mime : "application/octet-stream";
        var etag = new EntityTagHeaderValue('"' + blob.Sha256 + '"');

        if (store is FileSystemBlobStore fs)
            return new PhysicalFileResult(fs.PathFor(blob.StorageKey), mime) { EntityTag = etag, EnableRangeProcessing = true };
        var stream = await store.OpenReadAsync(blob.StorageKey, ct);
        return new FileStreamResult(stream, mime) { EntityTag = etag, EnableRangeProcessing = true };
    }

    private static string? ReadMime(string? meta)
    {
        if (meta is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(meta);
            return doc.RootElement.TryGetProperty("mime", out var m) ? m.GetString() : null;
        }
        catch (JsonException) { return null; }
    }
}

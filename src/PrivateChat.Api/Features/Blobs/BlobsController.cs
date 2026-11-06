using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;
using PrivateChat.Api.Domain;
using PrivateChat.Api.Features.Auth;
using PrivateChat.Api.Infrastructure;
using PrivateChat.Api.Infrastructure.Blobs;

namespace PrivateChat.Api.Features.Blobs;

[ApiController]
[Authorize]
public sealed class BlobsController(AppDbContext db, IBlobStore store, DerivedStore derived, BlobAccessService access) : ControllerBase
{
    /// <summary>秒传探测：内容已存在则直接返回 blobId，否则走 tus 上传 /api/uploads。</summary>
    [HttpPost("api/blobs/precheck")]
    [Authorize(Policy = AuthPolicies.Member)]
    public async Task<ActionResult<PrecheckResponse>> Precheck([FromBody] PrecheckRequest req, CancellationToken ct)
    {
        if (req.Sha256 is null || req.Sha256.Length != 64 || !req.Sha256.All(Uri.IsHexDigit))
            throw ApiException.BadRequest("sha256 必须是 64 位十六进制");
        var sha = req.Sha256.ToLowerInvariant();
        var blob = await db.Blobs.FirstOrDefaultAsync(b => b.Sha256 == sha, ct);
        if (blob is null || blob.Size != req.Size) return new PrecheckResponse(false, null);
        return new PrecheckResponse(true, blob.Id);
    }

    /// <summary>
    /// 取原件或派生产物。同源 Cookie 鉴权，img / video 标签直接可用。
    /// v=thumb-320|thumb-1280|poster|play-720p|audio-m4a；产物未就绪时回退原件（不缓存）。
    /// name= 下载文件名；download=1 强制下载。
    /// </summary>
    [HttpGet("api/blobs/{id:guid}")]
    [HttpHead("api/blobs/{id:guid}")]
    [ProducesResponseType(200)]
    [ProducesResponseType<ProblemDetails>(403)]
    [ProducesResponseType<ProblemDetails>(404)]
    public async Task<IActionResult> Get(Guid id, [FromQuery] string? v, [FromQuery] string? name, [FromQuery] bool download = false, CancellationToken ct = default)
    {
        var blob = await db.Blobs.FirstOrDefaultAsync(b => b.Id == id, ct) ?? throw ApiException.NotFound("文件");
        if (!await access.CanReadAsync(id, User.UserId(), ct)) throw ApiException.Forbidden("无权访问该文件");

        if (!string.IsNullOrEmpty(v))
        {
            if (!Variants.All.Contains(v)) throw ApiException.BadRequest("未知的变体");
            var d = await db.DerivedBlobs.FirstOrDefaultAsync(x => x.BlobId == id && x.Variant == v, ct);
            if (d is { Status: DerivedStatus.Ready })
            {
                var mime = ReadMime(d.Meta) ?? "application/octet-stream";
                Response.Headers.CacheControl = "private, max-age=31536000, immutable";
                Response.Headers.XContentTypeOptions = "nosniff";
                return PhysicalFile(derived.PathFor(d.StorageKey), mime, enableRangeProcessing: true);
            }
            // 还没生成或失败：给原件，但不让浏览器缓存，等 MessageUpdated 后会再取一次
            Response.Headers.CacheControl = "no-store";
            return await ServeOriginalAsync(blob, name, download, ct);
        }

        Response.Headers.CacheControl = "private, max-age=31536000, immutable";
        return await ServeOriginalAsync(blob, name, download, ct);
    }

    private async Task<IActionResult> ServeOriginalAsync(Blob blob, string? name, bool download, CancellationToken ct)
    {
        var inline = !download && MimeRules.IsInlineSafe(blob.Mime);
        var fileName = MimeRules.SafeFileName(name, blob.Sha256[..12]);
        var disposition = new ContentDispositionHeaderValue(inline ? "inline" : "attachment");
        disposition.SetHttpFileName(fileName);
        Response.Headers.ContentDisposition = disposition.ToString();
        Response.Headers.XContentTypeOptions = "nosniff";
        // 不安全的类型统一按二进制下发，浏览器不会当 HTML / 脚本执行
        var mime = inline ? blob.Mime : "application/octet-stream";
        var etag = new EntityTagHeaderValue('"' + blob.Sha256 + '"');

        if (store is FileSystemBlobStore fs)
            return PhysicalFile(fs.PathFor(blob.StorageKey), mime, null, etag, enableRangeProcessing: true);
        var stream = await store.OpenReadAsync(blob.StorageKey, ct);
        return File(stream, mime, null, etag, enableRangeProcessing: true);
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

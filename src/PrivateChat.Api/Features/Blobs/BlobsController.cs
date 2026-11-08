using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PrivateChat.Api.Features.Auth;
using PrivateChat.Api.Infrastructure;

namespace PrivateChat.Api.Features.Blobs;

[ApiController]
[Authorize]
public sealed class BlobsController(AppDbContext db, BlobAccessService access, BlobServing serving) : ControllerBase
{
    /// <summary>秒传探测：内容已存在则直接返回 blobId，否则走 tus 上传 /api/uploads。</summary>
    [HttpPost("api/blobs/precheck")]
    public async Task<ActionResult<PrecheckResponse>> Precheck([FromBody] PrecheckRequest req, CancellationToken ct)
    {
        if (req.Sha256 is null || req.Sha256.Length != 64 || !req.Sha256.All(Uri.IsHexDigit))
            throw ApiException.BadRequest("sha256 必须是 64 位十六进制");
        var sha = req.Sha256.ToLowerInvariant();
        var blob = await db.Blobs.FirstOrDefaultAsync(b => b.Sha256 == sha, ct);
        if (blob is null || blob.Size != req.Size) return new PrecheckResponse(false, null);
        // 秒传：拿得出哈希就视为持有（家庭场景可接受"已知哈希即可确认文件存在"的风险），登记后才能引用
        await BlobAccessService.ClaimAsync(db, blob.Id, User.UserId(), ct);
        return new PrecheckResponse(true, blob.Id);
    }

    /// <summary>
    /// 取原件或派生产物。同源 Cookie 鉴权，img / video 标签直接可用。
    /// v=thumb-320|thumb-1280|play-720p|audio-m4a：图片缩略图、720p、m4a 未就绪时回退原件（不缓存），视频缩略图未就绪 404。
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
        return await serving.ServeAsync(Response, blob, v, name, download, ct);
    }
}

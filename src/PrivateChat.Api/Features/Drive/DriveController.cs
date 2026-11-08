using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PrivateChat.Api.Features.Auth;
using PrivateChat.Api.Features.Chat;

namespace PrivateChat.Api.Features.Drive;

/// <summary>网盘 REST（docs/DESIGN.md 附录 A · Drive）。所有权限判定在服务层。</summary>
[ApiController]
[Authorize]
[Route("api/drive")]
public sealed class DriveController(DriveService drive, DriveSharing sharing, GalleryService gallery) : ControllerBase
{
    private DriveActor Actor => new(User.UserId(), User.Role());

    /// <summary>我的空间（个人 + 家庭，首次访问时创建）与磁盘余量。</summary>
    [HttpGet]
    public Task<DriveOverviewDto> Overview(CancellationToken ct) => drive.OverviewAsync(Actor, ct);

    /// <summary>管理员：配额、家庭空间孩子只读。</summary>
    [HttpPatch("spaces/{id:guid}")]
    public Task<SpaceDto> PatchSpace(Guid id, [FromBody] PatchSpaceRequest req, CancellationToken ct) => drive.PatchSpaceAsync(id, req, Actor, ct);

    /// <summary>列目录：folderId 为空即空间根目录。被共享的人用共享节点的 id 进入。</summary>
    [HttpGet("list")]
    public Task<ListingDto> List([FromQuery] Guid spaceId, [FromQuery] Guid? folderId, CancellationToken ct) => drive.ListAsync(spaceId, folderId, Actor, ct);

    /// <summary>移动对话框：空间内全部文件夹。</summary>
    [HttpGet("spaces/{id:guid}/folders")]
    public Task<List<FolderRefDto>> Folders(Guid id, CancellationToken ct) => drive.FoldersAsync(id, Actor, ct);

    [HttpPost("folders")]
    public Task<NodeDto> CreateFolder([FromBody] CreateFolderRequest req, CancellationToken ct) => drive.CreateFolderAsync(req, Actor, ct);

    /// <summary>上传完成（或秒传命中）后登记为网盘文件；重名默认自动改名，也可选择覆盖（旧文件进回收站）。</summary>
    [HttpPost("files")]
    public Task<NodeDto> CreateFile([FromBody] CreateFileRequest req, CancellationToken ct) => drive.CreateFileAsync(req, Actor, ct);

    /// <summary>改名 / 移动（可跨空间）。</summary>
    [HttpPatch("nodes/{id:guid}")]
    public Task<NodeDto> Patch(Guid id, [FromBody] PatchNodeRequest req, CancellationToken ct) => drive.PatchAsync(id, req, Actor, ct);

    /// <summary>移到回收站（整棵子树）。</summary>
    [HttpDelete("nodes/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await drive.DeleteAsync(id, Actor, ct);
        return NoContent();
    }

    [HttpGet("trash")]
    public Task<List<TrashItemDto>> Trash(CancellationToken ct) => drive.TrashAsync(Actor, ct);

    [HttpPost("trash/{id:guid}/restore")]
    public Task<NodeDto> Restore(Guid id, CancellationToken ct) => drive.RestoreAsync(id, Actor, ct);

    /// <summary>彻底删除一条回收站记录。</summary>
    [HttpDelete("trash/{id:guid}")]
    public async Task<IActionResult> Purge(Guid id, CancellationToken ct)
    {
        await drive.PurgeAsync(id, Actor, ct);
        return NoContent();
    }

    /// <summary>清空回收站（只清自己能管理的）。</summary>
    [HttpDelete("trash")]
    public async Task<ActionResult<int>> EmptyTrash(CancellationToken ct) => await drive.EmptyTrashAsync(Actor, ct);

    [HttpGet("shared-with-me")]
    public Task<List<SharedNodeDto>> SharedWithMe(CancellationToken ct) => sharing.SharedWithMeAsync(Actor, ct);

    [HttpGet("nodes/{id:guid}/shares")]
    public Task<List<ShareDto>> Shares(Guid id, CancellationToken ct) => sharing.ListAsync(id, Actor, ct);

    /// <summary>共享给成员（Read / Write），或生成外链（可选密码、过期天数）。</summary>
    [HttpPost("nodes/{id:guid}/shares")]
    public Task<ShareDto> Share(Guid id, [FromBody] CreateShareRequest req, CancellationToken ct) => sharing.CreateAsync(id, req, Actor, ct);

    [HttpDelete("shares/{id:guid}")]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken ct)
    {
        await sharing.RevokeAsync(id, Actor, ct);
        return NoContent();
    }

    /// <summary>发送到聊天：建一条引用同一份内容的消息（不复制数据）。</summary>
    [HttpPost("nodes/{id:guid}/send")]
    public Task<MessageDto> Send(Guid id, [FromBody] SendToChatRequest req, CancellationToken ct) => sharing.SendToChatAsync(id, req, Actor, ct);

    /// <summary>相册时间线：游标分页（上一页最后一项的 sortAt + blobId）。</summary>
    [HttpGet("gallery")]
    public Task<GalleryPageDto> Gallery([FromQuery] DateTimeOffset? before, [FromQuery] Guid? beforeId, [FromQuery] int limit = 60, CancellationToken ct = default)
        => gallery.PageAsync(User.UserId(), before, beforeId, limit, ct);
}

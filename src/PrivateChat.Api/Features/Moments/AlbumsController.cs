using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PrivateChat.Api.Features.Auth;

namespace PrivateChat.Api.Features.Moments;

/// <summary>相册：全家共享的手动相册 + 按月自动相册（docs/DESIGN.md 4.7）。</summary>
[ApiController]
[Authorize]
[Route("api/albums")]
public sealed class AlbumsController(AlbumService albums) : ControllerBase
{
    /// <summary>手动相册（最近改动的在前）与按月相册（我能看到的照片按月份分组）。</summary>
    [HttpGet]
    public Task<AlbumsOverviewDto> Overview(CancellationToken ct) => albums.OverviewAsync(User.UserId(), ct);

    [HttpPost]
    public Task<AlbumDto> Create([FromBody] CreateAlbumRequest req, CancellationToken ct) => albums.CreateAsync(User.UserId(), req, ct);

    [HttpPatch("{id:guid}")]
    public Task<AlbumDto> Patch(Guid id, [FromBody] PatchAlbumRequest req, CancellationToken ct)
        => albums.PatchAsync(User.UserId(), User.Role(), id, req, ct);

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await albums.DeleteAsync(User.UserId(), User.Role(), id, ct);
        return NoContent();
    }

    [HttpGet("{id:guid}/items")]
    public Task<AlbumItemsPageDto> Items(Guid id, [FromQuery] DateTimeOffset? before, [FromQuery] Guid? beforeId, [FromQuery] int limit = 60, CancellationToken ct = default)
        => albums.ItemsAsync(id, before, beforeId, limit, ct);

    [HttpPost("{id:guid}/items")]
    public Task<AddAlbumItemsResult> AddItems(Guid id, [FromBody] AddAlbumItemsRequest req, CancellationToken ct)
        => albums.AddItemsAsync(User.UserId(), id, req, ct);

    [HttpDelete("{id:guid}/items/{blobId:guid}")]
    public Task<AlbumDto> RemoveItem(Guid id, Guid blobId, CancellationToken ct)
        => albums.RemoveItemAsync(User.UserId(), User.Role(), id, blobId, ct);
}

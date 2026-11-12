using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PrivateChat.Api.Features.Auth;

namespace PrivateChat.Api.Features.Moments;

/// <summary>动态、评论、点赞、那年今天（docs/DESIGN.md 4.7、附录 A · Moments）。权限判定在服务层。</summary>
[ApiController]
[Authorize]
[Route("api/posts")]
public sealed class MomentsController(MomentsService moments) : ControllerBase
{
    /// <summary>时间倒序的动态；游标为上一页最后一条的 createdAt + id；authorId 只看某个人的。</summary>
    [HttpGet]
    public Task<PostPageDto> Feed([FromQuery] DateTimeOffset? before, [FromQuery] Guid? beforeId, [FromQuery] Guid? authorId, [FromQuery] int limit = 20, CancellationToken ct = default)
        => moments.FeedAsync(User.UserId(), before, beforeId, authorId, limit, ct);

    /// <summary>往年今天的照片与动态；date（yyyy-MM-dd）默认今天（家庭时区）。</summary>
    [HttpGet("on-this-day")]
    public Task<OnThisDayDto> OnThisDay([FromQuery] DateOnly? date, CancellationToken ct)
        => moments.OnThisDayAsync(User.UserId(), date, ct);

    [HttpGet("{id:guid}")]
    public Task<PostDto> Get(Guid id, CancellationToken ct) => moments.GetAsync(User.UserId(), id, ct);

    [HttpPost]
    public Task<PostDto> Create([FromBody] CreatePostRequest req, CancellationToken ct) => moments.CreateAsync(User.UserId(), req, ct);

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await moments.DeleteAsync(User.UserId(), User.Role(), id, ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/comments")]
    public Task<PostDto> Comment(Guid id, [FromBody] CreateCommentRequest req, CancellationToken ct)
        => moments.CommentAsync(User.UserId(), id, req, ct);

    [HttpDelete("~/api/comments/{id:guid}")]
    public Task<PostDto> DeleteComment(Guid id, CancellationToken ct) => moments.DeleteCommentAsync(User.UserId(), User.Role(), id, ct);

    /// <summary>点赞开关：同一个表情再点一次取消。</summary>
    [HttpPost("{id:guid}/reactions")]
    public Task<PostDto> React(Guid id, [FromBody] ToggleReactionRequest req, CancellationToken ct)
        => moments.TogglePostReactionAsync(User.UserId(), id, req.Emoji, ct);

    [HttpPost("~/api/comments/{id:guid}/reactions")]
    public Task<PostDto> ReactToComment(Guid id, [FromBody] ToggleReactionRequest req, CancellationToken ct)
        => moments.ToggleCommentReactionAsync(User.UserId(), id, req.Emoji, ct);
}

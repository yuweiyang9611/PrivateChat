using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using PrivateChat.Api.Features.Auth;
using PrivateChat.Api.Features.Notifications;

namespace PrivateChat.Api.Features.Chat;

[ApiController]
[Authorize]
public sealed class MessagesController(MessageService messages, IHubContext<ChatHub> hub, PushService push) : ControllerBase
{
    /// <summary>向前翻页：seq 小于 beforeSeq 的消息，按 seq 倒序。</summary>
    [HttpGet("api/conversations/{conversationId:guid}/messages")]
    public async Task<ActionResult<List<MessageDto>>> List(Guid conversationId, [FromQuery] long? beforeSeq, [FromQuery] int limit = 50, CancellationToken ct = default)
    {
        var list = await messages.ListAsync(conversationId, User.UserId(), beforeSeq, limit, ct);
        return list.Select(MessageDto.From).ToList();
    }

    /// <summary>REST 备用发送通道（主通道是 ChatHub.SendMessage）。</summary>
    [HttpPost("api/conversations/{conversationId:guid}/messages")]
    [Authorize(Policy = AuthPolicies.Member)]
    public async Task<ActionResult<MessageDto>> Send(Guid conversationId, [FromBody] SendMessageRequest req, CancellationToken ct)
    {
        var (message, created) = await messages.SendAsync(User.UserId(), req with { ConversationId = conversationId }, ct);
        var dto = MessageDto.From(message);
        if (created)
        {
            await hub.Clients.Group(ChatEvents.ConversationGroup(conversationId)).SendAsync(ChatEvents.MessageReceived, dto, ct);
            await push.QueueMessageAsync(message, ct);
        }
        return dto;
    }

    /// <summary>表情回应开关（REST 备用通道，主通道是 ChatHub.React）。</summary>
    [HttpPost("api/messages/{id:guid}/reactions")]
    public async Task<ActionResult<ReactionsDto>> React(Guid id, [FromBody] ReactRequest req, CancellationToken ct)
    {
        var dto = await messages.ToggleReactionAsync(id, User.UserId(), req.Emoji, ct);
        await hub.Clients.Group(ChatEvents.ConversationGroup(dto.ConversationId)).SendAsync(ChatEvents.ReactionChanged, dto, ct);
        return dto;
    }

    /// <summary>撤回。</summary>
    [HttpDelete("api/messages/{id:guid}")]
    public async Task<IActionResult> Recall(Guid id, CancellationToken ct)
    {
        var m = await messages.RecallAsync(id, User.UserId(), User.Role(), ct);
        await hub.Clients.Group(ChatEvents.ConversationGroup(m.ConversationId))
            .SendAsync(ChatEvents.MessageDeleted, new { messageId = m.Id, conversationId = m.ConversationId, seq = m.Seq }, ct);
        return NoContent();
    }
}

using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using PrivateChat.Api.Domain;
using PrivateChat.Api.Features.Chat;
using PrivateChat.Api.Features.Notifications;
using PrivateChat.Api.Infrastructure;

namespace PrivateChat.Api.Features.Moments;

/// <summary>
/// 动态与相册的实时通知：
/// - SignalR PostChanged / AlbumChanged：能看到的人刷新（家人可见的动态和相册发给所有人，私密动态只发给作者）；
/// - Web Push：有人评论、回复、点赞时通知当事人，只推给当前不在线的人（在线的人页面会实时刷新），同一条动态的通知互相替换。
/// </summary>
public sealed class MomentsNotifier(AppDbContext db, IHubContext<ChatHub> hub, PresenceTracker presence, PushService push)
{
    public Task PostChangedAsync(Post post, string change, CancellationToken ct)
    {
        var target = post.Visibility == PostVisibility.Family ? hub.Clients.All : hub.Clients.Group(ChatEvents.UserGroup(post.AuthorId));
        return target.SendAsync(ChatEvents.PostChanged, new PostChangedDto(post.Id, post.AuthorId, change), ct);
    }

    public Task AlbumChangedAsync(Guid albumId, bool deleted, CancellationToken ct)
        => hub.Clients.All.SendAsync(ChatEvents.AlbumChanged, new AlbumChangedDto(albumId, deleted), ct);

    /// <summary>titleFormat 里的 {0} 换成做这件事的人的名字。</summary>
    public async Task PushAsync(IEnumerable<Guid> userIds, Guid actorId, string titleFormat, string body, Guid postId, CancellationToken ct)
    {
        var targets = userIds.Where(u => u != actorId && !presence.IsOnline(u)).Distinct().ToList();
        if (targets.Count == 0) return;
        var actor = await db.Users.Where(u => u.Id == actorId).Select(u => u.DisplayName).FirstOrDefaultAsync(ct) ?? "家人";
        var payload = new PushPayload(string.Format(titleFormat, actor), body, $"/moments/posts/{postId}", $"post:{postId}", Kind: PushKinds.Moment);
        push.SendToUsersInBackground(targets, payload, ttlSeconds: 24 * 3600);
    }
}

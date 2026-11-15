using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using PrivateChat.Api.Features.Auth;
using PrivateChat.Api.Features.Notifications;
using PrivateChat.Api.Infrastructure;

namespace PrivateChat.Api.Features.Chat;

/// <summary>服务端 → 客户端事件名（docs/DESIGN.md 附录 B）。</summary>
public static class ChatEvents
{
    public const string MessageReceived = "MessageReceived";
    public const string MessageUpdated = "MessageUpdated";
    public const string MessageDeleted = "MessageDeleted";
    public const string ReactionChanged = "ReactionChanged";
    public const string ReadReceipt = "ReadReceipt";
    public const string Typing = "Typing";
    public const string Presence = "Presence";
    public const string ConversationUpdated = "ConversationUpdated";
    public const string IncomingCall = "IncomingCall";
    public const string CallUpdated = "CallUpdated";
    public const string ParticipantJoined = "ParticipantJoined";
    public const string ParticipantLeft = "ParticipantLeft";
    public const string ParticipantUpdated = "ParticipantUpdated";
    public const string CallDeclined = "CallDeclined";
    public const string TrackPublished = "TrackPublished";
    public const string TrackUnpublished = "TrackUnpublished";
    public const string DriveChanged = "DriveChanged";
    public const string PostChanged = "PostChanged";
    public const string AlbumChanged = "AlbumChanged";
    public const string Notification = "Notification";
    public const string RemoteSessionChanged = "RemoteSessionChanged";
    public const string DeviceChanged = "DeviceChanged";

    public static string UserGroup(Guid userId) => $"user:{userId}";
    public static string ConversationGroup(Guid conversationId) => $"conv:{conversationId}";
}

/// <summary>在线连接跟踪（单实例内存版）。</summary>
public sealed class PresenceTracker
{
    private readonly Dictionary<Guid, HashSet<string>> _connections = new();
    /// <summary>每条连接怎么掐断（HubCallerContext.Abort）。</summary>
    private readonly Dictionary<string, Action> _aborts = new();
    private readonly Lock _lock = new();

    public bool Connected(Guid userId, string connectionId, Action? abort = null)
    {
        lock (_lock)
        {
            if (!_connections.TryGetValue(userId, out var set)) _connections[userId] = set = [];
            set.Add(connectionId);
            if (abort is not null) _aborts[connectionId] = abort;
            return set.Count == 1;
        }
    }

    /// <summary>
    /// 掐断该用户的所有连接（被停用、改了角色）：连接上的身份是连上那一刻的，不断开就一直沿用。
    /// 客户端会自动重连，重连时按库里现在的状态重新认证——被停用的连不上，改了角色的拿到新角色。
    /// </summary>
    public void Kick(Guid userId)
    {
        List<Action> aborts;
        lock (_lock)
        {
            aborts = _connections.TryGetValue(userId, out var set)
                ? set.Select(id => _aborts.GetValueOrDefault(id)).OfType<Action>().ToList()
                : [];
        }
        foreach (var abort in aborts) abort();
    }

    public bool Disconnected(Guid userId, string connectionId)
    {
        lock (_lock)
        {
            _aborts.Remove(connectionId);
            if (!_connections.TryGetValue(userId, out var set)) return false;
            set.Remove(connectionId);
            if (set.Count > 0) return false;
            _connections.Remove(userId);
            return true;
        }
    }

    public bool IsOnline(Guid userId) { lock (_lock) return _connections.ContainsKey(userId); }

    /// <summary>该用户当前所有连接（新建会话后把在线成员的连接加进会话组）。</summary>
    public List<string> ConnectionsOf(Guid userId)
    {
        lock (_lock) return _connections.TryGetValue(userId, out var set) ? [.. set] : [];
    }
}

[Authorize]
public sealed class ChatHub(AppDbContext db, MessageService messages, PresenceTracker presence, PushService push) : Hub
{
    public override async Task OnConnectedAsync()
    {
        var userId = Context.User!.UserId();
        await Groups.AddToGroupAsync(Context.ConnectionId, ChatEvents.UserGroup(userId));
        var convs = await db.ConversationMembers.Where(m => m.UserId == userId).Select(m => m.ConversationId).ToListAsync();
        foreach (var c in convs) await Groups.AddToGroupAsync(Context.ConnectionId, ChatEvents.ConversationGroup(c));

        if (presence.Connected(userId, Context.ConnectionId, Context.Abort))
            await Clients.Others.SendAsync(ChatEvents.Presence, new { userId, online = true, lastSeenAt = (DateTimeOffset?)null });
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var userId = Context.User!.UserId();
        if (presence.Disconnected(userId, Context.ConnectionId))
            await Clients.Others.SendAsync(ChatEvents.Presence, new { userId, online = false, lastSeenAt = DateTimeOffset.UtcNow });
        await base.OnDisconnectedAsync(exception);
    }

    public async Task<MessageDto> SendMessage(SendMessageRequest req)
    {
        var (message, created) = await messages.SendAsync(Context.User!.UserId(), req, Context.ConnectionAborted);
        var dto = MessageDto.From(message);
        if (created)
        {
            await Clients.Group(ChatEvents.ConversationGroup(req.ConversationId)).SendAsync(ChatEvents.MessageReceived, dto);
            await push.QueueMessageAsync(message, Context.ConnectionAborted);
        }
        return dto;
    }

    public async Task Typing(Guid conversationId)
    {
        var userId = Context.User!.UserId();
        if (!await messages.IsMemberAsync(conversationId, userId, Context.ConnectionAborted)) return;
        await Clients.OthersInGroup(ChatEvents.ConversationGroup(conversationId))
            .SendAsync(ChatEvents.Typing, new TypingDto(conversationId, userId));
    }

    public async Task<ReactionsDto> React(Guid messageId, string emoji)
    {
        var dto = await messages.ToggleReactionAsync(messageId, Context.User!.UserId(), emoji, Context.ConnectionAborted);
        await Clients.Group(ChatEvents.ConversationGroup(dto.ConversationId)).SendAsync(ChatEvents.ReactionChanged, dto);
        return dto;
    }

    public async Task MarkRead(Guid conversationId, long seq)
    {
        var userId = Context.User!.UserId();
        var effective = await messages.MarkReadAsync(conversationId, userId, seq, Context.ConnectionAborted);
        await Clients.Group(ChatEvents.ConversationGroup(conversationId))
            .SendAsync(ChatEvents.ReadReceipt, new ReadReceiptDto(conversationId, userId, effective));
    }

    /// <summary>重连后补拉：每个会话 since seq 之后的消息，超过 500 条则提示整页刷新。</summary>
    public async Task<SyncResultDto> Sync(Dictionary<Guid, long> lastSeqs)
    {
        try
        {
            return await messages.SyncAsync(Context.User!.UserId(), lastSeqs, Context.ConnectionAborted);
        }
        catch (OperationCanceledException) when (Context.ConnectionAborted.IsCancellationRequested)
        {
            // 客户端在补拉途中断开 / 刷新了页面：不是错误，下次连上会再补
            return new SyncResultDto([], []);
        }
    }
}

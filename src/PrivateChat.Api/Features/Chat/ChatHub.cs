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
    public const string TrackPublished = "TrackPublished";
    public const string TrackUnpublished = "TrackUnpublished";
    public const string DriveChanged = "DriveChanged";
    public const string Notification = "Notification";
    public const string RemoteSessionChanged = "RemoteSessionChanged";

    public static string UserGroup(Guid userId) => $"user:{userId}";
    public static string ConversationGroup(Guid conversationId) => $"conv:{conversationId}";
}

/// <summary>在线连接跟踪（单实例内存版）。</summary>
public sealed class PresenceTracker
{
    private readonly Dictionary<Guid, HashSet<string>> _connections = new();
    private readonly Lock _lock = new();

    public bool Connected(Guid userId, string connectionId)
    {
        lock (_lock)
        {
            if (!_connections.TryGetValue(userId, out var set)) _connections[userId] = set = [];
            set.Add(connectionId);
            return set.Count == 1;
        }
    }

    public bool Disconnected(Guid userId, string connectionId)
    {
        lock (_lock)
        {
            if (!_connections.TryGetValue(userId, out var set)) return false;
            set.Remove(connectionId);
            if (set.Count > 0) return false;
            _connections.Remove(userId);
            return true;
        }
    }

    public bool IsOnline(Guid userId) { lock (_lock) return _connections.ContainsKey(userId); }
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

        if (presence.Connected(userId, Context.ConnectionId))
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

    public async Task MarkRead(Guid conversationId, long seq)
    {
        var userId = Context.User!.UserId();
        var effective = await messages.MarkReadAsync(conversationId, userId, seq, Context.ConnectionAborted);
        await Clients.Group(ChatEvents.ConversationGroup(conversationId))
            .SendAsync(ChatEvents.ReadReceipt, new ReadReceiptDto(conversationId, userId, effective));
    }

    /// <summary>重连后补拉：每个会话 since seq 之后的消息，超过 500 条则提示整页刷新。</summary>
    public Task<SyncResultDto> Sync(Dictionary<Guid, long> lastSeqs)
        => messages.SyncAsync(Context.User!.UserId(), lastSeqs, Context.ConnectionAborted);
}

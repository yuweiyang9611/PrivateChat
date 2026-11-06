using System.Collections.Concurrent;
using System.Text.Json;
using Lib.Net.Http.WebPush.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PrivateChat.Api.Domain;
using PrivateChat.Api.Features.Chat;
using PrivateChat.Api.Infrastructure;
using PrivateChat.Api.Infrastructure.Keys;

namespace PrivateChat.Api.Features.Notifications;

/// <summary>Service Worker 收到的 JSON。tag 相同的通知会互相替换（同一会话只留一条）。</summary>
public sealed record PushPayload(string Title, string Body, string Url, string Tag, int Count = 1);

/// <summary>
/// Web Push 发送（docs/DESIGN.md 3.5）：
/// - 新消息只推给不在线、未静音的会话成员，按用户去抖 3 秒合并成一条；
/// - 推送服务返回 404 / 410 时删掉失效订阅。
/// </summary>
public sealed class PushService(IHttpClientFactory httpFactory, IServiceScopeFactory scopes, KeyStore keys, IOptions<WebPushOptions> options, PresenceTracker presence, ILogger<PushService> logger)
{
    private sealed class Batch
    {
        public readonly Lock Gate = new();
        public DateTimeOffset LastAdded;
        public readonly List<Guid> MessageIds = [];
    }

    private readonly ConcurrentDictionary<Guid, Batch> _batches = new();
    private readonly TimeSpan _debounce = TimeSpan.FromSeconds(3);
    private readonly Lazy<VapidAuthentication> _vapid = new(() => new VapidAuthentication(keys.Vapid.PublicKey, keys.Vapid.PrivateKey) { Subject = options.Value.Subject });

    public static string? EndpointOf(string? stored)
    {
        if (stored is null) return null;
        try { return JsonSerializer.Deserialize<StoredPushSubscription>(stored)?.Endpoint; }
        catch (JsonException) { return null; }
    }

    /// <summary>新消息入队：不在线的成员 3 秒后收到一条合并通知。调用方不等待。</summary>
    public async Task QueueMessageAsync(Message message, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTimeOffset.UtcNow;
        var recipients = await db.ConversationMembers
            .Where(m => m.ConversationId == message.ConversationId && m.UserId != message.SenderId && (m.MutedUntil == null || m.MutedUntil < now))
            .Select(m => m.UserId).ToListAsync(ct);
        foreach (var userId in recipients)
        {
            if (presence.IsOnline(userId)) continue;
            var batch = _batches.GetOrAdd(userId, _ => new Batch());
            lock (batch.Gate)
            {
                batch.MessageIds.Add(message.Id);
                batch.LastAdded = now;
            }
        }
    }

    /// <summary>由 PushWorker 每秒调用：把静默超过 3 秒的批次发出去。</summary>
    public async Task FlushDueAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (userId, batch) in _batches)
        {
            List<Guid> ids;
            lock (batch.Gate)
            {
                if (now - batch.LastAdded < _debounce || batch.MessageIds.Count == 0) continue;
                ids = [.. batch.MessageIds];
                batch.MessageIds.Clear();
            }
            _batches.TryRemove(userId, out _);
            // 发送前再看一眼：这 3 秒里上线了就不打扰
            if (presence.IsOnline(userId)) continue;
            try
            {
                var payload = await BuildPayloadAsync(ids, ct);
                if (payload is not null) await SendToUserAsync(userId, payload, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "推送失败 {UserId}", userId);
            }
        }
    }

    private async Task<PushPayload?> BuildPayloadAsync(List<Guid> messageIds, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var messages = await db.Messages.Where(m => messageIds.Contains(m.Id) && m.DeletedAt == null).OrderBy(m => m.Seq).ToListAsync(ct);
        if (messages.Count == 0) return null;
        var last = messages[^1];
        var conv = await db.Conversations.FirstAsync(c => c.Id == last.ConversationId, ct);
        var sender = await db.Users.FirstAsync(u => u.Id == last.SenderId, ct);
        var convIds = messages.Select(m => m.ConversationId).Distinct().Count();

        var preview = ConversationsController.Preview(last);
        if (preview.Length > 80) preview = preview[..80] + "…";
        var title = conv.Type == ConversationType.Direct ? sender.DisplayName : $"{sender.DisplayName} · {conv.Title}";
        var body = messages.Count == 1 ? preview : $"{messages.Count} 条新消息{(convIds > 1 ? $"（{convIds} 个会话）" : "")}：{preview}";
        return new PushPayload(title, body, $"/messages/{last.ConversationId}", $"conv:{last.ConversationId}", messages.Count);
    }

    /// <summary>向该用户所有浏览器订阅发送；返回成功数。</summary>
    public async Task<int> SendToUserAsync(Guid userId, PushPayload payload, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var devices = await db.Devices.Where(d => d.UserId == userId && d.Kind == DeviceKind.Browser && d.RevokedAt == null && d.PushSubscription != null).ToListAsync(ct);
        if (devices.Count == 0) return 0;

        var sender = new PushSender(httpFactory.CreateClient(nameof(PushService)), _vapid.Value, logger);
        var sent = 0;
        foreach (var d in devices)
        {
            StoredPushSubscription? sub;
            try { sub = JsonSerializer.Deserialize<StoredPushSubscription>(d.PushSubscription!); }
            catch (JsonException) { sub = null; }
            if (sub is null) { d.PushSubscription = null; continue; }

            switch (await sender.SendAsync(sub, payload, ct))
            {
                case PushSendResult.Sent:
                    sent++;
                    break;
                case PushSendResult.Gone:
                    logger.LogInformation("推送订阅已失效，移除 {DeviceId}", d.Id);
                    d.PushSubscription = null;
                    break;
            }
        }
        await db.SaveChangesAsync(ct);
        return sent;
    }

}

/// <summary>每秒刷一次去抖批次。</summary>
public sealed class PushWorker(PushService push) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(ct))
            await push.FlushDueAsync(ct);
    }
}

using System.Net;
using System.Text.Json;
using Lib.Net.Http.WebPush;
using Lib.Net.Http.WebPush.Authentication;

namespace PrivateChat.Api.Features.Notifications;

public enum PushSendResult { Sent, Gone, Failed }

/// <summary>单条推送的发送核心：VAPID 签名 + aes128gcm 加密由 Lib.Net.Http.WebPush 完成。无数据库依赖，便于单测。</summary>
public sealed class PushSender(HttpClient http, VapidAuthentication vapid, ILogger logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<PushSendResult> SendAsync(StoredPushSubscription sub, PushPayload payload, CancellationToken ct)
    {
        var client = new PushServiceClient(http) { DefaultAuthentication = vapid };
        var target = new PushSubscription { Endpoint = sub.Endpoint };
        target.SetKey(PushEncryptionKeyName.P256DH, sub.P256dh);
        target.SetKey(PushEncryptionKeyName.Auth, sub.Auth);
        var message = new PushMessage(JsonSerializer.Serialize(payload, JsonOptions))
        {
            Topic = SafeTopic(payload.Tag),
            Urgency = PushMessageUrgency.High,
            TimeToLive = 24 * 3600,
        };
        try
        {
            await client.RequestPushMessageDeliveryAsync(target, message, ct);
            return PushSendResult.Sent;
        }
        catch (PushServiceClientException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            return PushSendResult.Gone;
        }
        catch (PushServiceClientException ex)
        {
            logger.LogWarning("推送服务返回 {Status} {Body}", ex.StatusCode, ex.Body);
            return PushSendResult.Failed;
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "推送服务不可达 {Host}", Uri.TryCreate(sub.Endpoint, UriKind.Absolute, out var u) ? u.Host : "?");
            return PushSendResult.Failed;
        }
    }

    /// <summary>Topic 头只允许最多 32 个 URL-safe 字符。</summary>
    public static string SafeTopic(string tag)
    {
        var s = new string(tag.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').ToArray());
        return s.Length > 32 ? s[..32] : s;
    }
}

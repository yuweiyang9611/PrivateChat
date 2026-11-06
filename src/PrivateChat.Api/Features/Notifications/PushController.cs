using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PrivateChat.Api.Domain;
using PrivateChat.Api.Features.Auth;
using PrivateChat.Api.Infrastructure;
using PrivateChat.Api.Infrastructure.Keys;

namespace PrivateChat.Api.Features.Notifications;

public sealed record VapidPublicKeyResponse(string PublicKey);
public sealed record PushKeys(string P256dh, string Auth);
/// <summary>浏览器 PushSubscription.toJSON() 的结构。</summary>
public sealed record PushSubscribeRequest(string Endpoint, PushKeys Keys, string? UserAgent = null);
public sealed record PushUnsubscribeRequest(string Endpoint);
public sealed record PushStatusResponse(bool Subscribed, int DeviceCount);

/// <summary>订阅 JSON 的存储形态（devices.push_subscription）。</summary>
public sealed record StoredPushSubscription(string Endpoint, string P256dh, string Auth);

[ApiController]
[Authorize]
public sealed class PushController(AppDbContext db, KeyStore keys, PushService push) : ControllerBase
{
    [HttpGet("api/push/vapid-public-key")]
    public ActionResult<VapidPublicKeyResponse> VapidPublicKey() => new VapidPublicKeyResponse(keys.Vapid.PublicKey);

    /// <summary>登记 / 刷新本浏览器的推送订阅；同一 endpoint 只保留一条设备记录。</summary>
    [HttpPost("api/push/subscribe")]
    public async Task<ActionResult<PushStatusResponse>> Subscribe([FromBody] PushSubscribeRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Endpoint) || !Uri.TryCreate(req.Endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "https")
            throw ApiException.BadRequest("endpoint 必须是 https 地址");
        if (string.IsNullOrWhiteSpace(req.Keys?.P256dh) || string.IsNullOrWhiteSpace(req.Keys.Auth))
            throw ApiException.BadRequest("缺少加密密钥");

        var userId = User.UserId();
        var stored = JsonSerializer.Serialize(new StoredPushSubscription(req.Endpoint, req.Keys.P256dh, req.Keys.Auth));
        var devices = await db.Devices.Where(d => d.UserId == userId && d.Kind == DeviceKind.Browser && d.RevokedAt == null && d.PushSubscription != null).ToListAsync(ct);
        var existing = devices.FirstOrDefault(d => PushService.EndpointOf(d.PushSubscription) == req.Endpoint);
        if (existing is null)
        {
            existing = new Device { UserId = userId, Kind = DeviceKind.Browser, Platform = "web" };
            db.Devices.Add(existing);
            devices.Add(existing);
        }
        existing.Name = DeviceName(req.UserAgent ?? Request.Headers.UserAgent.ToString());
        existing.PushSubscription = stored;
        existing.LastSeenAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return new PushStatusResponse(true, devices.Count);
    }

    [HttpDelete("api/push/subscribe")]
    public async Task<ActionResult<PushStatusResponse>> Unsubscribe([FromBody] PushUnsubscribeRequest req, CancellationToken ct)
    {
        var userId = User.UserId();
        var devices = await db.Devices.Where(d => d.UserId == userId && d.Kind == DeviceKind.Browser && d.PushSubscription != null).ToListAsync(ct);
        var hit = devices.Where(d => PushService.EndpointOf(d.PushSubscription) == req.Endpoint).ToList();
        foreach (var d in hit) d.PushSubscription = null;
        await db.SaveChangesAsync(ct);
        return new PushStatusResponse(false, devices.Count - hit.Count);
    }

    /// <summary>给自己发一条测试通知，设置页用。</summary>
    [HttpPost("api/push/test")]
    public async Task<IActionResult> Test(CancellationToken ct)
    {
        var sent = await push.SendToUserAsync(User.UserId(), new PushPayload("PrivateChat", "通知已开启，这是一条测试消息", "/settings", "test"), ct);
        return Ok(new { sent });
    }

    /// <summary>从 UA 里挑个短名字：Chrome / Safari / Firefox + 系统。</summary>
    private static string DeviceName(string ua)
    {
        string browser = ua.Contains("Edg/") ? "Edge" : ua.Contains("Firefox/") ? "Firefox" : ua.Contains("Chrome/") ? "Chrome" : ua.Contains("Safari/") ? "Safari" : "浏览器";
        string os = ua.Contains("iPhone") ? "iPhone" : ua.Contains("iPad") ? "iPad" : ua.Contains("Android") ? "Android" : ua.Contains("Windows") ? "Windows" : ua.Contains("Mac OS") ? "Mac" : ua.Contains("Linux") ? "Linux" : "";
        return string.IsNullOrEmpty(os) ? browser : $"{browser} · {os}";
    }
}

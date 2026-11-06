using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PrivateChat.Api.Infrastructure;

namespace PrivateChat.Api.Features.Auth;

/// <summary>Agent 等非浏览器客户端：Authorization: Bearer &lt;设备令牌&gt;。</summary>
public sealed class DeviceTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    DeviceTokenService tokens,
    AppDbContext db)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var auth = Request.Headers.Authorization.ToString();
        if (!auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return AuthenticateResult.NoResult();
        var token = auth["Bearer ".Length..].Trim();
        if (!tokens.TryValidate(token, out var payload)) return AuthenticateResult.Fail("设备令牌无效");

        var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == payload.DeviceId, Context.RequestAborted);
        if (device is null || device.RevokedAt is not null || device.UserId != payload.UserId
            || device.TokenHash != DeviceTokenService.Hash(token))
            return AuthenticateResult.Fail("设备令牌已吊销");

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == device.UserId && u.DisabledAt == null, Context.RequestAborted);
        if (user is null) return AuthenticateResult.Fail("用户不可用");

        device.LastSeenAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(Context.RequestAborted);

        return AuthenticateResult.Success(new AuthenticationTicket(user.ToPrincipal(Scheme.Name, device.Id), Scheme.Name));
    }
}

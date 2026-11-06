using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace PrivateChat.Api.Features.Auth;

/// <summary>
/// 读 Cf-Access-Jwt-Assertion 头（nginx 只在来自 cloudflared 的 :80 监听上透传），
/// 校验后按邮箱解析 / 自动建号。没有该头则返回 NoResult，交给其它方案。
/// </summary>
public sealed class CloudflareAccessAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    CloudflareAccessTokenValidator validator)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(AuthSchemes.AccessHeader, out var header) || string.IsNullOrWhiteSpace(header))
            return AuthenticateResult.NoResult();

        var result = await validator.ValidateAsync(header.ToString(), Context.RequestAborted);
        if (!result.IsValid) return AuthenticateResult.Fail(result.Error ?? "Access 令牌无效");

        // 服务令牌（Agent）：Access 放行但无 email，由 DeviceToken 方案识别设备与用户
        if (result.Email is null) return AuthenticateResult.NoResult();

        var provisioning = Context.RequestServices.GetRequiredService<UserProvisioningService>();
        var user = await provisioning.ResolveByEmailAsync(result.Email, Context.RequestAborted);
        if (user is null) return AuthenticateResult.Fail("该邮箱不在家庭成员或邀请列表中");

        var ticket = new AuthenticationTicket(user.ToPrincipal(Scheme.Name), Scheme.Name);
        return AuthenticateResult.Success(ticket);
    }
}

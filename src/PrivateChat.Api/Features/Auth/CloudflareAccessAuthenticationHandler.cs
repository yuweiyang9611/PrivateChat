using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace PrivateChat.Api.Features.Auth;

/// <summary>
/// 读 Cf-Access-Jwt-Assertion 头（nginx 只在来自 cloudflared 的 :80 监听上透传），
/// 校验后按邮箱解析 / 自动建号。没有该头则返回 NoResult，交给其它方案。
/// 认不出人时的 401 带上原因（见 <see cref="AccessProblemCodes"/>）：前端据此显示"不是家庭成员"，
/// 而不是把它当成局域网模式去显示密码登录页。
/// </summary>
public sealed class CloudflareAccessAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    CloudflareAccessTokenValidator validator)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    /// <summary>Access 验明了的邮箱，但它不是家庭成员、没被邀请，或已被停用。</summary>
    private string? _rejectedEmail;

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
        if (user is null)
        {
            _rejectedEmail = result.Email;
            return AuthenticateResult.Fail("该邮箱不在家庭成员或邀请列表中");
        }

        var ticket = new AuthenticationTicket(user.ToPrincipal(Scheme.Name), Scheme.Name);
        return AuthenticateResult.Success(ticket);
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        // 同一个请求里处理器只有一个实例，认证结果有缓存；这里只是确保 _rejectedEmail 已经算出来
        await HandleAuthenticateOnceSafeAsync();

        var problem = _rejectedEmail is { } email
            ? new ProblemDetails
            {
                Status = StatusCodes.Status401Unauthorized,
                Title = "该邮箱不在家庭成员或邀请列表中",
                Extensions = { ["code"] = AccessProblemCodes.NotMember, ["email"] = email },
            }
            : new ProblemDetails
            {
                Status = StatusCodes.Status401Unauthorized,
                Title = "Cloudflare Access 身份校验未通过",
                Extensions = { ["code"] = AccessProblemCodes.Invalid },
            };

        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.CacheControl = "no-store";
        await Response.WriteAsJsonAsync(problem, options: null, contentType: "application/problem+json", Context.RequestAborted);
    }
}

/// <summary>经 Cloudflare Access 进来的请求被拒时，401 正文里的 code。局域网 / 本地模式的 401 没有正文。</summary>
public static class AccessProblemCodes
{
    /// <summary>Access 验明了邮箱，但它不是家庭成员、没被邀请，或已被停用。正文同时带 email。</summary>
    public const string NotMember = "access_not_member";
    /// <summary>带了 Access 头，但令牌无效（签名 / AUD / 过期 / 没有可用的身份）。</summary>
    public const string Invalid = "access_invalid";
}

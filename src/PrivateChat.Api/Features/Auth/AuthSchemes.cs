using System.Security.Claims;
using PrivateChat.Api.Domain;

namespace PrivateChat.Api.Features.Auth;

public static class AuthSchemes
{
    /// <summary>默认方案：按请求特征转发到 Access / DeviceToken / Local。</summary>
    public const string Smart = "Smart";
    public const string CloudflareAccess = "CloudflareAccess";
    public const string DeviceToken = "DeviceToken";
    public const string Local = "Local";

    public const string AccessHeader = "Cf-Access-Jwt-Assertion";
}

public static class AuthPolicies
{
    public const string Admin = "Admin";
    public const string Member = "Member"; // Admin 或 Member（不含 Child）
}

public static class AppClaims
{
    public const string DeviceId = "device_id";
    public const string Scheme = "auth_scheme";
}

public static class ClaimsPrincipalExtensions
{
    public static Guid UserId(this ClaimsPrincipal p)
        => Guid.TryParse(p.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            ? id
            : throw new UnauthorizedAccessException("未登录");

    public static Guid? DeviceId(this ClaimsPrincipal p)
        => Guid.TryParse(p.FindFirstValue(AppClaims.DeviceId), out var id) ? id : null;

    public static UserRole Role(this ClaimsPrincipal p)
        => Enum.TryParse<UserRole>(p.FindFirstValue(ClaimTypes.Role), out var r) ? r : UserRole.Child;

    public static ClaimsPrincipal ToPrincipal(this User user, string scheme, Guid? deviceId = null)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.DisplayName),
            new(ClaimTypes.Email, user.Email),
            new(ClaimTypes.Role, user.Role.ToString()),
            new(AppClaims.Scheme, scheme),
        };
        if (deviceId is { } d) claims.Add(new Claim(AppClaims.DeviceId, d.ToString()));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, scheme));
    }
}

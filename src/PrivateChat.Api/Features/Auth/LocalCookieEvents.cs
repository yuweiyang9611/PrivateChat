using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using PrivateChat.Api.Infrastructure;

namespace PrivateChat.Api.Features.Auth;

/// <summary>
/// 局域网密码登录的 Cookie 里记的是登录那一刻的身份，有效期 30 天。每次请求和库里对一下：
/// 被停用（或已不存在）的人立刻失效，角色、名字改了的立刻按新的来——
/// 和另外两种方式（Access 按邮箱、设备令牌按设备）一样，每个请求都以库里的成员状态为准。
/// </summary>
public static class LocalCookieEvents
{
    public static async Task ValidatePrincipalAsync(CookieValidatePrincipalContext context)
    {
        var principal = context.Principal;
        var db = context.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
        var user = Guid.TryParse(principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            ? await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id && u.DisabledAt == null, context.HttpContext.RequestAborted)
            : null;
        if (user is null)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(AuthSchemes.Local);
            return;
        }

        if (principal!.FindFirstValue(ClaimTypes.Role) != user.Role.ToString() || principal.FindFirstValue(ClaimTypes.Name) != user.DisplayName)
        {
            context.ReplacePrincipal(user.ToPrincipal(AuthSchemes.Local));
            context.ShouldRenew = true;
        }
    }
}

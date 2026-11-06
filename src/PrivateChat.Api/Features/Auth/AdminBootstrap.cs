using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PrivateChat.Api.Domain;
using PrivateChat.Api.Infrastructure;

namespace PrivateChat.Api.Features.Auth;

/// <summary>
/// 启动引导：配置了 App:AdminEmail 时确保管理员账号存在；
/// 再配置 App:AdminPassword 则写入（或刷新）其本地密码，用于局域网直连 / 本地开发时的密码登录。
/// </summary>
public static class AdminBootstrap
{
    public static async Task RunAsync(IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var app = scope.ServiceProvider.GetRequiredService<IOptions<AppOptions>>().Value;
        var email = app.AdminEmail?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(email)) return;

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(AdminBootstrap));

        var admin = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);
        if (admin is null)
        {
            admin = new User
            {
                Email = email,
                Username = email.Split('@')[0],
                DisplayName = email.Split('@')[0],
                Role = UserRole.Admin,
            };
            db.Users.Add(admin);
            logger.LogInformation("创建管理员 {Email}", email);
        }
        else if (admin.Role != UserRole.Admin)
        {
            admin.Role = UserRole.Admin;
        }

        if (!string.IsNullOrEmpty(app.AdminPassword))
        {
            var hasher = new PasswordHasher<User>();
            var needsUpdate = admin.PasswordHash is null
                || hasher.VerifyHashedPassword(admin, admin.PasswordHash, app.AdminPassword) == PasswordVerificationResult.Failed;
            if (needsUpdate)
            {
                admin.PasswordHash = hasher.HashPassword(admin, app.AdminPassword);
                logger.LogInformation("已写入管理员本地密码（局域网备用登录）");
            }
        }

        await db.SaveChangesAsync(ct);
    }
}

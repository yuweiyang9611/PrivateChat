using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PrivateChat.Api.Domain;
using PrivateChat.Api.Infrastructure;

namespace PrivateChat.Api.Features.Auth;

/// <summary>
/// Access 放行的邮箱 → 应用用户：
///  已有用户直接返回；等于 AdminEmail 自动建 Admin；在 invites 表里则按邀请角色建号；否则拒绝。
/// </summary>
public sealed class UserProvisioningService(AppDbContext db, IOptions<AppOptions> app, ILogger<UserProvisioningService> logger)
{
    public async Task<User?> ResolveByEmailAsync(string email, CancellationToken ct = default)
    {
        email = email.Trim().ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);
        if (user is not null) return user.DisabledAt is null ? user : null;

        var role = (UserRole?)null;
        Invite? invite = null;
        if (string.Equals(email, app.Value.AdminEmail?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            role = UserRole.Admin;
        }
        else
        {
            var now = DateTimeOffset.UtcNow;
            invite = await db.Invites.FirstOrDefaultAsync(i => i.Email == email && i.UsedBy == null && (i.ExpiresAt == null || i.ExpiresAt > now), ct);
            if (invite is not null) role = invite.Role;
        }
        if (role is null) return null;

        user = new User
        {
            Email = email,
            Username = await UniqueUsernameAsync(email, ct),
            DisplayName = email.Split('@')[0],
            Role = role.Value,
        };
        db.Users.Add(user);
        if (invite is not null)
        {
            invite.UsedBy = user.Id;
            invite.UsedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(ct);
        logger.LogInformation("自动建号 {Email} 角色 {Role}", email, role);
        return user;
    }

    private async Task<string> UniqueUsernameAsync(string email, CancellationToken ct)
    {
        var baseName = new string(email.Split('@')[0].Where(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-').ToArray());
        if (baseName.Length == 0) baseName = "user";
        var name = baseName;
        for (var i = 2; await db.Users.AnyAsync(u => u.Username == name, ct); i++) name = $"{baseName}{i}";
        return name;
    }
}

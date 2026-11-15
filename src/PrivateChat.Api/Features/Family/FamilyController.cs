using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PrivateChat.Api.Domain;
using PrivateChat.Api.Features.Auth;
using PrivateChat.Api.Infrastructure;

namespace PrivateChat.Api.Features.Family;

public sealed record MemberDto(Guid Id, string Email, string Username, string DisplayName, UserRole Role, bool ElderMode, Guid? AvatarBlobId, DateTimeOffset CreatedAt, bool Disabled);
public sealed record CreateInviteRequest(string Email, UserRole Role = UserRole.Member, int ExpiresInDays = 30);
public sealed record InviteDto(Guid Id, string? Email, UserRole Role, DateTimeOffset? ExpiresAt, Guid? UsedBy);
public sealed record PatchMemberRequest(string? DisplayName, UserRole? Role, bool? ElderMode, bool? Disabled);

[ApiController]
[Route("api/family")]
[Authorize]
public sealed class FamilyController(AppDbContext db, PrivateChat.Api.Features.Remote.RemoteService remote, PrivateChat.Api.Features.Chat.PresenceTracker presence) : ControllerBase
{
    [HttpGet("members")]
    public async Task<ActionResult<List<MemberDto>>> Members(CancellationToken ct)
        => await db.Users.OrderBy(u => u.CreatedAt)
            .Select(u => new MemberDto(u.Id, u.Email, u.Username, u.DisplayName, u.Role, u.ElderMode, u.AvatarBlobId, u.CreatedAt, u.DisabledAt != null))
            .ToListAsync(ct);

    [HttpGet("invites")]
    [Authorize(Policy = AuthPolicies.Admin)]
    public async Task<ActionResult<List<InviteDto>>> Invites(CancellationToken ct)
        => await db.Invites.OrderByDescending(i => i.CreatedAt)
            .Select(i => new InviteDto(i.Id, i.Email, i.Role, i.ExpiresAt, i.UsedBy)).ToListAsync(ct);

    /// <summary>
    /// 登记家人邮箱；该邮箱通过 Cloudflare Access 登录时自动建号（记得同时加进 Access 策略）。
    /// 同一个邮箱已经有一条没用过的邀请（包括过期的）时不再插一条，而是把它的角色和有效期改成这次的。
    /// </summary>
    [HttpPost("invites")]
    [Authorize(Policy = AuthPolicies.Admin)]
    public async Task<ActionResult<InviteDto>> CreateInvite([FromBody] CreateInviteRequest req, CancellationToken ct)
    {
        var email = req.Email.Trim().ToLowerInvariant();
        if (!IsPlausibleEmail(email)) throw ApiException.BadRequest("邮箱格式不正确");
        var existing = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);
        if (existing is not null)
            throw ApiException.Conflict(existing.DisabledAt is null ? "该邮箱已是成员" : "该邮箱是已停用的成员，在成员列表里恢复即可");

        var expiresAt = DateTimeOffset.UtcNow.AddDays(Math.Clamp(req.ExpiresInDays, 1, 365));
        var invite = await db.Invites.Where(i => i.Email == email && i.UsedBy == null).OrderByDescending(i => i.CreatedAt).FirstOrDefaultAsync(ct);
        if (invite is null)
        {
            invite = new Invite { Email = email, CreatedBy = User.UserId() };
            db.Invites.Add(invite);
        }
        invite.Role = req.Role;
        invite.ExpiresAt = expiresAt;
        await db.SaveChangesAsync(ct);
        return new InviteDto(invite.Id, invite.Email, invite.Role, invite.ExpiresAt, invite.UsedBy);
    }

    /// <summary>撤销一条还没用过的邀请：这个邮箱之后即使过了 Cloudflare Access 也进不来。已经用过的邀请是历史记录，撤不了（要拦人去停用成员）。</summary>
    [HttpDelete("invites/{id:guid}")]
    [Authorize(Policy = AuthPolicies.Admin)]
    public async Task<IActionResult> RevokeInvite(Guid id, CancellationToken ct)
    {
        var invite = await db.Invites.FirstOrDefaultAsync(i => i.Id == id, ct) ?? throw ApiException.NotFound("邀请");
        if (invite.UsedBy is not null) throw ApiException.Conflict("对方已经加入了，要拦住他请在成员列表里停用");
        db.Invites.Remove(invite);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>够用的格式检查：一个 @，两边都有内容，域名带点，没有空白。真伪由 Cloudflare Access 的验证码去验。</summary>
    private static bool IsPlausibleEmail(string email)
    {
        if (email.Length is 0 or > 254 || email.Any(char.IsWhiteSpace)) return false;
        var at = email.IndexOf('@');
        if (at <= 0 || at != email.LastIndexOf('@')) return false;
        var domain = email[(at + 1)..];
        return domain.Contains('.') && !domain.StartsWith('.') && !domain.EndsWith('.');
    }

    [HttpPatch("members/{id:guid}")]
    [Authorize(Policy = AuthPolicies.Admin)]
    public async Task<ActionResult<MemberDto>> PatchMember(Guid id, [FromBody] PatchMemberRequest req, CancellationToken ct)
    {
        var u = await db.Users.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw ApiException.NotFound("成员");
        if (req.DisplayName is not null)
        {
            var name = req.DisplayName.Trim();
            if (name.Length is 0 or > 32) throw ApiException.BadRequest("名字要有 1 到 32 个字");
            u.DisplayName = name;
        }
        if (req.Role is { } role)
        {
            if (u.Id == User.UserId() && role != UserRole.Admin) throw ApiException.BadRequest("不能降级自己");
            u.Role = role;
        }
        if (req.ElderMode is { } elder) u.ElderMode = elder;
        if (req.Disabled is { } disabled)
        {
            if (u.Id == User.UserId()) throw ApiException.BadRequest("不能停用自己");
            u.DisabledAt = disabled ? DateTimeOffset.UtcNow : null;
        }
        await db.SaveChangesAsync(ct);
        // 被降为孩子、被停用：正在进行的远程控制立即结束；已经连着的实时连接掐断，重连时按新的状态重新认证
        if (req.Role is not null || req.Disabled == true)
        {
            await remote.EndDisallowedAsync(controllerId: u.Id);
            presence.Kick(u.Id);
        }
        return new MemberDto(u.Id, u.Email, u.Username, u.DisplayName, u.Role, u.ElderMode, u.AvatarBlobId, u.CreatedAt, u.DisabledAt != null);
    }
}

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
public sealed class FamilyController(AppDbContext db, PrivateChat.Api.Features.Remote.RemoteService remote) : ControllerBase
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

    /// <summary>登记家人邮箱；该邮箱通过 Cloudflare Access 登录时自动建号（记得同时加进 Access 策略）。</summary>
    [HttpPost("invites")]
    [Authorize(Policy = AuthPolicies.Admin)]
    public async Task<ActionResult<InviteDto>> CreateInvite([FromBody] CreateInviteRequest req, CancellationToken ct)
    {
        var email = req.Email.Trim().ToLowerInvariant();
        if (!email.Contains('@')) throw ApiException.BadRequest("邮箱格式不正确");
        if (await db.Users.AnyAsync(u => u.Email == email, ct)) throw ApiException.Conflict("该邮箱已是成员");

        var invite = new Invite
        {
            Email = email,
            Role = req.Role,
            CreatedBy = User.UserId(),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(Math.Clamp(req.ExpiresInDays, 1, 365)),
        };
        db.Invites.Add(invite);
        await db.SaveChangesAsync(ct);
        return new InviteDto(invite.Id, invite.Email, invite.Role, invite.ExpiresAt, invite.UsedBy);
    }

    [HttpPatch("members/{id:guid}")]
    [Authorize(Policy = AuthPolicies.Admin)]
    public async Task<ActionResult<MemberDto>> PatchMember(Guid id, [FromBody] PatchMemberRequest req, CancellationToken ct)
    {
        var u = await db.Users.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw ApiException.NotFound("成员");
        if (req.DisplayName is not null) u.DisplayName = req.DisplayName.Trim();
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
        // 被降为孩子、被停用：正在进行的远程控制立即结束
        if (req.Role is not null || req.Disabled == true) await remote.EndDisallowedAsync(controllerId: u.Id);
        return new MemberDto(u.Id, u.Email, u.Username, u.DisplayName, u.Role, u.ElderMode, u.AvatarBlobId, u.CreatedAt, u.DisabledAt != null);
    }
}

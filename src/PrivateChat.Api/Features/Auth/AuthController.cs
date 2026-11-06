using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PrivateChat.Api.Domain;
using PrivateChat.Api.Infrastructure;

namespace PrivateChat.Api.Features.Auth;

public sealed record UserDto(Guid Id, string Email, string Username, string DisplayName, UserRole Role, bool ElderMode, Guid? AvatarBlobId, string AuthScheme)
{
    public static UserDto From(User u, string scheme) => new(u.Id, u.Email, u.Username, u.DisplayName, u.Role, u.ElderMode, u.AvatarBlobId, scheme);
}

public sealed record LocalLoginRequest(string UsernameOrEmail, string Password);
public sealed record DeviceCodeResponse(string Code, string PollToken, int ExpiresInSeconds);
public sealed record DevicePollResponse(string Status, string? DeviceToken, Guid? DeviceId);
public sealed record DeviceConfirmRequest(string Code, string DeviceName, string Platform);

[ApiController]
[Route("api/auth")]
public sealed class AuthController(AppDbContext db, DeviceTokenService deviceTokens) : ControllerBase
{
    // TODO: 设备码目前放在进程内存，重启即失效；后续落到 devices/jobs 表
    private static readonly ConcurrentDictionary<string, PendingDeviceCode> PendingCodes = new();
    private sealed class PendingDeviceCode
    {
        public required string PollToken { get; init; }
        public DateTimeOffset ExpiresAt { get; init; } = DateTimeOffset.UtcNow.AddMinutes(10);
        public string? IssuedToken { get; set; }
        public Guid? DeviceId { get; set; }
    }

    [HttpGet("me")]
    [Authorize]
    public async Task<ActionResult<UserDto>> Me(CancellationToken ct)
    {
        var user = await db.Users.FirstAsync(u => u.Id == User.UserId(), ct);
        return UserDto.From(user, User.FindFirst(AppClaims.Scheme)?.Value ?? "");
    }

    /// <summary>局域网直连备用：没有 Cloudflare Access 时用应用内密码登录，签发 HttpOnly Cookie。</summary>
    [HttpPost("local/login")]
    [AllowAnonymous]
    [ProducesResponseType<UserDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<UserDto>> LocalLogin([FromBody] LocalLoginRequest req, CancellationToken ct)
    {
        var key = req.UsernameOrEmail.Trim().ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(u => (u.Email == key || u.Username == key) && u.DisabledAt == null, ct);
        if (user?.PasswordHash is null) return Unauthorized(new ProblemDetails { Title = "用户名或密码错误" });

        var hasher = new PasswordHasher<User>();
        var verify = hasher.VerifyHashedPassword(user, user.PasswordHash, req.Password);
        if (verify == PasswordVerificationResult.Failed) return Unauthorized(new ProblemDetails { Title = "用户名或密码错误" });

        await HttpContext.SignInAsync(AuthSchemes.Local, user.ToPrincipal(AuthSchemes.Local),
            new AuthenticationProperties { IsPersistent = true, ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30) });
        return UserDto.From(user, AuthSchemes.Local);
    }

    [HttpPost("local/logout")]
    [Authorize]
    public async Task<IActionResult> LocalLogout()
    {
        await HttpContext.SignOutAsync(AuthSchemes.Local);
        return NoContent();
    }

    /// <summary>Agent 首次启动：申请 8 位设备码，然后轮询等待用户在网页确认。</summary>
    [HttpPost("device/code")]
    [AllowAnonymous]
    public ActionResult<DeviceCodeResponse> DeviceCode()
    {
        string code;
        do code = RandomNumberGenerator.GetInt32(0, 100_000_000).ToString("D8");
        while (PendingCodes.ContainsKey(code));
        var pollToken = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        PendingCodes[code] = new PendingDeviceCode { PollToken = pollToken };
        return new DeviceCodeResponse(code, pollToken, 600);
    }

    [HttpGet("device/poll")]
    [AllowAnonymous]
    public ActionResult<DevicePollResponse> DevicePoll([FromQuery] string code, [FromQuery] string pollToken)
    {
        if (!PendingCodes.TryGetValue(code, out var pending) || pending.PollToken != pollToken)
            return NotFound();
        if (pending.ExpiresAt < DateTimeOffset.UtcNow)
        {
            PendingCodes.TryRemove(code, out _);
            return new DevicePollResponse("expired", null, null);
        }
        if (pending.IssuedToken is null) return new DevicePollResponse("pending", null, null);
        PendingCodes.TryRemove(code, out _);
        return new DevicePollResponse("approved", pending.IssuedToken, pending.DeviceId);
    }

    /// <summary>用户在网页"设备"页输入设备码确认，为该设备签发长期令牌。</summary>
    [HttpPost("device/confirm")]
    [Authorize(Policy = AuthPolicies.Member)]
    public async Task<IActionResult> DeviceConfirm([FromBody] DeviceConfirmRequest req, CancellationToken ct)
    {
        if (!PendingCodes.TryGetValue(req.Code, out var pending) || pending.ExpiresAt < DateTimeOffset.UtcNow || pending.IssuedToken is not null)
            throw ApiException.NotFound("设备码");

        var device = new Device
        {
            UserId = User.UserId(),
            Kind = DeviceKind.Agent,
            Name = req.DeviceName,
            Platform = req.Platform,
            RemoteCapable = true,
        };
        var token = deviceTokens.Issue(device.Id, device.UserId);
        device.TokenHash = DeviceTokenService.Hash(token);
        db.Devices.Add(device);
        await db.SaveChangesAsync(ct);

        pending.IssuedToken = token;
        pending.DeviceId = device.Id;
        return NoContent();
    }
}

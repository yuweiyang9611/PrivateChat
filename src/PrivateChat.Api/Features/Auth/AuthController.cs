using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using PrivateChat.Api.Domain;
using PrivateChat.Api.Infrastructure;

namespace PrivateChat.Api.Features.Auth;

public sealed record UserDto(Guid Id, string Email, string Username, string DisplayName, UserRole Role, bool ElderMode, Guid? AvatarBlobId, string AuthScheme)
{
    public static UserDto From(User u, string scheme) => new(u.Id, u.Email, u.Username, u.DisplayName, u.Role, u.ElderMode, u.AvatarBlobId, scheme);
}

public sealed record LocalLoginRequest(string UsernameOrEmail, string Password);
/// <summary>Agent 申请设备码时报上自己的名字（计算机名）和平台，网页确认时显示给用户核对。</summary>
public sealed record DeviceCodeRequest(string? Name, string? Platform);
public sealed record DeviceCodeResponse(string Code, string PollToken, int ExpiresInSeconds);
public sealed record DevicePollResponse(string Status, string? DeviceToken, Guid? DeviceId);
public sealed record DeviceLookupResponse(string Name, string Platform);
/// <summary>deviceName 不填用 Agent 报上来的名字；ownerId 只有管理员能填（替家人添加）。</summary>
public sealed record DeviceConfirmRequest(string Code, string? DeviceName, Guid? OwnerId);
public sealed record DeviceConfirmResponse(Guid DeviceId);

[ApiController]
[Route("api/auth")]
public sealed class AuthController(AppDbContext db, DeviceTokenService deviceTokens) : ControllerBase
{
    // 设备码只在进程内存里（10 分钟有效）：服务重启后 Agent 轮询拿到 404，自动重新申请一个
    private static readonly ConcurrentDictionary<string, PendingDeviceCode> PendingCodes = new();
    /// <summary>匿名接口：限制同时等待确认的设备码数量，防止被刷爆内存。</summary>
    private const int MaxPendingCodes = 200;
    /// <summary>同一个来源（Cloudflare 给的客户端 IP，局域网直连时是连接地址）最多同时挂几个码。</summary>
    private const int MaxCodesPerClient = 5;
    private sealed class PendingDeviceCode
    {
        public required string PollToken { get; init; }
        public required string Name { get; init; }
        public required string Platform { get; init; }
        public required string Client { get; init; }
        public DateTimeOffset ExpiresAt { get; init; } = DateTimeOffset.UtcNow.AddMinutes(10);
        /// <summary>已被某次确认占用（并发确认同一个码只有一个成功）。</summary>
        public int Claimed;
        public string? IssuedToken { get; set; }
        public Guid? DeviceId { get; set; }
    }

    private static void PurgeExpiredCodes()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (code, pending) in PendingCodes)
            if (pending.ExpiresAt < now) PendingCodes.TryRemove(code, out _);
    }

    private static string Clip(string? value, string fallback, int max)
    {
        var v = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        return v.Length > max ? v[..max] : v;
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

    /// <summary>
    /// 重新验证跳板。PWA 的 Service Worker 会从缓存直接返回页面，整页刷新到不了 Cloudflare 边缘，
    /// Access 会话过期后就没机会跳登录页。前端改为整页导航到这里：/api 不经 Service Worker，
    /// 请求先被 Access 拦下去登录，登录后回到这里，再 302 回原页面。只接受站内相对地址，防开放重定向。
    /// </summary>
    [HttpGet("reauth")]
    [AllowAnonymous]
    [ApiExplorerSettings(IgnoreApi = true)]
    public IActionResult Reauth([FromQuery] string? returnUrl)
    {
        Response.Headers.CacheControl = "no-store";
        return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl! : "/");
    }

    /// <summary>Agent 首次启动：申请 8 位设备码，然后轮询等待用户在网页确认。</summary>
    [HttpPost("device/code")]
    [AllowAnonymous]
    public ActionResult<DeviceCodeResponse> DeviceCode([FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] DeviceCodeRequest? req)
    {
        PurgeExpiredCodes();
        var client = Request.Headers["X-Real-IP"].ToString() is { Length: > 0 } ip ? ip : HttpContext.Connection.RemoteIpAddress?.ToString() ?? "";
        // 同一个来源只保留最近的几个；全局满了就挤掉最早的、还没被确认的（不让刷码的人把所有人都挡在外面）。
        // 被挤掉的 Agent 轮询拿到 404，会自动换一个新码
        foreach (var (old, _) in PendingCodes.Where(x => x.Value.Client == client && x.Value.Claimed == 0)
                     .OrderBy(x => x.Value.ExpiresAt).SkipLast(MaxCodesPerClient - 1).ToList())
            PendingCodes.TryRemove(old, out _);
        while (PendingCodes.Count >= MaxPendingCodes)
        {
            var oldest = PendingCodes.Where(x => x.Value.Claimed == 0).OrderBy(x => x.Value.ExpiresAt).Select(x => x.Key).FirstOrDefault();
            if (oldest is null)
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new ProblemDetails { Title = "待确认的设备码太多，请稍后再试" });
            PendingCodes.TryRemove(oldest, out _);
        }
        var pollToken = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        var pending = new PendingDeviceCode { PollToken = pollToken, Name = Clip(req?.Name, "电脑", 64), Platform = Clip(req?.Platform, "windows", 64), Client = client };
        string code;
        do code = RandomNumberGenerator.GetInt32(0, 100_000_000).ToString("D8");
        while (!PendingCodes.TryAdd(code, pending));
        return new DeviceCodeResponse(code, pollToken, 600);
    }

    [HttpGet("device/poll")]
    [AllowAnonymous]
    public ActionResult<DevicePollResponse> DevicePoll([FromQuery] string code, [FromQuery] string pollToken)
    {
        if (!PendingCodes.TryGetValue(code ?? "", out var pending)
            || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(pending.PollToken), Encoding.ASCII.GetBytes(pollToken ?? "")))
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

    /// <summary>网页输入设备码后先查一下是哪台电脑（Agent 报上来的名字），让用户核对。</summary>
    [HttpGet("device/lookup")]
    [Authorize(Policy = AuthPolicies.Member)]
    public ActionResult<DeviceLookupResponse> DeviceLookup([FromQuery] string code)
    {
        if (!PendingCodes.TryGetValue(code?.Trim() ?? "", out var pending) || pending.ExpiresAt < DateTimeOffset.UtcNow || pending.Claimed != 0)
            throw ApiException.NotFound("设备码");
        return new DeviceLookupResponse(pending.Name, pending.Platform);
    }

    /// <summary>
    /// 用户在网页"设备"页输入设备码确认，为该设备签发长期令牌。设备归确认的人；管理员可以替家人添加（ownerId）。
    /// 默认远程名单：除主人外的所有管理员（帮爸妈修电脑的场景），主人可以随时改。
    /// </summary>
    [HttpPost("device/confirm")]
    [Authorize(Policy = AuthPolicies.Member)]
    public async Task<ActionResult<DeviceConfirmResponse>> DeviceConfirm([FromBody] DeviceConfirmRequest req, CancellationToken ct)
    {
        if (!PendingCodes.TryGetValue(req.Code?.Trim() ?? "", out var pending) || pending.ExpiresAt < DateTimeOffset.UtcNow)
            throw ApiException.NotFound("设备码");

        var owner = User.UserId();
        if (req.OwnerId is { } ownerId && ownerId != owner)
        {
            if (User.Role() != UserRole.Admin) throw ApiException.Forbidden("只有管理员可以替家人添加电脑");
            if (!await db.Users.AnyAsync(u => u.Id == ownerId && u.DisabledAt == null, ct)) throw ApiException.NotFound("成员");
            owner = ownerId;
        }
        if (Interlocked.CompareExchange(ref pending.Claimed, 1, 0) != 0) throw ApiException.NotFound("设备码");
        try
        {
            var admins = await db.Users.Where(u => u.Role == UserRole.Admin && u.DisabledAt == null && u.Id != owner).Select(u => u.Id).ToListAsync(ct);
            var device = new Device
            {
                UserId = owner,
                Kind = DeviceKind.Agent,
                Name = Clip(req.DeviceName, pending.Name, 64),
                Platform = pending.Platform,
                RemoteCapable = true,
                AllowedControllers = JsonSerializer.Serialize(admins),
            };
            var token = deviceTokens.Issue(device.Id, device.UserId);
            device.TokenHash = DeviceTokenService.Hash(token);
            db.Devices.Add(device);
            await db.SaveChangesAsync(ct);

            pending.DeviceId = device.Id;
            pending.IssuedToken = token;
            return new DeviceConfirmResponse(device.Id);
        }
        catch
        {
            Interlocked.Exchange(ref pending.Claimed, 0);
            throw;
        }
    }
}

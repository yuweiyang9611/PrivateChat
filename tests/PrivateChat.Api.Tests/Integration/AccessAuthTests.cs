using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using PrivateChat.Api.Domain;
using PrivateChat.Api.Features.Auth;
using PrivateChat.Api.Infrastructure;

namespace PrivateChat.Api.Tests.Integration;

/// <summary>
/// 经 Cloudflare Access 进来的请求（带 Cf-Access-Jwt-Assertion）：成员 / 被邀请的邮箱直接进，
/// 别的邮箱拿到带原因的 401（前端据此显示"不是家庭成员"，而不是密码登录页），这条路上不能用密码登录。
/// </summary>
public class AccessAuthTests
{
    private const string Team = "https://family.cloudflareaccess.com";
    private const string Aud = "aud-tag-1234";

    private sealed class StaticKeys(params SecurityKey[] keys) : IAccessKeyProvider
    {
        public Task<IReadOnlyCollection<SecurityKey>> GetKeysAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyCollection<SecurityKey>>(keys);
    }

    private sealed class Ctx : IAsyncDisposable
    {
        private readonly ApiFactory _app = new();
        private readonly RsaSecurityKey _key = new(RSA.Create(2048)) { KeyId = "k1" };
        public WebApplicationFactory<Program> Host { get; }

        public Ctx()
        {
            Host = _app.WithWebHostBuilder(b =>
            {
                b.UseSetting("CloudflareAccess:TeamDomain", Team);
                b.UseSetting("CloudflareAccess:Audience", Aud);
                b.ConfigureTestServices(s => s.AddSingleton<IAccessKeyProvider>(new StaticKeys(_key)));
            });
        }

        /// <summary>Cloudflare 签发的令牌：人有 email，服务令牌只有 common_name。</summary>
        public string Token(string? email, string? commonName = null)
        {
            var claims = new List<Claim>();
            if (email is not null) claims.Add(new Claim("email", email));
            if (commonName is not null) claims.Add(new Claim("common_name", commonName));
            return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Issuer = Team,
                Audience = Aud,
                Subject = new ClaimsIdentity(claims),
                NotBefore = DateTime.UtcNow.AddMinutes(-5),
                Expires = DateTime.UtcNow.AddHours(1),
                SigningCredentials = new SigningCredentials(_key, SecurityAlgorithms.RsaSha256),
            });
        }

        /// <summary>像 nginx :80 那样带着 Access 头的客户端；header 为 null 就是局域网直连。</summary>
        public HttpClient Client(string? header)
        {
            var c = Host.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true, AllowAutoRedirect = false });
            if (header is not null) c.DefaultRequestHeaders.Add(AuthSchemes.AccessHeader, header);
            return c;
        }

        public HttpClient As(string email) => Client(Token(email));

        public async Task<T> WithDbAsync<T>(Func<AppDbContext, Task<T>> action)
        {
            using var scope = Host.Services.CreateScope();
            return await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
        }

        // 派生出来的 Host 由 ApiFactory 一并释放
        public ValueTask DisposeAsync() => _app.DisposeAsync();
    }

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage res)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.Equal("application/problem+json", res.Content.Headers.ContentType?.MediaType);
        return JsonSerializer.Deserialize<JsonElement>(await res.Content.ReadAsStringAsync());
    }

    [PgFact]
    public async Task Email_that_is_not_a_member_gets_401_with_the_reason_and_no_account()
    {
        await using var x = new Ctx();
        var stranger = x.As("Stranger@Example.com");

        var problem = await ProblemAsync(await stranger.GetAsync("/api/auth/me"));
        Assert.Equal(AccessProblemCodes.NotMember, problem.GetProperty("code").GetString());
        Assert.Equal("stranger@example.com", problem.GetProperty("email").GetString());

        // 别的接口、SignalR 协商也一样：前端任何一个 401 都能问到原因
        problem = await ProblemAsync(await stranger.GetAsync("/api/family/members"));
        Assert.Equal(AccessProblemCodes.NotMember, problem.GetProperty("code").GetString());
        problem = await ProblemAsync(await stranger.PostAsync("/hubs/chat/negotiate?negotiateVersion=1", null));
        Assert.Equal(AccessProblemCodes.NotMember, problem.GetProperty("code").GetString());

        Assert.False(await x.WithDbAsync(db => db.Users.AnyAsync(u => u.Email == "stranger@example.com")));
    }

    [PgFact]
    public async Task Admin_email_and_invited_email_are_signed_in_and_provisioned()
    {
        await using var x = new Ctx();

        var admin = await (await x.As("admin@example.com").GetAsync("/api/auth/me")).ReadAsync<UserDto>();
        Assert.Equal(UserRole.Admin, admin.Role);
        Assert.Equal(AuthSchemes.CloudflareAccess, admin.AuthScheme);

        // 被邀请之前进不来，邀请之后同一个人再试就进来了
        var mom = x.As("mom@example.com");
        Assert.Equal(HttpStatusCode.Unauthorized, (await mom.GetAsync("/api/auth/me")).StatusCode);
        var invite = await x.As("admin@example.com").PostJsonAsync("/api/family/invites", new { email = "Mom@Example.com", role = "Member" });
        Assert.Equal(HttpStatusCode.OK, invite.StatusCode);

        var me = await (await mom.GetAsync("/api/auth/me")).ReadAsync<UserDto>();
        Assert.Equal("mom@example.com", me.Email);
        Assert.Equal(UserRole.Member, me.Role);
        Assert.Equal(AuthSchemes.CloudflareAccess, me.AuthScheme);
    }

    [PgFact]
    public async Task Revoked_or_expired_invite_does_not_let_the_email_in()
    {
        await using var x = new Ctx();
        var admin = x.As("admin@example.com");
        await admin.GetAsync("/api/auth/me");
        var invite = await (await admin.PostJsonAsync("/api/family/invites", new { email = "mom@example.com" })).ReadAsync<Features.Family.InviteDto>();
        var mom = x.As("mom@example.com");

        // 过期：进不来；管理员重新邀请（同一条续期）后才行——这里先撤销，验证撤销同样拦得住
        await x.WithDbAsync(async db =>
        {
            var row = await db.Invites.SingleAsync(i => i.Id == invite.Id);
            row.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
            return 0;
        });
        Assert.Equal(AccessProblemCodes.NotMember, (await ProblemAsync(await mom.GetAsync("/api/auth/me"))).GetProperty("code").GetString());

        (await admin.PostJsonAsync("/api/family/invites", new { email = "mom@example.com" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/family/invites/{invite.Id}")).StatusCode);
        Assert.Equal(AccessProblemCodes.NotMember, (await ProblemAsync(await mom.GetAsync("/api/auth/me"))).GetProperty("code").GetString());
        Assert.False(await x.WithDbAsync(db => db.Users.AnyAsync(u => u.Email == "mom@example.com")));
    }

    [PgFact]
    public async Task Disabled_member_gets_the_same_reason()
    {
        await using var x = new Ctx();
        var admin = x.As("admin@example.com");
        await admin.GetAsync("/api/auth/me");
        (await admin.PostJsonAsync("/api/family/invites", new { email = "bob@example.com", role = "Member" })).EnsureSuccessStatusCode();
        var bob = x.As("bob@example.com");
        var bobId = (await (await bob.GetAsync("/api/auth/me")).ReadAsync<UserDto>()).Id;

        (await admin.PatchJsonAsync($"/api/family/members/{bobId}", new { disabled = true })).EnsureSuccessStatusCode();

        var problem = await ProblemAsync(await bob.GetAsync("/api/auth/me"));
        Assert.Equal(AccessProblemCodes.NotMember, problem.GetProperty("code").GetString());
        Assert.Equal("bob@example.com", problem.GetProperty("email").GetString());
    }

    [PgFact]
    public async Task Invalid_or_identity_less_access_token_gets_access_invalid()
    {
        await using var x = new Ctx();

        var problem = await ProblemAsync(await x.Client("not.a.jwt").GetAsync("/api/auth/me"));
        Assert.Equal(AccessProblemCodes.Invalid, problem.GetProperty("code").GetString());
        Assert.False(problem.TryGetProperty("email", out _));

        // 服务令牌（Agent 过门禁用的）没有邮箱：不带设备令牌就不是任何人
        problem = await ProblemAsync(await x.Client(x.Token(null, "agent.access")).GetAsync("/api/auth/me"));
        Assert.Equal(AccessProblemCodes.Invalid, problem.GetProperty("code").GetString());
    }

    [PgFact]
    public async Task Lan_request_without_access_header_gets_a_bare_401()
    {
        await using var x = new Ctx();

        var res = await x.Client(null).GetAsync("/api/auth/me");

        // 没有正文：前端按局域网 / 本地模式显示密码登录页
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.Equal("", await res.Content.ReadAsStringAsync());
    }

    [PgFact]
    public async Task Password_login_is_refused_on_the_access_path_and_still_works_on_lan()
    {
        await using var x = new Ctx();
        var login = new { usernameOrEmail = "admin@example.com", password = ApiFactory.Password };

        // 过了 Access 的陌生邮箱、只有服务令牌的程序：密码对了也不给登录
        foreach (var header in new[] { x.Token("stranger@example.com"), x.Token(null, "agent.access") })
        {
            var refused = await x.Client(header).PostJsonAsync("/api/auth/local/login", login);
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.False(refused.Headers.Contains("Set-Cookie"));
        }

        var lan = x.Client(null);
        Assert.Equal(HttpStatusCode.OK, (await lan.PostJsonAsync("/api/auth/local/login", login)).StatusCode);
        var me = await (await lan.GetAsync("/api/auth/me")).ReadAsync<UserDto>();
        Assert.Equal(AuthSchemes.Local, me.AuthScheme);
    }
}

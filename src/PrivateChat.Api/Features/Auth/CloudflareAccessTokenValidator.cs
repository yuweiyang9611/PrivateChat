using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using PrivateChat.Api.Infrastructure;

namespace PrivateChat.Api.Features.Auth;

/// <summary>提供 Access 签名公钥（JWKS）。生产实现从 {TeamDomain}/cdn-cgi/access/certs 拉取并缓存 1 小时。</summary>
public interface IAccessKeyProvider
{
    Task<IReadOnlyCollection<SecurityKey>> GetKeysAsync(CancellationToken ct = default);
}

public sealed class JwksAccessKeyProvider(HttpClient http, IOptions<CloudflareAccessOptions> options, ILogger<JwksAccessKeyProvider> logger)
    : IAccessKeyProvider
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IReadOnlyCollection<SecurityKey> _keys = [];
    private DateTimeOffset _fetchedAt = DateTimeOffset.MinValue;

    public async Task<IReadOnlyCollection<SecurityKey>> GetKeysAsync(CancellationToken ct = default)
    {
        if (DateTimeOffset.UtcNow - _fetchedAt < CacheTtl && _keys.Count > 0) return _keys;
        await _lock.WaitAsync(ct);
        try
        {
            if (DateTimeOffset.UtcNow - _fetchedAt < CacheTtl && _keys.Count > 0) return _keys;
            var url = options.Value.TeamDomain.TrimEnd('/') + "/cdn-cgi/access/certs";
            var json = await http.GetStringAsync(url, ct);
            var jwks = new JsonWebKeySet(json);
            _keys = jwks.GetSigningKeys().ToList();
            _fetchedAt = DateTimeOffset.UtcNow;
            logger.LogInformation("已刷新 Cloudflare Access JWKS，{Count} 个密钥", _keys.Count);
            return _keys;
        }
        finally
        {
            _lock.Release();
        }
    }
}

public sealed record AccessTokenResult(bool IsValid, string? Email, string? Error)
{
    public static AccessTokenResult Fail(string error) => new(false, null, error);
}

/// <summary>校验 Cf-Access-Jwt-Assertion：签名（JWKS）、iss = 团队域名、aud = 应用 AUD、未过期。</summary>
public sealed class CloudflareAccessTokenValidator(IAccessKeyProvider keys, IOptions<CloudflareAccessOptions> options)
{
    private readonly JsonWebTokenHandler _handler = new();

    public async Task<AccessTokenResult> ValidateAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return AccessTokenResult.Fail("令牌为空");
        var opt = options.Value;
        if (!opt.Enabled) return AccessTokenResult.Fail("未配置 CloudflareAccess");

        var signingKeys = await keys.GetKeysAsync(ct);
        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = opt.TeamDomain.TrimEnd('/'),
            ValidateAudience = true,
            ValidAudience = opt.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = signingKeys,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(2),
        };

        var result = await _handler.ValidateTokenAsync(token, parameters);
        if (!result.IsValid) return AccessTokenResult.Fail(result.Exception?.Message ?? "令牌无效");

        var email = result.Claims.TryGetValue("email", out var e) ? e?.ToString() : null;
        // 服务令牌（Agent）没有 email，只有 common_name；由设备令牌层再识别用户
        if (string.IsNullOrWhiteSpace(email))
        {
            var cn = result.Claims.TryGetValue("common_name", out var c) ? c?.ToString() : null;
            return string.IsNullOrWhiteSpace(cn)
                ? AccessTokenResult.Fail("令牌缺少 email")
                : new AccessTokenResult(true, null, null);
        }
        return new AccessTokenResult(true, email.ToLowerInvariant(), null);
    }
}

using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using PrivateChat.Api.Features.Auth;
using PrivateChat.Api.Infrastructure;

namespace PrivateChat.Api.Tests;

public sealed class CloudflareAccessTokenValidatorTests
{
    private const string Team = "https://family.cloudflareaccess.com";
    private const string Aud = "aud-tag-1234";

    private sealed class StaticKeys(params SecurityKey[] keys) : IAccessKeyProvider
    {
        public Task<IReadOnlyCollection<SecurityKey>> GetKeysAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyCollection<SecurityKey>>(keys);
    }

    private static RsaSecurityKey NewKey(string kid) => new(RSA.Create(2048)) { KeyId = kid };

    private static string Sign(RsaSecurityKey key, string? email, DateTime expires, string iss = Team, string aud = Aud)
    {
        var claims = new List<Claim>();
        if (email is not null) claims.Add(new Claim("email", email));
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = iss,
            Audience = aud,
            Subject = new ClaimsIdentity(claims),
            NotBefore = DateTime.UtcNow.AddMinutes(-5),
            Expires = expires,
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256),
        });
    }

    private static CloudflareAccessTokenValidator Validator(params SecurityKey[] trusted)
        => new(new StaticKeys(trusted), Options.Create(new CloudflareAccessOptions { TeamDomain = Team, Audience = Aud }));

    [Fact]
    public async Task Valid_token_yields_lowercased_email()
    {
        var key = NewKey("k1");
        var token = Sign(key, "Mom@Example.com", DateTime.UtcNow.AddHours(1));
        var result = await Validator(key).ValidateAsync(token);
        Assert.True(result.IsValid, result.Error);
        Assert.Equal("mom@example.com", result.Email);
    }

    [Fact]
    public async Task Token_signed_by_unknown_key_is_rejected()
    {
        var trusted = NewKey("k1");
        var rogue = NewKey("k2");
        var token = Sign(rogue, "mom@example.com", DateTime.UtcNow.AddHours(1));
        var result = await Validator(trusted).ValidateAsync(token);
        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task Expired_token_is_rejected()
    {
        var key = NewKey("k1");
        var token = Sign(key, "mom@example.com", DateTime.UtcNow.AddHours(-1));
        var result = await Validator(key).ValidateAsync(token);
        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task Wrong_audience_is_rejected()
    {
        var key = NewKey("k1");
        var token = Sign(key, "mom@example.com", DateTime.UtcNow.AddHours(1), aud: "other-app");
        var result = await Validator(key).ValidateAsync(token);
        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task Garbage_is_rejected()
    {
        var result = await Validator(NewKey("k1")).ValidateAsync("not.a.jwt");
        Assert.False(result.IsValid);
    }
}

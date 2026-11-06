using PrivateChat.Api.Features.Auth;
using PrivateChat.Api.Infrastructure.Keys;

namespace PrivateChat.Api.Tests;

public sealed class DeviceTokenServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "privatechat-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Issue_then_validate_roundtrips()
    {
        var svc = new DeviceTokenService(new KeyStore(_dir));
        var device = Guid.CreateVersion7();
        var user = Guid.CreateVersion7();
        var token = svc.Issue(device, user);

        Assert.True(svc.TryValidate(token, out var payload));
        Assert.Equal(device, payload.DeviceId);
        Assert.Equal(user, payload.UserId);
    }

    [Fact]
    public void Tampered_token_is_rejected()
    {
        var svc = new DeviceTokenService(new KeyStore(_dir));
        var token = svc.Issue(Guid.NewGuid(), Guid.NewGuid());
        var tampered = token[..^2] + (token[^1] == 'A' ? "BB" : "AA");
        Assert.False(svc.TryValidate(tampered, out _));
        Assert.False(svc.TryValidate("", out _));
        Assert.False(svc.TryValidate("a.b.c", out _));
    }

    [Fact]
    public void Token_from_other_key_is_rejected()
    {
        var token = new DeviceTokenService(new KeyStore(_dir)).Issue(Guid.NewGuid(), Guid.NewGuid());
        var other = new DeviceTokenService(new KeyStore(_dir + "-other"));
        Assert.False(other.TryValidate(token, out _));
    }

    [Fact]
    public void KeyStore_persists_keys_across_instances()
    {
        var a = new KeyStore(_dir);
        var b = new KeyStore(_dir);
        Assert.Equal(a.DeviceTokenKey, b.DeviceTokenKey);
        Assert.Equal(a.Vapid, b.Vapid);
        Assert.Equal(65, Base64Url.Decode(a.Vapid.PublicKey).Length);
        Assert.Equal(32, Base64Url.Decode(a.Vapid.PrivateKey).Length);
    }

    public void Dispose()
    {
        foreach (var d in new[] { _dir, _dir + "-other" })
            if (Directory.Exists(d)) Directory.Delete(d, recursive: true);
    }
}

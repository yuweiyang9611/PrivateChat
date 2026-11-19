using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Http;
using PrivateChat.Api.Features.Remote;
using PrivateChat.Api.Infrastructure.Keys;
using PrivateChat.Contracts.Remote;

namespace PrivateChat.Api.Tests;

/// <summary>被控端的局域网直连入口：门口放行什么、证书指纹、开关条件、Kestrel 平时听的地址。</summary>
public class DirectEntranceTests
{
    private static HttpRequest Request(string path, string? authorization = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        if (authorization is not null) context.Request.Headers.Authorization = authorization;
        return context.Request;
    }

    [Theory]
    [InlineData("/healthz", null, true)]
    [InlineData("/hubs/agent/negotiate", "Bearer t", true)]
    [InlineData("/hubs/agent", "bearer t", true)]
    [InlineData("/ws/remote/0198a1b2-0000-7000-8000-000000000001/agent", "Bearer t", true)]
    [InlineData("/api/remote/sessions/0198a1b2-0000-7000-8000-000000000001/consent", "Bearer t", true)]
    [InlineData("/api/remote/agent/direct", "Bearer t", true)]
    // 没带设备令牌：被控端的接口也不放（Cookie、Access 头都不认）
    [InlineData("/hubs/agent/negotiate", null, false)]
    [InlineData("/api/remote/agent/direct", "Basic dTpw", false)]
    // 带了令牌也只能用被控端的接口
    [InlineData("/api/devices", "Bearer t", false)]
    [InlineData("/api/auth/local/login", "Bearer t", false)]
    [InlineData("/hubs/chat/negotiate", "Bearer t", false)]
    [InlineData("/hubs/agentx", "Bearer t", false)]
    [InlineData("/", null, false)]
    [InlineData("/scalar", null, false)]
    public void Only_the_agents_own_endpoints_get_through(string path, string? authorization, bool allowed)
        => Assert.Equal(allowed, DirectEntrance.Allows(Request(path, authorization)));

    [Theory]
    [InlineData("https://192.168.88.3:3508", 8443, true)]
    [InlineData("https://family.amd3500u.home:3508/", 8443, true)]
    [InlineData("", 8443, false)]
    // 不加密的地址不开：直连靠的就是 TLS + 证书指纹
    [InlineData("http://192.168.88.3:3508", 8443, false)]
    [InlineData("192.168.88.3:3508", 8443, false)]
    [InlineData("https://192.168.88.3:3508", 0, false)]
    public void Opens_only_for_an_https_address(string url, int port, bool enabled)
        => Assert.Equal(enabled, new RemoteOptions { DirectUrl = url, DirectPort = port }.DirectEnabled);

    [Fact]
    public void Keeps_listening_where_kestrel_listened_before()
    {
        Assert.Equal(["http://0.0.0.0:8080"], DirectEntrance.ConfiguredUrls("http://0.0.0.0:8080", "8080", null));
        Assert.Equal(["http://localhost:8080", "https://localhost:7175"], DirectEntrance.ConfiguredUrls("http://localhost:8080; https://localhost:7175", null, null));
        // aspnet 镜像只设了 ASPNETCORE_HTTP_PORTS
        Assert.Equal(["http://*:8080"], DirectEntrance.ConfiguredUrls(null, "8080", null));
        Assert.Equal(["http://*:80", "https://*:443"], DirectEntrance.ConfiguredUrls("", "80", "443"));
        Assert.Equal(["http://localhost:5000"], DirectEntrance.ConfiguredUrls(null, null, null));
    }

    [Fact]
    public void Pin_is_the_public_key_hash_and_rejects_any_other_certificate()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pc-direct-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var cert = new KeyStore(dir).DirectCertificate;
            var pin = CertificatePin.Of(cert);
            Assert.Equal(Convert.ToBase64String(SHA256.HashData(cert.PublicKey.ExportSubjectPublicKeyInfo())), pin);
            // 服务器身份验证用的、带私钥、ECDSA P-256
            Assert.True(cert.HasPrivateKey);
            Assert.Contains(cert.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single().EnhancedKeyUsages.Cast<Oid>(), o => o.Value == "1.3.6.1.5.5.7.3.1");
            Assert.NotNull(cert.GetECDsaPublicKey());

            // TLS 回调里拿到的是 X509Certificate（不是 X509Certificate2）：一样认
            using var plain = new X509Certificate(cert.RawData);
            Assert.True(CertificatePin.Matches(plain, pin));
            // 同一个文件读回来还是这张
            Assert.Equal(pin, CertificatePin.Of(new KeyStore(dir).DirectCertificate));

            // 别的证书（哪怕名字一样）、空证书、空指纹：都不认
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var impostor = new CertificateRequest(cert.SubjectName, key, HashAlgorithmName.SHA256).CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
            Assert.False(CertificatePin.Matches(impostor, pin));
            Assert.False(CertificatePin.Matches(null, pin));
            Assert.False(CertificatePin.Matches(cert, ""));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}

using System.ComponentModel;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using PrivateChat.Agent;

namespace PrivateChat.Api.Tests;

/// <summary>
/// 被控端程序连不上服务器时说的话。异常的形状都是在真实环境里抓到的 .NET 实际抛出的样子：
/// 加密握手失败时最外层永远只有一句 "The SSL connection could not be established"，原因在里层。
/// </summary>
public class AgentConnectionDiagnosisTests
{
    private static readonly Uri Server = new("https://family.example.com");

    private static ConnectionProblem Describe(Exception ex, bool tls13 = true, CertificateIssue? cert = null, string proxy = "", DateTime? now = null)
        => ConnectionDiagnosis.Describe(ex, Server, cert, tls13, proxy, now);

    private static HttpRequestException Tls(Exception inner)
        => new(HttpRequestError.SecureConnectionError, "The SSL connection could not be established, see inner exception.", inner);

    private static CertificateIssue Cert(SslPolicyErrors errors, X509ChainStatusFlags chain = X509ChainStatusFlags.NoError, string issuer = "CN=WE1, O=Google Trust Services, C=US")
        => new("CN=family.example.com", issuer, new DateTime(2026, 10, 1), new DateTime(2026, 12, 30), errors, chain);

    [Fact]
    public void Windows_10_against_a_server_that_only_takes_tls_13_is_named_as_such()
    {
        // 把协议限制到 TLS 1.2（Windows 10 的上限）去连只收 TLS 1.3 的服务器，抓到的就是这一串
        var ex = Tls(new AuthenticationException("Authentication failed, see inner exception.", new Win32Exception(unchecked((int)0x80090302))));

        var old = Describe(ex, tls13: false);
        Assert.Equal("这台电脑的系统不支持服务器要求的 TLS 1.3", old.Summary);
        Assert.Contains("最低 TLS 版本", old.Advice);
        Assert.Contains("1.2", old.Advice);
        // 详细信息里是完整的异常链，不再只有最外层那句
        Assert.Contains("SecureConnectionError", old.Technical);
        Assert.Contains("0x80090302", old.Technical);
        Assert.Contains("没有 TLS 1.3", old.Technical);

        // 同样的错误出现在 Windows 11 上：不是系统的问题，是中间有东西
        var modern = Describe(ex, tls13: true);
        Assert.DoesNotContain("最低 TLS 版本", modern.Advice);
        Assert.Contains("HTTPS 扫描", modern.Advice);
    }

    [Theory]
    [InlineData(unchecked((int)0x80090326), null)] // SEC_E_ILLEGAL_MESSAGE
    [InlineData(unchecked((int)0x80090331), null)] // SEC_E_ALGORITHM_MISMATCH
    [InlineData(0, "Authentication failed because the remote party sent a TLS alert: 'ProtocolVersion'.")]
    [InlineData(0, "Authentication failed because the remote party sent a TLS alert: 'HandshakeFailure'.")]
    public void Other_ways_a_version_mismatch_shows_up_are_recognized_too(int native, string? message)
    {
        var inner = message is null
            ? new AuthenticationException("Authentication failed, see inner exception.", new Win32Exception(native))
            : new AuthenticationException(message);

        Assert.Equal("这台电脑的系统不支持服务器要求的 TLS 1.3", Describe(Tls(inner), tls13: false).Summary);
    }

    [Fact]
    public void A_handshake_cut_off_half_way_points_at_the_network_or_on_old_windows_at_tls_13_first()
    {
        var ex = Tls(new IOException("Unable to read data from the transport connection: An existing connection was forcibly closed by the remote host.",
            new SocketException((int)SocketError.ConnectionReset)));

        var modern = Describe(ex, tls13: true);
        Assert.Equal("加密握手被中断", modern.Summary);
        Assert.Contains("换个网络", modern.Advice);

        // 看不出是谁断的：旧系统上先说最常见的原因，也给出另一种可能
        var old = Describe(ex, tls13: false);
        Assert.Contains("TLS 1.3", old.Summary);
        Assert.Contains("最低 TLS 版本", old.Advice);
        Assert.Contains("换个网络", old.Advice);
    }

    [Fact]
    public void Certificate_problems_say_what_is_wrong_and_who_issued_it()
    {
        var rejected = Tls(new AuthenticationException("The remote certificate was rejected by the provided RemoteCertificateValidationCallback."));

        // 酒店 / 访客 Wi-Fi 的登录页
        var portal = Describe(rejected, cert: Cert(SslPolicyErrors.RemoteCertificateNameMismatch, issuer: "CN=YR1, O=Let's Encrypt, C=US"));
        Assert.Equal("连到的不是我们的服务器（证书是别的网站的）", portal.Summary);
        Assert.Contains("先在浏览器里登录", portal.Advice);
        Assert.Contains("YR1 / Let's Encrypt", portal.Advice);

        // 公司网关、杀毒软件换了证书
        var intercepted = Describe(rejected, cert: Cert(SslPolicyErrors.RemoteCertificateChainErrors, X509ChainStatusFlags.UntrustedRoot, "CN=Corp Inspection CA, O=Corp, L=Somewhere, C=CN"));
        Assert.Equal("服务器证书不被这台电脑信任", intercepted.Summary);
        Assert.Contains("Corp Inspection CA / Corp", intercepted.Advice);
        Assert.DoesNotContain("L=Somewhere", intercepted.Advice);
        Assert.Contains("UntrustedRoot", intercepted.Technical);

        // 电脑时间比证书生效日期还早：一定是电脑的钟不对
        var clockBehind = Describe(rejected, cert: Cert(SslPolicyErrors.RemoteCertificateChainErrors, X509ChainStatusFlags.NotTimeValid), now: new DateTime(2019, 1, 1, 8, 0, 0));
        Assert.Equal("这台电脑的日期和时间不对", clockBehind.Summary);
        Assert.Contains("2019-01-01 08:00", clockBehind.Advice);

        // 过了有效期：可能是证书真过期了，也可能是钟走快了，两种都说
        var afterExpiry = Describe(rejected, cert: Cert(SslPolicyErrors.RemoteCertificateChainErrors, X509ChainStatusFlags.NotTimeValid), now: new DateTime(2027, 3, 1));
        Assert.Contains("过期", afterExpiry.Summary);
        Assert.Contains("2026-12-30", afterExpiry.Advice);

        // 没记到证书（不该发生）：也不能把它说成"握手被中断"
        Assert.Equal("服务器证书没有通过校验", Describe(rejected).Summary);
    }

    [Fact]
    public void Without_the_observer_the_reason_is_read_from_the_dotnet_message()
    {
        var mismatch = Tls(new AuthenticationException("The remote certificate is invalid according to the validation procedure: RemoteCertificateNameMismatch"));
        Assert.Equal("连到的不是我们的服务器（证书是别的网站的）", Describe(mismatch).Summary);

        var untrusted = Tls(new AuthenticationException("The remote certificate is invalid because of errors in the certificate chain: UntrustedRoot"));
        Assert.Equal("服务器证书不被这台电脑信任", Describe(untrusted).Summary);
    }

    [Fact]
    public void Plain_network_failures_are_told_apart()
    {
        var dns = new HttpRequestException(HttpRequestError.NameResolutionError, "No such host is known. (family.example.com:443)", new SocketException((int)SocketError.HostNotFound));
        Assert.Equal("找不到服务器（域名解析失败）", Describe(dns).Summary);
        Assert.Contains("family.example.com", Describe(dns).Advice);

        var refused = new HttpRequestException(HttpRequestError.ConnectionError, "No connection could be made because the target machine actively refused it.", new SocketException((int)SocketError.ConnectionRefused));
        Assert.Equal("服务器拒绝了连接", Describe(refused).Summary);

        var unreachable = new HttpRequestException(HttpRequestError.ConnectionError, "A socket operation was attempted to an unreachable network.", new SocketException((int)SocketError.NetworkUnreachable));
        Assert.Equal("网络不通", Describe(unreachable).Summary);

        var timeout = new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 20 seconds elapsing.", new TimeoutException("A task was canceled."));
        Assert.Equal("服务器迟迟没有回应", Describe(timeout).Summary);

        var notTls = Tls(new AuthenticationException("Cannot determine the frame size or a corrupted frame was received."));
        Assert.Equal("对方回的不是加密连接", Describe(notTls).Summary);

        var proxyRefused = new HttpRequestException(HttpRequestError.ProxyTunnelError, "The proxy tunnel request to proxy 'http://127.0.0.1:7890/' failed with status code '403'.");
        var viaProxy = Describe(proxyRefused, proxy: "http://127.0.0.1:7890");
        Assert.Equal("系统代理没有放行", viaProxy.Summary);
        Assert.Contains("http://127.0.0.1:7890", viaProxy.Advice);
    }

    [Fact]
    public void A_system_proxy_in_use_is_always_mentioned()
    {
        var ex = Tls(new IOException("Received an unexpected EOF or 0 bytes from the transport stream."));

        var direct = Describe(ex);
        Assert.DoesNotContain("代理（", direct.Advice);
        Assert.Contains("直连", direct.Technical);

        var proxied = Describe(ex, proxy: "http://127.0.0.1:7890");
        Assert.Contains("通过代理（http://127.0.0.1:7890）", proxied.Advice);
        Assert.Contains("系统代理 http://127.0.0.1:7890", proxied.Technical);
    }

    [Fact]
    public void Answers_from_the_server_or_from_a_login_page_in_front_of_it()
    {
        // Cloudflare Access 不认配置串里的服务令牌
        var forbidden = Describe(new ServerException(HttpStatusCode.Forbidden, "Forbidden"));
        Assert.Equal("服务器不认这台电脑的通行令牌", forbidden.Summary);
        Assert.Contains("重新复制配置串", forbidden.Advice);

        // 隧道那头的服务器没开
        Assert.Equal("服务器暂时不可用（502）", Describe(new ServerException(HttpStatusCode.BadGateway, "error code: 502")).Summary);
        Assert.Equal("服务器拒绝了请求（400）", Describe(new ServerException(HttpStatusCode.BadRequest, "{}")).Summary);
        // 粘错了地址：连上的是别的网站
        Assert.Equal("这个地址上不是 PrivateChat 服务器（404）", Describe(new ServerException(HttpStatusCode.NotFound, "<html>")).Summary);

        // 没带服务令牌：被转到登录页，回来的是网页不是 JSON
        Assert.Equal("服务器回的不是预期的内容", Describe(new JsonException("'<' is an invalid start of a value.")).Summary);
        Assert.Equal("服务器回的不是预期的内容", Describe(new NotSupportedException("The provided ContentType is not supported; the supported types are 'application/json' and the structured syntax suffix 'application/+json'.")).Summary);
    }

    [Fact]
    public void With_the_bundled_tls_13_an_old_windows_is_no_longer_the_explanation()
    {
        // 程序自带的 TLS 1.3 握手失败时抛出来的样子（BouncyCastle 的警报名字在消息里）
        static Exception Bundled(string alert) => new HttpRequestException(HttpRequestError.SecureConnectionError, $"TLS 1.3 握手失败：{alert} (family.example.com:80)",
            new AuthenticationException($"TLS 1.3 握手失败：{alert}", new IOException(alert)));

        // 服务器（或中间的设备）不收 TLS 1.3：说的是对方，不再让管理员去调低版本
        var refused = ConnectionDiagnosis.Describe(Bundled("protocol_version(70)"), Server, systemHasTls13: false, proxy: "", bundledTls: true);
        Assert.Equal("服务器不接受 TLS 1.3", refused.Summary);
        Assert.DoesNotContain("最低 TLS 版本", refused.Advice);
        Assert.Contains("没有 TLS 1.3，用的是程序自带的 TLS 1.3", refused.Technical);

        // 握手半路断了：按网络问题说，不提"系统不支持 TLS 1.3"
        var cut = ConnectionDiagnosis.Describe(Bundled("Unable to read data from the transport connection"), Server, systemHasTls13: false, proxy: "", bundledTls: true);
        Assert.Equal("加密握手被中断", cut.Summary);
        Assert.DoesNotContain("最低 TLS 版本", cut.Advice);

        // 证书不过关：自带的那一路自己校验，把证书报给 TlsObserver，说法和系统 TLS 一样
        var untrusted = ConnectionDiagnosis.Describe(Bundled("certificate_unknown(46)"), Server,
            Cert(SslPolicyErrors.RemoteCertificateChainErrors, X509ChainStatusFlags.UntrustedRoot, "CN=Corp Inspection CA, O=Corp"), systemHasTls13: false, proxy: "", bundledTls: true);
        Assert.Equal("服务器证书不被这台电脑信任", untrusted.Summary);
        Assert.Contains("Corp Inspection CA / Corp", untrusted.Advice);
    }

    [Fact]
    public void A_redirect_means_a_login_page_is_in_the_way()
    {
        // 自带 TLS 的那一路不跟随重定向（它只能连一台服务器）：没带服务令牌时 Cloudflare Access 回 302
        var redirected = Describe(new ServerException(HttpStatusCode.Found, "<html>"));
        Assert.Equal("服务器把请求转去了登录页", redirected.Summary);
        Assert.Contains("重新复制配置串", redirected.Advice);
    }

    [Fact]
    public void The_observer_takes_reports_from_the_bundled_tls_too()
    {
        TlsObserver.Report(Cert(SslPolicyErrors.RemoteCertificateNameMismatch));
        Assert.Equal(SslPolicyErrors.RemoteCertificateNameMismatch, TlsObserver.Take()?.Errors);
        Assert.Null(TlsObserver.Take());
    }

    [Fact]
    public void The_observer_rejects_exactly_what_default_validation_rejects_and_remembers_why()
    {
        Assert.True(TlsObserver.Validate(null, null, null, SslPolicyErrors.None));
        Assert.Null(TlsObserver.Take());

        Assert.False(TlsObserver.Validate(null, null, null, SslPolicyErrors.RemoteCertificateNameMismatch));
        var issue = TlsObserver.Take();
        Assert.Equal(SslPolicyErrors.RemoteCertificateNameMismatch, issue?.Errors);
        Assert.Null(TlsObserver.Take()); // 取走就没了：下一次失败不会拿到上一次的证书
    }
}

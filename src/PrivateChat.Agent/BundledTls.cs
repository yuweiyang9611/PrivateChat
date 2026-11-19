using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;
using PrivateChat.Contracts.Remote;

namespace PrivateChat.Agent;

public enum TlsMode
{
    /// <summary>Windows 自带的 TLS（Schannel）：.NET 的默认做法。</summary>
    System,
    /// <summary>程序自带的 TLS 1.3（BouncyCastle）：系统没有 TLS 1.3 时用。</summary>
    Bundled,
}

/// <summary>
/// 被控端到服务器的三条连接（REST、SignalR、远程画面的 WebSocket）怎么建立加密通道。
///
/// .NET 在 Windows 上用的是系统自带的 TLS，而 Windows 10 及更早的系统没有 TLS 1.3：服务器只收 TLS 1.3 时，
/// 这些电脑在申请设备码这一步就连不上。所以系统没有 TLS 1.3 时，改用程序自带的 TLS 1.3
/// （BouncyCastle，纯托管代码），不降到 TLS 1.2。Windows 11 照旧用系统的。
///
/// 自带的这一路只做三件事以外的都不做：只说 TLS 1.3（服务器不支持就连不上，不回退）、不带客户端证书、
/// 证书链和域名仍然交给 Windows / .NET 校验（同一套受信任根证书、同一套规则，吊销检查和 HttpClient 默认一样不做）。
///
/// 接法：.NET 对 https:// 的连接一定会自己套一层系统 TLS，所以这一路把请求地址改成 http:// / ws://，
/// 实际连接在 ConnectCallback 里建立——TCP 连到服务器的 TLS 端口（或经系统代理 CONNECT），握手之后把加密流交给 .NET。
/// 每个处理器只给一台服务器用，别的域名连不出去。
/// </summary>
public static class AgentTransport
{
    /// <summary>强制用哪一种（排查问题、自动化测试用）：bundled / system；不设就按系统有没有 TLS 1.3 决定。</summary>
    public const string ModeVariable = "PRIVATECHAT_AGENT_TLS";
    /// <summary>自动化测试用（只在 PRIVATECHAT_AGENT_TEST=1 时生效）：只信任这个 PEM 文件里的根证书，好对着本机的测试服务器跑。</summary>
    public const string TestRootVariable = "PRIVATECHAT_AGENT_TEST_CA";

    private static readonly Lazy<X509Certificate2?> TestRootCertificate = new(() =>
    {
        if (Environment.GetEnvironmentVariable("PRIVATECHAT_AGENT_TEST") != "1") return null;
        var path = Environment.GetEnvironmentVariable(TestRootVariable);
        if (string.IsNullOrWhiteSpace(path)) return null;
        AgentLog.Warn($"测试模式：自带的 TLS 只信任 {path} 里的根证书（{TestRootVariable}），不能用于正常使用");
        return X509CertificateLoader.LoadCertificateFromFile(path);
    });

    internal static X509Certificate2? TestRoot => TestRootCertificate.Value;

    /// <summary>打包进来的 BouncyCastle 的版本（程序集版本号固定是 2.0.0，要看产品版本）。</summary>
    public static string LibraryVersion
        => typeof(TlsClientProtocol).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0] ?? "?";

    public static TlsMode ModeFor(Uri server)
    {
        // 明文 HTTP（局域网 8080 那种地址、本机测试）没有 TLS，谈不上哪一种
        if (server.Scheme is not ("https" or "wss")) return TlsMode.System;
        return Environment.GetEnvironmentVariable(ModeVariable)?.Trim().ToLowerInvariant() switch
        {
            "bundled" => TlsMode.Bundled,
            "system" => TlsMode.System,
            _ => ConnectionDiagnosis.SystemHasTls13 ? TlsMode.System : TlsMode.Bundled,
        };
    }

    public static string Describe(Uri server) => server.Scheme is not ("https" or "wss")
        ? "不加密（http 地址）"
        : ModeFor(server) == TlsMode.Bundled
            ? $"程序自带的 TLS 1.3（BouncyCastle {LibraryVersion}，这台电脑的系统{(ConnectionDiagnosis.SystemHasTls13 ? "有" : "没有")} TLS 1.3）"
            : "系统自带的 TLS";

    /// <summary>请求实际要用的地址：自带 TLS 时 https → http、wss → ws，端口由处理器决定；否则原样。</summary>
    public static Uri Address(Uri uri)
    {
        if (ModeFor(uri) != TlsMode.Bundled) return uri;
        return new UriBuilder(uri) { Scheme = uri.Scheme == "wss" ? "ws" : "http", Port = -1 }.Uri;
    }

    /// <summary>
    /// 局域网直连连不上时多久放弃（好改走公网）：家里的服务器正常几毫秒就应答；
    /// 不在家时那个地址多半根本不存在，不设的话 Windows 要等二十来秒才报连不上。
    /// </summary>
    public static readonly TimeSpan DirectConnectTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// 给这台服务器发 HTTP 请求用的处理器。两种方式都跟随系统代理；连不上时证书的问题都会记到 <see cref="TlsObserver"/>。
    /// pin 不为空 = 局域网直连：服务器证书只按指纹认（<see cref="CertificatePin"/>，服务器自己生成的自签证书），
    /// 不经代理（就是要直连），连不上 <see cref="DirectConnectTimeout"/> 就放弃。
    /// </summary>
    public static HttpMessageHandler CreateHandler(Uri server, string? pin = null)
    {
        if (ModeFor(server) != TlsMode.Bundled)
        {
            var handler = TlsObserver.CreateHandler();
            if (pin is not null)
            {
                handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, _) => CertificatePin.Matches(certificate, pin);
                handler.UseProxy = false;
                handler.ConnectTimeout = DirectConnectTimeout;
            }
            return handler;
        }
        var origin = new Uri(server.GetLeftPart(UriPartial.Authority));
        return new OriginHandler(origin, new SocketsHttpHandler
        {
            // 代理在 ConnectCallback 里自己走 CONNECT：交给 .NET 的话，http:// 的请求会被明文发给代理
            UseProxy = false,
            // 服务器自己从不重定向；出现重定向只可能是被登录页拦住了（这个处理器也连不了别的域名），原样交回去由上层说明
            AllowAutoRedirect = false,
            ConnectTimeout = pin is null ? Timeout.InfiniteTimeSpan : DirectConnectTimeout,
            ConnectCallback = (context, ct) => ConnectAsync(origin, context.DnsEndPoint.Host, pin, ct),
        });
    }

    /// <summary>WebSocket 用的调用器；系统 TLS 又不是局域网直连时返回 null（ClientWebSocket 自己连就行）。</summary>
    public static HttpMessageInvoker? CreateInvoker(Uri server, string? pin = null)
        => ModeFor(server) == TlsMode.Bundled || pin is not null ? new HttpMessageInvoker(CreateHandler(server, pin)) : null;

    /// <summary>连 WebSocket：有调用器（自带 TLS）就经它连改写后的地址。</summary>
    public static Task ConnectVia(this ClientWebSocket socket, Uri uri, HttpMessageInvoker? invoker, CancellationToken ct)
        => invoker is null ? socket.ConnectAsync(uri, ct) : socket.ConnectAsync(Address(uri), invoker, ct);

    /// <summary>地址改成了 http://host/，Host 头要还原成服务器原来的（带非默认端口时尤其要紧）。</summary>
    private sealed class OriginHandler(Uri origin, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Host = origin.IsDefaultPort ? origin.IdnHost : $"{origin.IdnHost}:{origin.Port}";
            return base.SendAsync(request, cancellationToken);
        }
    }

    private static async ValueTask<Stream> ConnectAsync(Uri origin, string requestedHost, string? pin, CancellationToken ct)
    {
        if (!string.Equals(requestedHost, origin.IdnHost, StringComparison.OrdinalIgnoreCase) && !string.Equals(requestedHost, origin.Host, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"这个连接只给 {origin.Host} 用，不能连 {requestedHost}");

        var transport = await OpenTransportAsync(origin, useProxy: pin is null, ct).ConfigureAwait(false);
        try
        {
            var client = new Tls13Client(new BcTlsCrypto(new SecureRandom()), origin, pin);
            var protocol = new TlsClientProtocol(transport);
            // 握手是阻塞的：取消（超时）时把连接掐掉，它就会抛出来
            await using (ct.Register(transport.Dispose))
            {
                try
                {
                    await Task.Run(() => protocol.Connect(client), CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    ct.ThrowIfCancellationRequested();
                    // 和系统 TLS 失败时一个形状（AuthenticationException），连不上的说明才认得出来；警报的名字在消息里
                    throw new AuthenticationException($"TLS 1.3 握手失败：{ex.Message}", ex);
                }
            }
            return new TlsDuplexStream(protocol, transport);
        }
        catch
        {
            transport.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 到服务器 TLS 端口的 TCP 通道：直连，或者经系统代理 CONNECT（和 .NET 默认一样跟随 Windows 的代理设置）。
    /// 局域网直连（useProxy = false）一律不经代理。
    /// </summary>
    private static async Task<Stream> OpenTransportAsync(Uri origin, bool useProxy, CancellationToken ct)
    {
        var proxy = useProxy ? SystemProxyFor(origin) : null;
        if (proxy is not null && proxy.Scheme != "http")
            throw new HttpRequestException(HttpRequestError.ProxyTunnelError, $"系统代理 {proxy} 不是 HTTP 代理，自带的 TLS 用不了它");
        var socket = proxy is null
            ? await ConnectSocketAsync(origin.IdnHost, origin.Port, ct).ConfigureAwait(false)
            : await ConnectSocketAsync(proxy.IdnHost, proxy.Port, ct).ConfigureAwait(false);
        try
        {
            if (proxy is null) return new NetworkStream(socket, ownsSocket: true);
            var stream = new NetworkStream(socket, ownsSocket: true);
            var target = $"{origin.IdnHost}:{origin.Port}";
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT {target} HTTP/1.1\r\nHost: {target}\r\nProxy-Connection: keep-alive\r\n\r\n"), ct).ConfigureAwait(false);
            // 一个字节一个字节读到空行为止：不能多读，后面紧跟着的就是 TLS 的数据
            var head = new StringBuilder();
            var one = new byte[1];
            while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                if (head.Length > 8192 || await stream.ReadAsync(one, ct).ConfigureAwait(false) == 0)
                    throw new HttpRequestException(HttpRequestError.ProxyTunnelError, $"系统代理 {proxy} 没有正常应答");
                head.Append((char)one[0]);
            }
            var status = head.ToString().Split("\r\n")[0];
            var parts = status.Split(' ');
            if (parts.Length < 2 || parts[1] != "200")
                throw new HttpRequestException(HttpRequestError.ProxyTunnelError, $"系统代理 {proxy} 拒绝了连接：{status}");
            return stream;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 域名有多个地址（IPv6 + IPv4）时不挨个等：先连第一个，过 250 毫秒还没连上就同时试下一个，谁先通用谁。
    /// 有的网络发了 IPv6 地址却走不通，挨个试的话每条新连接都要先白等二十来秒；浏览器也是这样并行试的。
    /// </summary>
    internal static async Task<Socket> ConnectSocketAsync(string host, int port, CancellationToken ct)
    {
        var addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        if (addresses.Length == 0) throw new SocketException((int)SocketError.HostNotFound);

        using var attemptsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var attempts = new List<Task<Socket>>();
        Exception? last = null;
        var next = 0;
        try
        {
            while (true)
            {
                if (next < addresses.Length) attempts.Add(AttemptAsync(addresses[next++], port, attemptsCts.Token));
                if (attempts.Count == 0) throw last ?? new SocketException((int)SocketError.HostUnreachable);
                // 还有没试过的地址：最多等 250 毫秒就加一路；都试上了：等其中一路出结果
                var stagger = next < addresses.Length ? Task.Delay(250, attemptsCts.Token) : Task.Delay(Timeout.Infinite, attemptsCts.Token);
                var done = await Task.WhenAny(attempts.Cast<Task>().Append(stagger)).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (done == stagger) continue;
                var attempt = (Task<Socket>)done;
                attempts.Remove(attempt);
                if (attempt.IsCompletedSuccessfully) return attempt.Result;
                last = attempt.Exception?.InnerException ?? last;
            }
        }
        finally
        {
            // 其余的不要了：还没连上的取消掉，碰巧也连上了的关掉
            foreach (var other in attempts)
                _ = other.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result.Dispose(); }, TaskScheduler.Default);
            attemptsCts.Cancel();
        }
    }

    private static async Task<Socket> AttemptAsync(IPAddress address, int port, CancellationToken ct)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(address, port, ct).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static Uri? SystemProxyFor(Uri origin)
    {
        try
        {
            var proxy = HttpClient.DefaultProxy;
            if (proxy.IsBypassed(origin)) return null;
            var via = proxy.GetProxy(origin);
            return via is null || via == origin ? null : via;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>只说 TLS 1.3 的客户端：不带客户端证书，证书校验交给 <see cref="WindowsTrust"/>（局域网直连时交给 <see cref="PinnedTrust"/>）。</summary>
internal sealed class Tls13Client(TlsCrypto crypto, Uri origin, string? pin = null) : DefaultTlsClient(crypto)
{
    protected override ProtocolVersion[] GetSupportedVersions() => ProtocolVersion.TLSv13.Only();

    // 地址是 IP 的不发 SNI（规范不允许）
    protected override IList<ServerName>? GetSniServerNames()
        => origin.HostNameType == UriHostNameType.Dns ? [new ServerName(NameType.host_name, Encoding.ASCII.GetBytes(origin.IdnHost))] : null;

    // 改写成 http:// 之后 .NET 只会说 HTTP/1.1
    protected override IList<ProtocolName> GetProtocolNames() => [ProtocolName.Http_1_1];

    public override TlsAuthentication GetAuthentication() => pin is null ? new WindowsTrust(origin.IdnHost) : new PinnedTrust(pin);
}

/// <summary>
/// 局域网直连：服务器那张自签证书按指纹认（指纹是之前经公网从服务器拿到的），不看签发者、有效期和域名。
/// 指纹对不上（局域网里别的机器冒充、服务器换了证书）就中止握手，上层改走公网。
/// </summary>
internal sealed class PinnedTrust(string pin) : TlsAuthentication
{
    public void NotifyServerCertificate(TlsServerCertificate serverCertificate)
    {
        var list = serverCertificate.Certificate.GetCertificateList();
        if (list.Length == 0) throw new TlsFatalAlert(AlertDescription.bad_certificate);
        using var leaf = X509CertificateLoader.LoadCertificate(list[0].GetEncoded());
        if (!CertificatePin.Matches(leaf, pin)) throw new TlsFatalAlert(AlertDescription.bad_certificate);
    }

    public TlsCredentials? GetClientCredentials(Org.BouncyCastle.Tls.CertificateRequest certificateRequest) => null;
}

/// <summary>
/// 服务器证书交给 Windows / .NET 校验：证书链用系统的受信任根证书，必须是"服务器身份验证"用途，域名必须对得上。
/// 和系统 TLS 的规则一样；不通过就中止握手，并把是哪张证书、哪里不对记下来（连不上的说明里要用）。
/// </summary>
internal sealed class WindowsTrust(string host) : TlsAuthentication
{
    public void NotifyServerCertificate(TlsServerCertificate serverCertificate)
    {
        var list = serverCertificate.Certificate.GetCertificateList();
        if (list.Length == 0) throw new TlsFatalAlert(AlertDescription.bad_certificate);
        var certs = list.Select(c => X509CertificateLoader.LoadCertificate(c.GetEncoded())).ToArray();
        try
        {
            var leaf = certs[0];
            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
            foreach (var extra in certs.Skip(1)) chain.ChainPolicy.ExtraStore.Add(extra);
            if (AgentTransport.TestRoot is { } testRoot)
            {
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.Add(testRoot);
            }

            var errors = SslPolicyErrors.None;
            var flags = X509ChainStatusFlags.NoError;
            if (!chain.Build(leaf))
            {
                errors |= SslPolicyErrors.RemoteCertificateChainErrors;
                foreach (var status in chain.ChainStatus) flags |= status.Status;
            }
            if (!leaf.MatchesHostname(host)) errors |= SslPolicyErrors.RemoteCertificateNameMismatch;
            if (errors == SslPolicyErrors.None) return;

            TlsObserver.Report(new CertificateIssue(leaf.Subject, leaf.Issuer, leaf.NotBefore, leaf.NotAfter, errors, flags));
            throw new TlsFatalAlert(errors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors) ? AlertDescription.certificate_unknown : AlertDescription.bad_certificate);
        }
        finally
        {
            foreach (var c in certs) c.Dispose();
        }
    }

    public TlsCredentials? GetClientCredentials(Org.BouncyCastle.Tls.CertificateRequest certificateRequest) => null;
}

/// <summary>
/// BouncyCastle 的 TLS 流是阻塞式的，而 HttpClient / WebSocket 要一边等着读、一边写。
/// Stream 默认的异步实现会把读和写排成一队（等着读的时候写不出去），所以这里把读、写各自放到线程池里，互不相干；
/// BouncyCastle 内部对写有锁，支持一个线程读、一个线程写。
/// </summary>
internal sealed class TlsDuplexStream(TlsClientProtocol protocol, Stream transport) : Stream
{
    private readonly Stream _tls = protocol.Stream;
    private readonly SemaphoreSlim _writes = new(1, 1);
    private int _closed;

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override int Read(byte[] buffer, int offset, int count) => _tls.Read(buffer, offset, count);
    public override void Write(byte[] buffer, int offset, int count) => _tls.Write(buffer, offset, count);

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        // 阻塞的读取消不了：取消时把连接掐掉，读就会抛出来
        await using var registration = cancellationToken.Register(Abort);
        try
        {
            return await Task.Run(() => _tls.Read(buffer.Span), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await _writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var registration = cancellationToken.Register(Abort);
            try
            {
                await Task.Run(() => _tls.Write(buffer.Span), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
        }
        finally
        {
            _writes.Release();
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private void Abort()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0) transport.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _closed, 1) == 0)
        {
            try { protocol.Close(); } // 告诉对方我们要关了（close_notify）
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
            transport.Dispose();
        }
        base.Dispose(disposing);
    }
}

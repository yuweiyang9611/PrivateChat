using System.ComponentModel;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace PrivateChat.Agent;

/// <summary>服务器回了一个不成功的状态码。</summary>
public sealed class ServerException(HttpStatusCode status, string body) : Exception($"服务器返回 {(int)status}：{body}")
{
    public HttpStatusCode Status { get; } = status;
}

/// <summary>
/// 连不上服务器时给人看的说明。Summary：一句原因（托盘、窗口里的大字）；Advice：该怎么办；
/// Technical：给管理员看的技术细节（完整的异常链、系统版本、代理、证书），写进日志，窗口里可以复制。
/// </summary>
public sealed record ConnectionProblem(string Summary, string Advice, string Technical);

/// <summary>握手时看到的、没通过校验的服务器证书。</summary>
public sealed record CertificateIssue(string Subject, string Issuer, DateTime NotBefore, DateTime NotAfter, SslPolicyErrors Errors, X509ChainStatusFlags Chain);

/// <summary>
/// 记下没通过校验的服务器证书（校验结果不变：有任何问题照样拒绝）。
/// 挂了回调之后 .NET 的报错只剩"被回调拒绝"，具体是域名不对、过期还是不受信任，要靠这里记下来的。
/// </summary>
public static class TlsObserver
{
    private static readonly Lock Gate = new();
    private static CertificateIssue? _last;
    private static DateTime _at;

    public static bool Validate(object? sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None) return true;
        try
        {
            var flags = X509ChainStatusFlags.NoError;
            foreach (var status in chain?.ChainStatus ?? []) flags |= status.Status;
            var cert = certificate as X509Certificate2 ?? (certificate is null ? null : new X509Certificate2(certificate));
            lock (Gate)
            {
                _last = new CertificateIssue(cert?.Subject ?? "", cert?.Issuer ?? "", cert?.NotBefore ?? default, cert?.NotAfter ?? default, errors, flags);
                _at = DateTime.UtcNow;
            }
        }
        catch (Exception)
        {
            // 记不下来不影响结论：证书有问题，拒绝
        }
        return false;
    }

    /// <summary>自带的 TLS 那一路自己做证书校验，不通过时也记到这里。</summary>
    public static void Report(CertificateIssue issue)
    {
        lock (Gate)
        {
            _last = issue;
            _at = DateTime.UtcNow;
        }
    }

    /// <summary>取走刚才那次失败看到的证书（半分钟以前的不算）。</summary>
    public static CertificateIssue? Take()
    {
        lock (Gate)
        {
            var issue = DateTime.UtcNow - _at < TimeSpan.FromSeconds(30) ? _last : null;
            _last = null;
            return issue;
        }
    }

    /// <summary>和 new HttpClient() 默认的行为一样（跟随系统代理、自动跳转），只多了记证书。</summary>
    public static SocketsHttpHandler CreateHandler()
    {
        var handler = new SocketsHttpHandler();
        handler.SslOptions.RemoteCertificateValidationCallback = Validate;
        return handler;
    }
}

/// <summary>
/// 把"连不上服务器"的异常翻译成人话。.NET 对加密握手失败只在最外层说一句
/// "The SSL connection could not be established, see inner exception"，真正的原因在里层——
/// 以前日志只记了最外层，家里人看到的就只有"SSL 有问题"。
/// </summary>
public static class ConnectionDiagnosis
{
    /// <summary>Windows 自带的 TLS（Schannel）从 Windows 11 / Server 2022 起才有 TLS 1.3；.NET 在 Windows 上用的就是它。</summary>
    public static bool SystemHasTls13 => !OperatingSystem.IsWindows() || Environment.OSVersion.Version.Build >= 20348;

    // Schannel 在协议版本 / 算法谈不拢时给的错误码
    private const int SecEUnsupportedFunction = unchecked((int)0x80090302);
    private const int SecEIllegalMessage = unchecked((int)0x80090326);
    private const int SecEAlgorithmMismatch = unchecked((int)0x80090331);

    /// <param name="server">服务器地址（说明里要提到域名，查它走不走系统代理）。</param>
    /// <param name="certificate">这次失败看到的证书问题（<see cref="TlsObserver.Take"/>）。</param>
    /// <param name="systemHasTls13">测试用：这台电脑的系统有没有 TLS 1.3。</param>
    /// <param name="proxy">测试用：这次请求走的系统代理（空 = 直连）。</param>
    /// <param name="now">测试用：现在的时间。</param>
    /// <param name="bundledTls">这次用的是程序自带的 TLS 1.3（系统没有 TLS 1.3 时），不是系统的。</param>
    public static ConnectionProblem Describe(Exception ex, Uri server, CertificateIssue? certificate = null, bool? systemHasTls13 = null, string? proxy = null, DateTime? now = null, bool bundledTls = false)
    {
        var systemTls13 = systemHasTls13 ?? SystemHasTls13;
        // 用自带的 TLS 时，"系统没有 TLS 1.3"不再是连不上的原因
        var tls13 = systemTls13 || bundledTls;
        proxy ??= SystemProxyFor(server);
        var clock = now ?? DateTime.Now;
        var chain = new List<Exception>();
        for (var e = ex; e is not null && chain.Count < 8; e = e.InnerException) chain.Add(e);

        var technical = string.Join(" → ", chain.Select(Format))
            + $"｜系统 {Environment.OSVersion.Version}（{(systemTls13 ? "有" : "没有")} TLS 1.3{(bundledTls ? "，用的是程序自带的 TLS 1.3" : "")}）"
            + (proxy.Length > 0 ? $"｜系统代理 {proxy}" : "｜直连")
            + (certificate is null ? "" : $"｜证书 {certificate.Subject}，颁发者 {certificate.Issuer}，有效期 {certificate.NotBefore:yyyy-MM-dd}～{certificate.NotAfter:yyyy-MM-dd}，{certificate.Errors} {certificate.Chain}");
        ConnectionProblem Problem(string summary, string advice) => new(summary, advice, technical);
        var viaProxy = proxy.Length > 0 ? $"另外，这台电脑正在通过代理（{proxy}）上网，也可能是代理的问题。" : "";

        var http = chain.OfType<HttpRequestException>().FirstOrDefault();
        var socket = chain.OfType<SocketException>().FirstOrDefault();
        var win32 = chain.OfType<Win32Exception>().FirstOrDefault();
        var auth = chain.OfType<AuthenticationException>().FirstOrDefault();

        if (chain.OfType<ServerException>().FirstOrDefault() is { } refused)
        {
            var code = (int)refused.Status;
            return refused.Status switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => Problem(
                    "服务器不认这台电脑的通行令牌",
                    "配置串可能已经失效。请在网页「设备 → 添加电脑」里重新复制配置串，然后点「重新设置」粘贴进去。"),
                _ when code is >= 300 and < 400 => Problem(
                    "服务器把请求转去了登录页",
                    "配置串里缺通行令牌，或者令牌已经失效。请在网页「设备 → 添加电脑」里重新复制配置串，然后点「重新设置」粘贴进去。如果这个网络要先在浏览器里登录（酒店、访客 Wi-Fi），先登录。"),
                HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed => Problem(
                    $"这个地址上不是 PrivateChat 服务器（{code}）",
                    "配置串里的服务器地址可能不对。请在网页「设备 → 添加电脑」里重新复制配置串，然后点「重新设置」粘贴进去。"),
                _ when code >= 500 => Problem(
                    $"服务器暂时不可用（{code}）",
                    "家里的服务器可能没开机或正在重启。这里会自动重试；一直这样请告诉管理员。"),
                _ => Problem($"服务器拒绝了请求（{code}）", "请把下面的详细信息发给管理员。"),
            };
        }

        if (chain.Any(e => e is JsonException or NotSupportedException))
            return Problem(
                "服务器回的不是预期的内容",
                "多半是被登录页拦住了：配置串里缺通行令牌，或者令牌已经失效。请在网页「设备 → 添加电脑」里重新复制配置串，然后点「重新设置」粘贴进去。如果这个网络要先在浏览器里登录（酒店、访客 Wi-Fi），先登录。");

        if (http?.HttpRequestError == HttpRequestError.NameResolutionError || socket?.SocketErrorCode is SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData)
            return Problem(
                "找不到服务器（域名解析失败）",
                $"先看这台电脑能不能上网。如果别的网站都正常，可能是这个网络的 DNS 不认 {server.Host}，换个网络（比如手机热点）试试。");

        if (http?.HttpRequestError == HttpRequestError.ProxyTunnelError)
            return Problem(
                "系统代理没有放行",
                $"这台电脑设置了代理{(proxy.Length > 0 ? $"（{proxy}）" : "")}，它拒绝了连接。检查代理软件是不是在运行，或者在「设置 → 网络和 Internet → 代理」里关掉代理。");

        if (http?.HttpRequestError == HttpRequestError.SecureConnectionError || auth is not null)
        {
            if (certificate is not null) return Problem(CertificateSummary(certificate, clock, out var advice), advice + viaProxy);
            if (FromMessage(auth?.Message) is { } parsed) return Problem(CertificateSummary(parsed, clock, out var advice), advice + viaProxy);

            // 自带的 TLS 只说 1.3：对方回"协议版本不对"就是它不收 1.3
            if (bundledTls && chain.Any(e => e.Message.Contains("protocol_version", StringComparison.OrdinalIgnoreCase)))
                return Problem(
                    "服务器不接受 TLS 1.3",
                    "程序用的是自带的 TLS 1.3（只说 1.3，不降级），而服务器（或者夹在中间的安全设备、代理）不接受它。换个网络（比如手机热点）试试；一直这样请把下面的详细信息发给管理员。" + viaProxy);

            var versionMismatch = win32?.NativeErrorCode is SecEUnsupportedFunction or SecEIllegalMessage or SecEAlgorithmMismatch
                || chain.Any(e => e.Message.Contains("ProtocolVersion", StringComparison.OrdinalIgnoreCase) || e.Message.Contains("HandshakeFailure", StringComparison.OrdinalIgnoreCase));
            if (versionMismatch)
                return tls13
                    ? Problem(
                        "和服务器谈不拢加密方式",
                        "这台电脑的系统是支持 TLS 1.3 的，多半是网络里的安全设备、代理，或者杀毒软件的「HTTPS 扫描」夹在中间。换个网络（比如手机热点）试试，或者关掉 HTTPS 扫描。" + viaProxy)
                    : Problem(
                        "这台电脑的系统不支持服务器要求的 TLS 1.3",
                        "Windows 10 及更早的系统最高只支持 TLS 1.2，而服务器只接受 TLS 1.3。请让管理员把服务器（Cloudflare）的「最低 TLS 版本」调到 1.2，调好后这里会自动连上；或者换一台 Windows 11 的电脑。" + viaProxy);

            if (auth?.Message.Contains("RemoteCertificateValidationCallback", StringComparison.Ordinal) == true)
                return Problem(
                    "服务器证书没有通过校验",
                    "网络里可能有设备或软件把证书换掉了，或者这个网络要先在浏览器里登录。换个网络（比如手机热点）试试。" + viaProxy);
            if (auth?.Message.Contains("frame size", StringComparison.OrdinalIgnoreCase) == true || auth?.Message.Contains("corrupted frame", StringComparison.OrdinalIgnoreCase) == true)
                return Problem(
                    "对方回的不是加密连接",
                    "网络可能把请求转到了登录页或者代理上。看看这个网络是不是要先在浏览器里登录，系统代理设置对不对。" + viaProxy);
            // 握手半路断了，看不出是谁断的。旧系统上最常见的原因是服务器只收 TLS 1.3，先说这个
            return tls13
                ? Problem(
                    "加密握手被中断",
                    "这个网络可能拦截了这个网站，或者代理、安全软件挡住了连接。换个网络（比如手机热点）试试。" + viaProxy)
                : Problem(
                    "加密握手失败，多半是系统不支持 TLS 1.3",
                    "这台电脑是 Windows 10 或更早的系统，最高只支持 TLS 1.2。如果服务器只接受 TLS 1.3 就会这样：请让管理员把服务器（Cloudflare）的「最低 TLS 版本」调到 1.2，调好后这里会自动连上。如果服务器本来就接受 TLS 1.2，那是这个网络拦截了连接，换个网络（比如手机热点）试试。" + viaProxy);
        }

        if (chain.Any(e => e is TimeoutException) || ex is OperationCanceledException)
            return Problem(
                "服务器迟迟没有回应",
                "网络太慢，或者从这个网络到服务器的路不通。这里会自动重试；一直这样就换个网络试试。" + viaProxy);

        if (socket is not null || http?.HttpRequestError == HttpRequestError.ConnectionError)
            return socket?.SocketErrorCode switch
            {
                SocketError.ConnectionRefused => Problem("服务器拒绝了连接", "服务器地址或端口可能不对，或者服务没在运行。请核对配置串，或告诉管理员。" + viaProxy),
                SocketError.NetworkUnreachable or SocketError.HostUnreachable or SocketError.NetworkDown => Problem("网络不通", "先看这台电脑能不能上网。" + viaProxy),
                _ => Problem("连接被网络断开", "这个网络到服务器的连接不稳定或被拦截了。这里会自动重试；一直这样就换个网络试试。" + viaProxy),
            };

        return Problem("连不上服务器", "这里会自动重试。一直这样的话，请把下面的详细信息发给管理员。" + viaProxy);
    }

    private static string CertificateSummary(CertificateIssue c, DateTime now, out string advice)
    {
        var who = c.Issuer.Length > 0 ? $"（证书：{Short(c.Subject)}，颁发者：{Short(c.Issuer)}）" : "";
        if (c.Errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
        {
            advice = $"多半是这个网络要先在浏览器里登录（酒店、访客 Wi-Fi），或者它把这个网站转到了别处。先打开浏览器随便开一个网页，登录之后这里会自动重试。{who}";
            return "连到的不是我们的服务器（证书是别的网站的）";
        }
        if (c.Chain.HasFlag(X509ChainStatusFlags.NotTimeValid) && c.NotAfter != default)
        {
            if (now < c.NotBefore)
            {
                advice = $"这台电脑现在显示的时间是 {now:yyyy-MM-dd HH:mm}，比证书的生效日期（{c.NotBefore:yyyy-MM-dd}）还早。请把日期和时间调对（设置 → 时间和语言 → 自动设置时间）。";
                return "这台电脑的日期和时间不对";
            }
            advice = $"这台电脑现在显示的时间是 {now:yyyy-MM-dd HH:mm}，证书有效期到 {c.NotAfter:yyyy-MM-dd}。如果时间显示得不对，先把日期和时间调对；时间没错的话就是证书真的过期了，请告诉管理员。{who}";
            return "服务器证书过期了，或者这台电脑的时间不对";
        }
        advice = $"网络里可能有安全设备、代理，或者杀毒软件的「HTTPS 扫描」把证书换掉了：换个网络（比如手机热点）试试，或者关掉 HTTPS 扫描。如果颁发者是 Google Trust Services、Let's Encrypt 这样的正规机构，那是系统里缺根证书，运行一次 Windows 更新再试。{who}";
        return "服务器证书不被这台电脑信任";
    }

    /// <summary>没挂回调的连接（SignalR 的 WebSocket 等）：.NET 的报错里带着校验结果的名字，从里面认。</summary>
    private static CertificateIssue? FromMessage(string? message)
    {
        if (message is null) return null;
        var errors = SslPolicyErrors.None;
        var chain = X509ChainStatusFlags.NoError;
        if (message.Contains(nameof(SslPolicyErrors.RemoteCertificateNameMismatch))) errors |= SslPolicyErrors.RemoteCertificateNameMismatch;
        if (message.Contains(nameof(X509ChainStatusFlags.NotTimeValid))) chain |= X509ChainStatusFlags.NotTimeValid;
        if (message.Contains(nameof(X509ChainStatusFlags.UntrustedRoot))) chain |= X509ChainStatusFlags.UntrustedRoot;
        if (message.Contains(nameof(X509ChainStatusFlags.PartialChain))) chain |= X509ChainStatusFlags.PartialChain;
        if (chain != X509ChainStatusFlags.NoError || message.Contains(nameof(SslPolicyErrors.RemoteCertificateChainErrors))) errors |= SslPolicyErrors.RemoteCertificateChainErrors;
        return errors == SslPolicyErrors.None ? null : new CertificateIssue("", "", default, default, errors, chain);
    }

    /// <summary>证书上的名字只留 CN / O，一长串地址没人看。</summary>
    private static string Short(string distinguishedName)
    {
        var parts = distinguishedName.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p.StartsWith("CN=", StringComparison.OrdinalIgnoreCase) || p.StartsWith("O=", StringComparison.OrdinalIgnoreCase))
            .Select(p => p[(p.IndexOf('=') + 1)..]);
        var text = string.Join(" / ", parts);
        return text.Length > 0 ? text : distinguishedName;
    }

    private static string Format(Exception e) => e switch
    {
        HttpRequestException h => $"{e.GetType().Name}[{h.HttpRequestError}]: {e.Message}",
        SocketException s => $"{e.GetType().Name}[{s.SocketErrorCode}]: {e.Message}",
        Win32Exception w => $"{e.GetType().Name}[0x{w.NativeErrorCode:X8}]: {e.Message}",
        _ => $"{e.GetType().Name}: {e.Message}",
    };

    /// <summary>这次请求会走的系统代理（.NET 默认跟随 Windows 的代理设置）；直连返回空。</summary>
    private static string SystemProxyFor(Uri server)
    {
        try
        {
            var proxy = HttpClient.DefaultProxy;
            if (proxy.IsBypassed(server)) return "";
            var via = proxy.GetProxy(server);
            return via is null || via == server ? "" : via.GetLeftPart(UriPartial.Authority);
        }
        catch (Exception)
        {
            return "";
        }
    }
}

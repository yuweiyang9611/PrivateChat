using System.Net;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;
using PrivateChat.Api.Infrastructure.Keys;

namespace PrivateChat.Api.Features.Remote;

/// <summary>
/// 被控端的局域网直连入口：api 自己多听一个 HTTPS 端口，被控端在家时直接连它，不经 Cloudflare
/// （省掉出公网再绕回来的一圈，家里宽带的上下行也不用各跑一遍远程画面）。
///
/// - 加密照旧：证书是服务器自己生成的自签证书（<see cref="KeyStore.DirectCertificate"/>）。被控端先经公网
///   （正规证书 + Access）拿到它的指纹（GET /api/remote/agent/direct），直连时只认这一张，局域网里别的机器冒充不了。
/// - 这个端口只给被控端用：只放行带设备令牌的远程接口和 /healthz，网页、密码登录、Cookie 一律 404。
/// - 这里没有 Access 那道外门，靠的是设备令牌（和局域网 8080 直连靠密码一样），所以只该发布在局域网地址上。
/// </summary>
public static class DirectEntrance
{
    /// <summary>被控端会用到的接口（设备令牌本来也只能用于这些，见 DeviceTokenAuthenticationHandler）。</summary>
    private static readonly PathString[] AgentPaths = ["/hubs/agent", "/ws/remote", "/api/remote/sessions", "/api/remote/agent"];

    private static readonly string[] ForwardingHeaders = ["X-Forwarded-For", "X-Forwarded-Proto", "X-Forwarded-Host", "X-Real-IP"];

    /// <summary>配了直连地址就多听一个 HTTPS 端口（容器里默认 8443，compose 把它发布到宿主机的局域网地址上）。</summary>
    public static void AddDirectEntrance(this WebApplicationBuilder builder)
    {
        var options = builder.Configuration.GetSection(RemoteOptions.Section).Get<RemoteOptions>() ?? new();
        if (!options.DirectEnabled) return;
        // 代码里一写 Listen，ASPNETCORE_URLS 就不再生效（Kestrel 只听代码里的）：把它列的地址照样听上。
        // 启动时 Kestrel 会警告一句 "Overriding address(es) ..."，说的就是这件事，紧接着的 "Now listening on" 两行才是实际听的地址
        var listen = ConfiguredUrls(builder.Configuration[WebHostDefaults.ServerUrlsKey], builder.Configuration["http_ports"], builder.Configuration["https_ports"]);
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            foreach (var url in listen)
            {
                var address = BindingAddress.Parse(url);
                Action<ListenOptions> configure = l =>
                {
                    if (string.Equals(address.Scheme, "https", StringComparison.OrdinalIgnoreCase)) l.UseHttps();
                };
                if (string.Equals(address.Host, "localhost", StringComparison.OrdinalIgnoreCase)) kestrel.ListenLocalhost(address.Port, configure);
                else if (IPAddress.TryParse(address.Host, out var ip)) kestrel.Listen(ip, address.Port, configure);
                else kestrel.ListenAnyIP(address.Port, configure); // *、+、主机名
            }
            kestrel.ListenAnyIP(options.DirectPort, l =>
            {
                l.Protocols = HttpProtocols.Http1; // 被控端只说 HTTP/1.1（WebSocket 也走它）
                l.UseHttps(kestrel.ApplicationServices.GetRequiredService<KeyStore>().DirectCertificate);
            });
        });
    }

    /// <summary>平时 Kestrel 听哪些地址：ASPNETCORE_URLS，没有就 ASPNETCORE_HTTP_PORTS / HTTPS_PORTS，都没有是它的默认值。</summary>
    public static IReadOnlyList<string> ConfiguredUrls(string? urls, string? httpPorts, string? httpsPorts)
    {
        static string[] Split(string? s) => (s ?? "").Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var list = Split(urls);
        if (list.Length > 0) return list;
        var ports = Split(httpPorts).Select(p => $"http://*:{p}").Concat(Split(httpsPorts).Select(p => $"https://*:{p}")).ToList();
        return ports.Count > 0 ? ports : ["http://localhost:5000"];
    }

    /// <summary>直连端口上的请求：只放行被控端的接口。放在最前面（转发头处理之前）。</summary>
    public static void UseDirectEntranceGate(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<IOptions<RemoteOptions>>().Value;
        if (!options.DirectEnabled) return;
        var port = options.DirectPort;
        app.Use(async (context, next) =>
        {
            if (context.Connection.LocalPort != port)
            {
                await next(context);
                return;
            }
            // 这个端口前面没有反向代理：客户端自己带的转发头不算数
            foreach (var header in ForwardingHeaders) context.Request.Headers.Remove(header);
            if (!Allows(context.Request))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            await next(context);
        });
    }

    /// <summary>健康检查（被控端用它试通不通）随便访问；其余只放行带设备令牌（Bearer）的远程接口。</summary>
    public static bool Allows(HttpRequest request)
    {
        if (request.Path == "/healthz") return true;
        if (!request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return false;
        return AgentPaths.Any(p => request.Path.StartsWithSegments(p));
    }
}

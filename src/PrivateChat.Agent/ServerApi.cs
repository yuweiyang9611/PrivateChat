using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using PrivateChat.Contracts.Remote;

namespace PrivateChat.Agent;

public sealed record DeviceCode(string Code, string PollToken, int ExpiresInSeconds);
public sealed record DevicePoll(string Status, string? DeviceToken, Guid? DeviceId);
public sealed record SessionInfo(Guid Id, RemoteSessionState State, RemoteSessionMode Mode, string? EndReason);

/// <summary>
/// 服务器连接参数：每个请求都带 Access 服务令牌头（过 Cloudflare Access 门禁），有设备令牌时再带 Bearer。
/// Pin 不为空 = 局域网直连这条路（地址是服务器给的直连入口，证书按指纹认，没有 Access 头）。
/// </summary>
public sealed record ServerInfo(string Url, string AccessClientId, string AccessClientSecret, string DeviceToken, string? Pin = null)
{
    public bool IsDirect => Pin is not null;

    public void Apply(HttpHeaders headers, bool withToken = true)
    {
        if (AccessClientId.Length > 0 && AccessClientSecret.Length > 0)
        {
            headers.Remove("CF-Access-Client-Id");
            headers.Remove("CF-Access-Client-Secret");
            headers.TryAddWithoutValidation("CF-Access-Client-Id", AccessClientId);
            headers.TryAddWithoutValidation("CF-Access-Client-Secret", AccessClientSecret);
        }
        if (withToken && DeviceToken.Length > 0) headers.TryAddWithoutValidation("Authorization", $"Bearer {DeviceToken}");
    }

    public void Apply(ClientWebSocketOptions options)
    {
        if (AccessClientId.Length > 0 && AccessClientSecret.Length > 0)
        {
            options.SetRequestHeader("CF-Access-Client-Id", AccessClientId);
            options.SetRequestHeader("CF-Access-Client-Secret", AccessClientSecret);
        }
        options.SetRequestHeader("Authorization", $"Bearer {DeviceToken}");
    }

    public Uri WebSocketUri(string path)
    {
        var b = new UriBuilder(Url.TrimEnd('/') + path);
        b.Scheme = b.Scheme == "https" ? "wss" : "ws";
        return b.Uri;
    }
}

/// <summary>REST：设备码登记、回复同意、挂断、查会话。</summary>
public sealed class ServerApi(ServerInfo server) : IDisposable
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    // 系统有 TLS 1.3 时和默认的 HttpClient 一样（只多记一下没通过校验的证书）；没有时走程序自带的 TLS 1.3
    private readonly HttpClient _http = new(AgentTransport.CreateHandler(new Uri(server.Url), server.Pin))
    {
        BaseAddress = AgentTransport.Address(new Uri(server.Url.TrimEnd('/') + "/")),
        Timeout = TimeSpan.FromSeconds(20),
    };

    private HttpRequestMessage Request(HttpMethod method, string path, object? body = null, bool withToken = true)
    {
        var req = new HttpRequestMessage(method, path);
        server.Apply(req.Headers, withToken);
        if (body is not null) req.Content = JsonContent.Create(body, options: Json);
        return req;
    }

    public async Task<DeviceCode> RequestCodeAsync(string name, CancellationToken ct)
    {
        using var res = await _http.SendAsync(Request(HttpMethod.Post, "api/auth/device/code", new { name, platform = "windows" }, withToken: false), ct);
        await EnsureAsync(res);
        return (await res.Content.ReadFromJsonAsync<DeviceCode>(Json, ct))!;
    }

    /// <summary>null：服务器不认这个码了（过期、服务重启），重新申请。</summary>
    public async Task<DevicePoll?> PollAsync(DeviceCode code, CancellationToken ct)
    {
        using var res = await _http.SendAsync(Request(HttpMethod.Get, $"api/auth/device/poll?code={code.Code}&pollToken={code.PollToken}", withToken: false), ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureAsync(res);
        return await res.Content.ReadFromJsonAsync<DevicePoll>(Json, ct);
    }

    /// <summary>回复同意 / 拒绝；返回会话现在的状态，请求已经结束（超时、对方取消）时返回 null。</summary>
    public async Task<SessionInfo?> ConsentAsync(Guid sessionId, bool accept, CancellationToken ct)
    {
        using var res = await _http.SendAsync(Request(HttpMethod.Post, $"api/remote/sessions/{sessionId}/consent", new { accept }), ct);
        if (res.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.NotFound) return null;
        await EnsureAsync(res);
        return await res.Content.ReadFromJsonAsync<SessionInfo>(Json, ct);
    }

    public async Task EndAsync(Guid sessionId, CancellationToken ct)
    {
        using var res = await _http.SendAsync(Request(HttpMethod.Post, $"api/remote/sessions/{sessionId}/end"), ct);
        if (res.StatusCode != HttpStatusCode.NotFound) await EnsureAsync(res);
    }

    /// <summary>局域网直连的入口（地址 + 证书指纹）；服务器没开直连时 null（旧版服务器没有这个接口，也是 null）。</summary>
    public async Task<AgentDirectInfo?> GetDirectAsync(CancellationToken ct)
    {
        using var res = await _http.SendAsync(Request(HttpMethod.Get, "api/remote/agent/direct"), ct);
        if (res.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotFound) return null;
        await EnsureAsync(res);
        return await res.Content.ReadFromJsonAsync<AgentDirectInfo>(Json, ct);
    }

    /// <summary>试一下这条路通不通：拿到服务器的应答就算通（直连时证书指纹已经对上了）；连不上、证书不对都会抛出来。</summary>
    public async Task ProbeAsync(CancellationToken ct)
    {
        using var res = await _http.SendAsync(Request(HttpMethod.Get, "healthz", withToken: false), ct);
    }

    private static async Task EnsureAsync(HttpResponseMessage res)
    {
        if (res.IsSuccessStatusCode) return;
        // 正文可能是一整页 HTML（登录页、网关的报错页）：压成一行，日志和说明窗口里才看得清
        var body = string.Join(' ', (await res.Content.ReadAsStringAsync()).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        throw new ServerException(res.StatusCode, body.Length > 200 ? body[..200] : body);
    }

    public void Dispose() => _http.Dispose();
}

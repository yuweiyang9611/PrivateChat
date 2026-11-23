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

/// <summary>服务器连接参数：每个请求都带 Access 服务令牌头（过 Cloudflare Access 门禁），有设备令牌时再带 Bearer。</summary>
public sealed record ServerInfo(string Url, string AccessClientId, string AccessClientSecret, string DeviceToken)
{
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
    private readonly HttpClient _http = new(AgentTransport.CreateHandler(new Uri(server.Url)))
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

    /// <summary>
    /// 这次远程能不能走快速线路（画面和声音经 Cloudflare Realtime），以及连过去用的 ICE 服务器。
    /// 旧版服务器没有这个接口、或会话已经不在进行中时 null。
    /// </summary>
    /// <param name="withIce">false：只问有没有，不要 ICE 服务器（服务器不用去签发 TURN 凭据，答得快）。</param>
    public async Task<RemoteRealtimeInfo?> GetRealtimeAsync(Guid sessionId, CancellationToken ct, bool withIce = true)
    {
        using var res = await _http.SendAsync(Request(HttpMethod.Get, $"api/remote/sessions/{sessionId}/realtime{(withIce ? "" : "?ice=false")}"), ct);
        if (res.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict or HttpStatusCode.MethodNotAllowed) return null;
        await EnsureAsync(res);
        return await res.Content.ReadFromJsonAsync<RemoteRealtimeInfo>(Json, ct);
    }

    /// <summary>把画面和声音发布到快速线路：交 offer，拿 Cloudflare 的 answer（经服务器转手）。</summary>
    public async Task<RemoteRealtimePublishResponse> PublishRealtimeAsync(Guid sessionId, RemoteRealtimePublishRequest request, CancellationToken ct)
    {
        using var res = await _http.SendAsync(Request(HttpMethod.Post, $"api/remote/sessions/{sessionId}/realtime/publish", request), ct);
        await EnsureAsync(res);
        return (await res.Content.ReadFromJsonAsync<RemoteRealtimePublishResponse>(Json, ct))!;
    }

    /// <summary>
    /// 遥测：上报这台电脑的硬件和能力。服务器是老版本（没有这个接口）时返回 false——这次连接里就别再试了；别的错误照常抛。
    /// </summary>
    public async Task<bool> PutTelemetryAsync(AgentTelemetrySnapshot snapshot, CancellationToken ct)
    {
        using var res = await _http.SendAsync(Request(HttpMethod.Put, "api/remote/agent/telemetry", snapshot), ct);
        if (res.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed) return false;
        await EnsureAsync(res);
        return true;
    }

    public async Task<bool> PutUpdateStatusAsync(AgentUpdateStatusReport status, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Put, "api/remote/agent/update-status", status);
        using var res = await _http.SendAsync(request, ct);
        if (res.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed) return false;
        await EnsureAsync(res);
        return true;
    }

    /// <summary>
    /// 遥测：这次远程里被控端这边的汇总（中途每 60 秒、结束时各一次，服务器按会话覆盖）。
    /// 服务器是老版本、或者这次会话已经不收了（结束太久、会话不是这台电脑的）返回 false；别的错误照常抛。
    /// </summary>
    public async Task<bool> PutSessionTelemetryAsync(Guid sessionId, AgentSessionTelemetry telemetry, CancellationToken ct)
    {
        using var res = await _http.SendAsync(Request(HttpMethod.Put, $"api/remote/sessions/{sessionId}/telemetry", telemetry), ct);
        if (res.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed or HttpStatusCode.Conflict) return false;
        await EnsureAsync(res);
        return true;
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

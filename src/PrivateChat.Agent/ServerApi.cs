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

    private readonly HttpClient _http = new() { BaseAddress = new Uri(server.Url.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(20) };

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

    private static async Task EnsureAsync(HttpResponseMessage res)
    {
        if (res.IsSuccessStatusCode) return;
        var body = await res.Content.ReadAsStringAsync();
        throw new ServerException(res.StatusCode, body.Length > 300 ? body[..300] : body);
    }

    public void Dispose() => _http.Dispose();
}

public sealed class ServerException(HttpStatusCode status, string body) : Exception($"服务器返回 {(int)status}：{body}")
{
    public HttpStatusCode Status { get; } = status;
}

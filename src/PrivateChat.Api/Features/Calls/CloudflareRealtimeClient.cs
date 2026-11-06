using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using PrivateChat.Api.Infrastructure;

namespace PrivateChat.Api.Features.Calls;

// ---- Cloudflare Realtime SFU / TURN 的请求与响应形状 ----
// 依据 https://developers.cloudflare.com/realtime/ 的公开文档整理。
// TODO: 落地时逐项对照当前官方文档核对字段名（尤其 tracks/new 的 location/sessionId/trackName 与 renegotiate 的 sessionDescription）。

public sealed record SessionDescription([property: JsonPropertyName("type")] string Type, [property: JsonPropertyName("sdp")] string Sdp);

public sealed record NewSessionResponse([property: JsonPropertyName("sessionId")] string SessionId);

/// <summary>location = "local"：发布本端轨道（带 mid + trackName）；location = "remote"：拉取他人轨道（带 sessionId + trackName）。</summary>
public sealed record TrackObject(
    [property: JsonPropertyName("location")] string Location,
    [property: JsonPropertyName("trackName")] string TrackName,
    [property: JsonPropertyName("mid")] string? Mid = null,
    [property: JsonPropertyName("sessionId")] string? SessionId = null);

public sealed record TracksRequest(
    [property: JsonPropertyName("tracks")] List<TrackObject> Tracks,
    [property: JsonPropertyName("sessionDescription")] SessionDescription? SessionDescription = null);

public sealed record TrackResult(
    [property: JsonPropertyName("trackName")] string? TrackName,
    [property: JsonPropertyName("mid")] string? Mid,
    [property: JsonPropertyName("sessionId")] string? SessionId,
    [property: JsonPropertyName("errorCode")] string? ErrorCode,
    [property: JsonPropertyName("errorDescription")] string? ErrorDescription);

public sealed record TracksResponse(
    [property: JsonPropertyName("requiresImmediateRenegotiation")] bool RequiresImmediateRenegotiation,
    [property: JsonPropertyName("sessionDescription")] SessionDescription? SessionDescription,
    [property: JsonPropertyName("tracks")] List<TrackResult>? Tracks,
    [property: JsonPropertyName("errorCode")] string? ErrorCode,
    [property: JsonPropertyName("errorDescription")] string? ErrorDescription);

public sealed record RenegotiateRequest([property: JsonPropertyName("sessionDescription")] SessionDescription SessionDescription);

public sealed record CloseTracksRequest(
    [property: JsonPropertyName("tracks")] List<CloseTrackObject> Tracks,
    [property: JsonPropertyName("sessionDescription")] SessionDescription? SessionDescription = null,
    [property: JsonPropertyName("force")] bool Force = false);

public sealed record CloseTrackObject([property: JsonPropertyName("mid")] string Mid);

public sealed record TurnCredentials(
    [property: JsonPropertyName("iceServers")] IceServers IceServers);

public sealed record IceServers(
    [property: JsonPropertyName("urls")] List<string> Urls,
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("credential")] string Credential);

/// <summary>
/// 封装 Cloudflare Realtime 的 REST 调用（App Secret 只在服务端）。
/// 基址 https://rtc.live.cloudflare.com/v1；SFU：/apps/{appId}/...；TURN：/turn/keys/{keyId}/credentials/generate。
/// </summary>
public sealed class CloudflareRealtimeClient(HttpClient http, IOptions<CloudflareRealtimeOptions> options)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private CloudflareRealtimeOptions O => options.Value;
    private string AppBase => $"{O.BaseUrl.TrimEnd('/')}/apps/{O.AppId}";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(O.AppId) && !string.IsNullOrWhiteSpace(O.AppSecret);

    public async Task<string> NewSessionAsync(CancellationToken ct)
    {
        using var req = SfuRequest(HttpMethod.Post, $"{AppBase}/sessions/new");
        using var res = await http.SendAsync(req, ct);
        var body = await ReadAsync<NewSessionResponse>(res, ct);
        return body.SessionId;
    }

    public async Task<TracksResponse> AddTracksAsync(string sessionId, TracksRequest request, CancellationToken ct)
    {
        using var req = SfuRequest(HttpMethod.Post, $"{AppBase}/sessions/{sessionId}/tracks/new", request);
        using var res = await http.SendAsync(req, ct);
        return await ReadAsync<TracksResponse>(res, ct);
    }

    public async Task RenegotiateAsync(string sessionId, SessionDescription answer, CancellationToken ct)
    {
        using var req = SfuRequest(HttpMethod.Put, $"{AppBase}/sessions/{sessionId}/renegotiate", new RenegotiateRequest(answer));
        using var res = await http.SendAsync(req, ct);
        await EnsureOkAsync(res, ct);
    }

    public async Task<TracksResponse> CloseTracksAsync(string sessionId, CloseTracksRequest request, CancellationToken ct)
    {
        using var req = SfuRequest(HttpMethod.Put, $"{AppBase}/sessions/{sessionId}/tracks/close", request);
        using var res = await http.SendAsync(req, ct);
        return await ReadAsync<TracksResponse>(res, ct);
    }

    /// <summary>为一次通话签发短期 TURN 凭据（TTL 默认 24 h）。</summary>
    public async Task<TurnCredentials> GenerateTurnCredentialsAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{O.BaseUrl.TrimEnd('/')}/turn/keys/{O.TurnKeyId}/credentials/generate");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", O.TurnApiToken);
        req.Content = JsonContent.Create(new { ttl = O.TurnCredentialTtlSeconds }, options: Json);
        using var res = await http.SendAsync(req, ct);
        return await ReadAsync<TurnCredentials>(res, ct);
    }

    private HttpRequestMessage SfuRequest(HttpMethod method, string url, object? body = null)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", O.AppSecret);
        if (body is not null) req.Content = JsonContent.Create(body, body.GetType(), options: Json);
        return req;
    }

    private static async Task EnsureOkAsync(HttpResponseMessage res, CancellationToken ct)
    {
        if (res.IsSuccessStatusCode) return;
        var text = await res.Content.ReadAsStringAsync(ct);
        throw new ApiException(502, "Cloudflare Realtime 调用失败", $"{(int)res.StatusCode}: {text}");
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage res, CancellationToken ct)
    {
        await EnsureOkAsync(res, ct);
        return await res.Content.ReadFromJsonAsync<T>(Json, ct) ?? throw new ApiException(502, "Cloudflare Realtime 返回为空");
    }
}

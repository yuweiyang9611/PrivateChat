using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using PrivateChat.Api.Infrastructure;

namespace PrivateChat.Api.Features.Calls;

// ---- Cloudflare Realtime SFU / TURN 的请求与响应形状 ----
// 对照官方 OpenAPI（developers.cloudflare.com/realtime/static/realtime-api-2024-05-21.yaml）与
// "Connection patterns" / "Generate Credentials" 两页核对过，核对日期 2026-11-11。

public sealed record SessionDescription([property: JsonPropertyName("type")] string Type, [property: JsonPropertyName("sdp")] string Sdp);

public sealed record NewSessionResponse(
    [property: JsonPropertyName("sessionId")] string? SessionId,
    [property: JsonPropertyName("errorCode")] string? ErrorCode = null,
    [property: JsonPropertyName("errorDescription")] string? ErrorDescription = null);

/// <summary>location = "local"：发布本端轨道（带 mid + trackName）；location = "remote"：拉取他人轨道（带发布者的 sessionId + trackName）。一次请求里只能全是 local 或全是 remote。</summary>
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
    [property: JsonPropertyName("errorCode")] string? ErrorCode = null,
    [property: JsonPropertyName("errorDescription")] string? ErrorDescription = null);

public sealed record TracksResponse(
    [property: JsonPropertyName("requiresImmediateRenegotiation")] bool RequiresImmediateRenegotiation,
    [property: JsonPropertyName("sessionDescription")] SessionDescription? SessionDescription,
    [property: JsonPropertyName("tracks")] List<TrackResult>? Tracks,
    [property: JsonPropertyName("errorCode")] string? ErrorCode = null,
    [property: JsonPropertyName("errorDescription")] string? ErrorDescription = null);

public sealed record RenegotiateRequest([property: JsonPropertyName("sessionDescription")] SessionDescription SessionDescription);

public sealed record RenegotiateResponse(
    [property: JsonPropertyName("errorCode")] string? ErrorCode = null,
    [property: JsonPropertyName("errorDescription")] string? ErrorDescription = null);

/// <summary>force = true：不交换 SDP 直接停止转发；force = false：必须带浏览器的 offer，并把返回的 answer 应用回去。</summary>
public sealed record CloseTracksRequest(
    [property: JsonPropertyName("tracks")] List<CloseTrackObject> Tracks,
    [property: JsonPropertyName("force")] bool Force,
    [property: JsonPropertyName("sessionDescription")] SessionDescription? SessionDescription = null);

public sealed record CloseTrackObject([property: JsonPropertyName("mid")] string Mid);

public sealed record CloseTrackResult(
    [property: JsonPropertyName("mid")] string? Mid,
    [property: JsonPropertyName("errorCode")] string? ErrorCode = null,
    [property: JsonPropertyName("errorDescription")] string? ErrorDescription = null);

public sealed record CloseTracksResponse(
    [property: JsonPropertyName("sessionDescription")] SessionDescription? SessionDescription,
    [property: JsonPropertyName("tracks")] List<CloseTrackResult>? Tracks,
    [property: JsonPropertyName("errorCode")] string? ErrorCode = null,
    [property: JsonPropertyName("errorDescription")] string? ErrorDescription = null);

/// <summary>与浏览器 RTCIceServer 同形，直接交给 RTCPeerConnection。</summary>
public sealed record IceServer(
    [property: JsonPropertyName("urls")] List<string> Urls,
    [property: JsonPropertyName("username")] string? Username = null,
    [property: JsonPropertyName("credential")] string? Credential = null);

public sealed record IceServersResponse([property: JsonPropertyName("iceServers")] List<IceServer>? IceServers);

/// <summary>Cloudflare 返回的错误：HTTP 非 2xx，或 2xx 但带顶层 errorCode。</summary>
public sealed class CloudflareRealtimeException(int status, string? errorCode, string? errorDescription)
    : Exception($"Cloudflare Realtime {status} {errorCode}: {errorDescription}")
{
    public int Status { get; } = status;
    public string? ErrorCode { get; } = errorCode;
    public string? ErrorDescription { get; } = errorDescription;
    /// <summary>会话已不可用（过期 / PeerConnection 已关闭），需要换新会话重连。</summary>
    public bool SessionGone => Status == 410 || (ErrorCode == "session_error" && Status != 425);
}

/// <summary>
/// 封装 Cloudflare Realtime 的 REST 调用（App Secret 只在服务端）。
/// 基址 https://rtc.live.cloudflare.com/v1；SFU：/apps/{appId}/...；TURN：/turn/keys/{keyId}/credentials/generate-ice-servers。
/// HTTP 200 里仍可能带单条轨道的错误，调用方要逐条检查 Tracks[].ErrorCode。
/// </summary>
public sealed class CloudflareRealtimeClient(HttpClient http, IOptions<CloudflareRealtimeOptions> options, ILogger<CloudflareRealtimeClient> logger)
{
    public const string PublicStun = "stun:stun.cloudflare.com:3478";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private CloudflareRealtimeOptions O => options.Value;
    private string AppBase => $"{O.BaseUrl.TrimEnd('/')}/apps/{O.AppId}";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(O.AppId) && !string.IsNullOrWhiteSpace(O.AppSecret);
    public bool TurnConfigured => !string.IsNullOrWhiteSpace(O.TurnKeyId) && !string.IsNullOrWhiteSpace(O.TurnApiToken);

    /// <summary>建一个还没连上的会话。没用过的会话会过期，所以要等浏览器准备好协商时再建。</summary>
    public async Task<string> NewSessionAsync(CancellationToken ct)
    {
        var body = await SendAsync<NewSessionResponse>(HttpMethod.Post, $"{AppBase}/sessions/new", O.AppSecret, null, ct);
        if (body.ErrorCode is not null || string.IsNullOrEmpty(body.SessionId)) throw new CloudflareRealtimeException(200, body.ErrorCode ?? "empty_session", body.ErrorDescription);
        return body.SessionId;
    }

    public async Task<TracksResponse> AddTracksAsync(string sessionId, TracksRequest request, CancellationToken ct)
    {
        var body = await SendAsync<TracksResponse>(HttpMethod.Post, $"{AppBase}/sessions/{sessionId}/tracks/new", O.AppSecret, request, ct);
        if (body.ErrorCode is not null) throw new CloudflareRealtimeException(200, body.ErrorCode, body.ErrorDescription);
        return body;
    }

    public async Task RenegotiateAsync(string sessionId, SessionDescription answer, CancellationToken ct)
    {
        var body = await SendAsync<RenegotiateResponse>(HttpMethod.Put, $"{AppBase}/sessions/{sessionId}/renegotiate", O.AppSecret, new RenegotiateRequest(answer), ct);
        if (body.ErrorCode is not null) throw new CloudflareRealtimeException(200, body.ErrorCode, body.ErrorDescription);
    }

    /// <summary>关闭轨道。顶层错误不抛异常：协商式关闭即使部分失败也要把 answer 交回浏览器。</summary>
    public Task<CloseTracksResponse> CloseTracksAsync(string sessionId, IEnumerable<string> mids, SessionDescription? offer, CancellationToken ct)
        => SendAsync<CloseTracksResponse>(HttpMethod.Put, $"{AppBase}/sessions/{sessionId}/tracks/close", O.AppSecret,
            new CloseTracksRequest(mids.Select(m => new CloseTrackObject(m)).ToList(), Force: offer is null, offer), ct);

    /// <summary>
    /// 为一次通话签发短期 TURN 凭据。没配 TURN 密钥或签发失败时只返回公共 STUN：多数网络下仍能通话。
    /// 53 端口的地址浏览器会拦，去掉以免白等超时。
    /// </summary>
    public async Task<List<IceServer>> GenerateIceServersAsync(CancellationToken ct)
    {
        if (!TurnConfigured) return [new IceServer([PublicStun])];
        try
        {
            var body = await SendAsync<IceServersResponse>(HttpMethod.Post,
                $"{O.BaseUrl.TrimEnd('/')}/turn/keys/{O.TurnKeyId}/credentials/generate-ice-servers", O.TurnApiToken, new { ttl = O.TurnCredentialTtlSeconds }, ct);
            return (body.IceServers ?? [])
                .Select(s => s with { Urls = s.Urls.Where(u => !u.Contains(":53?") && !u.EndsWith(":53")).ToList() })
                .Where(s => s.Urls.Count > 0)
                .ToList();
        }
        catch (Exception ex) when (ex is CloudflareRealtimeException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("签发 TURN 凭据失败，退回只用 STUN：{Message}", ex.Message);
            return [new IceServer([PublicStun])];
        }
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string url, string bearer, object? body, CancellationToken ct) where T : class
    {
        using var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (body is not null) req.Content = JsonContent.Create(body, body.GetType(), options: Json);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        HttpResponseMessage res;
        string text;
        try
        {
            res = await http.SendAsync(req, timeout.Token);
            text = await res.Content.ReadAsStringAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new CloudflareRealtimeException(504, "timeout", "Cloudflare Realtime 没有在 10 秒内响应");
        }
        using (res)
        {
            T? parsed = null;
            // renegotiate 成功时正文是 {}，个别情况下为空，都按空对象处理
            try { parsed = JsonSerializer.Deserialize<T>(string.IsNullOrWhiteSpace(text) ? "{}" : text, Json); }
            catch (JsonException) { /* 非 JSON 的错误页 */ }

            if (!res.IsSuccessStatusCode)
            {
                string? code = null, description = null;
                try
                {
                    using var doc = JsonDocument.Parse(text);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        if (doc.RootElement.TryGetProperty("errorCode", out var c)) code = c.GetString();
                        if (doc.RootElement.TryGetProperty("errorDescription", out var d)) description = d.GetString();
                    }
                }
                catch (JsonException) { /* 同上 */ }
                throw new CloudflareRealtimeException((int)res.StatusCode, code, description ?? (text.Length > 200 ? text[..200] : text));
            }
            return parsed ?? throw new CloudflareRealtimeException((int)res.StatusCode, "empty_response", "Cloudflare Realtime 返回为空");
        }
    }
}

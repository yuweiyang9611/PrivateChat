using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PrivateChat.Api.Tests.Integration;

/// <summary>
/// 按官方 OpenAPI（realtime-api-2024-05-21.yaml）的形状应答的假 Cloudflare Realtime：
/// 记录每个请求，维护会话里发布 / 订阅的轨道，校验 Bearer 密钥。不处理真实媒体。
/// </summary>
public sealed class FakeRealtime : HttpMessageHandler
{
    public const string AppId = "test-app";
    public const string AppSecret = "test-app-secret";
    public const string TurnKeyId = "test-turn-key";
    public const string TurnToken = "test-turn-token";

    public sealed record Call(string Method, string Path, string? Authorization, JsonNode? Body);

    private sealed class Session
    {
        public readonly Dictionary<string, string> Published = new();   // trackName → mid
        public readonly HashSet<string> ClosedMids = [];
        public int NextMid;
        public bool AwaitingAnswer;
    }

    private readonly ConcurrentDictionary<string, Session> _sessions = new();
    private readonly ConcurrentQueue<(string PathPart, HttpStatusCode Status, string Json)> _failures = new();
    private int _sessionSeq;

    public ConcurrentQueue<Call> Calls { get; } = new();
    /// <summary>generate-ice-servers 返回的地址（默认含一个浏览器会拦的 53 端口地址）。</summary>
    public string[] TurnUrls { get; set; } = ["turn:turn.cloudflare.com:3478?transport=udp", "turn:turn.cloudflare.com:53?transport=udp", "turns:turn.cloudflare.com:443?transport=tcp"];

    public List<Call> CallsTo(string pathPart) => Calls.Where(c => c.Path.Contains(pathPart)).ToList();

    /// <summary>让下一个路径里含 pathPart 的请求失败。</summary>
    public void FailNext(string pathPart, HttpStatusCode status, string errorCode, string description = "simulated")
        => _failures.Enqueue((pathPart, status, JsonSerializer.Serialize(new { errorCode, errorDescription = description })));

    /// <summary>让下一个 tracks/new 以 200 返回，但每条轨道都带错误。</summary>
    public string? NextTrackError { get; set; }

    public IReadOnlyCollection<string> ClosedMids(string sessionId) => _sessions.TryGetValue(sessionId, out var s) ? s.ClosedMids : [];
    public bool AwaitingAnswer(string sessionId) => _sessions.TryGetValue(sessionId, out var s) && s.AwaitingAnswer;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        var text = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        var body = string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
        var auth = request.Headers.Authorization?.ToString();
        Calls.Enqueue(new Call(request.Method.Method, path, auth, body));

        if (_failures.TryPeek(out var f) && path.Contains(f.PathPart) && _failures.TryDequeue(out f))
            return Respond(f.Status, f.Json);

        if (path.StartsWith($"/v1/turn/keys/{TurnKeyId}/credentials/generate-ice-servers"))
        {
            if (auth != $"Bearer {TurnToken}") return Error(HttpStatusCode.Unauthorized, "unauthorized");
            return Respond(HttpStatusCode.Created, JsonSerializer.Serialize(new
            {
                iceServers = new object[]
                {
                    new { urls = new[] { "stun:stun.cloudflare.com:3478" } },
                    new { urls = TurnUrls, username = "turn-user", credential = "turn-pass" },
                },
            }));
        }

        var prefix = $"/v1/apps/{AppId}/sessions/";
        if (!path.StartsWith(prefix)) return Error(HttpStatusCode.NotFound, "not_found");
        if (auth != $"Bearer {AppSecret}") return Error(HttpStatusCode.Unauthorized, "unauthorized");
        var rest = path[prefix.Length..];

        if (rest == "new" && request.Method == HttpMethod.Post)
        {
            var id = $"cfsess{Interlocked.Increment(ref _sessionSeq):000}";
            _sessions[id] = new Session();
            return Respond(HttpStatusCode.Created, JsonSerializer.Serialize(new { sessionId = id }));
        }

        var slash = rest.IndexOf('/');
        var sessionId = slash < 0 ? rest : rest[..slash];
        var op = slash < 0 ? "" : rest[(slash + 1)..];
        if (!_sessions.TryGetValue(sessionId, out var session)) return Error(HttpStatusCode.Gone, "session_error");

        lock (session)
        {
            switch (op)
            {
                case "tracks/new" when request.Method == HttpMethod.Post:
                    return TracksNew(sessionId, session, body);
                case "renegotiate" when request.Method == HttpMethod.Put:
                    if (!session.AwaitingAnswer || body?["sessionDescription"]?["type"]?.GetValue<string>() != "answer")
                        return Error(HttpStatusCode.NotAcceptable, "invalid_session_description");
                    session.AwaitingAnswer = false;
                    return Respond(HttpStatusCode.OK, "{}");
                case "tracks/close" when request.Method == HttpMethod.Put:
                    return TracksClose(session, body);
                default:
                    return Error(HttpStatusCode.NotFound, "not_found");
            }
        }
    }

    private HttpResponseMessage TracksNew(string sessionId, Session session, JsonNode? body)
    {
        // 上一个 offer 还没回 answer 就来新的变更：真实服务会拒绝
        if (session.AwaitingAnswer) return Error(HttpStatusCode.NotAcceptable, "invalid_session_description");
        var tracks = body?["tracks"]?.AsArray() ?? [];
        var locations = tracks.Select(t => t?["location"]?.GetValue<string>()).Distinct().ToList();
        if (tracks.Count == 0 || locations.Count != 1) return Error(HttpStatusCode.BadRequest, "invalid_params");
        var forcedError = NextTrackError;
        NextTrackError = null;

        var results = new JsonArray();
        if (locations[0] == "local")
        {
            if (body?["sessionDescription"]?["type"]?.GetValue<string>() != "offer") return Error(HttpStatusCode.NotAcceptable, "invalid_session_description");
            foreach (var t in tracks)
            {
                var name = t!["trackName"]!.GetValue<string>();
                var mid = t["mid"]!.GetValue<string>();
                if (forcedError is not null)
                {
                    results.Add(new JsonObject { ["trackName"] = name, ["mid"] = mid, ["errorCode"] = forcedError, ["errorDescription"] = "simulated" });
                    continue;
                }
                session.Published[name] = mid;
                results.Add(new JsonObject { ["trackName"] = name, ["mid"] = mid });
            }
            return Respond(HttpStatusCode.OK, new JsonObject
            {
                ["requiresImmediateRenegotiation"] = false,
                ["tracks"] = results,
                ["sessionDescription"] = new JsonObject { ["type"] = "answer", ["sdp"] = $"v=0 answer {sessionId}" },
            }.ToJsonString());
        }

        var any = false;
        foreach (var t in tracks)
        {
            var owner = t!["sessionId"]?.GetValue<string>() ?? "";
            var name = t["trackName"]!.GetValue<string>();
            var found = forcedError is null && _sessions.TryGetValue(owner, out var pub) && pub.Published.TryGetValue(name, out var pubMid) && !pub.ClosedMids.Contains(pubMid);
            if (!found)
            {
                results.Add(new JsonObject { ["sessionId"] = owner, ["trackName"] = name, ["errorCode"] = forcedError ?? "not_found_track_error", ["errorDescription"] = "simulated" });
                continue;
            }
            any = true;
            results.Add(new JsonObject { ["sessionId"] = owner, ["trackName"] = name, ["mid"] = $"r{session.NextMid++}" });
        }
        var response = new JsonObject { ["requiresImmediateRenegotiation"] = any, ["tracks"] = results };
        if (any)
        {
            session.AwaitingAnswer = true;
            response["sessionDescription"] = new JsonObject { ["type"] = "offer", ["sdp"] = $"v=0 offer {sessionId}" };
        }
        return Respond(HttpStatusCode.OK, response.ToJsonString());
    }

    private static HttpResponseMessage TracksClose(Session session, JsonNode? body)
    {
        var force = body?["force"]?.GetValue<bool>() ?? false;
        var offer = body?["sessionDescription"];
        if (!force && offer?["type"]?.GetValue<string>() != "offer") return Error(HttpStatusCode.NotAcceptable, "invalid_session_description");
        var results = new JsonArray();
        foreach (var t in body?["tracks"]?.AsArray() ?? [])
        {
            var mid = t!["mid"]!.GetValue<string>();
            if (session.ClosedMids.Add(mid)) results.Add(new JsonObject { ["mid"] = mid });
            else results.Add(new JsonObject { ["mid"] = mid, ["errorCode"] = "close_track_error", ["errorDescription"] = "Track doesn't exist or was already closed" });
        }
        var response = new JsonObject { ["requiresImmediateRenegotiation"] = false, ["tracks"] = results };
        if (!force) response["sessionDescription"] = new JsonObject { ["type"] = "answer", ["sdp"] = "v=0 close-answer" };
        return Respond(HttpStatusCode.OK, response.ToJsonString());
    }

    private static HttpResponseMessage Error(HttpStatusCode status, string code)
        => Respond(status, JsonSerializer.Serialize(new { errorCode = code, errorDescription = "simulated" }));

    private static HttpResponseMessage Respond(HttpStatusCode status, string json)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

/// <summary>记录发往推送服务的请求（密文看不了，只看发给了谁、TTL、Topic）。</summary>
public sealed class CapturingPush : HttpMessageHandler
{
    public sealed record Sent(string Endpoint, string Ttl, string? Topic, string? Urgency);

    public ConcurrentQueue<Sent> Requests { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Enqueue(new Sent(
            request.RequestUri!.ToString(),
            request.Headers.TryGetValues("TTL", out var ttl) ? ttl.Single() : "",
            request.Headers.TryGetValues("Topic", out var topic) ? topic.Single() : null,
            request.Headers.TryGetValues("Urgency", out var urgency) ? urgency.Single() : null));
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("") });
    }
}

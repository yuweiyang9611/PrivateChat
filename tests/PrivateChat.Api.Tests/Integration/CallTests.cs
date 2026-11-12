using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PrivateChat.Api.Domain;
using PrivateChat.Api.Features.Calls;
using PrivateChat.Api.Features.Chat;

namespace PrivateChat.Api.Tests.Integration;

/// <summary>
/// 通话编排：发起 / 响铃 / 接听 / 拒接 / 未接 / 取消 / 掉线清理 / 会议室，以及发布、订阅经假 Cloudflare 的完整往返。
/// 对真实数据库跑 HTTP 流程，SignalR 广播用真实的 Hub 连接断言。
/// </summary>
public class CallTests
{
    private static readonly SessionDescription Offer = new("offer", "v=0 test-offer");
    private static readonly SessionDescription Answer = new("answer", "v=0 test-answer");

    private sealed record Ctx(ApiFactory App, HttpClient Admin, Guid AdminId, User Bob, HttpClient BobClient, User Carol, HttpClient CarolClient) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => App.DisposeAsync();

        public async Task<int> SweepAsync(TimeSpan later)
        {
            using var scope = App.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<CallService>().SweepAsync(DateTimeOffset.UtcNow + later, default);
        }

        public Task<CallSession> CallAsync(Guid id)
            => App.WithDbAsync(db => db.CallSessions.Include(c => c.Participants).ThenInclude(p => p.Tracks).AsNoTracking().FirstAsync(c => c.Id == id));
    }

    private static async Task<Ctx> SetupAsync(bool realtimeConfigured = true, bool turnConfigured = true)
    {
        var app = new ApiFactory(realtimeConfigured, turnConfigured);
        var admin = await app.LoginAsync("admin@example.com");
        var adminId = (await (await admin.GetAsync("/api/auth/me")).ReadAsync<Features.Auth.UserDto>()).Id;
        var bob = await app.AddUserAsync("bob", UserRole.Member);
        var carol = await app.AddUserAsync("carol", UserRole.Child);
        return new Ctx(app, admin, adminId, bob, await app.LoginAsync("bob"), carol, await app.LoginAsync("carol"));
    }

    private static async Task<Guid> ConversationAsync(HttpClient c, ConversationType type, params Guid[] others)
        => (await (await c.PostJsonAsync("/api/conversations", new { type, title = type == ConversationType.Group ? "一家人" : null, memberIds = others })).ReadAsync<ConversationDto>()).Id;

    private static async Task<CallDto> StartAsync(HttpClient c, Guid conversationId, CallType type = CallType.Video)
        => await (await c.PostJsonAsync("/api/calls", new StartCallRequest(conversationId, type))).ReadAsync<CallDto>();

    private static async Task<JoinResponse> JoinAsync(HttpClient c, Guid callId)
        => await (await c.PostAsync($"/api/calls/{callId}/join", null)).ReadAsync<JoinResponse>();

    private static async Task<PublishResponse> PublishAsync(HttpClient c, Guid callId, params (string Mid, TrackKind Kind)[] tracks)
        => await (await c.PostJsonAsync($"/api/calls/{callId}/publish", new PublishRequest(Offer, tracks.Select(t => new PublishTrackInput(t.Mid, t.Kind)).ToList()))).ReadAsync<PublishResponse>();

    private static async Task<CallDto> GetAsync(HttpClient c, Guid callId)
        => await (await c.GetAsync($"/api/calls/{callId}")).ReadAsync<CallDto>();

    private static async Task<List<MessageDto>> MessagesAsync(HttpClient c, Guid conversationId)
        => await (await c.GetAsync($"/api/conversations/{conversationId}/messages")).ReadAsync<List<MessageDto>>();

    private static async Task EventuallyAsync(Func<bool> condition, string what, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new Xunit.Sdk.XunitException($"等不到：{what}");
            await Task.Delay(25);
        }
    }

    [PgFact]
    public async Task Config_reports_whether_realtime_is_configured()
    {
        await using var x = await SetupAsync();
        var config = await (await x.Admin.GetAsync("/api/calls/config")).ReadAsync<CallConfigDto>();
        Assert.True(config.Enabled);
        Assert.Equal(60, config.RingSeconds);

        await using var off = await SetupAsync(realtimeConfigured: false);
        Assert.False((await (await off.Admin.GetAsync("/api/calls/config")).ReadAsync<CallConfigDto>()).Enabled);
        var conv = await ConversationAsync(off.Admin, ConversationType.Direct, off.Bob.Id);
        var res = await off.Admin.PostJsonAsync("/api/calls", new StartCallRequest(conv, CallType.Audio));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
        Assert.Empty(off.App.Realtime.Calls);
    }

    [PgFact]
    public async Task Starting_a_call_rings_the_other_members_and_pushes_to_their_devices()
    {
        await using var x = await SetupAsync();
        var conv = await ConversationAsync(x.Admin, ConversationType.Direct, x.Bob.Id);
        var bobPush = await x.App.AddPushSubscriptionAsync(x.Bob.Id, "bob-phone");
        await x.App.AddPushSubscriptionAsync(x.AdminId, "admin-phone");
        await using var bobHub = await x.App.ConnectHubAsync("bob");
        await using var adminHub = await x.App.ConnectHubAsync("admin@example.com");

        var call = await StartAsync(x.Admin, conv);
        Assert.Equal((conv, CallType.Video, x.AdminId), (call.ConversationId, call.Type, call.StartedBy));
        Assert.Null(call.AnsweredAt);
        Assert.Empty(call.Participants);

        var incoming = await bobHub.WaitAsync<CallDto>("IncomingCall");
        Assert.Equal(call.Id, incoming.Id);
        // 发起人自己不响铃，但他的其它设备知道有这通通话
        await adminHub.WaitAsync("CallUpdated");
        Assert.Empty(adminHub.All("IncomingCall"));

        // 在线也推：在线只说明某一台设备开着，手机照样要响；发起人不推
        await EventuallyAsync(() => x.App.Push.Requests.Count == 1, "来电推送");
        var sent = x.App.Push.Requests.Single();
        Assert.Equal(bobPush, sent.Endpoint);
        Assert.Equal(CallPush.RingTtlSeconds.ToString(), sent.Ttl);
        Assert.Equal("high", sent.Urgency);

        // 同一个会话再发起：返回同一通通话，不重复响铃
        var again = await StartAsync(x.BobClient, conv, CallType.Audio);
        Assert.Equal(call.Id, again.Id);
        Assert.Equal(CallType.Video, again.Type);
        Assert.Single(bobHub.All("IncomingCall"));
        Assert.Equal(1, await x.App.WithDbAsync(db => db.CallSessions.CountAsync()));

        // 重新打开页面后能查到响铃中的通话
        var active = await (await x.BobClient.GetAsync("/api/calls/active")).ReadAsync<List<CallDto>>();
        Assert.Equal(call.Id, Assert.Single(active).Id);
    }

    [PgFact]
    public async Task Parallel_starts_in_one_conversation_create_a_single_call()
    {
        await using var x = await SetupAsync();
        var conv = await ConversationAsync(x.Admin, ConversationType.Direct, x.Bob.Id);
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => StartAsync(i % 2 == 0 ? x.Admin : x.BobClient, conv)));
        Assert.Single(results.Select(r => r.Id).Distinct());
        Assert.Equal(1, await x.App.WithDbAsync(db => db.CallSessions.CountAsync()));
    }

    [PgFact]
    public async Task Direct_call_full_flow_publish_pull_and_hang_up_writes_a_call_message()
    {
        await using var x = await SetupAsync();
        var conv = await ConversationAsync(x.Admin, ConversationType.Direct, x.Bob.Id);
        await using var bobHub = await x.App.ConnectHubAsync("bob");
        await using var adminHub = await x.App.ConnectHubAsync("admin@example.com");

        var call = await StartAsync(x.Admin, conv);
        var adminJoin = await JoinAsync(x.Admin, call.Id);
        // TURN 凭据：53 端口的地址去掉
        Assert.Equal(2, adminJoin.IceServers.Count);
        Assert.Equal(["stun:stun.cloudflare.com:3478"], adminJoin.IceServers[0].Urls);
        Assert.Equal(["turn:turn.cloudflare.com:3478?transport=udp", "turns:turn.cloudflare.com:443?transport=tcp"], adminJoin.IceServers[1].Urls);
        Assert.Equal(("turn-user", "turn-pass"), (adminJoin.IceServers[1].Username, adminJoin.IceServers[1].Credential));
        Assert.Null(adminJoin.Call.AnsweredAt);
        // 加入时不建 Cloudflare 会话：没用过的会话会过期
        Assert.Empty(x.App.Realtime.CallsTo("/sessions/"));

        // 发起人先发布
        var published = await PublishAsync(x.Admin, call.Id, ("0", TrackKind.Audio), ("1", TrackKind.Video));
        Assert.Equal("answer", published.Answer.Type);
        Assert.Equal(["0", "1"], published.Tracks.Select(t => t.Mid));
        Assert.StartsWith("audio-", published.Tracks[0].TrackName);
        Assert.StartsWith("video-", published.Tracks[1].TrackName);
        var cfPublish = x.App.Realtime.CallsTo("/tracks/new").Single();
        Assert.Equal("Bearer " + FakeRealtime.AppSecret, cfPublish.Authorization);
        Assert.Equal("offer", cfPublish.Body!["sessionDescription"]!["type"]!.GetValue<string>());
        Assert.Equal("local", cfPublish.Body["tracks"]![0]!["location"]!.GetValue<string>());
        Assert.Equal("0", cfPublish.Body["tracks"]![0]!["mid"]!.GetValue<string>());
        Assert.Equal(published.Tracks[0].TrackName, cfPublish.Body["tracks"]![0]!["trackName"]!.GetValue<string>());
        // 还在响铃的对方也收到轨道事件（进来时快照里已经有了）
        await bobHub.WaitAsync("TrackPublished", 2);

        // 对方接听：接通
        var bobJoin = await JoinAsync(x.BobClient, call.Id);
        Assert.NotNull(bobJoin.Call.AnsweredAt);
        Assert.Equal(2, bobJoin.Call.Participants.Count);
        var adminInSnapshot = bobJoin.Call.Participants.Single(p => p.UserId == x.AdminId);
        Assert.Equal(2, adminInSnapshot.Tracks.Count);
        Assert.True(adminInSnapshot.CamOn);
        var answered = await adminHub.WaitAsync<CallDto>("CallUpdated", 2);
        Assert.NotNull(answered.AnsweredAt);
        Assert.Equal(bobJoin.ParticipantId, (await adminHub.WaitAsync<ParticipantEventDto>("ParticipantJoined", 2)).Participant.Id);

        // 订阅：只给参与者 Id + 轨道名，服务端换成对方的 Cloudflare 会话
        var pullReq = new PullRequest(adminInSnapshot.Tracks.Select(t => new PullTrackInput(adminInSnapshot.Id, t.TrackName)).ToList());
        var pulled = await (await x.BobClient.PostJsonAsync($"/api/calls/{call.Id}/pull", pullReq)).ReadAsync<PullResponse>();
        Assert.True(pulled.RequiresImmediateRenegotiation);
        Assert.Equal("offer", pulled.Offer!.Type);
        Assert.Equal(["r0", "r1"], pulled.Tracks.Select(t => t.Mid));
        Assert.Equal([TrackKind.Audio, TrackKind.Video], pulled.Tracks.Select(t => t.Kind));
        var stored = await x.CallAsync(call.Id);
        var adminSession = stored.Participants.Single(p => p.UserId == x.AdminId).CfSessionId!;
        var bobPullSession = stored.Participants.Single(p => p.UserId == x.Bob.Id).CfPullSessionId!;
        var cfPull = x.App.Realtime.CallsTo($"/sessions/{bobPullSession}/tracks/new").Single();
        Assert.Null(cfPull.Body!["sessionDescription"]);
        Assert.Equal("remote", cfPull.Body["tracks"]![0]!["location"]!.GetValue<string>());
        Assert.Equal(adminSession, cfPull.Body["tracks"]![0]!["sessionId"]!.GetValue<string>());
        Assert.NotEqual(adminSession, bobPullSession);

        // 浏览器的 answer 回给 Cloudflare 才算完成
        Assert.True(x.App.Realtime.AwaitingAnswer(bobPullSession));
        var reneg = await x.BobClient.PostJsonAsync($"/api/calls/{call.Id}/renegotiate", new RenegotiateApiRequest(Answer));
        Assert.Equal(HttpStatusCode.NoContent, reneg.StatusCode);
        Assert.False(x.App.Realtime.AwaitingAnswer(bobPullSession));
        Assert.Equal("answer", x.App.Realtime.CallsTo("/renegotiate").Single().Body!["sessionDescription"]!["type"]!.GetValue<string>());

        // 发布视频 = 开了摄像头，单独广播一次（加入快照里可能还是旧状态）
        var camOn = await adminHub.WaitAsync<ParticipantEventDto>("ParticipantUpdated");
        Assert.Equal((adminJoin.ParticipantId, true), (camOn.Participant.Id, camOn.Participant.CamOn));
        // 麦克风状态广播
        await x.BobClient.PostJsonAsync($"/api/calls/{call.Id}/state", new ParticipantStateRequest(false, false));
        var updated = await adminHub.WaitAsync<ParticipantEventDto>("ParticipantUpdated", 2);
        Assert.Equal((bobJoin.ParticipantId, false), (updated.Participant.Id, updated.Participant.MicOn));

        // 一方挂断：单聊直接结束，Cloudflare 侧强制关闭发起人的轨道
        Assert.Equal(HttpStatusCode.NoContent, (await x.BobClient.PostAsync($"/api/calls/{call.Id}/leave", null)).StatusCode);
        var ended = await adminHub.WaitAsync<CallDto>("CallUpdated", 3);
        Assert.Equal(CallEndReason.Completed, ended.EndReason);
        Assert.NotNull(ended.EndedAt);
        Assert.Empty(ended.Participants);
        Assert.Equal([x.AdminId, x.Bob.Id], ended.JoinedUserIds);
        // 关闭在后台进行（不在通话锁里等 Cloudflare）
        await EventuallyAsync(() => x.App.Realtime.ClosedMids(adminSession).Count == 2 && x.App.Realtime.ClosedMids(bobPullSession).Count == 2, "Cloudflare 关闭轨道");
        Assert.Equal(["0", "1"], x.App.Realtime.ClosedMids(adminSession).Order());
        var close = x.App.Realtime.CallsTo($"/sessions/{adminSession}/tracks/close").Single();
        Assert.True(close.Body!["force"]!.GetValue<bool>());
        // 离开的人订阅会话里拉着的轨道也关掉：Cloudflare 不会因为状态里删了就停止转发
        Assert.Equal(["r0", "r1"], x.App.Realtime.ClosedMids(bobPullSession).Order());

        // 通话记录消息：双方都收到，载荷可解析，参加了的人不算未读
        var message = await bobHub.WaitAsync<MessageDto>("MessageReceived");
        Assert.Equal((MessageType.Call, x.AdminId, call.Id), (message.Type, message.SenderId, message.ClientMessageId));
        Assert.StartsWith("视频通话 00:0", message.Body);
        var payload = JsonSerializer.Deserialize<CallMessagePayload>(message.Payload!, ApiFactory.Json)!;
        Assert.Equal((call.Id, CallType.Video, CallEndReason.Completed), (payload.CallId, payload.CallType, payload.Reason));
        Assert.Equal([x.AdminId, x.Bob.Id], payload.ParticipantIds);
        await adminHub.WaitAsync("MessageReceived");
        Assert.Equal(message.Id, Assert.Single(await MessagesAsync(x.Admin, conv)).Id);
        var convs = await (await x.BobClient.GetAsync("/api/conversations")).ReadAsync<List<ConversationDto>>();
        Assert.Equal((0L, "[通话]"), (convs.Single().Unread, convs.Single().LastMessagePreview));

        // 结束后：不能再加入，心跳 410，记录里有它，可以再打一通
        Assert.Equal(HttpStatusCode.Gone, (await x.Admin.PostAsync($"/api/calls/{call.Id}/join", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Gone, (await x.Admin.PostAsync($"/api/calls/{call.Id}/heartbeat", null)).StatusCode);
        Assert.Empty(await (await x.Admin.GetAsync("/api/calls/active")).ReadAsync<List<CallDto>>());
        var history = await (await x.BobClient.GetAsync($"/api/calls?conversationId={conv}")).ReadAsync<List<CallDto>>();
        Assert.Equal(call.Id, Assert.Single(history).Id);
        Assert.NotEqual(call.Id, (await StartAsync(x.BobClient, conv, CallType.Audio)).Id);
    }

    [PgFact]
    public async Task Declining_a_direct_call_ends_it_without_a_missed_call_push()
    {
        await using var x = await SetupAsync();
        var conv = await ConversationAsync(x.Admin, ConversationType.Direct, x.Bob.Id);
        await x.App.AddPushSubscriptionAsync(x.Bob.Id, "bob-phone");
        await using var adminHub = await x.App.ConnectHubAsync("admin@example.com");
        var call = await StartAsync(x.Admin, conv, CallType.Audio);
        await JoinAsync(x.Admin, call.Id);
        await EventuallyAsync(() => x.App.Push.Requests.Count == 1, "来电推送");

        // 发起人自己"拒接"没有意义
        await x.Admin.PostAsync($"/api/calls/{call.Id}/decline", null);
        Assert.Null((await GetAsync(x.Admin, call.Id)).EndedAt);

        Assert.Equal(HttpStatusCode.NoContent, (await x.BobClient.PostAsync($"/api/calls/{call.Id}/decline", null)).StatusCode);
        var ended = await adminHub.WaitAsync<CallDto>("CallUpdated", 2);
        Assert.Equal(CallEndReason.Declined, ended.EndReason);
        var message = Assert.Single(await MessagesAsync(x.BobClient, conv));
        Assert.Equal("已拒绝的语音通话", message.Body);
        // 重复拒接无害
        Assert.Equal(HttpStatusCode.NoContent, (await x.BobClient.PostAsync($"/api/calls/{call.Id}/decline", null)).StatusCode);
        Assert.Single(await MessagesAsync(x.BobClient, conv));
        // 拒接的人其它设备上还挂着响铃通知：补一条同 tag 的短时推送把它收掉（不是"未接来电"，后者 TTL 是一天）
        await EventuallyAsync(() => x.App.Push.Requests.Count == 2, "收掉响铃的推送");
        await Task.Delay(200);
        Assert.Equal(2, x.App.Push.Requests.Count);
        Assert.Equal(CallPush.RingTtlSeconds.ToString(), x.App.Push.Requests.Last().Ttl);
    }

    [PgFact]
    public async Task Unanswered_call_times_out_as_missed_and_pushes_a_missed_call()
    {
        await using var x = await SetupAsync();
        var conv = await ConversationAsync(x.Admin, ConversationType.Direct, x.Bob.Id);
        var bobPush = await x.App.AddPushSubscriptionAsync(x.Bob.Id, "bob-phone");
        await using var bobHub = await x.App.ConnectHubAsync("bob");
        var call = await StartAsync(x.Admin, conv);
        await JoinAsync(x.Admin, call.Id);
        await PublishAsync(x.Admin, call.Id, ("0", TrackKind.Audio));

        // 还没到 60 秒：不动
        Assert.Equal(0, await x.SweepAsync(TimeSpan.FromSeconds(30)));
        Assert.Null((await GetAsync(x.Admin, call.Id)).EndedAt);

        Assert.Equal(1, await x.SweepAsync(TimeSpan.FromSeconds(61)));
        var ended = await bobHub.WaitAsync<CallDto>("CallUpdated");
        Assert.Equal(CallEndReason.Missed, ended.EndReason);
        Assert.Null(ended.AnsweredAt);
        var message = await bobHub.WaitAsync<MessageDto>("MessageReceived");
        Assert.Equal("未接听的视频通话", message.Body);
        // 没接的人这条算未读
        var convs = await (await x.BobClient.GetAsync("/api/conversations")).ReadAsync<List<ConversationDto>>();
        Assert.Equal(1, convs.Single().Unread);

        // 响铃推送 + 未接来电推送（后者保留 24 小时），同一个 Topic 让推送服务替换掉还没送达的响铃
        await EventuallyAsync(() => x.App.Push.Requests.Count == 2, "未接来电推送");
        var pushes = x.App.Push.Requests.ToList();
        Assert.All(pushes, p => Assert.Equal(bobPush, p.Endpoint));
        Assert.Equal([CallPush.RingTtlSeconds.ToString(), "86400"], pushes.Select(p => p.Ttl));
        Assert.Equal(pushes[0].Topic, pushes[1].Topic);

        // 已结束的通话不会再被清理一次
        Assert.Equal(0, await x.SweepAsync(TimeSpan.FromSeconds(120)));
        Assert.Single(await MessagesAsync(x.Admin, conv));
    }

    [PgFact]
    public async Task Caller_hanging_up_before_answer_cancels_the_call()
    {
        await using var x = await SetupAsync();
        var conv = await ConversationAsync(x.Admin, ConversationType.Direct, x.Bob.Id);
        await x.App.AddPushSubscriptionAsync(x.Bob.Id, "bob-phone");
        var call = await StartAsync(x.Admin, conv, CallType.Audio);
        await JoinAsync(x.Admin, call.Id);
        await x.Admin.PostAsync($"/api/calls/{call.Id}/leave", null);

        var ended = await GetAsync(x.BobClient, call.Id);
        Assert.Equal(CallEndReason.Cancelled, ended.EndReason);
        Assert.Equal("已取消的语音通话", Assert.Single(await MessagesAsync(x.BobClient, conv)).Body);
        await EventuallyAsync(() => x.App.Push.Requests.Count == 2, "未接来电推送");
        // 对方这时再点接听：已经结束
        Assert.Equal(HttpStatusCode.Gone, (await x.BobClient.PostAsync($"/api/calls/{call.Id}/join", null)).StatusCode);
    }

    [PgFact]
    public async Task Group_call_survives_declines_and_leaves_until_the_last_one_leaves()
    {
        await using var x = await SetupAsync();
        var conv = await ConversationAsync(x.Admin, ConversationType.Group, x.Bob.Id, x.Carol.Id);
        await using var adminHub = await x.App.ConnectHubAsync("admin@example.com");
        await using var carolHub = await x.App.ConnectHubAsync("carol");

        var call = await StartAsync(x.Admin, conv);
        await JoinAsync(x.Admin, call.Id);
        await carolHub.WaitAsync("IncomingCall");

        // 群里有人拒接：只是告知，通话继续
        await x.BobClient.PostAsync($"/api/calls/{call.Id}/decline", null);
        var declined = await adminHub.WaitAsync<CallDeclinedDto>("CallDeclined");
        Assert.Equal((call.Id, x.Bob.Id), (declined.CallId, declined.UserId));
        Assert.Null((await GetAsync(x.Admin, call.Id)).EndedAt);

        // 孩子账号也能接
        var carolJoin = await JoinAsync(x.CarolClient, call.Id);
        Assert.NotNull(carolJoin.Call.AnsweredAt);

        // 发起人离开：群聊通话还在，剩下的人继续；拒接过的人之后还能进来
        await x.Admin.PostAsync($"/api/calls/{call.Id}/leave", null);
        var left = await carolHub.WaitAsync<ParticipantLeftDto>("ParticipantLeft");
        Assert.Equal((x.AdminId, "left"), (left.UserId, left.Reason));
        Assert.Null((await GetAsync(x.Admin, call.Id)).EndedAt);
        await JoinAsync(x.BobClient, call.Id);

        // 只有发起人或管理员能结束整通通话
        Assert.Equal(HttpStatusCode.Forbidden, (await x.BobClient.PostAsync($"/api/calls/{call.Id}/end", null)).StatusCode);

        await x.CarolClient.PostAsync($"/api/calls/{call.Id}/leave", null);
        Assert.Null((await GetAsync(x.Admin, call.Id)).EndedAt);
        await x.BobClient.PostAsync($"/api/calls/{call.Id}/leave", null);
        var ended = await GetAsync(x.Admin, call.Id);
        Assert.Equal(CallEndReason.Completed, ended.EndReason);
        Assert.Equal([x.AdminId, x.Carol.Id, x.Bob.Id], ended.JoinedUserIds);
        Assert.Single(await MessagesAsync(x.CarolClient, conv));
    }

    [PgFact]
    public async Task Starter_or_admin_can_end_a_call_for_everyone()
    {
        await using var x = await SetupAsync();
        var conv = await ConversationAsync(x.BobClient, ConversationType.Group, x.AdminId, x.Carol.Id);
        var call = await StartAsync(x.BobClient, conv);
        await JoinAsync(x.BobClient, call.Id);
        await JoinAsync(x.CarolClient, call.Id);
        await PublishAsync(x.CarolClient, call.Id, ("0", TrackKind.Audio));

        Assert.Equal(HttpStatusCode.Forbidden, (await x.CarolClient.PostAsync($"/api/calls/{call.Id}/end", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await x.Admin.PostAsync($"/api/calls/{call.Id}/end", null)).StatusCode);
        var stored = await x.CallAsync(call.Id);
        Assert.Equal(CallEndReason.Completed, stored.EndReason);
        Assert.All(stored.Participants, p => Assert.NotNull(p.LeftAt));
        Assert.All(stored.Participants.SelectMany(p => p.Tracks), t => Assert.NotNull(t.UnpublishedAt));
        // 被结束的人接着发布：410
        var res = await x.CarolClient.PostJsonAsync($"/api/calls/{call.Id}/publish", new PublishRequest(Offer, [new PublishTrackInput("1", TrackKind.Video)]));
        Assert.Equal(HttpStatusCode.Gone, res.StatusCode);
    }

    [PgFact]
    public async Task Participants_without_heartbeat_are_dropped_and_the_call_ends_when_nobody_is_left()
    {
        await using var x = await SetupAsync();
        var conv = await ConversationAsync(x.Admin, ConversationType.Group, x.Bob.Id, x.Carol.Id);
        await using var adminHub = await x.App.ConnectHubAsync("admin@example.com");
        var call = await StartAsync(x.Admin, conv);
        await JoinAsync(x.Admin, call.Id);
        var bobJoin = await JoinAsync(x.BobClient, call.Id);
        await PublishAsync(x.BobClient, call.Id, ("0", TrackKind.Audio));

        // 心跳正常：不清理
        Assert.Equal(HttpStatusCode.NoContent, (await x.BobClient.PostAsync($"/api/calls/{call.Id}/heartbeat", null)).StatusCode);
        Assert.Equal(0, await x.SweepAsync(TimeSpan.FromSeconds(20)));

        // Bob 掉线（没有心跳），管理员一直在心跳
        await x.App.WithDbAsync(db => db.CallParticipants.Where(p => p.UserId == x.AdminId).ExecuteUpdateAsync(s => s.SetProperty(p => p.LastSeenAt, DateTimeOffset.UtcNow.AddSeconds(30))));
        Assert.Equal(1, await x.SweepAsync(TimeSpan.FromSeconds(40)));
        var left = await adminHub.WaitAsync<ParticipantLeftDto>("ParticipantLeft");
        Assert.Equal((bobJoin.ParticipantId, "timeout"), (left.ParticipantId, left.Reason));
        var stored = await x.CallAsync(call.Id);
        Assert.Null(stored.EndedAt);
        Assert.NotNull(stored.Participants.Single(p => p.UserId == x.Bob.Id).Tracks.Single().UnpublishedAt);
        Assert.Equal(HttpStatusCode.Gone, (await x.BobClient.PostAsync($"/api/calls/{call.Id}/heartbeat", null)).StatusCode);

        // 最后一个人也掉线：通话结束
        Assert.Equal(1, await x.SweepAsync(TimeSpan.FromSeconds(120)));
        Assert.Equal(CallEndReason.Completed, (await GetAsync(x.Admin, call.Id)).EndReason);
    }

    [PgFact]
    public async Task Joining_again_replaces_the_previous_participant_of_the_same_user()
    {
        await using var x = await SetupAsync();
        var conv = await ConversationAsync(x.Admin, ConversationType.Direct, x.Bob.Id);
        await using var bobHub = await x.App.ConnectHubAsync("bob");
        var call = await StartAsync(x.Admin, conv);
        var first = await JoinAsync(x.Admin, call.Id);
        await JoinAsync(x.BobClient, call.Id);
        await PublishAsync(x.Admin, call.Id, ("0", TrackKind.Audio));
        var firstSession = (await x.CallAsync(call.Id)).Participants.Single(p => p.Id == first.ParticipantId).CfSessionId!;

        // 断网重进 / 换设备接听：旧的那一份被顶掉，通话不结束
        var second = await JoinAsync(x.Admin, call.Id);
        Assert.NotEqual(first.ParticipantId, second.ParticipantId);
        var left = await bobHub.WaitAsync<ParticipantLeftDto>("ParticipantLeft");
        Assert.Equal((first.ParticipantId, "replaced"), (left.ParticipantId, left.Reason));
        var after = await GetAsync(x.BobClient, call.Id);
        Assert.Null(after.EndedAt);
        Assert.Equal(2, after.Participants.Count);
        Assert.Empty(after.Participants.Single(p => p.Id == second.ParticipantId).Tracks);
        await EventuallyAsync(() => x.App.Realtime.ClosedMids(firstSession).Count == 1, "关闭被顶掉的那份的轨道");
        Assert.Equal(["0"], x.App.Realtime.ClosedMids(firstSession));

        // 新的一份重新发布：换了新的 Cloudflare 会话
        await PublishAsync(x.Admin, call.Id, ("0", TrackKind.Audio));
        var secondSession = (await x.CallAsync(call.Id)).Participants.Single(p => p.Id == second.ParticipantId).CfSessionId!;
        Assert.NotEqual(firstSession, secondSession);
    }

    private static async Task<string?> ProblemDetailAsync(HttpResponseMessage res)
        => (await res.Content.ReadFromJsonAsync<JsonElement>()).TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;

    [PgFact]
    public async Task A_replaced_device_cannot_take_the_call_back_end_it_or_touch_the_new_devices_session()
    {
        await using var x = await SetupAsync();
        var conv = await ConversationAsync(x.Admin, ConversationType.Direct, x.Bob.Id);
        var call = await StartAsync(x.Admin, conv);
        var laptop = await JoinAsync(x.Admin, call.Id);
        await JoinAsync(x.BobClient, call.Id);
        // 换到手机上接着打：笔记本那一份被顶掉（它睡着了，没收到通知）
        var phone = await JoinAsync(x.Admin, call.Id);

        // 笔记本醒来：心跳 410 replaced（不是"被清理"，客户端据此收场而不是重新加入）
        var hb = await x.Admin.PostAsync($"/api/calls/{call.Id}/heartbeat?participantId={laptop.ParticipantId}", null);
        Assert.Equal((HttpStatusCode.Gone, "replaced"), (hb.StatusCode, await ProblemDetailAsync(hb)));
        // 重连带上原来的身份：不会反过来把手机顶掉
        var rejoin = await x.Admin.PostAsync($"/api/calls/{call.Id}/join?replacing={laptop.ParticipantId}", null);
        Assert.Equal((HttpStatusCode.Gone, "replaced"), (rejoin.StatusCode, await ProblemDetailAsync(rejoin)));
        // 迟到的发布 / 订阅请求不会落到手机的会话上
        var publish = await x.Admin.PostJsonAsync($"/api/calls/{call.Id}/publish?participantId={laptop.ParticipantId}",
            new PublishRequest(Offer, [new PublishTrackInput("0", TrackKind.Audio)]));
        Assert.Equal(HttpStatusCode.Gone, publish.StatusCode);
        // 关笔记本页面发来的离开只离开笔记本：单聊不会因此结束，手机还在
        Assert.Equal(HttpStatusCode.NoContent, (await x.Admin.PostAsync($"/api/calls/{call.Id}/leave?participantId={laptop.ParticipantId}&via=unload", null)).StatusCode);
        var after = await GetAsync(x.BobClient, call.Id);
        Assert.Null(after.EndedAt);
        Assert.Contains(after.Participants, p => p.Id == phone.ParticipantId);
        Assert.Equal(HttpStatusCode.NoContent, (await x.Admin.PostAsync($"/api/calls/{call.Id}/heartbeat?participantId={phone.ParticipantId}", null)).StatusCode);

        // 只有一台设备、因为掉线被清理：410 但不是 replaced（客户端重新加入）
        await x.App.WithDbAsync(db => db.CallParticipants.Where(p => p.Id == phone.ParticipantId).ExecuteUpdateAsync(s => s.SetProperty(p => p.LeftAt, DateTimeOffset.UtcNow)));
        hb = await x.Admin.PostAsync($"/api/calls/{call.Id}/heartbeat?participantId={phone.ParticipantId}", null);
        Assert.Equal(HttpStatusCode.Gone, hb.StatusCode);
        Assert.NotEqual("replaced", await ProblemDetailAsync(hb));
    }

    [PgFact]
    public async Task A_device_replaced_by_another_that_already_hung_up_does_not_rejoin_by_itself()
    {
        await using var x = await SetupAsync();
        var group = await ConversationAsync(x.Admin, ConversationType.Group, x.Bob.Id, x.Carol.Id);
        var call = await StartAsync(x.Admin, group);
        var phone = await JoinAsync(x.Admin, call.Id);
        await JoinAsync(x.BobClient, call.Id);
        await JoinAsync(x.CarolClient, call.Id);
        // 手机睡着了；在笔记本上进来（顶掉手机），聊完在笔记本上挂了，群通话继续
        var laptop = await JoinAsync(x.Admin, call.Id);
        await x.Admin.PostAsync($"/api/calls/{call.Id}/leave?participantId={laptop.ParticipantId}", null);
        Assert.Null((await x.CallAsync(call.Id)).EndedAt);

        // 手机醒来：心跳、重连都得到 replaced，不会带着麦克风回到通话里
        var hb = await x.Admin.PostAsync($"/api/calls/{call.Id}/heartbeat?participantId={phone.ParticipantId}", null);
        Assert.Equal((HttpStatusCode.Gone, "replaced"), (hb.StatusCode, await ProblemDetailAsync(hb)));
        var rejoin = await x.Admin.PostAsync($"/api/calls/{call.Id}/join?replacing={phone.ParticipantId}", null);
        Assert.Equal((HttpStatusCode.Gone, "replaced"), (rejoin.StatusCode, await ProblemDetailAsync(rejoin)));
        Assert.DoesNotContain((await GetAsync(x.BobClient, call.Id)).Participants, p => p.UserId == x.AdminId);
    }

    [PgFact]
    public async Task Rejoin_after_a_timeout_or_a_lost_join_response_still_works()
    {
        await using var x = await SetupAsync();
        var group = await ConversationAsync(x.Admin, ConversationType.Group, x.Bob.Id, x.Carol.Id);
        var call = await StartAsync(x.Admin, group);
        var first = await JoinAsync(x.Admin, call.Id);
        await JoinAsync(x.BobClient, call.Id);

        // 只有这一台设备，因为掉线被清理：带着原来的身份重新加入可以
        await x.App.WithDbAsync(db => db.CallParticipants.Where(p => p.Id == first.ParticipantId).ExecuteUpdateAsync(s => s.SetProperty(p => p.LeftAt, DateTimeOffset.UtcNow)));
        var hb = await x.Admin.PostAsync($"/api/calls/{call.Id}/heartbeat?participantId={first.ParticipantId}", null);
        Assert.Equal(HttpStatusCode.Gone, hb.StatusCode);
        Assert.NotEqual("replaced", await ProblemDetailAsync(hb));
        var second = await (await x.Admin.PostAsync($"/api/calls/{call.Id}/join?replacing={first.ParticipantId}", null)).ReadAsync<JoinResponse>();

        // 重连的响应丢了，客户端拿同一个旧身份再试一次：认出是自己上次那份，顶掉它，而不是说"在别的设备上"
        var third = await x.Admin.PostAsync($"/api/calls/{call.Id}/join?replacing={first.ParticipantId}", null);
        Assert.Equal(HttpStatusCode.OK, third.StatusCode);
        var stored = await x.CallAsync(call.Id);
        Assert.NotNull(stored.Participants.Single(p => p.Id == second.ParticipantId).LeftAt);
        Assert.Single(stored.Participants, p => p.UserId == x.AdminId && p.LeftAt == null);
    }

    [PgFact]
    public async Task Cancelling_a_call_the_starter_could_not_join_never_ends_a_call_others_answered()
    {
        await using var x = await SetupAsync();
        var group = await ConversationAsync(x.Admin, ConversationType.Group, x.Bob.Id, x.Carol.Id);
        var call = await StartAsync(x.Admin, group);
        // 发起人还在等麦克风权限，家人们先接起来聊上了
        await JoinAsync(x.BobClient, call.Id);
        await JoinAsync(x.CarolClient, call.Id);
        Assert.Equal(HttpStatusCode.NoContent, (await x.Admin.PostAsync($"/api/calls/{call.Id}/end?ifUnanswered=true", null)).StatusCode);
        Assert.Null((await x.CallAsync(call.Id)).EndedAt);

        // 没人接的：照常取消
        var conv = await ConversationAsync(x.Admin, ConversationType.Direct, x.Bob.Id);
        var lonely = await StartAsync(x.Admin, conv);
        Assert.Equal(HttpStatusCode.NoContent, (await x.Admin.PostAsync($"/api/calls/{lonely.Id}/end?ifUnanswered=true", null)).StatusCode);
        Assert.Equal(CallEndReason.Cancelled, (await x.CallAsync(lonely.Id)).EndReason);
    }

    [PgFact]
    public async Task Joining_a_group_call_late_still_clears_the_ring_on_other_devices()
    {
        await using var x = await SetupAsync();
        var group = await ConversationAsync(x.Admin, ConversationType.Group, x.Bob.Id, x.Carol.Id);
        await x.App.AddPushSubscriptionAsync(x.Carol.Id, "carol-phone");
        var call = await StartAsync(x.Admin, group);
        await JoinAsync(x.Admin, call.Id);
        await JoinAsync(x.BobClient, call.Id);
        await EventuallyAsync(() => x.App.Push.Requests.Count(r => r.Endpoint.EndsWith("/carol-phone")) == 1, "Carol 的响铃推送");
        // 两分钟后 Carol 在笔记本上加入：手机上还挂着的响铃通知要收掉
        await x.App.WithDbAsync(db => db.CallSessions.Where(c => c.Id == call.Id).ExecuteUpdateAsync(s => s.SetProperty(c => c.StartedAt, DateTimeOffset.UtcNow.AddMinutes(-2))));
        await JoinAsync(x.CarolClient, call.Id);
        await EventuallyAsync(() => x.App.Push.Requests.Count(r => r.Endpoint.EndsWith("/carol-phone")) == 2, "收掉 Carol 手机上的响铃");
    }

    [PgFact]
    public async Task Sweep_right_after_an_outage_does_not_drop_participants_whose_heartbeats_could_not_arrive()
    {
        await using var x = await SetupAsync();
        var conv = await ConversationAsync(x.Admin, ConversationType.Direct, x.Bob.Id);
        var call = await StartAsync(x.Admin, conv);
        await JoinAsync(x.Admin, call.Id);
        await JoinAsync(x.BobClient, call.Id);
        var later = DateTimeOffset.UtcNow.AddMinutes(2);

        using (var scope = x.App.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<CallService>();
            // api 刚恢复 5 秒：心跳在停机期间本来就送不进来，不能据此判掉线
            Assert.Equal(0, await service.SweepAsync(later, default, healthySince: later.AddSeconds(-5)));
            Assert.Null((await x.CallAsync(call.Id)).EndedAt);
            // 恢复已经超过判定时长仍没有心跳：才算真的掉线
            Assert.Equal(1, await service.SweepAsync(later, default, healthySince: later.AddMinutes(-1)));
        }
        Assert.NotNull((await x.CallAsync(call.Id)).EndedAt);
    }

    [PgFact]
    public async Task Start_reports_whether_it_created_the_call()
    {
        await using var x = await SetupAsync();
        var conv = await ConversationAsync(x.Admin, ConversationType.Direct, x.Bob.Id);
        var first = await x.Admin.PostJsonAsync("/api/calls", new StartCallRequest(conv, CallType.Audio));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var id = (await first.ReadAsync<CallDto>()).Id;
        var again = await x.BobClient.PostJsonAsync("/api/calls", new StartCallRequest(conv, CallType.Audio));
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(id, (await again.ReadAsync<CallDto>()).Id);

        // 发起人自己没能进去（没有麦克风）：客户端用 end 取消，记为已取消，对方不再空响
        Assert.Equal(HttpStatusCode.NoContent, (await x.Admin.PostAsync($"/api/calls/{id}/end", null)).StatusCode);
        Assert.Equal(CallEndReason.Cancelled, (await x.CallAsync(id)).EndReason);
    }

    [PgFact]
    public async Task Ring_notifications_are_withdrawn_when_a_group_call_ends_or_is_declined()
    {
        await using var x = await SetupAsync();
        var group = await ConversationAsync(x.Admin, ConversationType.Group, x.Bob.Id, x.Carol.Id);
        await x.App.AddPushSubscriptionAsync(x.Bob.Id, "bob-phone");
        await x.App.AddPushSubscriptionAsync(x.Carol.Id, "carol-phone");
        var call = await StartAsync(x.Admin, group);
        await JoinAsync(x.Admin, call.Id);
        await EventuallyAsync(() => x.App.Push.Requests.Count == 2, "两人的响铃推送");

        // Carol 在笔记本上拒接：她手机上还在响的通知收掉
        await x.CarolClient.PostAsync($"/api/calls/{call.Id}/decline", null);
        await EventuallyAsync(() => x.App.Push.Requests.Count(r => r.Endpoint.EndsWith("/carol-phone")) == 2, "收掉 Carol 的响铃");

        // Bob 接了又挂：通话正常结束；没进来的 Carol 再收到一条收尾（同 tag，短 TTL），不是"未接来电"
        await JoinAsync(x.BobClient, call.Id);
        await x.BobClient.PostAsync($"/api/calls/{call.Id}/leave", null);
        await x.Admin.PostAsync($"/api/calls/{call.Id}/leave", null);
        Assert.Equal(CallEndReason.Completed, (await x.CallAsync(call.Id)).EndReason);
        await EventuallyAsync(() => x.App.Push.Requests.Count(r => r.Endpoint.EndsWith("/carol-phone")) == 3, "通话结束时收掉 Carol 的响铃");
        Assert.All(x.App.Push.Requests.Where(r => r.Endpoint.EndsWith("/carol-phone")).Skip(1), r => Assert.Equal(CallPush.RingTtlSeconds.ToString(), r.Ttl));
    }

    [PgFact]
    public async Task Answering_on_one_device_only_tells_other_devices_when_they_actually_rang()
    {
        await using var x = await SetupAsync();
        var conv = await ConversationAsync(x.Admin, ConversationType.Direct, x.Bob.Id);
        await x.App.AddPushSubscriptionAsync(x.Bob.Id, "bob-phone");
        // Bob 静音了这个会话：不响铃，接听时也不必去收响铃通知
        await x.App.WithDbAsync(db => db.ConversationMembers.Where(m => m.ConversationId == conv && m.UserId == x.Bob.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.MutedUntil, DateTimeOffset.UtcNow.AddDays(1))));
        var call = await StartAsync(x.Admin, conv);
        await JoinAsync(x.Admin, call.Id);
        await JoinAsync(x.BobClient, call.Id);
        await Task.Delay(300);
        Assert.Empty(x.App.Push.Requests);
    }

    [PgFact]
    public async Task Leaving_and_joining_do_not_wait_for_cloudflare_to_close_tracks()
    {
        await using var x = await SetupAsync();
        var room = (await (await x.Admin.GetAsync("/api/calls/rooms")).ReadAsync<List<CallDto>>()).Single();
        var admin = await JoinAsync(x.Admin, room.Id);
        var bob = await JoinAsync(x.BobClient, room.Id);
        await PublishAsync(x.Admin, room.Id, ("0", TrackKind.Audio), ("1", TrackKind.Video));
        await PublishAsync(x.BobClient, room.Id, ("0", TrackKind.Audio));
        var adminSession = (await x.CallAsync(room.Id)).Participants.Single(p => p.Id == admin.ParticipantId).CfSessionId!;

        // Cloudflare 关闭轨道卡住了：离开、别人的离开和加入都不能被它拖住
        x.App.Realtime.ForcedCloseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(HttpStatusCode.NoContent, (await x.Admin.PostAsync($"/api/calls/{room.Id}/leave?participantId={admin.ParticipantId}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await x.BobClient.PostAsync($"/api/calls/{room.Id}/leave?participantId={bob.ParticipantId}", null)).StatusCode);
        await JoinAsync(x.CarolClient, room.Id);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"离开 / 加入用了 {watch.ElapsedMilliseconds} ms");
        var inRoom = (await x.CallAsync(room.Id)).Participants.Where(p => p.LeftAt == null).Select(p => p.UserId).ToList();
        Assert.Equal([x.Carol.Id], inRoom);

        // 关闭请求已经发出去了，只是还没回来；放行后最终关掉
        await EventuallyAsync(() => x.App.Realtime.CallsTo($"/sessions/{adminSession}/tracks/close").Count == 1, "关闭请求已发出");
        Assert.Empty(x.App.Realtime.ClosedMids(adminSession));
        x.App.Realtime.ForcedCloseGate.SetResult();
        await EventuallyAsync(() => x.App.Realtime.ClosedMids(adminSession).Count == 2, "Cloudflare 最终关闭了轨道");
    }

    [PgFact]
    public async Task Sweep_prunes_old_living_room_participants()
    {
        await using var x = await SetupAsync();
        var room = (await (await x.Admin.GetAsync("/api/calls/rooms")).ReadAsync<List<CallDto>>()).Single();
        var old = await JoinAsync(x.Admin, room.Id);
        await x.Admin.PostAsync($"/api/calls/{room.Id}/leave?participantId={old.ParticipantId}", null);
        var recent = await JoinAsync(x.Admin, room.Id);
        await x.Admin.PostAsync($"/api/calls/{room.Id}/leave?participantId={recent.ParticipantId}", null);
        await x.App.WithDbAsync(db => db.CallParticipants.Where(p => p.Id == old.ParticipantId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.LeftAt, DateTimeOffset.UtcNow.AddHours(-2))));
        await x.SweepAsync(TimeSpan.Zero);
        var ids = await x.App.WithDbAsync(db => db.CallParticipants.Where(p => p.CallId == room.Id).Select(p => p.Id).ToListAsync());
        Assert.Equal([recent.ParticipantId], ids);
    }

    [PgFact]
    public async Task Only_conversation_members_can_see_join_or_pull_from_a_call()
    {
        await using var x = await SetupAsync();
        var conv = await ConversationAsync(x.Admin, ConversationType.Direct, x.Bob.Id);
        var call = await StartAsync(x.Admin, conv);
        var adminJoin = await JoinAsync(x.Admin, call.Id);
        var published = await PublishAsync(x.Admin, call.Id, ("0", TrackKind.Audio));

        // 不是会话成员：看不到、进不去、不能发起、不能拒接
        Assert.Equal(HttpStatusCode.Forbidden, (await x.CarolClient.GetAsync($"/api/calls/{call.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await x.CarolClient.PostAsync($"/api/calls/{call.Id}/join", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await x.CarolClient.PostAsync($"/api/calls/{call.Id}/decline", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await x.CarolClient.PostJsonAsync("/api/calls", new StartCallRequest(conv, CallType.Audio))).StatusCode);
        Assert.Empty(await (await x.CarolClient.GetAsync("/api/calls/active")).ReadAsync<List<CallDto>>());
        Assert.Empty(await (await x.CarolClient.GetAsync("/api/calls")).ReadAsync<List<CallDto>>());
        Assert.Equal(HttpStatusCode.Unauthorized, (await x.App.Anonymous().GetAsync("/api/calls/active")).StatusCode);

        // 在另一通通话里的人拉不到这通通话的轨道（参与者 Id、轨道名都对也不行）
        var other = await ConversationAsync(x.CarolClient, ConversationType.Direct, x.Bob.Id);
        var otherCall = await StartAsync(x.CarolClient, other);
        await JoinAsync(x.CarolClient, otherCall.Id);
        var steal = new PullRequest([new PullTrackInput(adminJoin.ParticipantId, published.Tracks[0].TrackName)]);
        var pulled = await (await x.CarolClient.PostJsonAsync($"/api/calls/{otherCall.Id}/pull", steal)).ReadAsync<PullResponse>();
        Assert.Equal(("not_found", (string?)null), (pulled.Tracks.Single().ErrorCode, pulled.Tracks.Single().Mid));
        Assert.Null(pulled.Offer);
        Assert.Single(x.App.Realtime.CallsTo("/tracks/new"));

        // 没加入就发布 / 订阅 / 心跳：410
        Assert.Equal(HttpStatusCode.Gone, (await x.BobClient.PostJsonAsync($"/api/calls/{call.Id}/pull", steal)).StatusCode);
        Assert.Equal(HttpStatusCode.Gone, (await x.BobClient.PostJsonAsync($"/api/calls/{call.Id}/publish", new PublishRequest(Offer, [new PublishTrackInput("0", TrackKind.Audio)]))).StatusCode);
        // 自己的轨道也拉不到（不回放自己的声音）
        var self = await (await x.Admin.PostJsonAsync($"/api/calls/{call.Id}/pull", steal)).ReadAsync<PullResponse>();
        Assert.Equal("not_found", self.Tracks.Single().ErrorCode);
    }

    [PgFact]
    public async Task Publish_validation_and_unpublish_with_negotiation()
    {
        await using var x = await SetupAsync();
        var conv = await ConversationAsync(x.Admin, ConversationType.Direct, x.Bob.Id);
        await using var bobHub = await x.App.ConnectHubAsync("bob");
        var call = await StartAsync(x.Admin, conv);
        await JoinAsync(x.Admin, call.Id);
        await JoinAsync(x.BobClient, call.Id);

        async Task<HttpStatusCode> Try(object body) => (await x.Admin.PostJsonAsync($"/api/calls/{call.Id}/publish", body)).StatusCode;
        Assert.Equal(HttpStatusCode.BadRequest, await Try(new { offer = Answer, tracks = new[] { new { mid = "0", kind = "Audio" } } }));
        Assert.Equal(HttpStatusCode.BadRequest, await Try(new { offer = Offer, tracks = Array.Empty<object>() }));
        Assert.Equal(HttpStatusCode.BadRequest, await Try(new { offer = Offer, tracks = new[] { new { mid = "0", kind = "Audio" }, new { mid = "1", kind = "Audio" } } }));
        Assert.Equal(HttpStatusCode.BadRequest, await Try(new { offer = new { type = "offer", sdp = new string('x', 300_000) }, tracks = new[] { new { mid = "0", kind = "Audio" } } }));
        Assert.Empty(x.App.Realtime.CallsTo("/tracks/new"));

        await PublishAsync(x.Admin, call.Id, ("0", TrackKind.Audio), ("1", TrackKind.Video));
        // 同一种轨道不能发布两条
        Assert.Equal(HttpStatusCode.Conflict, await Try(new PublishRequest(Offer, [new PublishTrackInput("2", TrackKind.Video)])));
        // 共享屏幕是第三条轨道，在同一条发布会话上追加
        var screen = await PublishAsync(x.Admin, call.Id, ("2", TrackKind.Screen));
        var name = screen.Tracks.Single().TrackName;
        Assert.StartsWith("screen-", name);
        Assert.Single(x.App.Realtime.CallsTo("/sessions/new"));
        var e = await bobHub.WaitAsync<TrackEventDto>("TrackPublished", 3);
        Assert.Equal((name, TrackKind.Screen), (e.TrackName, e.Kind));

        // 停止共享：协商式关闭，answer 交回浏览器
        var un = await (await x.Admin.PostJsonAsync($"/api/calls/{call.Id}/unpublish", new UnpublishRequest([name], Offer))).ReadAsync<UnpublishResponse>();
        Assert.Equal("answer", un.Answer!.Type);
        var close = x.App.Realtime.CallsTo("/tracks/close").Single();
        Assert.False(close.Body!["force"]!.GetValue<bool>());
        Assert.Equal("2", close.Body["tracks"]![0]!["mid"]!.GetValue<string>());
        Assert.Equal("offer", close.Body["sessionDescription"]!["type"]!.GetValue<string>());
        var gone = await bobHub.WaitAsync<TrackEventDto>("TrackUnpublished");
        Assert.Equal(name, gone.TrackName);
        Assert.Equal(2, (await GetAsync(x.BobClient, call.Id)).Participants.Single(p => p.UserId == x.AdminId).Tracks.Count);

        // 已经关掉的再关一次：没有可关的，不打扰 Cloudflare
        var again = await (await x.Admin.PostJsonAsync($"/api/calls/{call.Id}/unpublish", new UnpublishRequest([name]))).ReadAsync<UnpublishResponse>();
        Assert.Null(again.Answer);
        Assert.Single(x.App.Realtime.CallsTo("/tracks/close"));
        // 关掉之后可以重新发布同一种
        await PublishAsync(x.Admin, call.Id, ("3", TrackKind.Screen));

        // 订阅方不再接收：按自己会话里的 mid 强制关闭
        var adminP = (await GetAsync(x.BobClient, call.Id)).Participants.Single(p => p.UserId == x.AdminId);
        var pulled = await (await x.BobClient.PostJsonAsync($"/api/calls/{call.Id}/pull", new PullRequest(adminP.Tracks.Select(t => new PullTrackInput(adminP.Id, t.TrackName)).ToList()))).ReadAsync<PullResponse>();
        await x.BobClient.PostJsonAsync($"/api/calls/{call.Id}/renegotiate", new RenegotiateApiRequest(Answer));
        await x.BobClient.PostJsonAsync($"/api/calls/{call.Id}/pull/close", new ClosePulledRequest([pulled.Tracks[0].Mid!]));
        var bobPull = (await x.CallAsync(call.Id)).Participants.Single(p => p.UserId == x.Bob.Id).CfPullSessionId!;
        Assert.Equal([pulled.Tracks[0].Mid!], x.App.Realtime.ClosedMids(bobPull));
    }

    [PgFact]
    public async Task Cloudflare_errors_map_to_gone_or_bad_gateway_and_partial_failures_are_reported()
    {
        await using var x = await SetupAsync();
        var conv = await ConversationAsync(x.Admin, ConversationType.Direct, x.Bob.Id);
        var call = await StartAsync(x.Admin, conv);
        await JoinAsync(x.Admin, call.Id);
        var bobJoin = await JoinAsync(x.BobClient, call.Id);
        var body = new PublishRequest(Offer, [new PublishTrackInput("0", TrackKind.Audio)]);

        // Cloudflare 暂时不可用：502，不记轨道
        x.App.Realtime.FailNext("/tracks/new", HttpStatusCode.ServiceUnavailable, "temporarily_unavailable_error");
        var res = await x.Admin.PostJsonAsync($"/api/calls/{call.Id}/publish", body);
        Assert.Equal(HttpStatusCode.BadGateway, res.StatusCode);
        Assert.Contains("temporarily_unavailable_error", await res.Content.ReadAsStringAsync());
        Assert.DoesNotContain(FakeRealtime.AppSecret, await res.Content.ReadAsStringAsync());
        Assert.Empty((await GetAsync(x.Admin, call.Id)).Participants.Single(p => p.UserId == x.AdminId).Tracks);

        // 会话过期：410，客户端重新加入换新会话
        x.App.Realtime.FailNext("/tracks/new", HttpStatusCode.Gone, "session_error");
        Assert.Equal(HttpStatusCode.Gone, (await x.Admin.PostJsonAsync($"/api/calls/{call.Id}/publish", body)).StatusCode);

        // HTTP 200 但单条轨道失败：answer 照样返回，失败的轨道不登记
        x.App.Realtime.NextTrackError = "internal_error";
        var partial = await (await x.Admin.PostJsonAsync($"/api/calls/{call.Id}/publish", body)).ReadAsync<PublishResponse>();
        Assert.Equal("answer", partial.Answer.Type);
        Assert.Empty(partial.Tracks);

        // 正常发布后，对方订阅时源还没出媒体：这一条带错误码、没有 mid，可以稍后重试
        var ok = await PublishAsync(x.Admin, call.Id, ("0", TrackKind.Audio));
        var adminP = (await GetAsync(x.BobClient, call.Id)).Participants.Single(p => p.UserId == x.AdminId);
        var pull = new PullRequest([new PullTrackInput(adminP.Id, ok.Tracks[0].TrackName)]);
        x.App.Realtime.NextTrackError = "empty_track_error";
        var empty = await (await x.BobClient.PostJsonAsync($"/api/calls/{call.Id}/pull", pull)).ReadAsync<PullResponse>();
        Assert.Equal(("empty_track_error", (string?)null, false), (empty.Tracks.Single().ErrorCode, empty.Tracks.Single().Mid, empty.RequiresImmediateRenegotiation));
        var retry = await (await x.BobClient.PostJsonAsync($"/api/calls/{call.Id}/pull", pull)).ReadAsync<PullResponse>();
        Assert.Equal("r0", retry.Tracks.Single().Mid);

        // 上一个 offer 还没回 answer 就再订阅：Cloudflare 拒绝，映射成 502
        Assert.Equal(HttpStatusCode.BadGateway, (await x.BobClient.PostJsonAsync($"/api/calls/{call.Id}/pull", pull)).StatusCode);
        Assert.Equal(bobJoin.ParticipantId, (await GetAsync(x.Admin, call.Id)).Participants.Single(p => p.UserId == x.Bob.Id).Id);
    }

    [PgFact]
    public async Task Turn_failure_or_missing_turn_key_falls_back_to_public_stun()
    {
        await using var x = await SetupAsync();
        var conv = await ConversationAsync(x.Admin, ConversationType.Direct, x.Bob.Id);
        var call = await StartAsync(x.Admin, conv);
        x.App.Realtime.FailNext("/turn/", HttpStatusCode.InternalServerError, "internal_error");
        var join = await JoinAsync(x.Admin, call.Id);
        Assert.Equal(["stun:stun.cloudflare.com:3478"], Assert.Single(join.IceServers).Urls);
        Assert.Equal("Bearer " + FakeRealtime.TurnToken, x.App.Realtime.CallsTo("/turn/").Single().Authorization);
        Assert.Equal(86400, x.App.Realtime.CallsTo("/turn/").Single().Body!["ttl"]!.GetValue<int>());

        await using var noTurn = await SetupAsync(turnConfigured: false);
        var conv2 = await ConversationAsync(noTurn.Admin, ConversationType.Direct, noTurn.Bob.Id);
        var call2 = await StartAsync(noTurn.Admin, conv2);
        var join2 = await JoinAsync(noTurn.Admin, call2.Id);
        Assert.Equal(["stun:stun.cloudflare.com:3478"], Assert.Single(join2.IceServers).Urls);
        Assert.Empty(noTurn.App.Realtime.CallsTo("/turn/"));
    }

    [PgFact]
    public async Task Living_room_is_always_open_to_the_whole_family()
    {
        await using var x = await SetupAsync();
        await using var carolHub = await x.App.ConnectHubAsync("carol");
        var rooms = await Task.WhenAll(Enumerable.Range(0, 4).Select(async i => await (await (i % 2 == 0 ? x.Admin : x.BobClient).GetAsync("/api/calls/rooms")).ReadAsync<List<CallDto>>()));
        var room = rooms[0].Single();
        Assert.All(rooms, r => Assert.Equal(room.Id, r.Single().Id));
        Assert.Equal((CallService.LivingRoom, CallType.Meeting, (Guid?)null), (room.RoomSlug, room.Type, room.ConversationId));
        Assert.Equal(1, await x.App.WithDbAsync(db => db.CallSessions.CountAsync()));

        // 会议室不响铃、不出现在通话记录和"进行中的通话"里
        var bobJoin = await JoinAsync(x.BobClient, room.Id);
        Assert.Null(bobJoin.Call.AnsweredAt);
        Assert.Empty(carolHub.All("IncomingCall"));
        // 没进去的家人也看得到谁在里面
        Assert.Equal(x.Bob.Id, (await carolHub.WaitAsync<ParticipantEventDto>("ParticipantJoined")).Participant.UserId);
        await JoinAsync(x.CarolClient, room.Id);
        Assert.Empty(await (await x.Admin.GetAsync("/api/calls/active")).ReadAsync<List<CallDto>>());
        Assert.Empty(await (await x.Admin.GetAsync("/api/calls")).ReadAsync<List<CallDto>>());
        Assert.Equal(2, (await (await x.Admin.GetAsync("/api/calls/rooms")).ReadAsync<List<CallDto>>()).Single().Participants.Count);

        // 拒接、响铃超时对会议室没有意义；非管理员不能清场
        await x.Admin.PostAsync($"/api/calls/{room.Id}/decline", null);
        Assert.Equal(0, await x.SweepAsync(TimeSpan.FromSeconds(20)));
        Assert.Equal(HttpStatusCode.Forbidden, (await x.BobClient.PostAsync($"/api/calls/{room.Id}/end", null)).StatusCode);

        // 所有人离开：房间还在，下次还是同一间；管理员可以清场
        await x.BobClient.PostAsync($"/api/calls/{room.Id}/leave", null);
        Assert.Equal(HttpStatusCode.NoContent, (await x.Admin.PostAsync($"/api/calls/{room.Id}/end", null)).StatusCode);
        var after = (await (await x.Admin.GetAsync("/api/calls/rooms")).ReadAsync<List<CallDto>>()).Single();
        Assert.Equal((room.Id, (DateTimeOffset?)null, 0), (after.Id, after.EndedAt, after.Participants.Count));
        Assert.Equal("ended", (await carolHub.WaitAsync<ParticipantLeftDto>("ParticipantLeft", 2)).Reason);
        await JoinAsync(x.CarolClient, room.Id);

        // 掉线的人照样被清理，但房间不结束
        Assert.Equal(1, await x.SweepAsync(TimeSpan.FromSeconds(60)));
        Assert.Null((await x.CallAsync(room.Id)).EndedAt);
        Assert.Empty(await x.App.WithDbAsync(db => db.Messages.ToListAsync()));
    }

    [PgFact]
    public async Task Usage_estimate_accrues_per_track_and_is_admin_only()
    {
        await using var x = await SetupAsync();
        var conv = await ConversationAsync(x.Admin, ConversationType.Direct, x.Bob.Id);
        var call = await StartAsync(x.Admin, conv);
        await JoinAsync(x.Admin, call.Id);
        await JoinAsync(x.BobClient, call.Id);
        await PublishAsync(x.Admin, call.Id, ("0", TrackKind.Video));
        await PublishAsync(x.BobClient, call.Id, ("0", TrackKind.Video));
        // 让两条轨道都"发布了 100 秒"
        await x.App.WithDbAsync(db => db.CallTracks.ExecuteUpdateAsync(s => s.SetProperty(t => t.PublishedAt, DateTimeOffset.UtcNow.AddSeconds(-100))));
        await x.Admin.PostAsync($"/api/calls/{call.Id}/leave", null);

        Assert.Equal(HttpStatusCode.Forbidden, (await x.BobClient.GetAsync("/api/calls/usage")).StatusCode);
        var usage = await (await x.Admin.GetAsync("/api/calls/usage")).ReadAsync<CallUsageDto>();
        Assert.Equal(1, usage.Calls);
        // 两条各 100 秒 × 1 Mbps × 1 个接收者 ≈ 25 MB（Bob 的那条在通话结束时才下线，挂断的 Admin 仍算接收方）
        Assert.InRange(usage.EgressBytesEstimate, 24_000_000, 27_000_000);
    }

    [PgFact]
    public async Task New_conversation_reaches_members_who_are_already_connected()
    {
        await using var x = await SetupAsync();
        await using var bobHub = await x.App.ConnectHubAsync("bob");
        // Bob 连上之后才建的单聊：不用重连就能收到里面的消息
        var conv = await ConversationAsync(x.Admin, ConversationType.Direct, x.Bob.Id);
        await bobHub.WaitAsync("ConversationUpdated");
        var send = await x.Admin.PostJsonAsync($"/api/conversations/{conv}/messages", new SendMessageRequest(conv, Guid.CreateVersion7(), MessageType.Text, "在吗"));
        Assert.Equal(HttpStatusCode.OK, send.StatusCode);
        Assert.Equal("在吗", (await bobHub.WaitAsync<MessageDto>("MessageReceived")).Body);
    }

    [Theory]
    [InlineData(CallType.Video, CallEndReason.Completed, 75, "视频通话 01:15")]
    [InlineData(CallType.Audio, CallEndReason.Completed, 3725, "语音通话 1:02:05")]
    [InlineData(CallType.Audio, CallEndReason.Missed, 0, "未接听的语音通话")]
    [InlineData(CallType.Video, CallEndReason.Declined, 0, "已拒绝的视频通话")]
    [InlineData(CallType.Video, CallEndReason.Cancelled, 0, "已取消的视频通话")]
    public void Call_summary_text(CallType type, CallEndReason reason, int seconds, string expected)
        => Assert.Equal(expected, CallPush.Summary(type, reason, seconds));

    [Fact]
    public void Push_payloads_carry_kind_call_id_and_a_shared_tag()
    {
        var conv = new Conversation { Type = ConversationType.Group, Title = "全家群" };
        var call = new CallSession { ConversationId = conv.Id, Type = CallType.Video };
        var ring = CallPush.Ringing(call, conv, "爸爸");
        Assert.Equal(("爸爸 · 全家群", "邀请你视频通话", "call", call.Id), (ring.Title, ring.Body, ring.Kind, ring.CallId!.Value));
        Assert.Equal($"/messages/{conv.Id}?call={call.Id}", ring.Url);
        var missed = CallPush.Missed(call, new Conversation { Type = ConversationType.Direct }, "爸爸");
        Assert.Equal(("爸爸", "未接视频来电", "call-missed"), (missed.Title, missed.Body, missed.Kind));
        Assert.Equal(ring.Tag, missed.Tag);
        Assert.Equal(ring.Tag, CallPush.Answered(call, conv.Id).Tag);
        Assert.Equal("call-answered", CallPush.Answered(call, conv.Id).Kind);
    }
}

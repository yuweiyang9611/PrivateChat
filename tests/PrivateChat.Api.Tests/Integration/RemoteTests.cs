using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PrivateChat.Api.Domain;
using PrivateChat.Api.Features.Auth;
using PrivateChat.Api.Features.Remote;
using PrivateChat.Contracts.Remote;

namespace PrivateChat.Api.Tests.Integration;

/// <summary>远程控制：设备码登记、权限、同意 / 拒绝 / 超时、PIN、数据面中继、吊销。对真实数据库跑。</summary>
public class RemoteTests
{
    private sealed record Ctx(ApiFactory App, HttpClient Admin, Guid AdminId, User Bob, HttpClient BobClient, User Carol, HttpClient CarolClient, User Kid, HttpClient KidClient) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => App.DisposeAsync();
    }

    private static async Task<Ctx> SetupAsync(bool realtimeConfigured = true)
    {
        var app = new ApiFactory(realtimeConfigured: realtimeConfigured);
        var admin = await app.LoginAsync("admin@example.com");
        var adminId = (await (await admin.GetAsync("/api/auth/me")).ReadAsync<UserDto>()).Id;
        var bob = await app.AddUserAsync("bob", UserRole.Member);
        var carol = await app.AddUserAsync("carol", UserRole.Member);
        var kid = await app.AddUserAsync("kid", UserRole.Child);
        return new Ctx(app, admin, adminId, bob, await app.LoginAsync("bob"), carol, await app.LoginAsync("carol"), kid, await app.LoginAsync("kid"));
    }

    private sealed record Agent(Guid DeviceId, string Token, HttpClient Http);

    /// <summary>走一遍设备码：Agent 申请 → confirmer 在网页确认（可替 owner 添加）→ Agent 轮询拿到令牌。</summary>
    private static async Task<Agent> RegisterAsync(Ctx x, HttpClient confirmer, Guid? ownerId = null, string name = "爸爸的电脑")
    {
        var anon = x.App.Anonymous();
        var code = await (await anon.PostJsonAsync("/api/auth/device/code", new { name, platform = "windows" })).ReadAsync<DeviceCodeResponse>();
        Assert.Equal("pending", (await (await anon.GetAsync($"/api/auth/device/poll?code={code.Code}&pollToken={code.PollToken}")).ReadAsync<DevicePollResponse>()).Status);
        Assert.Equal(name, (await (await confirmer.GetAsync($"/api/auth/device/lookup?code={code.Code}")).ReadAsync<DeviceLookupResponse>()).Name);
        var confirmed = await (await confirmer.PostJsonAsync("/api/auth/device/confirm", new { code = code.Code, ownerId })).ReadAsync<DeviceConfirmResponse>();
        var poll = await (await anon.GetAsync($"/api/auth/device/poll?code={code.Code}&pollToken={code.PollToken}")).ReadAsync<DevicePollResponse>();
        Assert.Equal("approved", poll.Status);
        Assert.Equal(confirmed.DeviceId, poll.DeviceId);
        var http = x.App.Anonymous();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", poll.DeviceToken);
        return new Agent(confirmed.DeviceId, poll.DeviceToken!, http);
    }

    /// <summary>Agent 的 /hubs/agent 连接，收集服务端发来的事件。</summary>
    private sealed class AgentHubClient : IAsyncDisposable
    {
        private readonly List<(string Name, JsonElement? Payload)> _events = [];
        public HubConnection Connection { get; }

        public AgentHubClient(ApiFactory app, string token)
        {
            Connection = new HubConnectionBuilder()
                .WithUrl(new Uri(app.Server.BaseAddress, "hubs/agent"), o =>
                {
                    o.HttpMessageHandlerFactory = _ => app.Server.CreateHandler();
                    o.Transports = HttpTransportType.LongPolling;
                    o.AccessTokenProvider = () => Task.FromResult<string?>(token);
                })
                .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
                .Build();
            foreach (var name in new[] { AgentHubEvents.SessionRequested, AgentHubEvents.SessionEnded })
                Connection.On<JsonElement>(name, p => { lock (_events) _events.Add((name, p)); });
            Connection.On(AgentHubEvents.Revoked, () => { lock (_events) _events.Add((AgentHubEvents.Revoked, null)); });
        }

        public List<JsonElement> All(string name)
        {
            lock (_events) return [.. _events.Where(e => e.Name == name && e.Payload is not null).Select(e => e.Payload!.Value)];
        }

        public bool Got(string name)
        {
            lock (_events) return _events.Any(e => e.Name == name);
        }

        public async Task<T> WaitAsync<T>(string name, int count = 1)
        {
            await EventuallyAsync(() => All(name).Count >= count, $"Agent 收到第 {count} 个 {name}");
            return All(name)[count - 1].Deserialize<T>(ApiFactory.Json)!;
        }

        public ValueTask DisposeAsync() => Connection.DisposeAsync();
    }

    private static async Task<AgentHubClient> OnlineAsync(Ctx x, Agent agent)
    {
        var hub = new AgentHubClient(x.App, agent.Token);
        await hub.Connection.StartAsync();
        await EventuallyAsync(() => x.App.Services.GetRequiredService<AgentRegistry>().IsOnline(agent.DeviceId), "Agent 在线");
        await hub.Connection.InvokeAsync(AgentHubEvents.Report, new AgentReport("0.5.0", "Windows 11", [new AgentScreen(1920, 1080, true)]));
        return hub;
    }

    private static async Task EventuallyAsync(Func<bool> condition, string what, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new Xunit.Sdk.XunitException($"等不到：{what}");
            await Task.Delay(25);
        }
    }

    private static async Task<RemoteSessionDto> StartAsync(HttpClient c, Guid deviceId, string mode = "Control", string? pin = null)
        => await (await c.PostJsonAsync("/api/remote/sessions", new { targetDeviceId = deviceId, mode, pin })).ReadAsync<RemoteSessionDto>();

    private static async Task<RemoteSessionDto> ConsentAsync(Agent agent, Guid sessionId, bool accept)
        => await (await agent.Http.PostJsonAsync($"/api/remote/sessions/{sessionId}/consent", new { accept })).ReadAsync<RemoteSessionDto>();

    private static async Task<List<DeviceDto>> DevicesAsync(HttpClient c) => await (await c.GetAsync("/api/devices")).ReadAsync<List<DeviceDto>>();

    private static async Task SweepAsync(ApiFactory app, TimeSpan later)
    {
        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<RemoteService>().SweepAsync(DateTimeOffset.UtcNow + later, default);
    }

    // ---- 数据面 ----

    private static Uri WsUri(ApiFactory app, Guid sessionId, string role)
        => new UriBuilder(app.Server.BaseAddress) { Scheme = "ws", Path = $"/ws/remote/{sessionId}/{role}" }.Uri;

    private static async Task<WebSocket> ConnectAgentWsAsync(ApiFactory app, Agent agent, Guid sessionId)
    {
        var client = app.Server.CreateWebSocketClient();
        client.ConfigureRequest = r => r.Headers.Authorization = $"Bearer {agent.Token}";
        return await client.ConnectAsync(WsUri(app, sessionId, "agent"), default);
    }

    private static async Task<WebSocket> ConnectControllerWsAsync(ApiFactory app, string username, Guid sessionId)
    {
        var cookie = await CookieAsync(app, username);
        var client = app.Server.CreateWebSocketClient();
        client.ConfigureRequest = r => r.Headers.Cookie = cookie;
        return await client.ConnectAsync(WsUri(app, sessionId, "controller"), default);
    }

    private static async Task<string> CookieAsync(ApiFactory app, string username)
    {
        var raw = app.Server.CreateClient();
        var res = await raw.PostAsJsonAsync("/api/auth/local/login", new { usernameOrEmail = username, password = ApiFactory.Password });
        res.EnsureSuccessStatusCode();
        return string.Join("; ", res.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]));
    }

    private sealed record Received(WebSocketMessageType Type, byte[] Data, WebSocketCloseStatus? Close);

    private static async Task<Received> ReceiveAsync(WebSocket ws, int timeoutMs = 5000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        var buffer = new byte[1 << 16];
        var data = new MemoryStream();
        WebSocketReceiveResult r;
        do
        {
            r = await ws.ReceiveAsync(buffer, cts.Token);
            if (r.MessageType == WebSocketMessageType.Close) return new Received(r.MessageType, [], r.CloseStatus);
            data.Write(buffer, 0, r.Count);
        } while (!r.EndOfMessage);
        return new Received(r.MessageType, data.ToArray(), null);
    }

    private static async Task<bool> ReceivePeerAsync(WebSocket ws)
    {
        var m = await ReceiveAsync(ws);
        Assert.Equal(WebSocketMessageType.Text, m.Type);
        var notice = JsonSerializer.Deserialize<RelayNotice>(m.Data, JsonSerializerOptions.Web)!;
        Assert.Equal("peer", notice.Type);
        return notice.Connected;
    }

    private static Task SendAsync(WebSocket ws, byte[] frame) => ws.SendAsync(frame, WebSocketMessageType.Binary, true, default);

    // ---- 测试 ----

    [PgFact]
    public async Task Device_code_registers_a_computer_that_only_its_owner_and_allowed_people_see()
    {
        await using var x = await SetupAsync();
        // 管理员替 Bob 添加；Bob 自己是主人，管理员默认在名单里
        var agent = await RegisterAsync(x, x.Admin, x.Bob.Id);

        var bobs = await DevicesAsync(x.BobClient);
        var d = Assert.Single(bobs);
        Assert.Equal(("爸爸的电脑", x.Bob.Id, true, true, false), (d.Name, d.OwnerId, d.CanControl, d.CanManage, d.Online));
        Assert.Equal([x.AdminId], d.AllowedControllers);
        var admins = Assert.Single(await DevicesAsync(x.Admin));
        Assert.True(admins.CanControl && admins.CanManage);
        Assert.Empty(await DevicesAsync(x.CarolClient));
        Assert.Empty(await DevicesAsync(x.KidClient));

        // 上线后能看到显示器
        await using var hub = await OnlineAsync(x, agent);
        var online = Assert.Single(await DevicesAsync(x.BobClient));
        Assert.True(online.Online);
        Assert.Equal([new ScreenDto(1920, 1080, true)], online.Screens);
        Assert.Equal(("0.5.0", "Windows 11"), (online.AgentVersion, online.Os));

        // 设备令牌只能用于远程控制：拿不到主人的聊天、连不上网页的 SignalR
        Assert.Equal(HttpStatusCode.Unauthorized, (await agent.Http.GetAsync("/api/conversations")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await agent.Http.PostAsync("/hubs/chat/negotiate?negotiateVersion=1", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.Http.PostJsonAsync("/api/remote/sessions", new { targetDeviceId = agent.DeviceId, mode = "View" })).StatusCode);
        // 浏览器登录的人连不上 Agent 的 SignalR
        Assert.Equal(HttpStatusCode.Unauthorized, (await x.BobClient.PostAsync("/hubs/agent/negotiate?negotiateVersion=1", null)).StatusCode);

        // 普通成员不能替别人添加；同一个码不能确认两次
        var anon = x.App.Anonymous();
        var code = await (await anon.PostJsonAsync("/api/auth/device/code", new { name = "书房" })).ReadAsync<DeviceCodeResponse>();
        Assert.Equal(HttpStatusCode.Forbidden, (await x.BobClient.PostJsonAsync("/api/auth/device/confirm", new { code = code.Code, ownerId = x.Carol.Id })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await x.CarolClient.PostJsonAsync("/api/auth/device/confirm", new { code = code.Code })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await x.BobClient.PostJsonAsync("/api/auth/device/confirm", new { code = code.Code })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await x.KidClient.GetAsync($"/api/auth/device/lookup?code={code.Code}")).StatusCode);
    }

    [PgFact]
    public async Task Control_session_waits_for_consent_then_relays_frames_both_ways_until_hung_up()
    {
        await using var x = await SetupAsync();
        var agent = await RegisterAsync(x, x.BobClient);
        await using var hub = await OnlineAsync(x, agent);
        await using var adminEvents = await x.App.ConnectHubAsync("admin@example.com");
        await using var bobEvents = await x.App.ConnectHubAsync("bob");

        var res = await x.Admin.PostJsonAsync("/api/remote/sessions", new { targetDeviceId = agent.DeviceId, mode = "Control" });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var session = await res.ReadAsync<RemoteSessionDto>();
        Assert.Equal((RemoteSessionState.Pending, RemoteConsentType.Prompt), (session.State, session.Consent));

        var request = await hub.WaitAsync<AgentSessionRequest>(AgentHubEvents.SessionRequested);
        Assert.Equal((session.Id, "admin", RemoteSessionMode.Control, false), (request.SessionId, request.ControllerName, request.Mode, request.Active));
        await bobEvents.WaitAsync("RemoteSessionChanged"); // 主人也知道有人在请求

        // 还没同意：数据面连不上
        var early = await Assert.ThrowsAnyAsync<Exception>(() => ConnectControllerWsAsync(x.App, "admin@example.com", session.Id));
        Assert.Contains("409", early.Message);

        var accepted = await ConsentAsync(agent, session.Id, accept: true);
        Assert.Equal(RemoteSessionState.Active, accepted.State);
        Assert.Equal(RemoteSessionState.Active, (await adminEvents.WaitAsync<RemoteSessionDto>("RemoteSessionChanged", 2)).State);
        // 再同意一次：已经不是等待状态
        Assert.Equal(HttpStatusCode.Conflict, (await agent.Http.PostJsonAsync($"/api/remote/sessions/{session.Id}/consent", new { accept = true })).StatusCode);

        // 两端连上：先到的收到"对端不在"，后到的两边都收到"对端在"
        using var agentWs = await ConnectAgentWsAsync(x.App, agent, session.Id);
        Assert.False(await ReceivePeerAsync(agentWs));
        using var controllerWs = await ConnectControllerWsAsync(x.App, "admin@example.com", session.Id);
        Assert.True(await ReceivePeerAsync(controllerWs));
        Assert.True(await ReceivePeerAsync(agentWs));

        // 原样转发：视频帧到控制端，键鼠帧到 Agent
        var video = RemoteFrames.VideoFrame(40, true, new byte[200_000]); // 大帧（跨多个接收分片）
        await SendAsync(agentWs, video);
        var got = await ReceiveAsync(controllerWs);
        Assert.Equal(WebSocketMessageType.Binary, got.Type);
        Assert.Equal(video, got.Data);
        var move = RemoteFrames.MouseMove(0.25f, 0.75f);
        await SendAsync(controllerWs, move);
        Assert.Equal(move, (await ReceiveAsync(agentWs)).Data);

        // 控制端掉线重连：Agent 先收到"对端断开"，再收到"对端在"
        await controllerWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", default);
        Assert.False(await ReceivePeerAsync(agentWs));
        using var controllerWs2 = await ConnectControllerWsAsync(x.App, "admin@example.com", session.Id);
        Assert.True(await ReceivePeerAsync(controllerWs2));
        Assert.True(await ReceivePeerAsync(agentWs));

        // 挂断：两端以 4001 关闭，Agent 收到结束通知，记录里有流量
        var ended = await (await x.Admin.PostAsync($"/api/remote/sessions/{session.Id}/end", null)).ReadAsync<RemoteSessionDto>();
        Assert.Equal((RemoteSessionState.Ended, "controller"), (ended.State, ended.EndReason));
        Assert.Equal((WebSocketCloseStatus)RelayCloseCodes.Ended, (await ReceiveAsync(agentWs)).Close);
        Assert.Equal((WebSocketCloseStatus)RelayCloseCodes.Ended, (await ReceiveAsync(controllerWs2)).Close);
        Assert.Equal(session.Id, (await hub.WaitAsync<AgentSessionEnded>(AgentHubEvents.SessionEnded)).SessionId);
        var audit = await (await x.BobClient.GetAsync("/api/remote/sessions")).ReadAsync<RemoteSessionPageDto>();
        var row = Assert.Single(audit.Items);
        Assert.True(row.BytesRelayed >= video.Length + move.Length);
        Assert.Equal(x.AdminId, row.ControllerUserId);
        Assert.Empty((await (await x.CarolClient.GetAsync("/api/remote/sessions")).ReadAsync<RemoteSessionPageDto>()).Items);
    }

    [PgFact]
    public async Task View_only_sessions_drop_input_frames_in_the_relay()
    {
        await using var x = await SetupAsync();
        var agent = await RegisterAsync(x, x.BobClient);
        await using var hub = await OnlineAsync(x, agent);
        var session = await StartAsync(x.BobClient, agent.DeviceId, "View");
        await ConsentAsync(agent, session.Id, true);
        using var agentWs = await ConnectAgentWsAsync(x.App, agent, session.Id);
        Assert.False(await ReceivePeerAsync(agentWs));
        using var controllerWs = await ConnectControllerWsAsync(x.App, "bob", session.Id);
        Assert.True(await ReceivePeerAsync(controllerWs));
        Assert.True(await ReceivePeerAsync(agentWs));

        await SendAsync(controllerWs, RemoteFrames.MouseMove(0.5f, 0.5f));
        await SendAsync(controllerWs, RemoteFrames.Key(0x1E, true));
        await SendAsync(controllerWs, RemoteFrames.Clipboard("偷偷塞进剪贴板"));
        await SendAsync(controllerWs, [(byte)RemoteFrameType.RequestKeyFrame]);
        await SendAsync(controllerWs, RemoteFrames.FrameAck(7));
        var keyRequest = (await ReceiveAsync(agentWs)).Data;
        Assert.Equal(new[] { (byte)RemoteFrameType.RequestKeyFrame }, keyRequest);
        Assert.Equal(RemoteFrames.FrameAck(7), (await ReceiveAsync(agentWs)).Data);
    }

    [PgFact]
    public async Task Declined_and_unanswered_requests_end_and_the_agent_is_told()
    {
        await using var x = await SetupAsync();
        var agent = await RegisterAsync(x, x.BobClient);
        await using var hub = await OnlineAsync(x, agent);

        var first = await StartAsync(x.Admin, agent.DeviceId);
        var declined = await ConsentAsync(agent, first.Id, accept: false);
        Assert.Equal((RemoteSessionState.Ended, "declined"), (declined.State, declined.EndReason));

        var second = await StartAsync(x.Admin, agent.DeviceId);
        await SweepAsync(x.App, TimeSpan.FromSeconds(31));
        var timedOut = await (await x.Admin.GetAsync($"/api/remote/sessions/{second.Id}")).ReadAsync<RemoteSessionDto>();
        Assert.Equal((RemoteSessionState.Ended, "timeout"), (timedOut.State, timedOut.EndReason));
        Assert.Equal(second.Id, (await hub.WaitAsync<AgentSessionEnded>(AgentHubEvents.SessionEnded, 2)).SessionId);
        Assert.Equal(HttpStatusCode.Conflict, (await agent.Http.PostJsonAsync($"/api/remote/sessions/{second.Id}/consent", new { accept = true })).StatusCode);
    }

    [PgFact]
    public async Task Only_allowed_adults_can_control_and_one_session_per_computer()
    {
        await using var x = await SetupAsync();
        var agent = await RegisterAsync(x, x.BobClient);

        // 不在线
        Assert.Equal(HttpStatusCode.Conflict, (await x.BobClient.PostJsonAsync("/api/remote/sessions", new { targetDeviceId = agent.DeviceId, mode = "View" })).StatusCode);
        await using var hub = await OnlineAsync(x, agent);

        Assert.Equal(HttpStatusCode.Forbidden, (await x.KidClient.PostJsonAsync("/api/remote/sessions", new { targetDeviceId = agent.DeviceId, mode = "View" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await x.CarolClient.PostJsonAsync("/api/remote/sessions", new { targetDeviceId = agent.DeviceId, mode = "View" })).StatusCode);
        // 主人把 Carol 加进名单（孩子加不进去）
        Assert.Equal(HttpStatusCode.BadRequest, (await x.BobClient.PatchJsonAsync($"/api/devices/{agent.DeviceId}", new { allowedControllers = new[] { x.Kid.Id } })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await x.CarolClient.PatchJsonAsync($"/api/devices/{agent.DeviceId}", new { name = "我的" })).StatusCode); // 看不到也改不了
        var updated = await (await x.BobClient.PatchJsonAsync($"/api/devices/{agent.DeviceId}", new { allowedControllers = new[] { x.Carol.Id, x.AdminId }, name = "客厅电脑" })).ReadAsync<DeviceDto>();
        Assert.Equal("客厅电脑", updated.Name);
        var carolSees = Assert.Single(await DevicesAsync(x.CarolClient));
        Assert.True(carolSees.CanControl);
        Assert.False(carolSees.CanManage);
        Assert.Null(carolSees.AllowedControllers);

        var carols = await StartAsync(x.CarolClient, agent.DeviceId, "View");
        // 别人在用：冲突；Carol 自己再点一次：顶掉之前那次
        var busy = await x.Admin.PostJsonAsync("/api/remote/sessions", new { targetDeviceId = agent.DeviceId, mode = "Control" });
        Assert.Equal(HttpStatusCode.Conflict, busy.StatusCode);
        Assert.Contains("carol", await busy.Content.ReadAsStringAsync());
        var again = await StartAsync(x.CarolClient, agent.DeviceId, "Control");
        var old = await (await x.CarolClient.GetAsync($"/api/remote/sessions/{carols.Id}")).ReadAsync<RemoteSessionDto>();
        Assert.Equal((RemoteSessionState.Ended, "replaced"), (old.State, old.EndReason));
        Assert.Equal(again.Id, (await hub.WaitAsync<AgentSessionRequest>(AgentHubEvents.SessionRequested, 2)).SessionId);
        Assert.Equal(again.Id, Assert.Single(await DevicesAsync(x.BobClient)).Session?.Id);

        // 看不到别人的会话；主人可以替电脑挂断
        Assert.Equal(HttpStatusCode.NotFound, (await x.KidClient.GetAsync($"/api/remote/sessions/{again.Id}")).StatusCode);
        var stopped = await (await x.BobClient.PostAsync($"/api/remote/sessions/{again.Id}/end", null)).ReadAsync<RemoteSessionDto>();
        Assert.Equal("stopped", stopped.EndReason);
    }

    [PgFact]
    public async Task Unattended_pin_starts_immediately_and_locks_after_five_wrong_tries()
    {
        await using var x = await SetupAsync();
        var agent = await RegisterAsync(x, x.BobClient);
        await using var hub = await OnlineAsync(x, agent);

        Assert.Equal(HttpStatusCode.BadRequest, (await x.Admin.PostJsonAsync("/api/remote/sessions", new { targetDeviceId = agent.DeviceId, mode = "Control", pin = "123456" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await x.BobClient.PatchJsonAsync($"/api/devices/{agent.DeviceId}", new { pin = "12ab" })).StatusCode);
        Assert.True((await (await x.BobClient.PatchJsonAsync($"/api/devices/{agent.DeviceId}", new { pin = "246810" })).ReadAsync<DeviceDto>()).HasPin);

        for (var i = 1; i <= 4; i++)
        {
            var wrong = await x.Admin.PostJsonAsync("/api/remote/sessions", new { targetDeviceId = agent.DeviceId, mode = "Control", pin = "000000" });
            Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
            Assert.Contains($"还可以再试 {5 - i} 次", await wrong.Content.ReadAsStringAsync());
        }
        Assert.Equal(HttpStatusCode.TooManyRequests, (await x.Admin.PostJsonAsync("/api/remote/sessions", new { targetDeviceId = agent.DeviceId, mode = "Control", pin = "000000" })).StatusCode);
        // 锁定期间对的 PIN 也不行
        Assert.Equal(HttpStatusCode.TooManyRequests, (await x.Admin.PostJsonAsync("/api/remote/sessions", new { targetDeviceId = agent.DeviceId, mode = "Control", pin = "246810" })).StatusCode);
        Assert.NotNull(Assert.Single(await DevicesAsync(x.BobClient)).PinLockedUntil);

        // 主人重设 PIN 解锁；对的 PIN 直接开始，Agent 收到的是"已开始"
        await x.BobClient.PatchJsonAsync($"/api/devices/{agent.DeviceId}", new { pin = "13579135" });
        var session = await StartAsync(x.Admin, agent.DeviceId, pin: "13579135");
        Assert.Equal((RemoteSessionState.Active, RemoteConsentType.Unattended), (session.State, session.Consent));
        var request = await hub.WaitAsync<AgentSessionRequest>(AgentHubEvents.SessionRequested);
        Assert.True(request.Active);
        Assert.Equal(RemoteConsentType.Unattended, request.Consent);

        // 清掉 PIN 之后又只能等同意
        await (await x.Admin.PostAsync($"/api/remote/sessions/{session.Id}/end", null)).ReadAsync<RemoteSessionDto>();
        Assert.False((await (await x.BobClient.PatchJsonAsync($"/api/devices/{agent.DeviceId}", new { clearPin = true })).ReadAsync<DeviceDto>()).HasPin);
        Assert.Equal(HttpStatusCode.BadRequest, (await x.Admin.PostJsonAsync("/api/remote/sessions", new { targetDeviceId = agent.DeviceId, mode = "Control", pin = "13579135" })).StatusCode);
    }

    [PgFact]
    public async Task Revoking_a_computer_ends_its_session_and_disconnects_the_agent()
    {
        await using var x = await SetupAsync();
        var agent = await RegisterAsync(x, x.BobClient);
        await using var hub = await OnlineAsync(x, agent);
        var session = await StartAsync(x.Admin, agent.DeviceId);
        await ConsentAsync(agent, session.Id, true);
        using var agentWs = await ConnectAgentWsAsync(x.App, agent, session.Id);
        Assert.False(await ReceivePeerAsync(agentWs));

        Assert.Equal(HttpStatusCode.NotFound, (await x.CarolClient.DeleteAsync($"/api/devices/{agent.DeviceId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await x.BobClient.DeleteAsync($"/api/devices/{agent.DeviceId}")).StatusCode);

        Assert.Equal((WebSocketCloseStatus)RelayCloseCodes.Ended, (await ReceiveAsync(agentWs)).Close);
        await EventuallyAsync(() => hub.Got(AgentHubEvents.Revoked), "Agent 收到吊销");
        await EventuallyAsync(() => !x.App.Services.GetRequiredService<AgentRegistry>().IsOnline(agent.DeviceId), "Agent 被断开");
        var ended = await (await x.Admin.GetAsync($"/api/remote/sessions/{session.Id}")).ReadAsync<RemoteSessionDto>();
        Assert.Equal("revoked", ended.EndReason);
        Assert.Empty(await DevicesAsync(x.BobClient));
        Assert.Equal(HttpStatusCode.Unauthorized, (await agent.Http.GetAsync($"/api/remote/sessions/{session.Id}")).StatusCode);
    }

    [PgFact]
    public async Task Sessions_end_when_one_side_stays_away_and_strangers_cannot_join_the_relay()
    {
        await using var x = await SetupAsync();
        var agent = await RegisterAsync(x, x.BobClient);
        var other = await RegisterAsync(x, x.CarolClient, name: "Carol 的电脑");
        await using var hub = await OnlineAsync(x, agent);
        var session = await StartAsync(x.Admin, agent.DeviceId);
        await ConsentAsync(agent, session.Id, true);

        // 别的设备、别的人、主人（不是控制的人）都连不上
        var stranger = x.App.Server.CreateWebSocketClient();
        stranger.ConfigureRequest = r => r.Headers.Authorization = $"Bearer {other.Token}";
        Assert.Contains("404", (await Assert.ThrowsAnyAsync<Exception>(() => stranger.ConnectAsync(WsUri(x.App, session.Id, "agent"), default))).Message);
        Assert.Contains("404", (await Assert.ThrowsAnyAsync<Exception>(() => ConnectControllerWsAsync(x.App, "bob", session.Id))).Message);
        Assert.Contains("404", (await Assert.ThrowsAnyAsync<Exception>(() => ConnectControllerWsAsync(x.App, "admin@example.com", Guid.NewGuid()))).Message);

        // 只有 Agent 连上、控制端一直没来：30 秒后结束
        using var agentWs = await ConnectAgentWsAsync(x.App, agent, session.Id);
        Assert.False(await ReceivePeerAsync(agentWs));
        await SweepAsync(x.App, TimeSpan.FromSeconds(5));
        Assert.Equal(RemoteSessionState.Active, (await (await x.Admin.GetAsync($"/api/remote/sessions/{session.Id}")).ReadAsync<RemoteSessionDto>()).State);
        await SweepAsync(x.App, TimeSpan.FromSeconds(31));
        var ended = await (await x.Admin.GetAsync($"/api/remote/sessions/{session.Id}")).ReadAsync<RemoteSessionDto>();
        Assert.Equal("disconnected", ended.EndReason);
        Assert.Equal((WebSocketCloseStatus)RelayCloseCodes.Ended, (await ReceiveAsync(agentWs)).Close);
    }

    [PgFact]
    public async Task Losing_permission_ends_the_session_and_blocks_reconnecting()
    {
        await using var x = await SetupAsync();
        var agent = await RegisterAsync(x, x.BobClient);
        await using var hub = await OnlineAsync(x, agent);
        await x.BobClient.PatchJsonAsync($"/api/devices/{agent.DeviceId}", new { allowedControllers = new[] { x.Carol.Id, x.AdminId } });

        // Carol 被移出名单：她正在进行的会话立即结束，也连不回来
        var carols = await StartAsync(x.CarolClient, agent.DeviceId);
        await ConsentAsync(agent, carols.Id, true);
        using (var ws = await ConnectControllerWsAsync(x.App, "carol", carols.Id))
        {
            Assert.False(await ReceivePeerAsync(ws));
            await x.BobClient.PatchJsonAsync($"/api/devices/{agent.DeviceId}", new { allowedControllers = new[] { x.AdminId } });
            Assert.Equal((WebSocketCloseStatus)RelayCloseCodes.Ended, (await ReceiveAsync(ws)).Close);
        }
        Assert.Equal("stopped", (await (await x.Admin.GetAsync($"/api/remote/sessions/{carols.Id}")).ReadAsync<RemoteSessionDto>()).EndReason);

        // 中继每次连接都按现在的权限再看一遍（就算会话因为别的原因还没结束）
        await x.BobClient.PatchJsonAsync($"/api/devices/{agent.DeviceId}", new { allowedControllers = new[] { x.Carol.Id, x.AdminId } });
        var again = await StartAsync(x.CarolClient, agent.DeviceId);
        await ConsentAsync(agent, again.Id, true);
        await x.App.WithDbAsync(db => db.Devices.Where(d => d.Id == agent.DeviceId).ExecuteUpdateAsync(s => s.SetProperty(d => d.AllowedControllers, "[]")));
        Assert.Contains("403", (await Assert.ThrowsAnyAsync<Exception>(() => ConnectControllerWsAsync(x.App, "carol", again.Id))).Message);
    }

    [PgFact]
    public async Task Demoting_or_disabling_the_controller_ends_their_session()
    {
        await using var x = await SetupAsync();
        var agent = await RegisterAsync(x, x.BobClient);
        await using var hub = await OnlineAsync(x, agent);
        await x.BobClient.PatchJsonAsync($"/api/devices/{agent.DeviceId}", new { allowedControllers = new[] { x.Carol.Id } });
        var session = await StartAsync(x.CarolClient, agent.DeviceId);
        await ConsentAsync(agent, session.Id, true);

        Assert.Equal(HttpStatusCode.OK, (await x.Admin.PatchJsonAsync($"/api/family/members/{x.Carol.Id}", new { role = "Child" })).StatusCode);
        var ended = await (await x.Admin.GetAsync($"/api/remote/sessions/{session.Id}")).ReadAsync<RemoteSessionDto>();
        Assert.Equal((RemoteSessionState.Ended, "stopped"), (ended.State, ended.EndReason));
        Assert.Equal(session.Id, (await hub.WaitAsync<AgentSessionEnded>(AgentHubEvents.SessionEnded)).SessionId);
    }

    [PgFact]
    public async Task Changing_the_pin_ends_unattended_sessions_and_an_expired_lock_does_not_linger()
    {
        await using var x = await SetupAsync();
        var agent = await RegisterAsync(x, x.BobClient);
        await using var hub = await OnlineAsync(x, agent);
        await x.BobClient.PatchJsonAsync($"/api/devices/{agent.DeviceId}", new { pin = "246810" });
        var session = await StartAsync(x.Admin, agent.DeviceId, pin: "246810");
        Assert.Equal(RemoteSessionState.Active, session.State);
        await x.BobClient.PatchJsonAsync($"/api/devices/{agent.DeviceId}", new { pin = "135791" });
        Assert.Equal("stopped", (await (await x.Admin.GetAsync($"/api/remote/sessions/{session.Id}")).ReadAsync<RemoteSessionDto>()).EndReason);

        // 锁定过期之后输错一次：还剩 4 次，不是"已锁定"
        await x.App.WithDbAsync(db => db.Devices.Where(d => d.Id == agent.DeviceId)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.PinLockedUntil, DateTimeOffset.UtcNow.AddMinutes(-1))));
        Assert.Null(Assert.Single(await DevicesAsync(x.BobClient)).PinLockedUntil);
        var wrong = await x.Admin.PostJsonAsync("/api/remote/sessions", new { targetDeviceId = agent.DeviceId, mode = "Control", pin = "000000" });
        Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
        Assert.Contains("还可以再试 4 次", await wrong.Content.ReadAsStringAsync());
    }

    [PgFact]
    public async Task People_on_the_trusted_list_start_immediately_without_consent_or_pin()
    {
        await using var x = await SetupAsync();
        var agent = await RegisterAsync(x, x.BobClient);
        await using var hub = await OnlineAsync(x, agent);
        await x.BobClient.PatchJsonAsync($"/api/devices/{agent.DeviceId}", new { allowedControllers = new[] { x.Carol.Id, x.AdminId } });

        // 默认谁都不在免同意名单里：照旧等电脑前的人点同意
        var before = Assert.Single(await DevicesAsync(x.Admin));
        Assert.False(before.Trusted);
        Assert.Empty(before.TrustedControllers!);
        var asked = await StartAsync(x.Admin, agent.DeviceId);
        Assert.Equal((RemoteSessionState.Pending, RemoteConsentType.Prompt), (asked.State, asked.Consent));
        Assert.False((await hub.WaitAsync<AgentSessionRequest>(AgentHubEvents.SessionRequested, 1)).Active);
        await x.Admin.PostAsync($"/api/remote/sessions/{asked.Id}/end", null);

        // 主人把管理员和自己勾上
        var updated = await (await x.BobClient.PatchJsonAsync($"/api/devices/{agent.DeviceId}", new { trustedControllers = new[] { x.AdminId, x.Bob.Id } })).ReadAsync<DeviceDto>();
        Assert.Equal([x.AdminId, x.Bob.Id], updated.TrustedControllers);
        Assert.True(updated.Trusted);
        Assert.True(Assert.Single(await DevicesAsync(x.Admin)).Trusted);
        var carolSees = Assert.Single(await DevicesAsync(x.CarolClient));
        Assert.False(carolSees.Trusted);
        Assert.Null(carolSees.TrustedControllers); // 名单只给能管理的人看

        // 名单里的人：不带 PIN、没人点同意，直接开始，数据面马上能连
        var session = await StartAsync(x.Admin, agent.DeviceId, "View");
        Assert.Equal((RemoteSessionState.Active, RemoteConsentType.Trusted), (session.State, session.Consent));
        Assert.NotNull(session.AcceptedAt);
        // 已经装出去的 Agent 不认识 Trusted：按"无人值守、已开始"告诉它（它只看要不要弹窗）
        var request = await hub.WaitAsync<AgentSessionRequest>(AgentHubEvents.SessionRequested, 2);
        Assert.True(request.Active);
        Assert.Equal(RemoteConsentType.Unattended, request.Consent);
        using (var ws = await ConnectControllerWsAsync(x.App, "admin@example.com", session.Id))
            Assert.False(await ReceivePeerAsync(ws));
        // Agent 重连时补发的也一样（新连接顶掉旧的，后面这台电脑就靠它在线）
        await using var reconnected = await OnlineAsync(x, agent);
        var missed = await reconnected.WaitAsync<AgentSessionRequest>(AgentHubEvents.SessionRequested);
        Assert.Equal((session.Id, true, RemoteConsentType.Unattended), (missed.SessionId, missed.Active, missed.Consent));
        // 审计里记的是"免同意名单"，和 PIN 分得开
        var listed = (await (await x.BobClient.GetAsync("/api/remote/sessions")).ReadAsync<RemoteSessionPageDto>()).Items.First(s => s.Id == session.Id);
        Assert.Equal(RemoteConsentType.Trusted, listed.Consent);
        await x.Admin.PostAsync($"/api/remote/sessions/{session.Id}/end", null);

        // 主人远程自己的电脑也直接开始；在远程名单里但没被勾上的人照旧等同意
        var own = await StartAsync(x.BobClient, agent.DeviceId);
        Assert.Equal((RemoteSessionState.Active, RemoteConsentType.Trusted), (own.State, own.Consent));
        await x.BobClient.PostAsync($"/api/remote/sessions/{own.Id}/end", null);
        var carols = await StartAsync(x.CarolClient, agent.DeviceId);
        Assert.Equal((RemoteSessionState.Pending, RemoteConsentType.Prompt), (carols.State, carols.Consent));
    }

    [PgFact]
    public async Task Trusted_list_only_takes_the_owner_and_allowed_adults_and_follows_the_allowed_list()
    {
        await using var x = await SetupAsync();
        var agent = await RegisterAsync(x, x.BobClient); // 默认远程名单：除主人外的管理员
        await using var hub = await OnlineAsync(x, agent);
        var url = $"/api/devices/{agent.DeviceId}";

        // 不在远程名单里的人、孩子：勾不上
        Assert.Equal(HttpStatusCode.BadRequest, (await x.BobClient.PatchJsonAsync(url, new { trustedControllers = new[] { x.Carol.Id } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await x.BobClient.PatchJsonAsync(url, new { trustedControllers = new[] { x.Kid.Id } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await x.BobClient.PatchJsonAsync(url, new { trustedControllers = new[] { Guid.NewGuid() } })).StatusCode);
        Assert.Empty(Assert.Single(await DevicesAsync(x.BobClient)).TrustedControllers!);

        // 同一次保存里先加进远程名单、再勾免同意
        var both = await (await x.BobClient.PatchJsonAsync(url, new { allowedControllers = new[] { x.Carol.Id, x.AdminId }, trustedControllers = new[] { x.Carol.Id, x.Carol.Id } })).ReadAsync<DeviceDto>();
        Assert.Equal([x.Carol.Id], both.TrustedControllers);

        // 只有主人或管理员能改：Carol 能远程，但不能给自己（或别人）开免同意
        Assert.Equal(HttpStatusCode.Forbidden, (await x.CarolClient.PatchJsonAsync(url, new { trustedControllers = new[] { x.Carol.Id, x.AdminId } })).StatusCode);
        var byAdmin = await (await x.Admin.PatchJsonAsync(url, new { trustedControllers = new[] { x.Carol.Id, x.AdminId } })).ReadAsync<DeviceDto>();
        Assert.Equal([x.Carol.Id, x.AdminId], byAdmin.TrustedControllers);

        // 被移出远程名单（这次没提免同意名单）：也从免同意名单里出去；再加回远程名单不会自动回来
        var removed = await (await x.BobClient.PatchJsonAsync(url, new { allowedControllers = new[] { x.AdminId } })).ReadAsync<DeviceDto>();
        Assert.Equal([x.AdminId], removed.TrustedControllers);
        await x.BobClient.PatchJsonAsync(url, new { allowedControllers = new[] { x.Carol.Id, x.AdminId } });
        var carols = await StartAsync(x.CarolClient, agent.DeviceId);
        Assert.Equal((RemoteSessionState.Pending, RemoteConsentType.Prompt), (carols.State, carols.Consent));
        await x.CarolClient.PostAsync($"/api/remote/sessions/{carols.Id}/end", null);

        // 被降为孩子：名单里还留着名字也不算数
        Assert.Equal(HttpStatusCode.OK, (await x.Admin.PatchJsonAsync($"/api/family/members/{x.Carol.Id}", new { role = "Child" })).StatusCode);
        await x.App.WithDbAsync(db => db.Devices.Where(d => d.Id == agent.DeviceId)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.TrustedControllers, $"[\"{x.Carol.Id}\"]")));
        Assert.Equal(HttpStatusCode.Forbidden, (await x.CarolClient.PostJsonAsync("/api/remote/sessions", new { targetDeviceId = agent.DeviceId, mode = "View" })).StatusCode);
    }

    [PgFact]
    public async Task Leaving_the_trusted_list_ends_sessions_started_through_it_but_pin_changes_do_not()
    {
        await using var x = await SetupAsync();
        var agent = await RegisterAsync(x, x.BobClient);
        await using var hub = await OnlineAsync(x, agent);
        var url = $"/api/devices/{agent.DeviceId}";
        await x.BobClient.PatchJsonAsync(url, new { allowedControllers = new[] { x.Carol.Id, x.AdminId }, trustedControllers = new[] { x.Carol.Id }, pin = "246810" });

        var session = await StartAsync(x.CarolClient, agent.DeviceId);
        Assert.Equal((RemoteSessionState.Active, RemoteConsentType.Trusted), (session.State, session.Consent));
        using (var ws = await ConnectControllerWsAsync(x.App, "carol", session.Id))
        {
            Assert.False(await ReceivePeerAsync(ws));
            // 改 PIN、改名字、把别人加进免同意名单：和她这次会话无关
            await x.BobClient.PatchJsonAsync(url, new { pin = "135791", name = "书房电脑", trustedControllers = new[] { x.Carol.Id, x.AdminId } });
            Assert.Equal(RemoteSessionState.Active, (await (await x.CarolClient.GetAsync($"/api/remote/sessions/{session.Id}")).ReadAsync<RemoteSessionDto>()).State);

            // 把她移出免同意名单（还在远程名单里）：正在进行的这次立即结束
            await x.BobClient.PatchJsonAsync(url, new { trustedControllers = new[] { x.AdminId } });
            Assert.Equal((WebSocketCloseStatus)RelayCloseCodes.Ended, (await ReceiveAsync(ws)).Close);
        }
        Assert.Equal("stopped", (await (await x.CarolClient.GetAsync($"/api/remote/sessions/{session.Id}")).ReadAsync<RemoteSessionDto>()).EndReason);
        Assert.Equal(session.Id, (await hub.WaitAsync<AgentSessionEnded>(AgentHubEvents.SessionEnded)).SessionId);

        // 之后她再发起：回到等同意
        var asked = await StartAsync(x.CarolClient, agent.DeviceId);
        Assert.Equal((RemoteSessionState.Pending, RemoteConsentType.Prompt), (asked.State, asked.Consent));
        await x.CarolClient.PostAsync($"/api/remote/sessions/{asked.Id}/end", null);

        // 中继每次连接都再看一遍（就算会话因为别的原因还没结束）：人不在免同意名单里了就连不回来
        await x.BobClient.PatchJsonAsync(url, new { trustedControllers = new[] { x.Carol.Id } });
        var again = await StartAsync(x.CarolClient, agent.DeviceId);
        Assert.Equal(RemoteConsentType.Trusted, again.Consent);
        await x.App.WithDbAsync(db => db.Devices.Where(d => d.Id == agent.DeviceId).ExecuteUpdateAsync(s => s.SetProperty(d => d.TrustedControllers, "[]")));
        Assert.Contains("403", (await Assert.ThrowsAnyAsync<Exception>(() => ConnectControllerWsAsync(x.App, "carol", again.Id))).Message);
    }

    [PgFact]
    public async Task A_reconnecting_agent_is_told_about_sessions_it_missed()
    {
        await using var x = await SetupAsync();
        var agent = await RegisterAsync(x, x.BobClient);
        await x.BobClient.PatchJsonAsync($"/api/devices/{agent.DeviceId}", new { pin = "246810" });
        var first = await OnlineAsync(x, agent);
        var unattended = await StartAsync(x.Admin, agent.DeviceId, pin: "246810");
        await first.DisposeAsync();

        // 新连接（网络切换后）：进行中的无人值守会话也补发，带着还剩几秒
        await using var again = await OnlineAsync(x, agent);
        var request = await again.WaitAsync<AgentSessionRequest>(AgentHubEvents.SessionRequested);
        Assert.Equal((unattended.Id, true), (request.SessionId, request.Active));
    }

    [PgFact]
    public async Task One_client_cannot_hog_all_device_codes()
    {
        await using var x = await SetupAsync();
        var anon = x.App.Anonymous();
        var codes = new List<DeviceCodeResponse>();
        for (var i = 0; i < 6; i++)
            codes.Add(await (await anon.PostJsonAsync("/api/auth/device/code", new { name = $"pc{i}" })).ReadAsync<DeviceCodeResponse>());
        // 同一个来源最多同时挂 5 个：最早的那个被挤掉，Agent 轮询会拿到 404 自己换新码
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/api/auth/device/poll?code={codes[0].Code}&pollToken={codes[0].PollToken}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync($"/api/auth/device/poll?code={codes[5].Code}&pollToken={codes[5].PollToken}")).StatusCode);
        // 轮询凭证不对：当作不存在
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/api/auth/device/poll?code={codes[5].Code}&pollToken=nope")).StatusCode);
    }

    // ---- 快速线路：画面和声音经 Cloudflare Realtime（信令经我们的接口）----

    private static readonly object[] BothTracks = [new { mid = "0", kind = "audio" }, new { mid = "1", kind = "video" }];

    private static Task<HttpResponseMessage> PublishAsync(HttpClient c, Guid sessionId, object? tracks = null, string offer = "v=0 agent-offer")
        => c.PostJsonAsync($"/api/remote/sessions/{sessionId}/realtime/publish", new { offer, tracks = tracks ?? BothTracks });

    private static Task<HttpResponseMessage> PullAsync(HttpClient c, Guid sessionId) => c.PostJsonAsync($"/api/remote/sessions/{sessionId}/realtime/pull", new { });

    private static Task<HttpResponseMessage> AnswerAsync(HttpClient c, Guid sessionId, string answer = "v=0 browser-answer")
        => c.PostJsonAsync($"/api/remote/sessions/{sessionId}/realtime/renegotiate", new { answer });

    /// <summary>假 Cloudflare 上建过的会话，按先后顺序（从 tracks/new 的路径里取）。</summary>
    private static List<string> CloudflareSessions(ApiFactory app)
        => [.. app.Realtime.CallsTo("/tracks/new").Select(c => c.Path.Split('/')[^3]).Distinct()];

    private static Task CleanedUpAsync(ApiFactory app)
        => EventuallyAsync(() => app.Services.GetRequiredService<Features.Calls.CallCleanupQueue>().Pending == 0, "Cloudflare 那边的轨道关完");

    private static async Task<(Agent Agent, AgentHubClient Hub, RemoteSessionDto Session)> ActiveSessionAsync(Ctx x)
    {
        var agent = await RegisterAsync(x, x.BobClient);
        var hub = await OnlineAsync(x, agent);
        var session = await StartAsync(x.BobClient, agent.DeviceId);
        return (agent, hub, await ConsentAsync(agent, session.Id, true));
    }

    [PgFact]
    public async Task Fast_line_is_published_by_the_agent_and_pulled_by_the_controller()
    {
        await using var x = await SetupAsync();
        var (agent, hub, session) = await ActiveSessionAsync(x);
        await using var _ = hub;
        var url = $"/api/remote/sessions/{session.Id}/realtime";

        // 两边都先问能不能用、连 Cloudflare 用哪些 ICE 服务器（53 端口的浏览器会拦，不给）
        foreach (var party in new[] { agent.Http, x.BobClient })
        {
            var info = await (await party.GetAsync(url)).ReadAsync<RemoteRealtimeInfo>();
            Assert.True(info.Available);
            Assert.Contains(info.IceServers, s => s.Urls.Contains("stun:stun.cloudflare.com:3478"));
            var turn = Assert.Single(info.IceServers, s => s.Username == "turn-user");
            Assert.Equal("turn-pass", turn.Credential);
            Assert.DoesNotContain(turn.Urls, u => u.Contains(":53"));
        }
        // 只问有没有（被控端一连上就问，等着这个结果发第一份配置）：不去 Cloudflare 签发 TURN 凭据
        var turnCalls = x.App.Realtime.CallsTo("/generate-ice-servers").Count();
        var probe = await (await agent.Http.GetAsync(url + "?ice=false")).ReadAsync<RemoteRealtimeInfo>();
        Assert.True(probe.Available);
        Assert.Empty(probe.IceServers);
        Assert.Equal(turnCalls, x.App.Realtime.CallsTo("/generate-ice-servers").Count());
        // 不是这次会话的两方：当作没有这个会话（管理员、电脑的主人以外的人都一样）
        Assert.Equal(HttpStatusCode.NotFound, (await x.CarolClient.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await x.Admin.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PullAsync(x.CarolClient, session.Id)).StatusCode);

        // 被控端还没发布：没东西可拉
        Assert.Equal(HttpStatusCode.Conflict, (await PullAsync(x.BobClient, session.Id)).StatusCode);

        // 被控端发布：offer 换 answer；轨道名由服务器起，App Secret 只在服务器和 Cloudflare 之间
        var published = await (await PublishAsync(agent.Http, session.Id)).ReadAsync<RemoteRealtimePublishResponse>();
        var publishSession = Assert.Single(CloudflareSessions(x.App));
        Assert.Equal($"v=0 answer {publishSession}", published.Answer);
        var publishCall = Assert.Single(x.App.Realtime.CallsTo("/tracks/new"));
        Assert.Equal($"Bearer {FakeRealtime.AppSecret}", publishCall.Authorization);
        Assert.Equal("v=0 agent-offer", publishCall.Body!["sessionDescription"]!["sdp"]!.GetValue<string>());
        var sent = publishCall.Body["tracks"]!.AsArray();
        Assert.Equal(["local", "local"], sent.Select(t => t!["location"]!.GetValue<string>()));
        Assert.Equal(["0", "1"], sent.Select(t => t!["mid"]!.GetValue<string>()));
        Assert.StartsWith("audio-", sent[0]!["trackName"]!.GetValue<string>());
        Assert.StartsWith("video-", sent[1]!["trackName"]!.GetValue<string>());

        // 各干各的：控制端不能发布，被控端不能拉
        Assert.Equal(HttpStatusCode.Forbidden, (await PublishAsync(x.BobClient, session.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PullAsync(agent.Http, session.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await AnswerAsync(agent.Http, session.Id)).StatusCode);

        // 控制端拉流：拿到 Cloudflare 的 offer 和两条轨道，交回 answer 才算接上
        var pulled = await (await PullAsync(x.BobClient, session.Id)).ReadAsync<RemoteRealtimePullResponse>();
        var pullSession = CloudflareSessions(x.App)[1];
        Assert.Equal($"v=0 offer {pullSession}", pulled.Offer);
        Assert.Equal(["audio", "video"], pulled.Tracks.Select(t => t.Kind));
        Assert.All(pulled.Tracks, t => Assert.False(string.IsNullOrEmpty(t.Mid)));
        var pullCall = x.App.Realtime.CallsTo("/tracks/new")[1];
        Assert.All(pullCall.Body!["tracks"]!.AsArray(), t => Assert.Equal(("remote", publishSession), (t!["location"]!.GetValue<string>(), t["sessionId"]!.GetValue<string>())));
        Assert.True(x.App.Realtime.AwaitingAnswer(pullSession));
        Assert.Equal(HttpStatusCode.NoContent, (await AnswerAsync(x.BobClient, session.Id)).StatusCode);
        Assert.False(x.App.Realtime.AwaitingAnswer(pullSession));
        Assert.Equal("v=0 browser-answer", x.App.Realtime.CallsTo("/renegotiate").Single().Body!["sessionDescription"]!["sdp"]!.GetValue<string>());

        // 控制端换了个标签页重新拉：以前拉的那份让 Cloudflare 停掉
        var again = await (await PullAsync(x.BobClient, session.Id)).ReadAsync<RemoteRealtimePullResponse>();
        var secondPull = CloudflareSessions(x.App)[2];
        Assert.Equal($"v=0 offer {secondPull}", again.Offer);
        await CleanedUpAsync(x.App);
        Assert.Equal(pulled.Tracks.Select(t => t.Mid).Order(), x.App.Realtime.ClosedMids(pullSession).Order());
        Assert.Empty(x.App.Realtime.ClosedMids(publishSession));

        // 被控端重新发布（它那条连接断了重连）：旧的发布和旧的拉流都作废，控制端要重新拉
        await (await PublishAsync(agent.Http, session.Id)).ReadAsync<RemoteRealtimePublishResponse>();
        await CleanedUpAsync(x.App);
        Assert.Equal(["0", "1"], x.App.Realtime.ClosedMids(publishSession).Order());
        Assert.Equal(again.Tracks.Select(t => t.Mid).Order(), x.App.Realtime.ClosedMids(secondPull).Order());
        Assert.Equal(HttpStatusCode.Conflict, (await AnswerAsync(x.BobClient, session.Id)).StatusCode);
        var republished = CloudflareSessions(x.App)[3];
        var last = await (await PullAsync(x.BobClient, session.Id)).ReadAsync<RemoteRealtimePullResponse>();
        var lastPull = CloudflareSessions(x.App)[4];

        // 会话结束：两边在 Cloudflare 的轨道都关掉，之后不能再发布、再拉
        await (await x.BobClient.PostAsync($"/api/remote/sessions/{session.Id}/end", null)).ReadAsync<RemoteSessionDto>();
        await CleanedUpAsync(x.App);
        Assert.Equal(["0", "1"], x.App.Realtime.ClosedMids(republished).Order());
        Assert.Equal(last.Tracks.Select(t => t.Mid).Order(), x.App.Realtime.ClosedMids(lastPull).Order());
        Assert.Equal(0, x.App.Services.GetRequiredService<RemoteRealtimeStore>().Count);
        Assert.Equal(HttpStatusCode.Conflict, (await PublishAsync(agent.Http, session.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await PullAsync(x.BobClient, session.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await agent.Http.GetAsync(url)).StatusCode);
    }

    [PgFact]
    public async Task Fast_line_needs_an_active_session_and_a_sane_request()
    {
        await using var x = await SetupAsync();
        var agent = await RegisterAsync(x, x.BobClient);
        await using var hub = await OnlineAsync(x, agent);
        var session = await StartAsync(x.BobClient, agent.DeviceId);

        // 还在等电脑前的人点同意：不能发布
        Assert.Equal(RemoteSessionState.Pending, session.State);
        Assert.Equal(HttpStatusCode.Conflict, (await agent.Http.GetAsync($"/api/remote/sessions/{session.Id}/realtime")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await PublishAsync(agent.Http, session.Id)).StatusCode);
        await ConsentAsync(agent, session.Id, true);

        Assert.Equal(HttpStatusCode.BadRequest, (await PublishAsync(agent.Http, session.Id, offer: "")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PublishAsync(agent.Http, session.Id, offer: new string('x', 200_001))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PublishAsync(agent.Http, session.Id, tracks: Array.Empty<object>())).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PublishAsync(agent.Http, session.Id, tracks: new object[] { new { mid = "0", kind = "video" }, new { mid = "1", kind = "video" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PublishAsync(agent.Http, session.Id, tracks: new object[] { new { mid = "0", kind = "text" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PublishAsync(agent.Http, session.Id, tracks: new object[] { new { mid = "0", kind = "audio" }, new { mid = "0", kind = "video" } })).StatusCode);
        // 上面这些都没到 Cloudflare
        Assert.Empty(x.App.Realtime.CallsTo("/sessions/"));

        // 只发画面、不带声音也可以
        await (await PublishAsync(agent.Http, session.Id, tracks: new object[] { new { mid = "0", kind = "video" } })).ReadAsync<RemoteRealtimePublishResponse>();
        var pulled = await (await PullAsync(x.BobClient, session.Id)).ReadAsync<RemoteRealtimePullResponse>();
        Assert.Equal("video", Assert.Single(pulled.Tracks).Kind);
        Assert.Equal(HttpStatusCode.BadRequest, (await AnswerAsync(x.BobClient, session.Id, answer: " ")).StatusCode);
    }

    [PgFact]
    public async Task Fast_line_reports_cloudflare_trouble_without_breaking_the_session()
    {
        await using var x = await SetupAsync();
        var (agent, hub, session) = await ActiveSessionAsync(x);
        await using var _ = hub;

        // Cloudflare 报错、单条轨道被拒：发布失败（被控端据此退回中继），会话本身不受影响
        x.App.Realtime.FailNext("/tracks/new", HttpStatusCode.InternalServerError, "internal_error");
        Assert.Equal(HttpStatusCode.BadGateway, (await PublishAsync(agent.Http, session.Id)).StatusCode);
        x.App.Realtime.NextTrackError = "invalid_track";
        Assert.Equal(HttpStatusCode.BadGateway, (await PublishAsync(agent.Http, session.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await PullAsync(x.BobClient, session.Id)).StatusCode); // 没发布成，没东西可拉

        await (await PublishAsync(agent.Http, session.Id)).ReadAsync<RemoteRealtimePublishResponse>();
        // 拉的时候被控端那条连接已经没了：让控制端等它重新发布
        x.App.Realtime.NextTrackError = "not_found_track_error";
        Assert.Equal(HttpStatusCode.Conflict, (await PullAsync(x.BobClient, session.Id)).StatusCode);
        await (await PullAsync(x.BobClient, session.Id)).ReadAsync<RemoteRealtimePullResponse>();
        // 交 answer 时 Cloudflare 那边的会话已经过期
        x.App.Realtime.FailNext("/renegotiate", HttpStatusCode.Gone, "session_error");
        Assert.Equal(HttpStatusCode.Gone, (await AnswerAsync(x.BobClient, session.Id)).StatusCode);

        Assert.Equal(RemoteSessionState.Active, (await (await x.BobClient.GetAsync($"/api/remote/sessions/{session.Id}")).ReadAsync<RemoteSessionDto>()).State);
    }

    [PgFact]
    public async Task Fast_line_is_unavailable_when_cloudflare_realtime_is_not_configured()
    {
        await using var x = await SetupAsync(realtimeConfigured: false);
        var (agent, hub, session) = await ActiveSessionAsync(x);
        await using var _ = hub;

        var info = await (await agent.Http.GetAsync($"/api/remote/sessions/{session.Id}/realtime")).ReadAsync<RemoteRealtimeInfo>();
        Assert.False(info.Available);
        Assert.Empty(info.IceServers);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await PublishAsync(agent.Http, session.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await PullAsync(x.BobClient, session.Id)).StatusCode);
        Assert.Empty(x.App.Realtime.Calls);
    }
}

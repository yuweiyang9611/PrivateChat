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

    private static async Task<Ctx> SetupAsync()
    {
        var app = new ApiFactory();
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
}

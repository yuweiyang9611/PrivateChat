using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PrivateChat.Api.Domain;
using PrivateChat.Api.Features.Auth;
using PrivateChat.Api.Features.Remote;
using PrivateChat.Api.Infrastructure;
using PrivateChat.Contracts.Remote;

namespace PrivateChat.Api.Tests.Integration;

/// <summary>远程协助遥测的样例数据（一台装着 GTX 1050 Ti 的 Windows 11 电脑、一次一分钟的远程）。</summary>
internal static class TelemetrySamples
{
    public static AgentTelemetrySnapshot Snapshot(string version = "0.8.6", string cpu = "Intel(R) Core(TM) i5-8400 CPU @ 2.80GHz") => new(
        AgentVersion: version,
        CollectedAt: DateTimeOffset.UtcNow,
        Fingerprint: "3f1c0b9e",
        Os: new TelemetryOs("Windows 11 Pro", "24H2", 26100, 2454, "Professional", true),
        Cpu: new TelemetryCpu(cpu, 6, 6, 2808),
        MemoryBytes: 16L << 30,
        Gpus: [new TelemetryGpu("NVIDIA GeForce GTX 1050 Ti", 0x10DE, 0x1C82, "32.0.15.6094", 4L << 30, false, [@"\\.\DISPLAY1"])],
        Displays: [new TelemetryDisplay(@"\\.\DISPLAY1", 1920, 1080, 60, 100, true, "NVIDIA GeForce GTX 1050 Ti")],
        Encoders:
        [
            new TelemetryEncoder("Microsoft H.264 Video Encoder MFT", false, 0, true, false, ["CBR", "Quality"], true, false, true, true, false,
                new TelemetryEncodeTest(1920, 1080, 30, 30, 21.5, 9.2, 12.4, 180_000, null), null),
            new TelemetryEncoder("NVIDIA H.264 Encoder MFT", true, 0x10DE, true, true, ["CBR", "VBR", "LowDelayVBR"], true, true, true, true, true,
                null, "MF_E_TRANSFORM_NEED_MORE_INPUT"),
        ],
        Capture: new TelemetryCapture([new TelemetryDuplication(@"\\.\DISPLAY1", "NVIDIA GeForce GTX 1050 Ti", true, 16.6, null)], 18.3, 24.9, null),
        RemoteDesktopSession: false,
        Problems: []);

    public static TelemetryStat Stat(double p50) => new(60, p50, p50 / 2, p50 * 0.8, p50, p50 * 1.2, p50 * 1.5);

    public static AgentSessionTelemetry Agent(bool final = false, int seconds = 60) => new(
        AgentVersion: "0.8.6", Final: final,
        VideoSeconds: seconds, RelaySeconds: 0, RealtimeSeconds: seconds,
        Encoder: "Microsoft H.264（软件）", QualityMode: true, Capture: "GDI",
        Settings: [new TelemetrySettingsUse(RemoteVideoMode.Auto, 1920, 1200, 30, 8000, seconds)],
        Sizes: [new TelemetrySizeUse(1920, 1080, seconds)],
        SentFps: Stat(28), SentKbps: Stat(3000), TargetKbps: Stat(4000),
        CaptureMs: Stat(9), EncodeMs: Stat(12), CpuPercent: Stat(35), RttMs: Stat(8),
        CpuBoundSeconds: 2, SecureDesktopSeconds: 0,
        AutoNetworkSteps: 0, AutoCpuSteps: 0, AutoCpuUndos: 0,
        KeyFramesRequested: 1, Stalls: 0, RelaySendDrops: 0,
        MaxFpsCaps: 0, MinMaxFps: 0,
        LineConnects: 1, LineFailures: 0, LineProblems: [],
        LossPermille: Stat(1), ReceivedKbps: Stat(2900), JitterBufferMs: Stat(40), ViewerFps: Stat(27),
        Errors: []);

    public static ViewerSessionTelemetry Viewer(bool final = false, int seconds = 60, string browser = "Edge 131") => new(
        Final: final, Browser: browser, Platform: "Windows",
        ScreenWidth: 1920, ScreenHeight: 1080, DevicePixelRatio: 1.25, HardwareConcurrency: 8,
        Seconds: seconds, RelaySeconds: 0, RealtimeSeconds: seconds,
        DecodedFps: Stat(27),
        Freezes: 1, LongestFreezeMs: 650, FreezeMsTotal: 650,
        KeyFrameRequests: 0, MaxFpsCaps: 0, MinMaxFps: 0,
        RealtimeReconnects: 0, LineProblems: [],
        JitterBufferMs: Stat(40), LossPermille: Stat(1),
        Errors: []);
}

/// <summary>
/// 远程协助遥测：谁能报（被控端自己 / 这次发起远程的人）、什么时候收（进行中到结束后 24 小时）、结束汇总不被晚到的中途汇总盖掉、请求体限长和清理、
/// 只有管理员能看、90 天后删掉、吊销后不能再报。对真实数据库跑。
/// </summary>
public class RemoteTelemetryTests
{
    private sealed record Ctx(ApiFactory App, HttpClient Admin, Guid AdminId, User Bob, HttpClient BobClient, User Carol, HttpClient CarolClient) : IAsyncDisposable
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
        return new Ctx(app, admin, adminId, bob, await app.LoginAsync("bob"), carol, await app.LoginAsync("carol"));
    }

    private sealed record Agent(Guid DeviceId, string Token, HttpClient Http);

    /// <summary>走一遍设备码（同 RemoteTests）：confirmer 在网页确认，可替 owner 添加；不给 owner 时电脑归 confirmer。</summary>
    private static async Task<Agent> RegisterAsync(Ctx x, HttpClient confirmer, Guid? ownerId = null, string name = "爸爸的电脑")
    {
        var anon = x.App.Anonymous();
        var code = await (await anon.PostJsonAsync("/api/auth/device/code", new { name, platform = "windows" })).ReadAsync<DeviceCodeResponse>();
        var confirmed = await (await confirmer.PostJsonAsync("/api/auth/device/confirm", new { code = code.Code, ownerId })).ReadAsync<DeviceConfirmResponse>();
        var poll = await (await anon.GetAsync($"/api/auth/device/poll?code={code.Code}&pollToken={code.PollToken}")).ReadAsync<DevicePollResponse>();
        Assert.Equal("approved", poll.Status);
        var http = x.App.Anonymous();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", poll.DeviceToken);
        return new Agent(confirmed.DeviceId, poll.DeviceToken!, http);
    }

    /// <summary>Agent 连上 /hubs/agent（发起远程要它在线）。</summary>
    private static async Task<HubConnection> OnlineAsync(Ctx x, Agent agent)
    {
        var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(x.App.Server.BaseAddress, "hubs/agent"), o =>
            {
                o.HttpMessageHandlerFactory = _ => x.App.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;
                o.AccessTokenProvider = () => Task.FromResult<string?>(agent.Token);
            })
            .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
            .Build();
        await hub.StartAsync();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!x.App.Services.GetRequiredService<AgentRegistry>().IsOnline(agent.DeviceId))
        {
            if (DateTime.UtcNow > deadline) throw new Xunit.Sdk.XunitException("等不到：Agent 在线");
            await Task.Delay(25);
        }
        await hub.InvokeAsync(AgentHubEvents.Report, new AgentReport("0.8.6", "Windows 11", [new AgentScreen(1920, 1080, true)]));
        return hub;
    }

    /// <summary>controller 发起控制，被控端点同意：返回进行中的会话。</summary>
    private static async Task<RemoteSessionDto> ActiveSessionAsync(Agent agent, HttpClient controller)
    {
        var session = await (await controller.PostJsonAsync("/api/remote/sessions", new { targetDeviceId = agent.DeviceId, mode = "Control" })).ReadAsync<RemoteSessionDto>();
        if (session.State == RemoteSessionState.Active) return session;
        return await (await agent.Http.PostJsonAsync($"/api/remote/sessions/{session.Id}/consent", new { accept = true })).ReadAsync<RemoteSessionDto>();
    }

    private static async Task EndAsync(HttpClient c, Guid sessionId)
        => await (await c.PostAsync($"/api/remote/sessions/{sessionId}/end", null)).ReadAsync<RemoteSessionDto>();

    private const string SnapshotUrl = "/api/remote/agent/telemetry";
    private static string AgentUrl(Guid sessionId) => $"/api/remote/sessions/{sessionId}/telemetry";
    private static string ViewerUrl(Guid sessionId) => $"/api/remote/sessions/{sessionId}/viewer-telemetry";
    private static string DiagnosticsUrl(Guid deviceId) => $"/api/telemetry/devices/{deviceId}";

    private static Task<HttpResponseMessage> PutSnapshotAsync(HttpClient c, AgentTelemetrySnapshot s) => c.PutAsJsonAsync(SnapshotUrl, s, ApiFactory.Json);
    private static Task<HttpResponseMessage> PutAgentAsync(HttpClient c, Guid sessionId, AgentSessionTelemetry t) => c.PutAsJsonAsync(AgentUrl(sessionId), t, ApiFactory.Json);
    private static Task<HttpResponseMessage> PostViewerAsync(HttpClient c, Guid sessionId, ViewerSessionTelemetry t) => c.PostAsJsonAsync(ViewerUrl(sessionId), t, ApiFactory.Json);

    private static StringContent Json(string json, string contentType = "application/json") => new(json, Encoding.UTF8, contentType);

    private static async Task<DeviceTelemetryDto> DiagnosticsAsync(HttpClient c, Guid deviceId, int? limit = null)
        => await (await c.GetAsync(DiagnosticsUrl(deviceId) + (limit is { } l ? $"?limit={l}" : ""))).ReadAsync<DeviceTelemetryDto>();

    private static string Serialized(object? value) => JsonSerializer.Serialize(value, ApiFactory.Json);

    private static async Task<int> PurgeAsync(ApiFactory app, DateTimeOffset now)
    {
        using var scope = app.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<RemoteTelemetryService>().PurgeTelemetryAsync(now, default);
    }

    // ---- 测试 ----

    [PgFact]
    public async Task Agent_reports_its_hardware_and_a_newer_report_replaces_the_old_one()
    {
        await using var x = await SetupAsync();
        var agent = await RegisterAsync(x, x.BobClient);

        // 还没报过：诊断里只有设备名
        var empty = await DiagnosticsAsync(x.Admin, agent.DeviceId);
        Assert.Equal((agent.DeviceId, "爸爸的电脑"), (empty.DeviceId, empty.DeviceName));
        Assert.Null(empty.ReportedAt);
        Assert.Null(empty.Snapshot);
        Assert.Empty(empty.Sessions);

        Assert.Equal(HttpStatusCode.NoContent, (await PutSnapshotAsync(agent.Http, TelemetrySamples.Snapshot())).StatusCode);
        var first = await DiagnosticsAsync(x.Admin, agent.DeviceId);
        Assert.NotNull(first.ReportedAt);
        var s = first.Snapshot!;
        Assert.Equal(("0.8.6", "Intel(R) Core(TM) i5-8400 CPU @ 2.80GHz", 26100, 16L << 30), (s.AgentVersion, s.Cpu.Name, s.Os.Build, s.MemoryBytes));
        var gpu = Assert.Single(s.Gpus);
        Assert.Equal(("NVIDIA GeForce GTX 1050 Ti", 0x10DE, "32.0.15.6094"), (gpu.Name, gpu.VendorId, gpu.DriverVersion));
        Assert.Equal([@"\\.\DISPLAY1"], gpu.Outputs);
        Assert.Equal(["Microsoft H.264 Video Encoder MFT", "NVIDIA H.264 Encoder MFT"], s.Encoders.Select(e => e.Name));
        Assert.Equal(9.2, s.Encoders[0].Test!.AvgMs);
        Assert.Equal(("MF_E_TRANSFORM_NEED_MORE_INPUT", true), (s.Encoders[1].Error, s.Encoders[1].PerFrameQp));
        Assert.True(Assert.Single(s.Capture.Duplication).Ok);
        Assert.Equal((double?)18.3, s.Capture.GdiNativeMs);
        Assert.Equal((double?)24.9, s.Capture.GdiScaledMs);

        // 重连后同一份再报一次：还是一行，内容不变
        Assert.Equal(HttpStatusCode.NoContent, (await PutSnapshotAsync(agent.Http, TelemetrySamples.Snapshot() with { CollectedAt = s.CollectedAt })).StatusCode);
        Assert.Equal(1, await x.App.WithDbAsync(db => db.DeviceTelemetry.CountAsync()));
        Assert.Equal(Serialized(s), Serialized((await DiagnosticsAsync(x.Admin, agent.DeviceId)).Snapshot));

        // 升了版本、换了电脑里的东西：覆盖
        Assert.Equal(HttpStatusCode.NoContent, (await PutSnapshotAsync(agent.Http, TelemetrySamples.Snapshot("0.8.7", "AMD Ryzen 7 7840HS w/ Radeon 780M Graphics"))).StatusCode);
        var second = await DiagnosticsAsync(x.Admin, agent.DeviceId);
        Assert.Equal(("0.8.7", "AMD Ryzen 7 7840HS w/ Radeon 780M Graphics"), (second.Snapshot!.AgentVersion, second.Snapshot.Cpu.Name));
        Assert.True(second.ReportedAt >= first.ReportedAt);
        var row = await x.App.WithDbAsync(db => db.DeviceTelemetry.SingleAsync());
        Assert.Equal((agent.DeviceId, "0.8.7"), (row.DeviceId, row.AgentVersion));

        // 浏览器登录的人（电脑的主人、管理员都一样）不能替电脑报；没登录 401
        Assert.Equal(HttpStatusCode.Forbidden, (await PutSnapshotAsync(x.BobClient, TelemetrySamples.Snapshot("9.9.9"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PutSnapshotAsync(x.Admin, TelemetrySamples.Snapshot("9.9.9"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PutSnapshotAsync(x.App.Anonymous(), TelemetrySamples.Snapshot("9.9.9"))).StatusCode);
        Assert.Equal("0.8.7", (await DiagnosticsAsync(x.Admin, agent.DeviceId)).Snapshot!.AgentVersion);

        // 放开 /api/remote/agent 没有顺带放开别的：设备令牌照样进不了 /api/devices
        Assert.Equal(HttpStatusCode.Unauthorized, (await agent.Http.GetAsync("/api/devices")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await agent.Http.PatchJsonAsync($"/api/devices/{agent.DeviceId}", new { name = "改个名" })).StatusCode);
    }

    [PgFact]
    public async Task Session_telemetry_comes_only_from_this_sessions_agent_and_controller()
    {
        await using var x = await SetupAsync();
        var agent = await RegisterAsync(x, x.BobClient);
        var other = await RegisterAsync(x, x.CarolClient, name: "Carol 的电脑");
        await using var hub = await OnlineAsync(x, agent);
        var session = await ActiveSessionAsync(agent, x.Admin); // 管理员控制 Bob 的电脑

        Assert.Equal(HttpStatusCode.NoContent, (await PutAgentAsync(agent.Http, session.Id, TelemetrySamples.Agent())).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await PostViewerAsync(x.Admin, session.Id, TelemetrySamples.Viewer())).StatusCode);

        // 被控端那列：别的电脑当作没有这个会话；没有的会话 404；人（哪怕是控制的人）403
        Assert.Equal(HttpStatusCode.NotFound, (await PutAgentAsync(other.Http, session.Id, TelemetrySamples.Agent(seconds: 1))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PutAgentAsync(agent.Http, Guid.NewGuid(), TelemetrySamples.Agent(seconds: 1))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PutAgentAsync(x.Admin, session.Id, TelemetrySamples.Agent(seconds: 1))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PutAgentAsync(x.BobClient, session.Id, TelemetrySamples.Agent(seconds: 1))).StatusCode);

        // 控制端那列：只认发起的人；电脑的主人、别的家人当作没有；设备令牌（这台、别的都一样）403
        Assert.Equal(HttpStatusCode.NotFound, (await PostViewerAsync(x.BobClient, session.Id, TelemetrySamples.Viewer(browser: "Bob"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostViewerAsync(x.CarolClient, session.Id, TelemetrySamples.Viewer(browser: "Carol"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostViewerAsync(x.Admin, Guid.NewGuid(), TelemetrySamples.Viewer(browser: "Admin"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostViewerAsync(agent.Http, session.Id, TelemetrySamples.Viewer(browser: "Agent"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostViewerAsync(other.Http, session.Id, TelemetrySamples.Viewer(browser: "Other"))).StatusCode);

        // 被拒的都没进库：一行，两列都是各自那一份
        var row = await x.App.WithDbAsync(db => db.RemoteSessionTelemetry.SingleAsync());
        Assert.Equal((session.Id, agent.DeviceId), (row.SessionId, row.DeviceId));
        var seen = Assert.Single((await DiagnosticsAsync(x.Admin, agent.DeviceId)).Sessions);
        Assert.Equal((60, "Edge 131"), (seen.Agent!.VideoSeconds, seen.Viewer!.Browser));
        Assert.Empty((await DiagnosticsAsync(x.Admin, other.DeviceId)).Sessions);

        // Bob 控制他自己的电脑：管理员不是这次的哪一方，管理员的身份在这里不算数——报控制端那列当作没有这个会话（404）；
        // 管理员自己那台电脑上的设备令牌（带着管理员的角色）也一样：报被控端那列 404，报控制端那列 403
        await EndAsync(x.Admin, session.Id);
        var bobs = await ActiveSessionAsync(agent, x.BobClient);
        var adminsPc = await RegisterAsync(x, x.Admin, name: "管理员的电脑");
        Assert.Equal(HttpStatusCode.NoContent, (await PutAgentAsync(agent.Http, bobs.Id, TelemetrySamples.Agent(seconds: 30))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await PostViewerAsync(x.BobClient, bobs.Id, TelemetrySamples.Viewer(seconds: 30, browser: "Bob 的 Chrome"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostViewerAsync(x.Admin, bobs.Id, TelemetrySamples.Viewer(seconds: 999, browser: "Admin"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PutAgentAsync(adminsPc.Http, bobs.Id, TelemetrySamples.Agent(seconds: 999))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostViewerAsync(adminsPc.Http, bobs.Id, TelemetrySamples.Viewer(seconds: 999, browser: "管理员的电脑"))).StatusCode);

        // 那一行里只有 Bob 的电脑和 Bob 的浏览器报的；管理员的电脑名下没有会话
        var bobsRow = await x.App.WithDbAsync(db => db.RemoteSessionTelemetry.SingleAsync(t => t.SessionId == bobs.Id));
        Assert.Equal(agent.DeviceId, bobsRow.DeviceId);
        var seenBobs = Assert.Single((await DiagnosticsAsync(x.Admin, agent.DeviceId)).Sessions, s => s.SessionId == bobs.Id);
        Assert.Equal(("bob", 30, 30, "Bob 的 Chrome"), (seenBobs.ControllerName, seenBobs.Agent!.VideoSeconds, seenBobs.Viewer!.Seconds, seenBobs.Viewer.Browser));
        Assert.Empty((await DiagnosticsAsync(x.Admin, adminsPc.DeviceId)).Sessions);
        Assert.Equal(2, await x.App.WithDbAsync(db => db.RemoteSessionTelemetry.CountAsync()));
    }

    [PgFact]
    public async Task Session_telemetry_is_accepted_while_active_and_for_a_day_after_the_end()
    {
        await using var x = await SetupAsync();
        var agent = await RegisterAsync(x, x.BobClient);
        await using var hub = await OnlineAsync(x, agent);

        // 等同意中：还不收；被拒绝的（没开始过）也不收
        var pending = await (await x.Admin.PostJsonAsync("/api/remote/sessions", new { targetDeviceId = agent.DeviceId, mode = "Control" })).ReadAsync<RemoteSessionDto>();
        Assert.Equal(RemoteSessionState.Pending, pending.State);
        Assert.Equal(HttpStatusCode.Conflict, (await PutAgentAsync(agent.Http, pending.Id, TelemetrySamples.Agent())).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await PostViewerAsync(x.Admin, pending.Id, TelemetrySamples.Viewer())).StatusCode);
        await (await agent.Http.PostJsonAsync($"/api/remote/sessions/{pending.Id}/consent", new { accept = false })).ReadAsync<RemoteSessionDto>();
        Assert.Equal(HttpStatusCode.Conflict, (await PutAgentAsync(agent.Http, pending.Id, TelemetrySamples.Agent(final: true))).StatusCode);

        // 进行中收；挂断以后两边的结束汇总照样收（被控端的总在结束之后才到）
        var session = await ActiveSessionAsync(agent, x.Admin);
        Assert.Equal(HttpStatusCode.NoContent, (await PutAgentAsync(agent.Http, session.Id, TelemetrySamples.Agent(seconds: 60))).StatusCode);
        await EndAsync(x.Admin, session.Id);
        Assert.Equal(HttpStatusCode.NoContent, (await PutAgentAsync(agent.Http, session.Id, TelemetrySamples.Agent(final: true, seconds: 75))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await PostViewerAsync(x.Admin, session.Id, TelemetrySamples.Viewer(final: true, seconds: 74))).StatusCode);

        // 同一份结束汇总再发一次（被控端重试、sendBeacon 和 fetch 都到了）：还是一行，内容一样
        var before = Assert.Single((await DiagnosticsAsync(x.Admin, agent.DeviceId)).Sessions);
        Assert.Equal(HttpStatusCode.NoContent, (await PutAgentAsync(agent.Http, session.Id, TelemetrySamples.Agent(final: true, seconds: 75))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await PostViewerAsync(x.Admin, session.Id, TelemetrySamples.Viewer(final: true, seconds: 74))).StatusCode);
        Assert.Equal(1, await x.App.WithDbAsync(db => db.RemoteSessionTelemetry.CountAsync()));
        var after = Assert.Single((await DiagnosticsAsync(x.Admin, agent.DeviceId)).Sessions);
        Assert.Equal((Serialized(before.Agent), Serialized(before.Viewer)), (Serialized(after.Agent), Serialized(after.Viewer)));
        Assert.Equal((true, 75, true, 74), (after.Agent!.Final, after.Agent.VideoSeconds, after.Viewer!.Final, after.Viewer.Seconds));

        // 结束 23 小时：还收；超过 24 小时：不收了（被控端把 409 当作"这次不收了"，不再重试）
        await x.App.WithDbAsync(db => db.RemoteSessions.Where(s => s.Id == session.Id).ExecuteUpdateAsync(set => set.SetProperty(s => s.EndedAt, DateTimeOffset.UtcNow.AddHours(-23))));
        Assert.Equal(HttpStatusCode.NoContent, (await PutAgentAsync(agent.Http, session.Id, TelemetrySamples.Agent(final: true, seconds: 76))).StatusCode);
        await x.App.WithDbAsync(db => db.RemoteSessions.Where(s => s.Id == session.Id).ExecuteUpdateAsync(set => set.SetProperty(s => s.EndedAt, DateTimeOffset.UtcNow.AddHours(-25))));
        Assert.Equal(HttpStatusCode.Conflict, (await PutAgentAsync(agent.Http, session.Id, TelemetrySamples.Agent(final: true, seconds: 99))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await PostViewerAsync(x.Admin, session.Id, TelemetrySamples.Viewer(final: true, seconds: 99))).StatusCode);
        var last = Assert.Single((await DiagnosticsAsync(x.Admin, agent.DeviceId)).Sessions);
        Assert.Equal((76, 74), (last.Agent!.VideoSeconds, last.Viewer!.Seconds));
    }

    [PgFact]
    public async Task A_final_summary_is_kept_against_late_checkpoints_unless_they_counted_further()
    {
        await using var x = await SetupAsync();
        var agent = await RegisterAsync(x, x.BobClient);
        await using var hub = await OnlineAsync(x, agent);
        var session = await ActiveSessionAsync(agent, x.Admin);
        async Task<SessionTelemetryDto> RowAsync() => Assert.Single((await DiagnosticsAsync(x.Admin, agent.DeviceId)).Sessions);
        Task<DateTimeOffset> UpdatedAtAsync() => x.App.WithDbAsync(db => db.RemoteSessionTelemetry.Select(t => t.UpdatedAt).SingleAsync());

        // 被控端：结束汇总到了以后，排在后面的中途汇总（60 秒那次的重试、秒数一样的）照样回 204（不然被控端就不再报了），但不覆盖
        Assert.Equal(HttpStatusCode.NoContent, (await PutAgentAsync(agent.Http, session.Id, TelemetrySamples.Agent(seconds: 60))).StatusCode);
        await EndAsync(x.Admin, session.Id);
        Assert.Equal(HttpStatusCode.NoContent, (await PutAgentAsync(agent.Http, session.Id, TelemetrySamples.Agent(final: true, seconds: 75))).StatusCode);
        var finalAt = await UpdatedAtAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await PutAgentAsync(agent.Http, session.Id, TelemetrySamples.Agent(seconds: 60))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await PutAgentAsync(agent.Http, session.Id, TelemetrySamples.Agent(seconds: 75))).StatusCode);
        var kept = await RowAsync();
        Assert.Equal((true, 75), (kept.Agent!.Final, kept.Agent.VideoSeconds));
        Assert.Null(kept.Viewer);
        Assert.Equal(finalAt, await UpdatedAtAsync()); // 没覆盖的连更新时间也不动
        // 秒数比结束汇总多的中途汇总是后来的、更全的数据：照常覆盖；之后的结束汇总也照常覆盖
        Assert.Equal(HttpStatusCode.NoContent, (await PutAgentAsync(agent.Http, session.Id, TelemetrySamples.Agent(seconds: 90))).StatusCode);
        var later = await RowAsync();
        Assert.Equal((false, 90), (later.Agent!.Final, later.Agent.VideoSeconds));
        Assert.Equal(HttpStatusCode.NoContent, (await PutAgentAsync(agent.Http, session.Id, TelemetrySamples.Agent(final: true, seconds: 95))).StatusCode);
        var finalAgain = (await RowAsync()).Agent!;
        Assert.Equal((true, 95), (finalAgain.Final, finalAgain.VideoSeconds));

        // 控制端也一样，按 Seconds 比：被控端那列是结束汇总不妨碍控制端这列的中途汇总进来
        Assert.Equal(HttpStatusCode.NoContent, (await PostViewerAsync(x.Admin, session.Id, TelemetrySamples.Viewer(seconds: 30))).StatusCode);
        var firstViewer = (await RowAsync()).Viewer!;
        Assert.Equal((false, 30), (firstViewer.Final, firstViewer.Seconds));
        // 关页面时的结束汇总之后，还在路上的旧中途汇总不覆盖它
        Assert.Equal(HttpStatusCode.NoContent, (await PostViewerAsync(x.Admin, session.Id, TelemetrySamples.Viewer(final: true, seconds: 74))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await PostViewerAsync(x.Admin, session.Id, TelemetrySamples.Viewer(seconds: 60))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await PostViewerAsync(x.Admin, session.Id, TelemetrySamples.Viewer(seconds: 74))).StatusCode);
        var viewerKept = await RowAsync();
        Assert.Equal((true, 74), (viewerKept.Viewer!.Final, viewerKept.Viewer.Seconds));
        // 刷新回来的页面从存着的累计数接着数：秒数更多，照收
        Assert.Equal(HttpStatusCode.NoContent, (await PostViewerAsync(x.Admin, session.Id, TelemetrySamples.Viewer(seconds: 80, browser: "Edge 131（刷新后）"))).StatusCode);
        var reloaded = await RowAsync();
        Assert.Equal((false, 80, "Edge 131（刷新后）"), (reloaded.Viewer!.Final, reloaded.Viewer.Seconds, reloaded.Viewer.Browser));
        Assert.Equal(HttpStatusCode.NoContent, (await PostViewerAsync(x.Admin, session.Id, TelemetrySamples.Viewer(final: true, seconds: 100))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await PostViewerAsync(x.Admin, session.Id, TelemetrySamples.Viewer(seconds: 80))).StatusCode);
        var end = await RowAsync();
        Assert.Equal((true, 100), (end.Viewer!.Final, end.Viewer.Seconds));
        Assert.Equal((true, 95), (end.Agent!.Final, end.Agent.VideoSeconds)); // 控制端那列怎么写都不碰被控端那列
        Assert.Equal(1, await x.App.WithDbAsync(db => db.RemoteSessionTelemetry.CountAsync()));
    }

    [PgFact]
    public async Task Telemetry_bodies_are_size_limited_and_cleaned_before_storing()
    {
        await using var x = await SetupAsync();
        var agent = await RegisterAsync(x, x.BobClient);
        await using var hub = await OnlineAsync(x, agent);
        var session = await ActiveSessionAsync(agent, x.Admin);

        // 超过 64 KB：413（分块传的、带 Content-Length 的都一样）；不是 JSON 415；JSON 不对 400。都不进库
        var huge = TelemetrySamples.Snapshot() with { Problems = [new string('x', 70_000)] };
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await PutSnapshotAsync(agent.Http, huge)).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await agent.Http.PutAsync(SnapshotUrl, Json(Serialized(huge)))).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await x.Admin.PostAsync(ViewerUrl(session.Id), Json(Serialized(TelemetrySamples.Viewer() with { Errors = [new string('e', 70_000)] })))).StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await agent.Http.PutAsync(SnapshotUrl, Json(Serialized(TelemetrySamples.Snapshot()), "text/plain"))).StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await x.Admin.PostAsync(ViewerUrl(session.Id), Json(Serialized(TelemetrySamples.Viewer()), "text/plain"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await agent.Http.PutAsync(SnapshotUrl, Json("{\"agentVersion\":"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await agent.Http.PutAsync(SnapshotUrl, Json("null"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await agent.Http.PutAsync(AgentUrl(session.Id), Json(""))).StatusCode);
        Assert.Equal(0, await x.App.WithDbAsync(db => db.DeviceTelemetry.CountAsync()));
        Assert.Equal(0, await x.App.WithDbAsync(db => db.RemoteSessionTelemetry.CountAsync()));

        // 乱七八糟的硬件快照：截短、限长、夹范围、缺了的补上，进库的是清理过的
        var messy = TelemetrySamples.Snapshot() with
        {
            AgentVersion = "0.8.6" + new string('9', 100),
            Cpu = new TelemetryCpu("Intel\u0000 Core\r\n i5" + new string('c', 500), -4, 100_000, -1),
            MemoryBytes = -5,
            Gpus = [.. Enumerable.Range(0, 40).Select(i => new TelemetryGpu($"GPU {i}", -1, i, "1.0", -1, false, null!))],
            Displays = [new TelemetryDisplay(@"\\.\DISPLAY1", 99_999, -1, 5000, 100, true, null)],
            Capture = new TelemetryCapture(null!, -3.5, 1e300, null),
            Problems = [.. Enumerable.Range(0, 30).Select(i => $"problem {i} " + new string('p', 400))],
        };
        Assert.Equal(HttpStatusCode.NoContent, (await PutSnapshotAsync(agent.Http, messy)).StatusCode);
        var s = (await DiagnosticsAsync(x.Admin, agent.DeviceId)).Snapshot!;
        Assert.Equal(TelemetrySanitizer.MaxVersion, s.AgentVersion.Length);
        Assert.Equal(TelemetrySanitizer.MaxName, s.Cpu.Name.Length);
        Assert.StartsWith("Intel  Core   i5c", s.Cpu.Name);
        Assert.Equal((0, 4096, 0, 0L), (s.Cpu.Cores, s.Cpu.Threads, s.Cpu.BaseMhz, s.MemoryBytes));
        Assert.Equal(TelemetrySanitizer.MaxItems, s.Gpus.Count);
        Assert.All(s.Gpus, g => Assert.Equal((0, 0L), (g.VendorId, g.DedicatedMemoryBytes)));
        Assert.All(s.Gpus, g => Assert.Empty(g.Outputs));
        var display = Assert.Single(s.Displays);
        Assert.Equal((16384, 0, 1000), (display.Width, display.Height, display.RefreshHz));
        Assert.Empty(s.Capture.Duplication);
        Assert.Equal((double?)0, s.Capture.GdiNativeMs);
        Assert.Equal((double?)1e9, s.Capture.GdiScaledMs);
        Assert.Equal(TelemetrySanitizer.MaxMessages, s.Problems.Count);
        Assert.All(s.Problems, p => Assert.Equal(TelemetrySanitizer.MaxText, p.Length));
        // 库里存的就是清理过的（没有 \u0000，jsonb 也不收它）
        var stored = await x.App.WithDbAsync(db => db.DeviceTelemetry.Select(t => t.Data).SingleAsync());
        Assert.DoesNotContain("\\u0000", stored);

        // 会话里的两列也一样清理：整串 UA 截掉大半、负数当 0、Samples 为 0 的统计全是 0、不认识的档位当自动
        var ua = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36 Edg/131.0.0.0";
        var viewer = TelemetrySamples.Viewer() with
        {
            Browser = ua,
            Freezes = -3,
            DevicePixelRatio = 99,
            DecodedFps = new TelemetryStat(0, 5, 5, 5, 5, 5, 5),
            LineProblems = ["", "  ", "8 秒没连上"],
            Errors = [.. Enumerable.Range(0, 25).Select(i => $"e{i}")],
        };
        Assert.Equal(HttpStatusCode.NoContent, (await PostViewerAsync(x.Admin, session.Id, viewer)).StatusCode);
        var agentSide = TelemetrySamples.Agent() with
        {
            Settings = [new TelemetrySettingsUse((RemoteVideoMode)99, 99_999, 800, 500_000, 8000, -5)],
            Sizes = null!,
            LossPermille = null,
            Errors = [new string('e', 1000)],
        };
        Assert.Equal(HttpStatusCode.NoContent, (await PutAgentAsync(agent.Http, session.Id, agentSide)).StatusCode);
        var row = Assert.Single((await DiagnosticsAsync(x.Admin, agent.DeviceId)).Sessions);
        Assert.Equal(TelemetrySanitizer.MaxBrowser, row.Viewer!.Browser.Length);
        Assert.Equal((0, 16d), (row.Viewer.Freezes, row.Viewer.DevicePixelRatio));
        Assert.Equal(new TelemetryStat(0, 0, 0, 0, 0, 0, 0), row.Viewer.DecodedFps);
        Assert.Equal(["8 秒没连上"], row.Viewer.LineProblems);
        Assert.Equal(TelemetrySanitizer.MaxMessages, row.Viewer.Errors.Count);
        var setting = Assert.Single(row.Agent!.Settings);
        Assert.Equal((RemoteVideoMode.Auto, 16384, 800, 1000, 0), (setting.Mode, setting.MaxWidth, setting.MaxHeight, setting.Fps, setting.Seconds));
        Assert.Empty(row.Agent.Sizes);
        Assert.Null(row.Agent.LossPermille);
        Assert.NotNull(row.Agent.ReceivedKbps);
        Assert.Equal(TelemetrySanitizer.MaxText, Assert.Single(row.Agent.Errors).Length);

        // 只带了几个字段的（以后的网页 / 被控端少报了几样）：照收，缺的补空
        Assert.Equal(HttpStatusCode.NoContent, (await x.Admin.PostAsync(ViewerUrl(session.Id), Json("{\"final\":true,\"browser\":\"Safari 18\",\"unknownField\":1}"))).StatusCode);
        var partial = Assert.Single((await DiagnosticsAsync(x.Admin, agent.DeviceId)).Sessions).Viewer!;
        Assert.Equal((true, "Safari 18", "", 0), (partial.Final, partial.Browser, partial.Platform, partial.DecodedFps.Samples));
        Assert.Empty(partial.Errors);
        Assert.Null(partial.JitterBufferMs);
    }

    [PgFact]
    public async Task Only_admins_read_telemetry_and_see_recent_sessions_from_both_sides()
    {
        await using var x = await SetupAsync();
        var agent = await RegisterAsync(x, x.BobClient);
        await using var hub = await OnlineAsync(x, agent);
        await PutSnapshotAsync(agent.Http, TelemetrySamples.Snapshot());

        // 三次有遥测的远程：管理员、Bob、管理员；中间那次只有被控端报了。再来一次谁都没报的
        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var controller = i == 1 ? x.BobClient : x.Admin;
            var s = await ActiveSessionAsync(agent, controller);
            Assert.Equal(HttpStatusCode.NoContent, (await PutAgentAsync(agent.Http, s.Id, TelemetrySamples.Agent(final: true, seconds: 10 + i))).StatusCode);
            if (i != 1) Assert.Equal(HttpStatusCode.NoContent, (await PostViewerAsync(controller, s.Id, TelemetrySamples.Viewer(final: true, seconds: 20 + i))).StatusCode);
            await EndAsync(controller, s.Id);
            ids.Add(s.Id);
        }
        await EndAsync(x.Admin, (await ActiveSessionAsync(agent, x.Admin)).Id);

        var all = await DiagnosticsAsync(x.Admin, agent.DeviceId);
        Assert.NotNull(all.Snapshot);
        Assert.Equal(Enumerable.Reverse(ids), all.Sessions.Select(s => s.SessionId)); // 新的在前，没报过的那次不在里面
        var newest = all.Sessions[0];
        Assert.Equal(("admin", RemoteSessionMode.Control, "controller", 12, 22), (newest.ControllerName, newest.Mode, newest.EndReason, newest.Agent!.VideoSeconds, newest.Viewer!.Seconds));
        Assert.NotNull(newest.EndedAt);
        Assert.True(newest.UpdatedAt >= newest.StartedAt);
        var bobs = all.Sessions[1];
        Assert.Equal(("bob", 11), (bobs.ControllerName, bobs.Agent!.VideoSeconds));
        Assert.Null(bobs.Viewer);
        Assert.Equal((60, 28d, 14d, 42d), (newest.Agent.SentFps.Samples, newest.Agent.SentFps.P50, newest.Agent.SentFps.Min, newest.Agent.SentFps.Max));

        // limit：1–100，默认 20
        Assert.Equal([ids[2]], (await DiagnosticsAsync(x.Admin, agent.DeviceId, limit: 1)).Sessions.Select(s => s.SessionId));
        Assert.Single((await DiagnosticsAsync(x.Admin, agent.DeviceId, limit: 0)).Sessions);
        Assert.Equal(3, (await DiagnosticsAsync(x.Admin, agent.DeviceId, limit: 1000)).Sessions.Count);

        // 只有管理员：电脑的主人、别的家人 403；没登录 401；没有这台设备 404
        var url = DiagnosticsUrl(agent.DeviceId);
        Assert.Equal(HttpStatusCode.Forbidden, (await x.BobClient.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await x.CarolClient.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await x.App.Anonymous().GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await x.Admin.GetAsync(DiagnosticsUrl(Guid.NewGuid()))).StatusCode);

        // 设备令牌带着主人的角色：管理员自己那台电脑上的令牌也不能看（/api/telemetry 不在设备令牌能用的路径里，认证就不过：401）
        var adminsPc = await RegisterAsync(x, x.Admin, name: "管理员的电脑");
        Assert.Equal(HttpStatusCode.Unauthorized, (await adminsPc.Http.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await adminsPc.Http.GetAsync(DiagnosticsUrl(adminsPc.DeviceId))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await agent.Http.GetAsync(url)).StatusCode);

        // 接口文档照常生成，里面有诊断接口和它的返回类型
        var doc = await x.Admin.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, doc.StatusCode);
        var text = await doc.Content.ReadAsStringAsync();
        Assert.Contains("/api/telemetry/devices/{deviceId}", text);
        Assert.Contains("DeviceTelemetryDto", text);
    }

    [PgFact]
    public async Task Purge_removes_only_session_telemetry_older_than_90_days()
    {
        await using var x = await SetupAsync();
        var agent = await RegisterAsync(x, x.BobClient);
        await using var hub = await OnlineAsync(x, agent);
        await PutSnapshotAsync(agent.Http, TelemetrySamples.Snapshot());
        var old = await ActiveSessionAsync(agent, x.Admin);
        await PutAgentAsync(agent.Http, old.Id, TelemetrySamples.Agent(final: true));
        await EndAsync(x.Admin, old.Id);
        var recent = await ActiveSessionAsync(agent, x.Admin);
        Assert.Equal(HttpStatusCode.NoContent, (await PostViewerAsync(x.Admin, recent.Id, TelemetrySamples.Viewer())).StatusCode);

        var now = DateTimeOffset.UtcNow;
        await x.App.WithDbAsync(db => db.RemoteSessionTelemetry.Where(t => t.SessionId == old.Id).ExecuteUpdateAsync(set => set.SetProperty(t => t.UpdatedAt, now.AddDays(-91))));
        await x.App.WithDbAsync(db => db.RemoteSessionTelemetry.Where(t => t.SessionId == recent.Id).ExecuteUpdateAsync(set => set.SetProperty(t => t.UpdatedAt, now.AddDays(-89))));
        // 硬件那一行跟着设备，多老都不删
        await x.App.WithDbAsync(db => db.DeviceTelemetry.ExecuteUpdateAsync(set => set.SetProperty(t => t.ReportedAt, now.AddDays(-365))));

        Assert.Equal(1, await PurgeAsync(x.App, now));
        Assert.Equal([recent.Id], await x.App.WithDbAsync(db => db.RemoteSessionTelemetry.Select(t => t.SessionId).ToListAsync()));
        Assert.Equal(1, await x.App.WithDbAsync(db => db.DeviceTelemetry.CountAsync()));
        Assert.Equal(2, await x.App.WithDbAsync(db => db.RemoteSessions.CountAsync())); // 审计记录不动
        Assert.Equal(0, await PurgeAsync(x.App, now));
        // 再过两天，剩下那条也到 90 天了
        Assert.Equal(1, await PurgeAsync(x.App, now.AddDays(2)));
        Assert.Empty((await DiagnosticsAsync(x.Admin, agent.DeviceId)).Sessions);
        Assert.NotNull((await DiagnosticsAsync(x.Admin, agent.DeviceId)).Snapshot);
    }

    [PgFact]
    public async Task A_revoked_device_token_cannot_report_anymore()
    {
        await using var x = await SetupAsync();
        var agent = await RegisterAsync(x, x.BobClient);
        await using var hub = await OnlineAsync(x, agent);
        var session = await ActiveSessionAsync(agent, x.Admin);
        Assert.Equal(HttpStatusCode.NoContent, (await PutSnapshotAsync(agent.Http, TelemetrySamples.Snapshot())).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await PutAgentAsync(agent.Http, session.Id, TelemetrySamples.Agent())).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await x.BobClient.DeleteAsync($"/api/devices/{agent.DeviceId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PutSnapshotAsync(agent.Http, TelemetrySamples.Snapshot("9.9.9"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PutAgentAsync(agent.Http, session.Id, TelemetrySamples.Agent(final: true))).StatusCode);

        // 之前报的还在：管理员照样看得到这台电脑被移除之前的情况
        var diag = await DiagnosticsAsync(x.Admin, agent.DeviceId);
        Assert.Equal("0.8.6", diag.Snapshot!.AgentVersion);
        Assert.False(Assert.Single(diag.Sessions).Agent!.Final);
    }
}

/// <summary>遥测进库前的清理、请求体的读法：纯函数，不要数据库。</summary>
public class TelemetrySanitizerTests
{
    [Fact]
    public void Text_drops_control_characters_and_lone_surrogates_and_never_splits_a_pair()
    {
        Assert.Equal("", TelemetrySanitizer.Text(null, 10));
        Assert.Equal("a b  c", TelemetrySanitizer.Text("  a\u0000b\r\nc\t ", 10));
        Assert.Equal("abc", TelemetrySanitizer.Text("abcdef", 3));
        // 截在一对代理项中间：整对不要，不留半个
        Assert.Equal("ab", TelemetrySanitizer.Text("ab😀cd", 3));
        Assert.Equal("ab😀", TelemetrySanitizer.Text("ab😀cd", 4));
        Assert.Equal("ab", TelemetrySanitizer.Text("a\uD800b\uDC00", 10));
        Assert.Null(TelemetrySanitizer.TextOrNull(null, 10));
        Assert.Equal(["x", "y"], TelemetrySanitizer.Texts(["", null!, " x ", "\u0000", "y", "z"], 2, 10));
    }

    [Fact]
    public void Numbers_are_finite_non_negative_and_bounded()
    {
        Assert.Equal(0, TelemetrySanitizer.Number(double.NaN));
        Assert.Equal(0, TelemetrySanitizer.Number(double.PositiveInfinity));
        Assert.Equal(0, TelemetrySanitizer.Number(-1.5));
        Assert.Equal(1e9, TelemetrySanitizer.Number(1e300));
        Assert.Equal(33.333, TelemetrySanitizer.Number(100 / 3.0));
        Assert.Equal(16, TelemetrySanitizer.Number(20, 16));
        Assert.Null(TelemetrySanitizer.NumberOrNull(null));
        Assert.Equal(0, TelemetrySanitizer.Count(-7));
        Assert.Equal(1000, TelemetrySanitizer.Range(5000, 1000));
        Assert.Equal(0, TelemetrySanitizer.Bytes(-1));
    }

    [Fact]
    public void Desktop_duplication_frame_cost_is_kept_and_cleaned()
    {
        // 0.8.7 起被控端多报"每幅新画面多久"：照存，数不对的清成 null / 0，尺寸那一项截短；老版本不报就是 null
        var snapshot = TelemetrySamples.Snapshot() with
        {
            Capture = new TelemetryCapture(
            [
                new TelemetryDuplication(@"\\.\DISPLAY1", "Intel(R) UHD Graphics", true, 85.4, null, 6.2, 7.23, "1920x1200"),
                new TelemetryDuplication(@"\\.\DISPLAY2", "x", true, 1, null, double.NaN, -3, new string('9', 100)),
                new TelemetryDuplication(@"\\.\DISPLAY3", "y", false, null, "E_ACCESSDENIED"),
            ], 31.2, 33.8, null),
        };
        var clean = TelemetrySanitizer.Snapshot(snapshot).Capture.Duplication;
        Assert.Equal((6.2, 7.23, "1920x1200"), (clean[0].FrameMs, clean[0].FrameP90Ms, clean[0].FrameSize));
        Assert.Equal((0.0, 0.0), (clean[1].FrameMs, clean[1].FrameP90Ms));
        Assert.Equal(TelemetrySanitizer.MaxVersion, clean[1].FrameSize!.Length);
        Assert.Equal((null, null, null), (clean[2].FrameMs, clean[2].FrameP90Ms, clean[2].FrameSize));
    }

    [Fact]
    public void A_snapshot_with_everything_missing_comes_out_complete()
    {
        var s = TelemetrySanitizer.Snapshot(new AgentTelemetrySnapshot(null!, default, null!, null!, null!, -1, null!, null!, null!, null!, true, null!));
        Assert.Equal(("", "", "", "", 0L, true), (s.AgentVersion, s.Fingerprint, s.Os.ProductName, s.Cpu.Name, s.MemoryBytes, s.RemoteDesktopSession));
        Assert.Empty(s.Gpus);
        Assert.Empty(s.Displays);
        Assert.Empty(s.Encoders);
        Assert.Empty(s.Capture.Duplication);
        Assert.Null(s.Capture.GdiNativeMs);
        Assert.Empty(s.Problems);
        // 列表里的空项去掉，编码测试缺了就是缺了
        var e = Assert.Single(TelemetrySanitizer.Snapshot(TelemetrySamples.Snapshot() with
        {
            Encoders = [null!, new TelemetryEncoder(null!, true, -1, false, false, null!, false, false, false, false, false, null, null)],
        }).Encoders);
        Assert.Equal(("", 0), (e.Name, e.VendorId));
        Assert.Null(e.Test);
        Assert.Null(e.Error);
        Assert.Empty(e.RateControlModes);
    }

    [Fact]
    public void Session_summaries_with_missing_parts_come_out_complete()
    {
        var empty = new TelemetryStat(0, 0, 0, 0, 0, 0, 0);
        var a = TelemetrySanitizer.AgentSession(new AgentSessionTelemetry(null!, true, -1, -1, -1, null!, false, null!, null!, null!,
            null!, null!, null!, null!, null!, null!, null!, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, null!, null, null, null, null, null!));
        Assert.Equal(("", true, 0, "", ""), (a.AgentVersion, a.Final, a.VideoSeconds, a.Encoder, a.Capture));
        Assert.Equal(empty, a.SentFps);
        Assert.Equal(empty, a.RttMs);
        Assert.Empty(a.Settings);
        Assert.Empty(a.LineProblems);
        Assert.Null(a.ViewerFps);
        var v = TelemetrySanitizer.Viewer(new ViewerSessionTelemetry(false, null!, null!, -1, -1, double.NaN, -1, -1, -1, -1, new TelemetryStat(3, double.NaN, 1, 2, 3, 4, double.PositiveInfinity),
            -1, -1, -1, -1, -1, -1, -1, null!, null, null, null!));
        Assert.Equal(("", 0d, 0), (v.Browser, v.DevicePixelRatio, v.ScreenWidth));
        Assert.Equal(new TelemetryStat(3, 0, 1, 2, 3, 4, 0), v.DecodedFps);
        Assert.Empty(v.Errors);
    }

    private static HttpRequest Request(byte[] body, string? contentType = "application/json", bool withLength = false)
    {
        var context = new DefaultHttpContext();
        context.Request.ContentType = contentType;
        context.Request.Body = new MemoryStream(body);
        if (withLength) context.Request.ContentLength = body.Length;
        return context.Request;
    }

    private static async Task<int> StatusAsync(HttpRequest request)
    {
        var ex = await Assert.ThrowsAsync<ApiException>(() => TelemetryBody.ReadAsync<ViewerSessionTelemetry>(request, default));
        return ex.Status;
    }

    [Fact]
    public async Task Request_bodies_must_be_json_and_at_most_64_kb()
    {
        var ok = JsonSerializer.SerializeToUtf8Bytes(TelemetrySamples.Viewer(), RemoteTelemetryService.Json);
        var read = await TelemetryBody.ReadAsync<ViewerSessionTelemetry>(Request(ok, "application/json; charset=utf-8"), default);
        Assert.Equal(("Edge 131", 1.25, 27d), (read.Browser, read.DevicePixelRatio, read.DecodedFps.P50));
        // 枚举按字符串
        var agent = JsonSerializer.SerializeToUtf8Bytes(TelemetrySamples.Agent(), RemoteTelemetryService.Json);
        Assert.Contains("\"mode\":\"Auto\"", Encoding.UTF8.GetString(agent));
        Assert.Equal(RemoteVideoMode.Auto, Assert.Single((await TelemetryBody.ReadAsync<AgentSessionTelemetry>(Request(agent), default)).Settings).Mode);

        // 刚好 64 KB 还收（JSON 后面补空白），多一个字节不收：没有 Content-Length 的数着读，有的直接看
        var padded = ok.Concat(Enumerable.Repeat((byte)' ', TelemetryBody.MaxBytes - ok.Length)).ToArray();
        Assert.Equal("Edge 131", (await TelemetryBody.ReadAsync<ViewerSessionTelemetry>(Request(padded), default)).Browser);
        var over = padded.Append((byte)' ').ToArray();
        Assert.Equal(413, await StatusAsync(Request(over)));
        Assert.Equal(413, await StatusAsync(Request(over, withLength: true)));

        Assert.Equal(415, await StatusAsync(Request(ok, "text/plain")));
        Assert.Equal(415, await StatusAsync(Request(ok, null)));
        Assert.Equal(400, await StatusAsync(Request([])));
        Assert.Equal(400, await StatusAsync(Request("null"u8.ToArray())));
        Assert.Equal(400, await StatusAsync(Request("{\"final\":"u8.ToArray())));
        Assert.Equal(400, await StatusAsync(Request("{\"seconds\":1.5}"u8.ToArray()))); // 整数字段给了小数
    }

    [Fact]
    public void Cleaned_json_has_no_nul_escape_that_jsonb_would_reject()
    {
        var clean = TelemetrySanitizer.Snapshot(TelemetrySamples.Snapshot() with { Problems = ["探测\u0000失败"], Fingerprint = "a\u0001b" });
        var json = JsonSerializer.Serialize(clean, RemoteTelemetryService.Json);
        Assert.DoesNotContain("\\u0000", json);
        Assert.DoesNotContain("\\u0001", json);
        Assert.Equal(["探测 失败"], clean.Problems);
    }
}

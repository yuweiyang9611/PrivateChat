using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using PrivateChat.Api.Domain;
using PrivateChat.Api.Features.Auth;
using PrivateChat.Api.Infrastructure;
using PrivateChat.Contracts.Remote;

namespace PrivateChat.Api.Features.Remote;

/// <summary>
/// 远程协助遥测（docs/DESIGN.md 4.8"远程协助遥测"）：
/// - 存什么：被控端这台电脑的硬件和能力（每台设备一行，每次上报覆盖）；每次远程一行，被控端、控制端各一列（各自每 60 秒覆盖一次，结束时再一次）。
///   只有性能数字、硬件和驱动型号、出错的说明，进库之前清理一遍（<see cref="TelemetrySanitizer"/>）。
/// - 谁能写：硬件只能是被控端自己（设备令牌）。会话里被控端那列只能是这次被控的那台电脑，控制端那列只能是这次发起远程的人（浏览器登录，设备令牌不行）；
///   别人一律当作没有这个会话（404）。会话进行中、结束后 24 小时内都收：被控端的结束汇总总是在会话结束之后才到。
/// - 结束汇总：两列各自按会话覆盖、最后到的算数，只有一种例外——这一列存着的已经是结束汇总（final），来的却是中途汇总，
///   而且累计秒数（被控端 VideoSeconds、控制端 Seconds）不比存着的多：那是排在后面晚到的旧数据（重试的、关页面前还在路上的），不要它，
///   照样回 204（被控端、网页遇到 409 / 410 就不再报了）。秒数更多的照收：刷新以后的网页从存着的累计数接着数，发的是中途汇总。
/// - 谁能看：只有管理员（<see cref="TelemetryController"/>，设备令牌不行）。
/// - 留多久：每次远程那一行 90 天（按最后一次更新算，<see cref="RemoteTelemetryRetentionWorker"/>），硬件那一行跟着设备。
/// </summary>
public sealed class RemoteTelemetryService(AppDbContext db, ILogger<RemoteTelemetryService> logger)
{
    public static readonly TimeSpan AcceptAfterEnd = TimeSpan.FromHours(24);
    public static readonly TimeSpan Retention = TimeSpan.FromDays(90);
    public const int DefaultLimit = 20;
    public const int MaxLimit = 100;

    /// <summary>读请求体、存进 jsonb、从库里读出来都用这一份：camelCase、枚举按字符串（和接口的 JSON 一样）。</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    // ---- 被控端：这台电脑的硬件和能力 ----

    public async Task PutSnapshotAsync(ClaimsPrincipal user, AgentTelemetrySnapshot snapshot, CancellationToken ct)
    {
        var deviceId = AgentOf(user);
        var clean = TelemetrySanitizer.Snapshot(snapshot);
        var json = JsonSerializer.Serialize(clean, Json);
        var now = DateTimeOffset.UtcNow;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO device_telemetry (device_id, reported_at, agent_version, data) VALUES ({deviceId}, {now}, {clean.AgentVersion}, {json}::jsonb)
            ON CONFLICT (device_id) DO UPDATE SET reported_at = excluded.reported_at, agent_version = excluded.agent_version, data = excluded.data
            """, ct);
        logger.LogInformation("远程协助遥测：设备 {Device} 上报了硬件和能力（被控端 {Version}，{Gpus} 张显卡、{Encoders} 个编码器、{Problems} 个探测问题）",
            deviceId, clean.AgentVersion, clean.Gpus.Count, clean.Encoders.Count, clean.Problems.Count);
    }

    // ---- 一次远程：被控端、控制端各自的汇总 ----

    /// <summary>被控端那列：来的要是这次被控的那台电脑（设备令牌），会话要还收数据。先查这一步再读请求体。</summary>
    public async Task<RemoteSession> AgentSessionAsync(ClaimsPrincipal user, Guid sessionId, CancellationToken ct)
    {
        var deviceId = AgentOf(user);
        return await AcceptingAsync(sessionId, s => s.TargetDeviceId == deviceId, ct);
    }

    /// <summary>存被控端那列：已经存着结束汇总时，中途汇总要画面秒数（VideoSeconds）比它多才覆盖（见类注释"结束汇总"那条）。</summary>
    public async Task PutAgentAsync(RemoteSession session, AgentSessionTelemetry telemetry, CancellationToken ct)
    {
        var clean = TelemetrySanitizer.AgentSession(telemetry);
        var json = JsonSerializer.Serialize(clean, Json);
        var now = DateTimeOffset.UtcNow;
        // 判断和写在同一条语句里（ON CONFLICT … WHERE 看的是锁住的那一行），两份同时到也不会先读后写地互相盖掉
        var stored = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO remote_session_telemetry AS t (session_id, device_id, updated_at, agent) VALUES ({session.Id}, {session.TargetDeviceId}, {now}, {json}::jsonb)
            ON CONFLICT (session_id) DO UPDATE SET agent = excluded.agent, updated_at = excluded.updated_at
            WHERE {clean.Final} OR NOT COALESCE((t.agent ->> 'final')::boolean, false) OR {clean.VideoSeconds} > COALESCE((t.agent ->> 'videoSeconds')::int, 0)
            """, ct);
        if (stored == 0)
            logger.LogDebug("远程会话 {Session}：被控端的中途遥测（画面 {Seconds} 秒）比已有的结束汇总晚到，没有覆盖", session.Id, clean.VideoSeconds);
        else
            logger.LogDebug("远程会话 {Session}：收到被控端的遥测（{Kind}，画面 {Seconds} 秒）", session.Id, clean.Final ? "结束汇总" : "中途", clean.VideoSeconds);
    }

    /// <summary>控制端那列：来的要是这次发起远程的人、用浏览器登录（设备令牌不行），会话要还收数据。</summary>
    public async Task<RemoteSession> ViewerSessionAsync(ClaimsPrincipal user, Guid sessionId, CancellationToken ct)
    {
        if (user.DeviceId() is not null) throw ApiException.Forbidden("控制端的数据只能由发起远程的人的浏览器上报");
        var me = user.UserId();
        return await AcceptingAsync(sessionId, s => s.ControllerUserId == me, ct);
    }

    /// <summary>存控制端那列：已经存着结束汇总时，中途汇总要秒数（Seconds）比它多才覆盖（见类注释"结束汇总"那条）。</summary>
    public async Task PutViewerAsync(RemoteSession session, ViewerSessionTelemetry telemetry, CancellationToken ct)
    {
        var clean = TelemetrySanitizer.Viewer(telemetry);
        var json = JsonSerializer.Serialize(clean, Json);
        var now = DateTimeOffset.UtcNow;
        var stored = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO remote_session_telemetry AS t (session_id, device_id, updated_at, viewer) VALUES ({session.Id}, {session.TargetDeviceId}, {now}, {json}::jsonb)
            ON CONFLICT (session_id) DO UPDATE SET viewer = excluded.viewer, updated_at = excluded.updated_at
            WHERE {clean.Final} OR NOT COALESCE((t.viewer ->> 'final')::boolean, false) OR {clean.Seconds} > COALESCE((t.viewer ->> 'seconds')::int, 0)
            """, ct);
        if (stored == 0)
            logger.LogDebug("远程会话 {Session}：控制端的中途遥测（{Seconds} 秒）比已有的结束汇总晚到，没有覆盖", session.Id, clean.Seconds);
        else
            logger.LogDebug("远程会话 {Session}：收到控制端的遥测（{Kind}，{Seconds} 秒）", session.Id, clean.Final ? "结束汇总" : "中途", clean.Seconds);
    }

    private static Guid AgentOf(ClaimsPrincipal user)
        => user.DeviceId() ?? throw ApiException.Forbidden("只有被控电脑上的远程协助程序可以上报");

    /// <summary>
    /// 会话要存在、来的人要是这一方（不是的当作没有这个会话），而且还收数据：进行中，或者开始过、结束不到 24 小时。
    /// 等同意中、没开始就结束了（拒绝、超时）、结束太久的回 409（被控端把 404 / 409 都当作"这次不收了"，不再重试）。
    /// </summary>
    private async Task<RemoteSession> AcceptingAsync(Guid sessionId, Func<RemoteSession, bool> isParty, CancellationToken ct)
    {
        var session = await db.RemoteSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null || !isParty(session)) throw ApiException.NotFound("会话");
        if (session.EndedAt is { } ended)
        {
            if (session.AcceptedAt is null) throw ApiException.Conflict("这次远程没有开始过");
            if (DateTimeOffset.UtcNow - ended > AcceptAfterEnd) throw ApiException.Conflict("这次远程结束太久了，不再收遥测");
        }
        else if (session.State != RemoteSessionState.Active)
        {
            throw ApiException.Conflict("这次远程还没开始");
        }
        return session;
    }

    // ---- 管理员看 ----

    /// <summary>这台电脑的硬件和能力，和最近 limit 次（1–100）有遥测的远程，按开始时间新的在前。</summary>
    public async Task<DeviceTelemetryDto> DeviceAsync(Guid deviceId, int limit, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, MaxLimit);
        var device = await db.Devices.AsNoTracking()
            .Where(d => d.Id == deviceId && d.Kind == DeviceKind.Agent)
            .Select(d => new { d.Id, d.Name })
            .FirstOrDefaultAsync(ct) ?? throw ApiException.NotFound("设备");
        var snapshot = await db.DeviceTelemetry.AsNoTracking().FirstOrDefaultAsync(t => t.DeviceId == deviceId, ct);
        var rows = await (from t in db.RemoteSessionTelemetry.AsNoTracking()
                          join s in db.RemoteSessions.AsNoTracking() on t.SessionId equals s.Id
                          join u in db.Users.AsNoTracking() on s.ControllerUserId equals u.Id into controllers
                          from u in controllers.DefaultIfEmpty()
                          where t.DeviceId == deviceId
                          orderby s.StartedAt descending
                          select new { t, s, Controller = u == null ? null : u.DisplayName })
            .Take(limit)
            .ToListAsync(ct);
        return new DeviceTelemetryDto(
            device.Id, device.Name, snapshot?.ReportedAt, Parse<AgentTelemetrySnapshot>(snapshot?.Data),
            [.. rows.Select(r => new SessionTelemetryDto(
                r.s.Id, r.s.StartedAt, r.s.EndedAt, r.s.EndReason, r.s.Mode, r.Controller ?? "", r.t.UpdatedAt,
                Parse<AgentSessionTelemetry>(r.t.Agent), Parse<ViewerSessionTelemetry>(r.t.Viewer)))]);
    }

    /// <summary>库里的 JSON 读不出来（以后改了格式、手工改过库）：这一块当作没有，别让整个诊断页打不开。</summary>
    private T? Parse<T>(string? json) where T : class
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(json, Json);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "远程协助遥测：库里的 {Type} 读不出来", typeof(T).Name);
            return null;
        }
    }

    // ---- 维护 ----

    /// <summary>删掉最后一次更新在 90 天前的远程遥测（remote_sessions 里的审计记录不动）。返回删了几行。</summary>
    public Task<int> PurgeTelemetryAsync(DateTimeOffset now, CancellationToken ct)
    {
        var cutoff = now - Retention;
        return db.RemoteSessionTelemetry.Where(t => t.UpdatedAt < cutoff).ExecuteDeleteAsync(ct);
    }
}

/// <summary>每小时一次：删掉 90 天前的远程遥测（硬件和能力那一行跟着设备，不删）。</summary>
public sealed class RemoteTelemetryRetentionWorker(IServiceScopeFactory scopes, ILogger<RemoteTelemetryRetentionWorker> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // 启动后稍等再跑，别和迁移抢
        try { await Task.Delay(TimeSpan.FromMinutes(2), ct); } catch (OperationCanceledException) { return; }
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var purged = await scope.ServiceProvider.GetRequiredService<RemoteTelemetryService>().PurgeTelemetryAsync(DateTimeOffset.UtcNow, ct);
                if (purged > 0) logger.LogInformation("远程协助遥测：删掉 {Count} 次远程的旧数据（超过 {Days} 天）", purged, RemoteTelemetryService.Retention.TotalDays);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "远程协助遥测清理失败");
            }
        } while (await timer.WaitForNextTickAsync(ct));
    }
}

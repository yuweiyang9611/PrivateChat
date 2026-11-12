using System.Linq.Expressions;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PrivateChat.Api.Domain;
using PrivateChat.Api.Features.Auth;
using PrivateChat.Api.Infrastructure;
using PrivateChat.Contracts.Remote;

namespace PrivateChat.Api.Features.Remote;

/// <summary>
/// 远程控制的业务规则（docs/DESIGN.md 4.8）：
/// - 谁能控：Admin / Member（孩子不行），且是设备主人或在设备的白名单里；谁能管（改名、白名单、PIN、吊销）：主人或管理员。
/// - 一台设备同一时间只有一个未结束的会话（数据库部分唯一索引兜底）；同一个人再点一次就顶掉自己之前那次。
/// - 默认要被控端点同意（30 秒）；输入设备的无人值守 PIN 则直接开始。PIN 连错 5 次锁 15 分钟。
/// - 状态迁移都是带条件的 UPDATE（WHERE 还没结束 / 还在等同意），同意、超时、挂断同时发生也只会有一个生效。
/// - 进行中的会话任一端断开超过 30 秒就结束（RemoteMaintenanceWorker）。
/// </summary>
public sealed class RemoteService(AppDbContext db, AgentRegistry agents, RemoteRelay relay, RemoteNotifier notifier, ILogger<RemoteService> logger)
{
    public static readonly TimeSpan ConsentTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan AbsentTimeout = TimeSpan.FromSeconds(30);
    public const int MaxPinFailures = 5;
    public static readonly TimeSpan PinLockout = TimeSpan.FromMinutes(15);
    private static readonly PasswordHasher<Device> PinHasher = new();

    // ---- 权限 ----

    public static List<Guid> AllowedOf(Device d)
    {
        try { return JsonSerializer.Deserialize<List<Guid>>(d.AllowedControllers) ?? []; }
        catch (JsonException) { return []; }
    }

    public static bool CanManage(Device d, Guid me, UserRole role) => d.UserId == me || role == UserRole.Admin;

    public static bool CanControl(Device d, Guid me, UserRole role)
        => role != UserRole.Child && (d.UserId == me || AllowedOf(d).Contains(me));

    private async Task<Device> VisibleDeviceAsync(Guid id, Guid me, UserRole role, CancellationToken ct)
    {
        var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == id && d.Kind == DeviceKind.Agent && d.RevokedAt == null, ct);
        if (device is null || !(CanControl(device, me, role) || CanManage(device, me, role))) throw ApiException.NotFound("设备");
        return device;
    }

    // ---- 设备 ----

    public async Task<List<DeviceDto>> ListDevicesAsync(Guid me, UserRole role, CancellationToken ct)
    {
        var devices = await db.Devices.AsNoTracking()
            .Where(d => d.Kind == DeviceKind.Agent && d.RevokedAt == null)
            .OrderBy(d => d.CreatedAt)
            .ToListAsync(ct);
        var visible = devices.Where(d => CanControl(d, me, role) || CanManage(d, me, role)).ToList();
        var ids = visible.Select(d => d.Id).ToList();
        var open = await db.RemoteSessions.AsNoTracking().Where(s => ids.Contains(s.TargetDeviceId) && s.EndedAt == null).ToListAsync(ct);
        return [.. visible.Select(d => ToDto(d, me, role, open.FirstOrDefault(s => s.TargetDeviceId == d.Id)))];
    }

    private DeviceDto ToDto(Device d, Guid me, UserRole role, RemoteSession? open)
    {
        var entry = agents.Get(d.Id);
        var manage = CanManage(d, me, role);
        return new DeviceDto(
            d.Id, d.Name, d.Platform, d.UserId, entry is not null, d.LastSeenAt, d.CreatedAt, d.AgentVersion, d.Os,
            [.. (entry?.Report?.Screens ?? []).Select(s => new ScreenDto(s.Width, s.Height, s.Primary))],
            CanControl(d, me, role), manage, manage ? AllowedOf(d) : null,
            d.UnattendedPinHash is not null, manage && d.PinLockedUntil > DateTimeOffset.UtcNow ? d.PinLockedUntil : null,
            open is null ? null : RemoteSessionDto.From(open, d));
    }

    public async Task<DeviceDto> UpdateDeviceAsync(Guid me, UserRole role, Guid id, UpdateDeviceRequest req, CancellationToken ct)
    {
        var device = await VisibleDeviceAsync(id, me, role, ct);
        if (!CanManage(device, me, role)) throw ApiException.Forbidden("只有电脑的主人或管理员可以修改");

        if (req.Name is { } name)
        {
            name = name.Trim();
            if (name.Length is 0 or > 64) throw ApiException.BadRequest("名称 1 到 64 个字");
            device.Name = name;
        }
        if (req.AllowedControllers is { } allowed)
        {
            var wanted = allowed.Distinct().Where(u => u != device.UserId).ToList();
            var valid = await db.Users.Where(u => wanted.Contains(u.Id) && u.DisabledAt == null && u.Role != UserRole.Child).Select(u => u.Id).ToListAsync(ct);
            if (valid.Count != wanted.Count) throw ApiException.BadRequest("名单里有不存在、已停用或孩子账号的成员");
            device.AllowedControllers = JsonSerializer.Serialize(wanted);
        }
        var pinChanged = req.ClearPin == true || req.Pin is not null;
        if (req.ClearPin == true)
        {
            device.UnattendedPinHash = null;
            device.PinFailures = 0;
            device.PinLockedUntil = null;
        }
        else if (req.Pin is { } pin)
        {
            if (pin.Length is < 6 or > 12 || !pin.All(char.IsAsciiDigit)) throw ApiException.BadRequest("PIN 为 6 到 12 位数字");
            device.UnattendedPinHash = PinHasher.HashPassword(device, pin);
            device.PinFailures = 0;
            device.PinLockedUntil = null;
        }
        await db.SaveChangesAsync(ct);
        // 移出名单的人、凭旧 PIN 进来的无人值守会话：立即结束（不然对方还能一直控制、断了还能重连回来）
        await EndDisallowedAsync(deviceId: device.Id, endUnattended: pinChanged);
        await notifier.DeviceChangedAsync(device.Id);
        var open = await db.RemoteSessions.AsNoTracking().FirstOrDefaultAsync(s => s.TargetDeviceId == id && s.EndedAt == null, ct);
        return ToDto(device, me, role, open);
    }

    /// <summary>
    /// 权限变了（移出名单、改 / 清 PIN、成员被降为孩子或停用、设备被吊销）：结束不再被允许的未结束会话。
    /// endUnattended：凭 PIN 进来的会话也结束（PIN 改了 / 清了）。
    /// </summary>
    public async Task EndDisallowedAsync(Guid? deviceId = null, Guid? controllerId = null, bool endUnattended = false)
    {
        var rows = await (from s in db.RemoteSessions.AsNoTracking()
                          join d in db.Devices.AsNoTracking() on s.TargetDeviceId equals d.Id
                          join u in db.Users.AsNoTracking() on s.ControllerUserId equals u.Id
                          where s.EndedAt == null
                                && (deviceId == null || s.TargetDeviceId == deviceId)
                                && (controllerId == null || s.ControllerUserId == controllerId)
                          select new { s.Id, s.Consent, s.ControllerUserId, Device = d, u.Role, u.DisabledAt }).ToListAsync();
        foreach (var r in rows)
            if (r.DisabledAt is not null || r.Device.RevokedAt is not null || !CanControl(r.Device, r.ControllerUserId, r.Role)
                || (endUnattended && r.Consent == RemoteConsentType.Unattended))
                await EndCoreAsync(r.Id, "stopped");
    }

    /// <summary>吊销：令牌作废、正在进行的会话结束、断开 Agent。</summary>
    public async Task RevokeDeviceAsync(Guid me, UserRole role, Guid id, CancellationToken ct)
    {
        var device = await VisibleDeviceAsync(id, me, role, ct);
        if (!CanManage(device, me, role)) throw ApiException.Forbidden("只有电脑的主人或管理员可以移除");
        // 先告诉 Agent（它会删掉本地令牌、回到设备码登录），再作废令牌：反过来的话 Agent 下一个请求就被拒，收不到这条通知
        await notifier.RevokedAsync(id);
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            // 与发起远程用同一把设备行锁：吊销之后不会再冒出新会话
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM devices WHERE id = {id} FOR UPDATE", ct);
            device.RevokedAt = DateTimeOffset.UtcNow;
            device.TokenHash = null;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        // 已经提交：后面的清理不受请求取消影响
        foreach (var open in await db.RemoteSessions.Where(s => s.TargetDeviceId == id && s.EndedAt == null).Select(s => s.Id).ToListAsync(CancellationToken.None))
            await EndCoreAsync(open, "revoked");
        // 稍后强制断开（Agent 自己不断开的话）：立刻断开时上面的通知可能还没送到
        _ = Task.Delay(TimeSpan.FromSeconds(2)).ContinueWith(_ => agents.Abort(id), TaskScheduler.Default);
        await notifier.DeviceChangedAsync(id);
    }

    // ---- 会话 ----

    public async Task<RemoteSessionDto> CreateSessionAsync(Guid me, UserRole role, CreateRemoteSessionRequest req, CancellationToken ct)
    {
        if (role == UserRole.Child) throw ApiException.Forbidden("孩子账号不能远程控制电脑");
        if (!Enum.IsDefined(req.Mode)) throw ApiException.BadRequest("模式无效");
        var device = await db.Devices.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == req.TargetDeviceId && d.Kind == DeviceKind.Agent && d.RevokedAt == null, ct);
        if (device is null || !(CanControl(device, me, role) || CanManage(device, me, role))) throw ApiException.NotFound("设备");
        if (!CanControl(device, me, role)) throw ApiException.Forbidden("你不在这台电脑的远程控制名单里，请让电脑的主人把你加进去");
        if (!agents.IsOnline(device.Id)) throw ApiException.Conflict("这台电脑现在不在线（没开机，或者远程协助程序没有运行）");

        var consent = RemoteConsentType.Prompt;
        if (!string.IsNullOrEmpty(req.Pin))
        {
            await VerifyPinAsync(device.Id, req.Pin, ct);
            consent = RemoteConsentType.Unattended;
        }

        var now = DateTimeOffset.UtcNow;
        var session = new RemoteSession
        {
            ControllerUserId = me,
            TargetDeviceId = device.Id,
            Mode = req.Mode,
            Consent = consent,
            State = consent == RemoteConsentType.Unattended ? RemoteSessionState.Active : RemoteSessionState.Pending,
            StartedAt = now,
            ExpiresAt = now + ConsentTimeout,
            AcceptedAt = consent == RemoteConsentType.Unattended ? now : null,
        };
        List<Guid> replaced;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            // 锁住设备行：同一台设备上的发起排队，"顶掉自己之前那次"和"别人正在用"的判断才可靠；
            // 锁里重新读一遍：刚被吊销、刚把我移出名单的，这里就拦下
            var fresh = await db.Devices.FromSqlInterpolated($"SELECT * FROM devices WHERE id = {device.Id} AND revoked_at IS NULL FOR UPDATE")
                .AsNoTracking().FirstOrDefaultAsync(ct) ?? throw ApiException.NotFound("设备");
            if (!CanControl(fresh, me, role)) throw ApiException.Forbidden("你不在这台电脑的远程控制名单里，请让电脑的主人把你加进去");
            var open = await db.RemoteSessions.Where(s => s.TargetDeviceId == device.Id && s.EndedAt == null).ToListAsync(ct);
            if (open.FirstOrDefault(s => s.ControllerUserId != me) is { } busy)
            {
                var who = await db.Users.Where(u => u.Id == busy.ControllerUserId).Select(u => u.DisplayName).FirstOrDefaultAsync(ct) ?? "其他家人";
                throw ApiException.Conflict($"{who}正在远程这台电脑，请稍后再试");
            }
            foreach (var old in open)
            {
                old.State = RemoteSessionState.Ended;
                old.EndedAt = now;
                old.EndReason = "replaced";
            }
            replaced = [.. open.Select(o => o.Id)];
            db.RemoteSessions.Add(session);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                throw ApiException.Conflict("这台电脑正在被远程，请稍后再试");
            }
            await tx.CommitAsync(ct);
        }
        foreach (var id in replaced) await AfterEndedAsync(id, "replaced");

        var controller = await db.Users.Where(u => u.Id == me).Select(u => u.DisplayName).FirstAsync(ct);
        await notifier.SessionRequestedAsync(device.Id, ToAgentRequest(session, controller));
        var dto = RemoteSessionDto.From(session, device);
        await notifier.SessionChangedAsync(dto);
        logger.LogInformation("远程控制：{User} 请求{Mode} 设备 {Device}（{Consent}）", me, req.Mode, device.Id, consent);
        return dto;
    }

    /// <summary>倒计时用相对秒数：被控电脑的时钟可能不准，不能拿服务器的绝对时间去减它自己的时间。</summary>
    private static AgentSessionRequest ToAgentRequest(RemoteSession s, string controllerName)
        => new(s.Id, controllerName, s.Mode, s.Consent, s.ExpiresAt, s.State == RemoteSessionState.Active,
            Math.Max(0, (int)Math.Floor((s.ExpiresAt - DateTimeOffset.UtcNow).TotalSeconds)));

    /// <summary>在设备行锁里校验 PIN：并发猜 PIN 也只能一个个来，错满 5 次立刻锁上。</summary>
    private async Task VerifyPinAsync(Guid deviceId, string pin, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        bool ok;
        int left;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            var d = await db.Devices.FromSqlInterpolated($"SELECT * FROM devices WHERE id = {deviceId} FOR UPDATE").SingleAsync(ct);
            if (d.UnattendedPinHash is null) throw ApiException.BadRequest("这台电脑没有设置无人值守 PIN，请等对方点同意");
            if (d.PinLockedUntil is { } expired && expired <= now) d.PinLockedUntil = null;
            if (d.PinLockedUntil is { } until && until > now)
                throw new ApiException(429, "PIN 错误次数太多", $"请 {Math.Ceiling((until - now).TotalMinutes)} 分钟后再试");
            ok = PinHasher.VerifyHashedPassword(d, d.UnattendedPinHash, pin) != PasswordVerificationResult.Failed;
            if (ok)
            {
                d.PinFailures = 0;
                d.PinLockedUntil = null;
            }
            else if (++d.PinFailures >= MaxPinFailures)
            {
                d.PinFailures = 0;
                d.PinLockedUntil = now + PinLockout;
            }
            left = d.PinLockedUntil is { } locked && locked > now ? 0 : MaxPinFailures - d.PinFailures;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        db.ChangeTracker.Clear();
        if (!ok)
            throw left > 0
                ? ApiException.Forbidden($"PIN 不正确，还可以再试 {left} 次")
                : new ApiException(429, "PIN 错误次数太多", $"已锁定 {PinLockout.TotalMinutes:0} 分钟");
    }

    /// <summary>被控端 Agent 回复同意 / 拒绝。</summary>
    public async Task<RemoteSessionDto> ConsentAsync(Guid deviceId, Guid sessionId, bool accept, CancellationToken ct)
    {
        _ = await db.RemoteSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sessionId && s.TargetDeviceId == deviceId, ct)
            ?? throw ApiException.NotFound("会话");
        var now = DateTimeOffset.UtcNow;
        var pending = db.RemoteSessions.Where(s => s.Id == sessionId && s.State == RemoteSessionState.Pending && s.EndedAt == null && s.ExpiresAt > now);
        var changed = accept
            ? await pending.ExecuteUpdateAsync(set => set.SetProperty(s => s.State, RemoteSessionState.Active).SetProperty(s => s.AcceptedAt, now), ct)
            : await pending.ExecuteUpdateAsync(set => set
                .SetProperty(s => s.State, RemoteSessionState.Ended).SetProperty(s => s.EndedAt, now).SetProperty(s => s.EndReason, "declined"), ct);
        if (changed == 0) throw ApiException.Conflict("这次请求已经结束了");
        if (!accept)
        {
            await AfterEndedAsync(sessionId, "declined");
            return await DtoAsync(sessionId);
        }
        var dto = await DtoAsync(sessionId);
        await notifier.SessionChangedAsync(dto);
        return dto;
    }

    public async Task<RemoteSessionDto> GetSessionAsync(ClaimsPrincipal user, Guid id, CancellationToken ct)
    {
        var (session, device) = await VisibleSessionAsync(user, id, ct);
        return RemoteSessionDto.From(session, device);
    }

    /// <summary>挂断：控制的人、被控的 Agent、设备主人、管理员都可以。</summary>
    public async Task<RemoteSessionDto> EndSessionAsync(ClaimsPrincipal user, Guid id, CancellationToken ct)
    {
        var (session, _) = await VisibleSessionAsync(user, id, ct);
        var reason = user.DeviceId() is not null ? "agent" : session.ControllerUserId == user.UserId() ? "controller" : "stopped";
        await EndCoreAsync(id, reason);
        return await DtoAsync(id);
    }

    private async Task<(RemoteSession, Device)> VisibleSessionAsync(ClaimsPrincipal user, Guid id, CancellationToken ct)
    {
        var row = await (from s in db.RemoteSessions.AsNoTracking()
                         join d in db.Devices.AsNoTracking() on s.TargetDeviceId equals d.Id
                         where s.Id == id
                         select new { s, d }).FirstOrDefaultAsync(ct) ?? throw ApiException.NotFound("会话");
        var me = user.UserId();
        var visible = user.DeviceId() is { } deviceId
            ? deviceId == row.s.TargetDeviceId
            : row.s.ControllerUserId == me || row.d.UserId == me || user.Role() == UserRole.Admin;
        if (!visible) throw ApiException.NotFound("会话");
        return (row.s, row.d);
    }

    public async Task<RemoteSessionPageDto> ListSessionsAsync(Guid me, UserRole role, DateTimeOffset? before, int limit, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, 100);
        var q = from s in db.RemoteSessions.AsNoTracking()
                join d in db.Devices.AsNoTracking() on s.TargetDeviceId equals d.Id
                select new { s, d };
        if (role != UserRole.Admin) q = q.Where(x => x.s.ControllerUserId == me || x.d.UserId == me);
        if (before is { } b) q = q.Where(x => x.s.StartedAt < b);
        var rows = await q.OrderByDescending(x => x.s.StartedAt).Take(limit + 1).ToListAsync(ct);
        var items = rows.Take(limit).Select(x => RemoteSessionDto.From(x.s, x.d)).ToList();
        return new RemoteSessionPageDto(items, rows.Count > limit ? items[^1].StartedAt : null);
    }

    /// <summary>
    /// Agent 连上来时补发未结束的会话（请求发出时它恰好在重连、或服务器还记着它的旧连接）：
    /// 等同意的再弹一次窗，进行中的（无人值守）直接连数据面。Agent 对已经在跑的会话会忽略重复的通知。
    /// </summary>
    public async Task<List<AgentSessionRequest>> OpenRequestsAsync(Guid deviceId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var rows = await (from s in db.RemoteSessions.AsNoTracking()
                          join u in db.Users.AsNoTracking() on s.ControllerUserId equals u.Id
                          where s.TargetDeviceId == deviceId && s.EndedAt == null
                                && (s.State == RemoteSessionState.Active || (s.State == RemoteSessionState.Pending && s.ExpiresAt > now))
                          select new { s, u.DisplayName }).ToListAsync(ct);
        return [.. rows.Select(r => ToAgentRequest(r.s, r.DisplayName))];
    }

    /// <summary>带条件地结束：已经结束的不动。返回这次是否真的结束了它。通知不受调用方取消影响。</summary>
    public async Task<bool> EndCoreAsync(Guid id, string reason, Expression<Func<RemoteSession, bool>>? only = null)
    {
        var now = DateTimeOffset.UtcNow;
        var target = db.RemoteSessions.Where(s => s.Id == id && s.EndedAt == null);
        if (only is not null) target = target.Where(only);
        var changed = await target.ExecuteUpdateAsync(set => set
            .SetProperty(s => s.State, RemoteSessionState.Ended)
            .SetProperty(s => s.EndedAt, now)
            .SetProperty(s => s.EndReason, reason));
        if (changed == 0) return false;
        await AfterEndedAsync(id, reason);
        return true;
    }

    private async Task AfterEndedAsync(Guid id, string reason)
    {
        var bytes = await relay.CloseAsync(id);
        if (bytes > 0) await db.RemoteSessions.Where(s => s.Id == id).ExecuteUpdateAsync(set => set.SetProperty(s => s.BytesRelayed, s => s.BytesRelayed + bytes));
        var dto = await DtoAsync(id);
        await notifier.SessionEndedAsync(dto.TargetDeviceId, new AgentSessionEnded(id, reason));
        await notifier.SessionChangedAsync(dto);
        logger.LogInformation("远程控制会话 {Session} 结束：{Reason}，转发 {Bytes} 字节", id, reason, bytes);
    }

    private async Task<RemoteSessionDto> DtoAsync(Guid id)
    {
        var row = await (from s in db.RemoteSessions.AsNoTracking()
                         join d in db.Devices.AsNoTracking() on s.TargetDeviceId equals d.Id
                         where s.Id == id
                         select new { s, d }).FirstAsync();
        return RemoteSessionDto.From(row.s, row.d);
    }

    // ---- 维护 ----

    public async Task SweepAsync(DateTimeOffset now, CancellationToken ct)
    {
        var expired = await db.RemoteSessions
            .Where(s => s.EndedAt == null && s.State == RemoteSessionState.Pending && s.ExpiresAt <= now)
            .Select(s => s.Id).ToListAsync(ct);
        // 只结束仍在等同意的：与同意同时发生时，已经被同意的不能再被当成超时结束
        foreach (var id in expired) await EndCoreAsync(id, "timeout", s => s.State == RemoteSessionState.Pending && s.ExpiresAt <= now);

        var active = await db.RemoteSessions.AsNoTracking()
            .Where(s => s.EndedAt == null && s.State == RemoteSessionState.Active)
            .Select(s => new { s.Id, s.Mode }).ToListAsync(ct);
        foreach (var s in active)
        {
            var p = relay.Track(s.Id, s.Mode);
            if ((!p.Agent && now - p.AgentAbsentSince > AbsentTimeout) || (!p.Controller && now - p.ControllerAbsentSince > AbsentTimeout))
                await EndCoreAsync(s.Id, "disconnected");
        }

        // 中继里还挂着、会话却已结束的（结束和连上恰好同时发生）：关掉
        var tracked = relay.Sessions.Except(active.Select(a => a.Id)).ToList();
        if (tracked.Count > 0)
            foreach (var id in await db.RemoteSessions.Where(s => tracked.Contains(s.Id) && s.EndedAt != null).Select(s => s.Id).ToListAsync(ct))
                await relay.CloseAsync(id);
    }

    public Task TouchAsync(Guid deviceId)
        => db.Devices.Where(d => d.Id == deviceId).ExecuteUpdateAsync(set => set.SetProperty(d => d.LastSeenAt, DateTimeOffset.UtcNow));
}

using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PrivateChat.Api.Features.Auth;
using PrivateChat.Api.Infrastructure;
using PrivateChat.Contracts.Remote;

namespace PrivateChat.Api.Features.Remote;

/// <summary>环境变量 Remote__AgentAccessClientId / Remote__AgentAccessClientSecret / Remote__AgentDownloadUrl。</summary>
public sealed class RemoteOptions
{
    public const string Section = "Remote";
    /// <summary>Cloudflare Access 服务令牌：Agent 用它过 Access 门禁（写进"添加电脑"的配置串里）。</summary>
    public string AgentAccessClientId { get; set; } = "";
    public string AgentAccessClientSecret { get; set; } = "";
    /// <summary>Agent 安装包的下载地址（web 镜像里自带一份）。</summary>
    public string AgentDownloadUrl { get; set; } = "/downloads/PrivateChat.Agent.exe";
}

[ApiController]
[Route("api/devices")]
[Authorize]
public sealed class DevicesController(RemoteService remote) : ControllerBase
{
    /// <summary>我能远程或能管理的电脑（装了 Agent 的），带在线状态与正在进行的会话。</summary>
    [HttpGet]
    public Task<List<DeviceDto>> List(CancellationToken ct) => remote.ListDevicesAsync(User.UserId(), User.Role(), ct);

    /// <summary>改名、白名单、无人值守 PIN（主人或管理员）。</summary>
    [HttpPatch("{id:guid}")]
    public Task<DeviceDto> Update(Guid id, UpdateDeviceRequest req, CancellationToken ct)
        => remote.UpdateDeviceAsync(User.UserId(), User.Role(), id, req, ct);

    /// <summary>移除（吊销设备令牌）：Agent 立即断开，要重新输设备码才能再用。</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken ct)
    {
        await remote.RevokeDeviceAsync(User.UserId(), User.Role(), id, ct);
        return NoContent();
    }

    /// <summary>"添加电脑"向导：下载地址 + 配置串（Agent 第一次运行时粘贴，里面有服务器地址和 Access 服务令牌）。</summary>
    [HttpGet("agent-setup")]
    [Authorize(Policy = AuthPolicies.Member)]
    public AgentSetupDto AgentSetup([FromServices] IOptions<AppOptions> app, [FromServices] IOptions<RemoteOptions> options)
    {
        var o = options.Value;
        var server = app.Value.PublicUrl.TrimEnd('/');
        var configured = o.AgentAccessClientId.Length > 0 && o.AgentAccessClientSecret.Length > 0;
        var json = JsonSerializer.SerializeToUtf8Bytes(new { v = 1, url = server, id = configured ? o.AgentAccessClientId : null, secret = configured ? o.AgentAccessClientSecret : null });
        var code = "PCA1." + Convert.ToBase64String(json).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return new AgentSetupDto(server, string.IsNullOrWhiteSpace(o.AgentDownloadUrl) ? null : o.AgentDownloadUrl, code, configured);
    }
}

[ApiController]
[Route("api/remote/sessions")]
[Authorize]
public sealed class RemoteSessionsController(RemoteService remote) : ControllerBase
{
    /// <summary>发起远程（查看 / 控制）。带 PIN 时直接开始，否则等被控端同意（30 秒）。</summary>
    [HttpPost]
    [ProducesResponseType<RemoteSessionDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<RemoteSessionDto>> Create(CreateRemoteSessionRequest req, CancellationToken ct)
    {
        if (User.DeviceId() is not null) throw ApiException.Forbidden("设备不能发起远程");
        var dto = await remote.CreateSessionAsync(User.UserId(), User.Role(), req, ct);
        return Created($"/api/remote/sessions/{dto.Id}", dto);
    }

    /// <summary>远程记录（审计）：管理员看全部，其他人看自己发起的和自己电脑被远程的。</summary>
    [HttpGet]
    public Task<RemoteSessionPageDto> List([FromQuery] DateTimeOffset? before, [FromQuery] int limit = 30, CancellationToken ct = default)
    {
        if (User.DeviceId() is not null) throw ApiException.Forbidden();
        return remote.ListSessionsAsync(User.UserId(), User.Role(), before, limit, ct);
    }

    [HttpGet("{id:guid}")]
    public Task<RemoteSessionDto> Get(Guid id, CancellationToken ct) => remote.GetSessionAsync(User, id, ct);

    /// <summary>被控端 Agent 回复同意 / 拒绝。</summary>
    [HttpPost("{id:guid}/consent")]
    public Task<RemoteSessionDto> Consent(Guid id, RemoteConsentRequest req, CancellationToken ct)
    {
        var deviceId = User.DeviceId() ?? throw ApiException.Forbidden("只有被控电脑上的远程协助程序可以回复");
        return remote.ConsentAsync(deviceId, id, req.Accept, ct);
    }

    /// <summary>挂断：控制的人、被控端、设备主人、管理员。</summary>
    [HttpPost("{id:guid}/end")]
    public Task<RemoteSessionDto> End(Guid id, CancellationToken ct) => remote.EndSessionAsync(User, id, ct);
}

/// <summary>数据面：WebSocket /ws/remote/{sessionId}/{agent|controller}，会话进行中才能连。</summary>
[ApiController]
[ApiExplorerSettings(IgnoreApi = true)]
[Authorize]
public sealed class RemoteRelayController(AppDbContext db, RemoteRelay relay) : ControllerBase
{
    [HttpGet("/ws/remote/{sessionId:guid}/{role}")]
    public async Task Connect(Guid sessionId, string role)
    {
        if (!HttpContext.WebSockets.IsWebSocketRequest)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        var session = await db.RemoteSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sessionId, HttpContext.RequestAborted);
        var deviceId = User.DeviceId();
        RelayRole? relayRole = role switch
        {
            "agent" when session is not null && deviceId == session.TargetDeviceId => RelayRole.Agent,
            "controller" when session is not null && deviceId is null && User.UserId() == session.ControllerUserId => RelayRole.Controller,
            _ => null,
        };
        if (relayRole is null || session is null)
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        if (session.State != RemoteSessionState.Active || session.EndedAt is not null)
        {
            Response.StatusCode = StatusCodes.Status409Conflict;
            return;
        }
        // 每次连接都按现在的权限再看一遍：设备被吊销、被移出名单、被降为孩子或停用的，不能再连回来
        var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == session.TargetDeviceId, HttpContext.RequestAborted);
        var allowed = device is { RevokedAt: null };
        if (allowed && relayRole == RelayRole.Controller)
        {
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == session.ControllerUserId, HttpContext.RequestAborted);
            allowed = user is { DisabledAt: null } && RemoteService.CanControl(device!, user.Id, user.Role);
        }
        if (!allowed)
        {
            Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        using var socket = await HttpContext.WebSockets.AcceptWebSocketAsync();
        await relay.RunAsync(sessionId, relayRole.Value, session.Mode, socket, HttpContext.RequestAborted);
    }
}

/// <summary>每 5 秒：等同意超时的结束；进行中但一端断开超过 30 秒的结束。</summary>
public sealed class RemoteMaintenanceWorker(IServiceScopeFactory scopes, ILogger<RemoteMaintenanceWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<RemoteService>().SweepAsync(DateTimeOffset.UtcNow, stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "远程控制维护任务失败");
            }
        }
    }
}

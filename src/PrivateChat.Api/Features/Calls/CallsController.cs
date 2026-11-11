using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PrivateChat.Api.Features.Auth;
using PrivateChat.Api.Infrastructure;

namespace PrivateChat.Api.Features.Calls;

/// <summary>
/// 通话接口（docs/DESIGN.md 4.5 与附录 A）。浏览器永远不拿 App Secret，所有 Cloudflare 调用经这里转发；
/// 状态变化经 SignalR 广播：IncomingCall / CallUpdated / ParticipantJoined / ParticipantLeft / ParticipantUpdated /
/// TrackPublished / TrackUnpublished / CallDeclined。孩子账号也可以打电话。
/// </summary>
[ApiController]
[Route("api/calls")]
[Authorize]
public sealed class CallsController(CallService calls) : ControllerBase
{
    /// <summary>通话功能是否可用（部署时填了 Cloudflare Realtime 凭据）以及前端要遵守的时间参数。</summary>
    [HttpGet("config")]
    public ActionResult<CallConfigDto> Config()
        => new CallConfigDto(calls.Enabled, (int)CallService.RingTimeout.TotalSeconds, CallService.HeartbeatSeconds);

    /// <summary>通话记录（我所在会话里的），新的在前。</summary>
    [HttpGet]
    public async Task<ActionResult<List<CallDto>>> History([FromQuery] Guid? conversationId, [FromQuery] int limit = 50, CancellationToken ct = default)
        => await calls.HistoryAsync(User.UserId(), conversationId, limit, ct);

    /// <summary>进行中的通话：响铃中的来电、可以加入的通话。</summary>
    [HttpGet("active")]
    public async Task<ActionResult<List<CallDto>>> Active(CancellationToken ct)
        => await calls.ActiveAsync(User.UserId(), ct);

    /// <summary>常驻会议室（客厅）及当前在里面的人。</summary>
    [HttpGet("rooms")]
    public async Task<ActionResult<List<CallDto>>> Rooms(CancellationToken ct)
        => await calls.RoomsAsync(User.UserId(), ct);

    /// <summary>本月通话用量估算。</summary>
    [HttpGet("usage")]
    [Authorize(Policy = AuthPolicies.Admin)]
    public async Task<ActionResult<CallUsageDto>> Usage(CancellationToken ct)
        => await calls.UsageAsync(DateTimeOffset.UtcNow, ct);

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<CallDto>> Get(Guid id, CancellationToken ct)
        => await calls.GetAsync(User.UserId(), id, ct);

    /// <summary>在会话里发起通话，其他成员响铃；已有进行中的通话则返回它。之后调 join 进入。</summary>
    [HttpPost]
    public async Task<ActionResult<CallDto>> Start([FromBody] StartCallRequest req, CancellationToken ct)
        => (await calls.StartAsync(User.UserId(), req, ct)).Call;

    /// <summary>加入（接听）：返回参与者 Id、ICE 服务器（含短期 TURN 凭据）与通话快照。</summary>
    [HttpPost("{id:guid}/join")]
    public async Task<ActionResult<JoinResponse>> Join(Guid id, CancellationToken ct)
        => await calls.JoinAsync(User.UserId(), id, ct);

    [HttpPost("{id:guid}/decline")]
    public async Task<IActionResult> Decline(Guid id, CancellationToken ct)
    {
        await calls.DeclineAsync(User.UserId(), id, ct);
        return NoContent();
    }

    /// <summary>发布本端轨道：SDP offer → answer。</summary>
    [HttpPost("{id:guid}/publish")]
    public async Task<ActionResult<PublishResponse>> Publish(Guid id, [FromBody] PublishRequest req, CancellationToken ct)
        => await calls.PublishAsync(User.UserId(), id, req, ct);

    [HttpPost("{id:guid}/unpublish")]
    public async Task<ActionResult<UnpublishResponse>> Unpublish(Guid id, [FromBody] UnpublishRequest req, CancellationToken ct)
        => await calls.UnpublishAsync(User.UserId(), id, req, ct);

    /// <summary>订阅他人轨道：返回 Cloudflare 的 offer，浏览器回 answer 后调 renegotiate。</summary>
    [HttpPost("{id:guid}/pull")]
    public async Task<ActionResult<PullResponse>> Pull(Guid id, [FromBody] PullRequest req, CancellationToken ct)
        => await calls.PullAsync(User.UserId(), id, req, ct);

    [HttpPost("{id:guid}/renegotiate")]
    public async Task<IActionResult> Renegotiate(Guid id, [FromBody] RenegotiateApiRequest req, CancellationToken ct)
    {
        await calls.RenegotiateAsync(User.UserId(), id, req.Answer, ct);
        return NoContent();
    }

    /// <summary>不再接收某些轨道（按订阅会话里的 mid）。</summary>
    [HttpPost("{id:guid}/pull/close")]
    public async Task<IActionResult> ClosePulled(Guid id, [FromBody] ClosePulledRequest req, CancellationToken ct)
    {
        await calls.ClosePulledAsync(User.UserId(), id, req, ct);
        return NoContent();
    }

    /// <summary>麦克风 / 摄像头开关状态，给别人显示图标用（媒体本身不经过这里）。</summary>
    [HttpPost("{id:guid}/state")]
    public async Task<IActionResult> State(Guid id, [FromBody] ParticipantStateRequest req, CancellationToken ct)
    {
        await calls.SetStateAsync(User.UserId(), id, req, ct);
        return NoContent();
    }

    /// <summary>每 10 秒一次；返回 410 表示已不在通话里。</summary>
    [HttpPost("{id:guid}/heartbeat")]
    public async Task<IActionResult> Heartbeat(Guid id, CancellationToken ct)
    {
        await calls.HeartbeatAsync(User.UserId(), id, ct);
        return NoContent();
    }

    /// <summary>离开。关页面时浏览器用 sendBeacon 调它（没有请求体）。</summary>
    [HttpPost("{id:guid}/leave")]
    public async Task<IActionResult> Leave(Guid id)
    {
        // 不跟随请求取消：关页面发来的 beacon 连接随时会断，离开必须做完
        await calls.LeaveAsync(User.UserId(), id, CancellationToken.None);
        return NoContent();
    }

    [HttpPost("{id:guid}/end")]
    public async Task<IActionResult> End(Guid id, CancellationToken ct)
    {
        await calls.EndAsync(User.UserId(), User.Role(), id, ct);
        return NoContent();
    }
}

/// <summary>每 5 秒扫一遍：响铃超时记未接，心跳断了的参与者按离开处理。</summary>
public sealed class CallMaintenanceWorker(IServiceScopeFactory scopes, ILogger<CallMaintenanceWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<CallService>().SweepAsync(DateTimeOffset.UtcNow, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "通话清理任务出错");
            }
        }
    }
}

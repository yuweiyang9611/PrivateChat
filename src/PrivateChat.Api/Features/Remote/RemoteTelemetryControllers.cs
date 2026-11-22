using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PrivateChat.Api.Features.Auth;
using PrivateChat.Api.Infrastructure;
using PrivateChat.Contracts.Remote;

namespace PrivateChat.Api.Features.Remote;

/// <summary>
/// 远程协助遥测的上报（docs/DESIGN.md 4.8"远程协助遥测"）。先看来的是谁、会话还收不收，再读请求体。
/// 请求体自己读（<see cref="TelemetryBody"/>）：限 64 KB，不靠服务器全局的 64 MB；缺了的字段不报错，清理时补空值——遥测有毛病也不该让被控端、网页出错。
/// </summary>
[ApiController]
[Authorize]
public sealed class RemoteTelemetryController(RemoteTelemetryService telemetry) : ControllerBase
{
    /// <summary>
    /// 被控端上报这台电脑的硬件和能力（AgentTelemetrySnapshot，连上服务器后一次）。只认设备令牌，浏览器登录的人 403。
    /// /api/remote/agent 这个前缀在设备令牌能用的路径里，下面只放被控端自己用的接口。
    /// </summary>
    [HttpPut("/api/remote/agent/telemetry")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> PutSnapshot(CancellationToken ct)
    {
        if (User.DeviceId() is null) throw ApiException.Forbidden("只有被控电脑上的远程协助程序可以上报");
        var snapshot = await TelemetryBody.ReadAsync<AgentTelemetrySnapshot>(Request, ct);
        await telemetry.PutSnapshotAsync(User, snapshot, ct);
        return NoContent();
    }

    /// <summary>被控端这次远程的汇总（AgentSessionTelemetry，中途每 60 秒、结束时各一次，按会话覆盖）。会话进行中、结束后 24 小时内都收。</summary>
    [HttpPut("/api/remote/sessions/{id:guid}/telemetry")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> PutAgent(Guid id, CancellationToken ct)
    {
        var session = await telemetry.AgentSessionAsync(User, id, ct);
        var body = await TelemetryBody.ReadAsync<AgentSessionTelemetry>(Request, ct);
        await telemetry.PutAgentAsync(session, body, ct);
        return NoContent();
    }

    /// <summary>
    /// 控制端浏览器这次远程的汇总（ViewerSessionTelemetry，每 60 秒一次，结束 / 关页面时用 sendBeacon 再发一次）。
    /// 只认发起这次远程的人（Cookie / Access 登录），设备令牌 403。Content-Type 要是 application/json（sendBeacon 发 JSON Blob 就是）。
    /// </summary>
    [HttpPost("/api/remote/sessions/{id:guid}/viewer-telemetry")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> PostViewer(Guid id, CancellationToken ct)
    {
        var session = await telemetry.ViewerSessionAsync(User, id, ct);
        var body = await TelemetryBody.ReadAsync<ViewerSessionTelemetry>(Request, ct);
        await telemetry.PutViewerAsync(session, body, ct);
        return NoContent();
    }
}

/// <summary>管理员看遥测（设备页的"诊断"）。不在设备令牌能用的路径里。</summary>
[ApiController]
[Route("api/telemetry")]
[Authorize(Policy = AuthPolicies.Admin)]
public sealed class TelemetryController(RemoteTelemetryService telemetry) : ControllerBase
{
    /// <summary>这台电脑的硬件和能力，和最近 limit 次（1–100，默认 20）有遥测的远程（新的在前）。</summary>
    [HttpGet("devices/{deviceId:guid}")]
    public Task<DeviceTelemetryDto> GetDevice(Guid deviceId, CancellationToken ct, [FromQuery] int limit = RemoteTelemetryService.DefaultLimit)
    {
        // 设备令牌带着主人的身份和角色：管理员自己那台电脑上的令牌也过得了 Admin 策略。
        // /api/telemetry 本来就不在设备令牌能用的路径里（认证这一步就是 401），这里再拦一道，免得以后放宽路径时漏掉
        if (User.DeviceId() is not null) throw ApiException.Forbidden("设备不能查看遥测");
        return telemetry.DeviceAsync(deviceId, limit, ct);
    }
}

/// <summary>遥测的请求体：要是 JSON（415），最多 64 KB（413，分块传、没有 Content-Length 的也数着读），JSON 不对 400。</summary>
public static class TelemetryBody
{
    /// <summary>硬件快照一般几 KB，字段都有长度上限；超过 64 KB 的不是正常的被控端 / 网页。</summary>
    public const int MaxBytes = 64 * 1024;

    public static async Task<T> ReadAsync<T>(HttpRequest request, CancellationToken ct) where T : class
    {
        if (!request.HasJsonContentType()) throw new ApiException(StatusCodes.Status415UnsupportedMediaType, "请求无效", "遥测数据要用 JSON（Content-Type: application/json）");
        if (request.ContentLength > MaxBytes) throw TooLarge();
        using var body = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await request.Body.ReadAsync(chunk.AsMemory(), ct)) > 0)
        {
            if (body.Length + read > MaxBytes) throw TooLarge();
            body.Write(chunk, 0, read);
        }
        T? value;
        try
        {
            value = JsonSerializer.Deserialize<T>(body.GetBuffer().AsSpan(0, (int)body.Length), RemoteTelemetryService.Json);
        }
        catch (JsonException)
        {
            throw ApiException.BadRequest("遥测数据不是有效的 JSON");
        }
        return value ?? throw ApiException.BadRequest("遥测数据是空的");
    }

    private static ApiException TooLarge() => new(StatusCodes.Status413PayloadTooLarge, "请求太大", $"遥测数据最多 {MaxBytes / 1024} KB");
}

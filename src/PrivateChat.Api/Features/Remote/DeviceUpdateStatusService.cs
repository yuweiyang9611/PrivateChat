using Microsoft.EntityFrameworkCore;
using PrivateChat.Api.Domain;
using PrivateChat.Api.Infrastructure;
using PrivateChat.Contracts.Remote;

namespace PrivateChat.Api.Features.Remote;

public sealed class DeviceUpdateStatusService(AppDbContext db, AgentRegistry agents)
{
    public async Task PutAsync(Guid deviceId, AgentUpdateStatusReport report, CancellationToken ct)
    {
        Validate(report);
        // PostgreSQL stores microseconds; compare the same precision on the first insert and on retries.
        var ticks = report.CheckedAt.UtcTicks;
        var checkedAt = new DateTimeOffset(ticks - ticks % 10, TimeSpan.Zero);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Lock the parent even on the first report, when there is no status row to lock.
        // AgentHub.Report holds the same row lock while updating the version and confirming a restart.
        var authorized = await db.Database.SqlQuery<bool>($"""
            SELECT TRUE AS "Value" FROM devices WHERE id = {deviceId} AND kind = 'Agent' AND revoked_at IS NULL FOR UPDATE
            """).SingleOrDefaultAsync(ct);
        if (!authorized) throw ApiException.Forbidden("只有已登记的被控电脑可以上报");
        var row = await db.DeviceUpdateStatuses.SingleOrDefaultAsync(s => s.DeviceId == deviceId, ct);
        var now = DateTimeOffset.UtcNow;
        if (row is null)
        {
            row = new DeviceUpdateStatus { DeviceId = deviceId };
            db.DeviceUpdateStatuses.Add(row);
        }
        else if (row.CheckId == report.CheckId)
        {
            if (checkedAt != row.CheckedAt || row.Source != report.Source || row.CurrentVersion != report.CurrentVersion ||
                (row.TargetVersion is not null && report.TargetVersion is not null && row.TargetVersion != report.TargetVersion) ||
                !Advances(row, report)) return;
        }
        else if (checkedAt <= row.CheckedAt) return;

        if (row.CheckId != report.CheckId)
        {
            row.CheckId = report.CheckId;
            row.CheckedAt = checkedAt;
            row.FirstReceivedAt = now;
            row.Source = report.Source;
            row.CurrentVersion = report.CurrentVersion;
            row.TargetVersion = null;
            row.ConfirmedAt = null;
        }
        row.ReceivedAt = now;
        row.TargetVersion = report.TargetVersion ?? row.TargetVersion;
        row.State = report.State;
        row.ElapsedMs = report.ElapsedMs;
        row.FailureStage = report.FailureStage;
        row.ErrorCode = report.ErrorCode;
        // The new process may have reported its version while the previous process's final HTTP message was in flight.
        if (row.State == "restarting" && agents.Get(deviceId) is { Report: { } online } entry &&
            row.TargetVersion == online.Version && entry.Since >= row.FirstReceivedAt)
            row.ConfirmedAt = now;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private static bool Advances(DeviceUpdateStatus old, AgentUpdateStatusReport next)
    {
        if (next.ElapsedMs < old.ElapsedMs || old.ConfirmedAt.HasValue) return false;
        if (old.State == "restarting") return next.State is "failed" or "cancelled";
        var previous = Step(old.State);
        return previous < 5 && Step(next.State) >= previous &&
            (old.State != next.State || old.ElapsedMs < next.ElapsedMs || (old.TargetVersion == null && next.TargetVersion != null));
    }

    private static int Step(string state) => state switch
    {
        "checking" => 0, "available" => 1, "downloading" => 2, "verifying" => 3, "restarting" => 4, _ => 5,
    };

    private static bool ValidVersion(string? text) => text is { Length: > 0 and <= 32 } &&
        text.All(c => char.IsAsciiDigit(c) || c == '.') && Version.TryParse(text, out _);

    private static void Validate(AgentUpdateStatusReport r)
    {
        if (r.CheckId == Guid.Empty || r.CheckedAt == default || r.CheckedAt > DateTimeOffset.UtcNow.AddMinutes(10) ||
            r.Source is not ("manual" or "auto") || !ValidVersion(r.CurrentVersion) ||
            (r.TargetVersion is not null && !ValidVersion(r.TargetVersion)) || r.ElapsedMs < 0 ||
            r.State is not ("checking" or "available" or "downloading" or "verifying" or "restarting" or "current" or
                "busy" or "postponed" or "cancelled" or "failed" or "noManifest" or "unavailable") ||
            r.FailureStage is not (null or "check" or "download" or "verify" or "install") ||
            r.ErrorCode is not (null or "network" or "timeout" or "integrity" or "permission" or "storage" or "launch" or
                "invalidManifest" or "server" or "unknown"))
            throw ApiException.BadRequest("更新状态字段无效");
        if (r.State == "restarting" && (r.TargetVersion is null || Version.Parse(r.TargetVersion) <= Version.Parse(r.CurrentVersion)))
            throw ApiException.BadRequest("重启升级必须有更高的目标版本");
    }
}

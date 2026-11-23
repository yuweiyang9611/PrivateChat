using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR.Client;
using PrivateChat.Contracts.Remote;

namespace PrivateChat.Api.Tests.Integration;

public partial class RemoteTelemetryTests
{
    private const string UpdateStatusUrl = "/api/remote/agent/update-status";
    private static AgentUpdateStatusReport Check() => new(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(-5),
        "manual", "0.8.6", null, "checking", 0, null, null);
    private static Task<HttpResponseMessage> PutUpdate(HttpClient client, AgentUpdateStatusReport report) =>
        client.PutAsJsonAsync(UpdateStatusUrl, report, ApiFactory.Json);

    [PgFact]
    public async Task Update_status_is_device_scoped_and_visible_only_to_the_admin()
    {
        await using var x = await SetupAsync();
        var agent = await RegisterAsync(x, x.BobClient);
        var other = await RegisterAsync(x, x.CarolClient);
        var check = Check();
        Assert.Null((await DiagnosticsAsync(x.Admin, agent.DeviceId)).RecentUpdate);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PutUpdate(x.App.Anonymous(), check)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PutUpdate(x.BobClient, check)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PutUpdate(x.Admin, check)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await PutUpdate(agent.Http, check)).StatusCode);
        Assert.NotNull((await DiagnosticsAsync(x.Admin, agent.DeviceId)).RecentUpdate);
        Assert.Null((await DiagnosticsAsync(x.Admin, other.DeviceId)).RecentUpdate);
        Assert.Equal(HttpStatusCode.Forbidden, (await x.BobClient.GetAsync(DiagnosticsUrl(agent.DeviceId))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await x.CarolClient.GetAsync(DiagnosticsUrl(agent.DeviceId))).StatusCode);
        Assert.False((await agent.Http.GetAsync(DiagnosticsUrl(agent.DeviceId))).IsSuccessStatusCode);
        (await x.Admin.DeleteAsync($"/api/devices/{agent.DeviceId}")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await PutUpdate(agent.Http, check)).StatusCode);
    }

    [PgFact]
    public async Task Update_status_rejects_free_text_and_late_messages_cannot_replace_a_newer_or_terminal_check()
    {
        await using var x = await SetupAsync();
        var agent = await RegisterAsync(x, x.BobClient);
        var check = Check();
        foreach (var invalid in new[] {
            check with { Source = "unrecognized" }, check with { TargetVersion = "https://example.invalid" },
            check with { CurrentVersion = @"C:\example" }, check with { FailureStage = "details" },
            check with { ErrorCode = "an exception message" }, check with { State = "installed" },
            check with { ElapsedMs = -1 }, check with { CheckId = Guid.Empty },
            check with { State = "restarting", TargetVersion = check.CurrentVersion },
        })
            Assert.Equal(HttpStatusCode.BadRequest, (await PutUpdate(agent.Http, invalid)).StatusCode);
        var extra = Serialized(check).TrimEnd('}') + ",\"details\":\"unexpected\"}";
        Assert.Equal(HttpStatusCode.BadRequest, (await agent.Http.PutAsync(UpdateStatusUrl, Json(extra))).StatusCode);

        var downloading = check with { State = "downloading", TargetVersion = "0.8.7", ElapsedMs = 80 };
        await Task.WhenAll(PutUpdate(agent.Http, check), PutUpdate(agent.Http, downloading));
        Assert.Equal("downloading", (await DiagnosticsAsync(x.Admin, agent.DeviceId)).RecentUpdate!.State);
        var failed = downloading with { State = "failed", FailureStage = "download", ErrorCode = "timeout", ElapsedMs = 150 };
        (await PutUpdate(agent.Http, failed)).EnsureSuccessStatusCode();
        var stored = (await DiagnosticsAsync(x.Admin, agent.DeviceId)).RecentUpdate!;
        Assert.Equal(("failed", "download", "timeout", 150), (stored.State, stored.FailureStage, stored.ErrorCode, stored.ElapsedMs));
        Assert.Null(stored.ConfirmedAt);
        (await PutUpdate(agent.Http, downloading with { State = "verifying", ElapsedMs = 500 })).EnsureSuccessStatusCode();
        (await PutUpdate(agent.Http, check with { CheckId = Guid.NewGuid(), CheckedAt = check.CheckedAt.AddSeconds(-1) })).EnsureSuccessStatusCode();
        Assert.Equal(stored, (await DiagnosticsAsync(x.Admin, agent.DeviceId)).RecentUpdate);
        var fresh = check with { CheckId = Guid.NewGuid(), CheckedAt = check.CheckedAt.AddSeconds(1), Source = "auto", State = "noManifest" };
        (await PutUpdate(agent.Http, fresh)).EnsureSuccessStatusCode();
        var latest = (await DiagnosticsAsync(x.Admin, agent.DeviceId)).RecentUpdate!;
        Assert.Equal(("auto", "noManifest", null, null), (latest.Source, latest.State, latest.TargetVersion, latest.ErrorCode));
    }

    [PgFact]
    public async Task Update_status_only_confirms_a_newer_target_reported_by_a_new_hub_connection()
    {
        await using var x = await SetupAsync();
        var agent = await RegisterAsync(x, x.BobClient);
        var check = Check();
        var restarting = check with { TargetVersion = "0.8.7", State = "restarting", ElapsedMs = 30 };
        var target = new AgentReport("0.8.7", "Windows", []);
        await using (var old = await OnlineAsync(x, agent))
        {
            (await PutUpdate(agent.Http, check)).EnsureSuccessStatusCode();
            (await PutUpdate(agent.Http, restarting)).EnsureSuccessStatusCode();
            await old.InvokeAsync(AgentHubEvents.Report, target);
            Assert.Null((await DiagnosticsAsync(x.Admin, agent.DeviceId)).RecentUpdate!.ConfirmedAt);
        }
        await using (var fresh = await OnlineAsync(x, agent))
        {
            Assert.Null((await DiagnosticsAsync(x.Admin, agent.DeviceId)).RecentUpdate!.ConfirmedAt);
            await fresh.InvokeAsync(AgentHubEvents.Report, target);
            var confirmed = (await DiagnosticsAsync(x.Admin, agent.DeviceId)).RecentUpdate!;
            Assert.NotNull(confirmed.ConfirmedAt);
            (await PutUpdate(agent.Http, restarting with { State = "failed", FailureStage = "install", ErrorCode = "launch", ElapsedMs = 90 })).EnsureSuccessStatusCode();
            Assert.Equal(confirmed, (await DiagnosticsAsync(x.Admin, agent.DeviceId)).RecentUpdate);

            var later = Check() with { CheckedAt = check.CheckedAt.AddSeconds(1) };
            (await PutUpdate(agent.Http, later)).EnsureSuccessStatusCode();
            // Once an installation and a new connection race, the final HTTP result may arrive last.
            await using var next = await OnlineAsync(x, agent);
            await next.InvokeAsync(AgentHubEvents.Report, target);
            (await PutUpdate(agent.Http, later with { State = "restarting", TargetVersion = "0.8.7" })).EnsureSuccessStatusCode();
            Assert.NotNull((await DiagnosticsAsync(x.Admin, agent.DeviceId)).RecentUpdate!.ConfirmedAt);
            (await PutUpdate(agent.Http, Check() with { CurrentVersion = "0.8.7", TargetVersion = "0.8.7", State = "current",
                CheckedAt = check.CheckedAt.AddSeconds(2) })).EnsureSuccessStatusCode();
            Assert.Null((await DiagnosticsAsync(x.Admin, agent.DeviceId)).RecentUpdate!.ConfirmedAt);
        }
    }
}

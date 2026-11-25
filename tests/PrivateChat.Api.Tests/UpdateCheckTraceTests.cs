using System.Net;
using PrivateChat.Agent;
using PrivateChat.Agent.Telemetry;
using PrivateChat.Contracts.Remote;

namespace PrivateChat.Api.Tests;

public class UpdateCheckTraceTests
{
    [Fact]
    public void Byte_samples_stay_local_and_only_safe_stage_changes_are_reported()
    {
        var reports = new List<AgentUpdateStatusReport>();
        var trace = new UpdateCheckTrace("manual", "1.0.0-beta", reports.Add, null);
        trace.State("checking");
        trace.Found("1.1.0+build");
        trace.Report(new(UpdateStage.Connecting, 0, 100));
        trace.Report(new(UpdateStage.Downloading, 30, 100));
        trace.Report(new(UpdateStage.Downloading, 100, 100));
        trace.Report(new(UpdateStage.Verifying, 100, 100));
        var failure = new UpdateIntegrityException("synthetic local path");
        trace.Failed(failure);
        trace.Failed(failure);

        Assert.Equal(["checking", "downloading", "verifying", "failed"], reports.Select(r => r.State));
        Assert.Single(reports.Select(r => (r.CheckId, r.CheckedAt)).Distinct());
        Assert.Equal(("1.0.0", "1.1.0", "verify", "integrity"),
            (reports[^1].CurrentVersion, reports[^1].TargetVersion, reports[^1].FailureStage, reports[^1].ErrorCode));
    }

    [Fact]
    public void A_refused_release_signature_is_its_own_category()
    {
        // 装成服务后拒装没签名 / 签名不对的新版本：管理员要看得出是签名的事，不是"原因未知"
        var reports = new List<AgentUpdateStatusReport>();
        var trace = new UpdateCheckTrace("auto", "0.8.12", reports.Add, null);
        trace.Found("0.8.13");
        trace.Installing();
        trace.Failed(new ReleaseSignatureException("没有发布签名"));
        Assert.Equal(("install", "signature"), (reports[^1].FailureStage, reports[^1].ErrorCode));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "permission")]
    [InlineData(HttpStatusCode.Forbidden, "permission")]
    [InlineData(HttpStatusCode.BadGateway, "server")]
    public void Checking_errors_use_categories_without_response_details(HttpStatusCode status, string expected)
    {
        var reports = new List<AgentUpdateStatusReport>();
        var trace = new UpdateCheckTrace("auto", "1.0.0", reports.Add, null);
        trace.Failed(new ServerException(status, "synthetic response"));
        Assert.Equal(("check", expected), (reports.Single().FailureStage, reports.Single().ErrorCode));
    }
}

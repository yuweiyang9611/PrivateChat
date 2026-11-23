using PrivateChat.Agent;

namespace PrivateChat.Api.Tests;

public class UpdateProgressTests
{
    [Fact]
    public void Only_the_first_body_byte_finishes_connecting()
    {
        var meter = new UpdateTransferMeter(12_000);
        meter.Received(0, 400);
        Assert.Equal(new UpdateProgress(UpdateStage.Connecting, 0, 12_000, ElapsedMs: 530), meter.Sample(530));

        meter.Received(2000, 800);
        var first = meter.Sample(850);
        Assert.Equal(new UpdateProgress(UpdateStage.Downloading, 2000, 12_000, FirstByteMs: 800, TransferMs: 50, ElapsedMs: 850), first);
    }

    [Fact]
    public void Recent_speed_counts_only_new_bytes_and_falls_to_zero_when_the_server_stalls()
    {
        var meter = new UpdateTransferMeter(12_000);
        meter.Received(2000, 800);
        meter.Received(1000, 1100);
        Assert.Null(meter.Sample(1299).BytesPerSecond);
        Assert.Equal(2000, meter.Sample(1300).BytesPerSecond);

        meter.Received(200, 1500);
        Assert.Equal(400, meter.Sample(1800).BytesPerSecond);
        var stalled = meter.Sample(2800);
        Assert.Equal(0, stalled.BytesPerSecond);
        Assert.Equal(3200, stalled.BytesReceived);
        Assert.Equal(800, stalled.FirstByteMs);
        Assert.Equal(2000, stalled.TransferMs);
    }
}

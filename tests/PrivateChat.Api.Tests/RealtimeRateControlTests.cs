using PrivateChat.Agent.Session;

namespace PrivateChat.Api.Tests;

/// <summary>快速线路（经 Cloudflare Realtime）的码率控制：只靠控制端每秒的汇报和 SFU 的丢包报告。</summary>
public class RealtimeRateControlTests
{
    private const int Min = 100, Max = 2500, Start = 600;

    private static RealtimeRateController New() => new(Min, Max, Start);

    /// <summary>
    /// 过一秒：按目标码率的 share 倍发 20 帧，然后控制端汇报一次。
    /// received：它收到的占发出的多少；loss：它看到的丢包千分比；buffer：浏览器里画面缓冲了多久。
    /// </summary>
    private static void Second(RealtimeRateController rate, ref long now, double share = 1, double received = 1, int loss = 0, int buffer = 20, int audioKbps = 0)
    {
        var kbps = rate.TargetKbps * share;
        var perFrame = (int)(kbps * 1000 / 8 / 20);
        for (var i = 0; i < 20; i++) rate.OnSent((uint)(now + i * 50), perFrame, now + i * 50);
        // 声音：一秒 50 包，只记账
        for (var i = 0; i < 50 && audioKbps > 0; i++) rate.OnSentUntracked(audioKbps * 1000 / 8 / 50, now + i * 20);
        now += 1000;
        rate.OnReceiverReport(loss, (int)(kbps * received), buffer, 12, now);
    }

    [Fact]
    public void Climbs_to_the_cap_while_the_picture_is_moving_and_nothing_is_wrong()
    {
        var rate = New();
        long now = 0;
        Second(rate, ref now);
        Assert.Equal(780, rate.TargetKbps); // 还没降过：一步三成
        for (var i = 0; i < 10; i++) Second(rate, ref now);
        Assert.Equal(Max, rate.TargetKbps);
    }

    [Fact]
    public void Does_not_climb_on_a_still_screen()
    {
        var rate = New();
        long now = 0;
        for (var i = 0; i < 10; i++) Second(rate, ref now, share: 0.1);
        // 码率没用起来，升了也不知道线路撑不撑得住
        Assert.Equal(Start, rate.TargetKbps);
    }

    [Fact]
    public void Heavy_loss_cuts_the_rate_in_proportion_and_not_twice_in_a_row()
    {
        var rate = New();
        long now = 0;
        Second(rate, ref now, loss: 200);
        Assert.Equal(510, rate.TargetKbps); // 丢两成：降一成，但最少降一成半
        Second(rate, ref now, loss: 200);
        Assert.Equal(510, rate.TargetKbps); // 刚降过，要等降速之后的效果
        Second(rate, ref now, loss: 600);
        Assert.Equal(357, rate.TargetKbps); // 丢六成：降三成
        now += 2000;
        Second(rate, ref now, loss: 1000);
        Assert.Equal(178, rate.TargetKbps); // 一次最多降一半
        for (var i = 0; i < 10; i++)
        {
            now += 2000;
            Second(rate, ref now, loss: 1000);
        }
        Assert.Equal(Min, rate.TargetKbps);
    }

    [Fact]
    public void Moderate_loss_holds_the_rate()
    {
        var rate = New();
        long now = 0;
        for (var i = 0; i < 6; i++) Second(rate, ref now, loss: 50);
        Assert.Equal(Start, rate.TargetKbps);
        // 丢包没了就接着升
        Second(rate, ref now, loss: 5);
        Assert.Equal(780, rate.TargetKbps);
    }

    [Fact]
    public void Two_reports_of_receiving_much_less_than_was_sent_drop_to_what_arrives()
    {
        var rate = New();
        long now = 0;
        Second(rate, ref now, received: 0.5);
        Assert.Equal(Start, rate.TargetKbps); // 一次不算（凑巧少算了一帧），但也不升
        Second(rate, ref now, received: 0.6);
        Assert.Equal(324, rate.TargetKbps); // 收到 360：降到它的九成
        // 降下来之后收发对上了：先等一等降速的效果，再慢慢往回升（降过之后步子小）
        Second(rate, ref now);
        Assert.Equal(324, rate.TargetKbps);
        Second(rate, ref now);
        Assert.Equal(359, rate.TargetKbps);
    }

    [Fact]
    public void Receiving_far_less_than_was_sent_never_cuts_more_than_half_at_once()
    {
        var rate = new RealtimeRateController(Min, Max, 2000);
        long now = 0;
        Second(rate, ref now, received: 0.05);
        Second(rate, ref now, received: 0.05);
        // 收到的只有 100：不是一步降到 90，这个信号没有丢包那么确凿
        Assert.Equal(1000, rate.TargetKbps);
    }

    [Fact]
    public void Sound_is_not_counted_as_picture_that_went_missing()
    {
        // 在真实的 Cloudflare 上踩过：声音算进了"发出的"，控制端汇报的却只有画面，码率低的时候看着像丢了三成，
        // 码率被一步从 2227 压到 112 kbps，接着一个大帧攒不够额度，画面卡了 6 秒
        var rate = new RealtimeRateController(Min, Max, 2000);
        long now = 0;
        for (var i = 0; i < 6; i++) Second(rate, ref now, share: 0.16, audioKbps: 160); // 画面 320 kbps，声音 160 kbps，画面全收到了
        Assert.Equal(2000, rate.TargetKbps);
        Assert.Equal(320, rate.SentKbps);
        // 声音照样占预算：发完一秒的画面和声音，账上欠着的比只发画面多
        var withSound = new RealtimeRateController(Min, Max, 600);
        var pictureOnly = new RealtimeRateController(Min, Max, 600);
        withSound.OnSent(1, 30_000, 0);
        withSound.OnSentUntracked(15_000, 0);
        pictureOnly.OnSent(1, 30_000, 0);
        Assert.True(pictureOnly.Idle(400));
        Assert.False(withSound.Idle(400));
        Assert.True(withSound.Idle(600));
    }

    [Fact]
    public void A_small_shortfall_at_a_low_rate_is_not_congestion()
    {
        var rate = new RealtimeRateController(Min, Max, 2000);
        long now = 0;
        // 画面只发了 280 kbps，收到 170：数字太小，统计的时间差一点点就能差出这么多，不算
        for (var i = 0; i < 6; i++) Second(rate, ref now, share: 0.14, received: 0.6);
        Assert.Equal(2000, rate.TargetKbps);
    }

    [Fact]
    public void A_single_light_frame_is_not_mistaken_for_congestion()
    {
        var rate = New();
        long now = 0;
        // 发得很少的时候，收到的比例说明不了什么
        for (var i = 0; i < 5; i++) Second(rate, ref now, share: 0.1, received: 0.3);
        Assert.Equal(Start, rate.TargetKbps);
    }

    [Fact]
    public void A_deep_buffer_in_the_browser_twice_in_a_row_backs_off()
    {
        var rate = New();
        long now = 0;
        Second(rate, ref now, buffer: 400);
        Assert.Equal(Start, rate.TargetKbps);
        Second(rate, ref now, buffer: 400);
        Assert.Equal(510, rate.TargetKbps);
    }

    [Fact]
    public void Loss_reported_by_the_sfu_counts_while_it_is_fresh()
    {
        var rate = New();
        long now = 0;
        rate.OnUpstreamLoss(0.3, now);
        Second(rate, ref now);
        Assert.Equal(510, rate.TargetKbps); // 上行丢三成：降一成半
        // 同一份报告只算一次：SFU 没再报丢包，过了等待期就接着往上爬
        Second(rate, ref now);
        Assert.Equal(510, rate.TargetKbps);
        Second(rate, ref now);
        Assert.Equal(560, rate.TargetKbps);

        // 放了 6 秒以上的报告不算（这期间控制端没汇报过，比如它的标签页被挂起了）
        rate.OnUpstreamLoss(0.5, now);
        now += 7000;
        Second(rate, ref now);
        Assert.True(rate.TargetKbps >= 560);
    }

    [Fact]
    public void A_big_frame_is_paid_off_at_the_target_rate_before_the_next_one()
    {
        var rate = New();
        Assert.True(rate.CanSend(0, 50));
        Assert.True(rate.Idle(0));
        rate.OnSent(1, 30_000, 0); // 600 kbps 下要 400 ms 才"发完"
        Assert.False(rate.CanSend(0, 50));
        Assert.False(rate.CanSend(250, 50));
        Assert.True(rate.CanSend(300, 50)); // 欠的不超过 100 ms 就可以再发
        Assert.False(rate.Idle(300));
        Assert.True(rate.Idle(400));
        // 突发额度：安静之后的第一次变化可以多用一些，持续变化时每帧只有一帧时间的量
        Assert.Equal(350 * 600 / 8, rate.ChangeBudget(400, sustained: false, 50));
        Assert.Equal(50 * 600 / 8, rate.ChangeBudget(400, sustained: true, 50));
        Assert.Equal(200 * 600 / 8, rate.Credit(400));
        Assert.Equal(0, rate.InFlight);
        Assert.False(rate.Stalled(60_000));
    }

    [Fact]
    public void The_cap_follows_the_quality_preset_and_round_trip_adds_both_legs()
    {
        var rate = new RealtimeRateController(Min, 6000, 4000);
        rate.SetMax(2500);
        Assert.Equal((2500, 2500), (rate.TargetKbps, rate.MaxKbps));
        Assert.Equal(0, rate.MinRttMs);
        rate.SetUpstreamRtt(8);
        rate.OnReceiverReport(0, 0, 20, 30, 1000);
        Assert.Equal(38, rate.LastRttMs);
        rate.OnReceiverReport(0, 0, 20, 12, 2000);
        Assert.Equal((20, 20), (rate.LastRttMs, rate.MinRttMs));
        Assert.Equal(2000, rate.LastReportAt);
    }
}

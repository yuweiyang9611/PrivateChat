using PrivateChat.Agent.Session;
using PrivateChat.Contracts.Remote;

namespace PrivateChat.Api.Tests;

/// <summary>
/// 自动档的分辨率：网络持续慢降一级、持续够快升回去（门槛按像素数折算，画面不动时不算慢）；电脑持续忙不过来降一级、一分钟内不升回去，
/// 往上升之前先估一下升上去忙不忙得过来。时间都是传进去的毫秒数。
/// </summary>
public class AutoResolutionTests
{
    /// <summary>30 帧：一帧 33.3 ms，八成五是 28.3 ms，六成是 20 ms。</summary>
    private const double Interval = 1000.0 / 30;

    private static AutoResolution New(int width, int height, VideoSettings? settings = null)
    {
        var auto = new AutoResolution();
        auto.Reset(width, height, settings ?? VideoSettings.Auto);
        return auto;
    }

    /// <summary>
    /// 只报目标码率、不量每帧用时：从 from 到 to（含）每 100 ms 报一次。moving：画面在不在动（不动时不算网络慢）。返回期间的日志。
    /// </summary>
    private static List<string> Network(AutoResolution auto, int kbps, long from, long to, bool moving = true)
    {
        var logs = new List<string>();
        for (var t = from; t <= to; t += 100)
            if (auto.OnNetwork(kbps, moving, Interval, t) is { } log) logs.Add(log);
        return logs;
    }

    /// <summary>
    /// 画面在动：从 from 到 to（含）每 every ms（默认 50）编一帧、每帧用 costMs；给了 kbps 就同时报目标码率。
    /// interval：按现在用的帧率一帧有多少毫秒（默认 30 帧）。返回期间的日志。
    /// </summary>
    private static List<string> Moving(AutoResolution auto, double costMs, long from, long to, int? kbps = null, double interval = Interval, int every = 50) =>
        Moving(auto, _ => costMs, from, to, kbps, interval, every);

    /// <summary>同上，每帧用多久看现在编的分辨率（cost 拿到的是现在的宽高）。</summary>
    private static List<string> Moving(AutoResolution auto, Func<(int Width, int Height), double> cost, long from, long to, int? kbps = null,
        double interval = Interval, int every = 50)
    {
        var logs = new List<string>();
        for (var t = from; t <= to; t += every)
        {
            if (auto.OnFrameCost(cost(auto.Size), interval, t) is { } busy) logs.Add(busy);
            if (kbps is { } k && auto.OnNetwork(k, true, interval, t) is { } net) logs.Add(net);
        }
        return logs;
    }

    /// <summary>
    /// 截屏慢的电脑（每一级每帧都是 costMs，默认 100 ms），1920×1200：忙了 3 秒多降到 1280×800；那里量到的帧很稀（每 600 ms 一帧，
    /// 算不上一直在动，下不了结论），网络又慢：5 秒后因为网络降到 960×600，那次"降了有没有用"就不看了。返回这期间的日志。
    /// </summary>
    private static List<string> CpuStepThenSlowNetwork(AutoResolution auto, double costMs = 100)
    {
        var logs = Moving(auto, costMs, 0, 3100);
        for (var t = 3200L; t <= 8200; t += 100)
        {
            if (t % 600 == 0 && auto.OnFrameCost(costMs, Interval, t) is { } cpu) logs.Add(cpu);
            if (auto.OnNetwork(300, true, Interval, t) is { } net) logs.Add(net);
        }
        return logs;
    }

    [Fact]
    public void Steps_down_after_five_seconds_of_a_slow_network_not_before()
    {
        // 1920×1200 的屏幕：1920×1200、1280×800、960×600 三级；第 0 级的像素是 1280×800 的 2.25 倍，低于 900 kbps 算慢
        var auto = New(1920, 1200);
        Assert.Equal(((1920, 1200), 3), (auto.Size, auto.Rungs));
        Assert.Empty(Network(auto, 850, 0, 4900));
        Assert.Equal((1920, 1200), auto.Size);
        Assert.Equal(["自动画质：网络慢（目标码率 850 kbps），分辨率降到 1280x800"], Network(auto, 850, 5000, 5000));
        Assert.Equal((1, AutoResolutionReason.Network, RemoteStatsFlags.LoweredForNetwork), (auto.Rung, auto.Reason, auto.Flags));

        // 再降一级要重新数 5 秒，门槛是这一级的 400 kbps
        Assert.Empty(Network(auto, 300, 5100, 10_000));
        Assert.Equal(["自动画质：网络慢（目标码率 300 kbps），分辨率降到 960x600"], Network(auto, 300, 10_100, 10_100));
        // 最小的一级了：再慢也不降
        Assert.Empty(Network(auto, 100, 10_200, 40_000));
        Assert.Equal((960, 600), auto.Size);
    }

    [Fact]
    public void The_slow_timer_starts_over_when_the_network_recovers_for_a_moment()
    {
        var auto = New(1920, 1200);
        Assert.Empty(Network(auto, 850, 0, 4000));
        Assert.Empty(Network(auto, 1000, 4100, 4100));
        Assert.Empty(Network(auto, 850, 4200, 9100));
        Assert.Single(Network(auto, 850, 9200, 9200));
    }

    [Fact]
    public void A_still_screen_is_not_a_slow_network()
    {
        // 1920×1080 的第 0 级低于 810 kbps 算慢，刚连上时目标码率还停在起步的 600：画面不动时码率没理由往上涨，不算网络慢
        var auto = New(1920, 1080);
        Assert.Empty(Network(auto, 600, 0, 60_000, moving: false));
        Assert.Equal((0, RemoteStatsFlags.None), (auto.Rung, auto.Flags));
        // 动了 4 秒、停了一下：从头数
        Assert.Empty(Network(auto, 600, 60_100, 64_100));
        Assert.Empty(Network(auto, 600, 64_200, 64_200, moving: false));
        Assert.Empty(Network(auto, 600, 64_300, 69_200));
        Assert.Equal(["自动画质：网络慢（目标码率 600 kbps），分辨率降到 1280x720"], Network(auto, 600, 69_300, 69_300));
    }

    [Fact]
    public void Steps_back_up_after_ten_seconds_of_enough_bitrate()
    {
        var auto = New(1920, 1200);
        Network(auto, 850, 0, 5000);
        Assert.Equal((1280, 800), auto.Size);
        // 回到 1920×1200 要高于 900 × 2.25 = 2025 kbps：2000 等多久都不升。升不管画面动不动
        Assert.Empty(Network(auto, 2000, 5100, 30_000, moving: false));
        Assert.Empty(Network(auto, 2100, 30_100, 40_000, moving: false));
        Assert.Equal(["自动画质：分辨率回到 1920x1200"], Network(auto, 2100, 40_100, 40_100, moving: false));
        Assert.Equal((0, AutoResolutionReason.None, RemoteStatsFlags.None), (auto.Rung, auto.Reason, auto.Flags));
    }

    [Fact]
    public void Thresholds_scale_with_the_number_of_pixels()
    {
        // 1280×720 是 1280×800 的九成：低于 360 kbps 才降（1920×1200 上 850 就降了），从 960×540 回来要高于 810 kbps
        var auto = New(1280, 720);
        Assert.Empty(Network(auto, 380, 0, 20_000));
        Assert.Single(Network(auto, 350, 20_100, 25_100));
        Assert.Equal((960, 540), auto.Size);
        Assert.Empty(Network(auto, 800, 25_200, 50_000));
        Assert.Single(Network(auto, 820, 50_100, 60_100));
        Assert.Equal((1280, 720), auto.Size);
    }

    [Fact]
    public void Sizes_that_are_not_smaller_than_the_one_above_are_dropped()
    {
        // 1280×720 缩到 1280×800 以内还是 1280×720：只剩两级
        var small = New(1280, 720);
        Assert.Equal(((1280, 720), 2), (small.Size, small.Rungs));
        Network(small, 100, 0, 5000);
        Assert.Equal((960, 540), small.Size);

        // 800×600 哪一级都放得下：只有一级，网络再慢也不降
        var tiny = New(800, 600);
        Assert.Equal(1, tiny.Rungs);
        Assert.Empty(Network(tiny, 100, 0, 30_000));
        Assert.Equal((800, 600), tiny.Size);

        var wide = New(1920, 1080);
        Assert.Equal(((1920, 1080), 3), (wide.Size, wide.Rungs));
        Network(wide, 100, 0, 5000);
        Assert.Equal((1280, 720), wide.Size);

        // 第 0 级是画质设置的框：网页发来原始分辨率（0×0）时，4K 屏从 3840×2160 开始
        var native = New(3840, 2160, VideoSettings.From(new ControlFrame.SetVideo(RemoteVideoMode.Auto, 0, 0, 30, 0)));
        Assert.Equal(((3840, 2160), 3), (native.Size, native.Rungs));
    }

    [Fact]
    public void Steps_down_when_capture_and_encode_take_most_of_a_frame_for_three_seconds()
    {
        var auto = New(1280, 720);
        // 每帧 40 ms，一帧只有 33 ms。在这一级上量到第 3 帧（100 ms）才开始看忙不忙，从那时起忙满 3 秒降
        Assert.Empty(Moving(auto, 40, 0, 3050));
        Assert.Equal(["自动画质：电脑忙不过来（采集加编码每帧 40 ms，一帧只有 33 ms），分辨率降到 960x540"], Moving(auto, 40, 3100, 3100));
        Assert.Equal((1, AutoResolutionReason.Cpu, RemoteStatsFlags.LoweredForCpu), (auto.Rung, auto.Reason, auto.Flags));
    }

    [Fact]
    public void Busy_starts_at_eighty_five_percent_of_a_frame()
    {
        // 30 帧：八成五是 28.3 ms。一直 28 ms 不算忙
        var calm = New(1280, 720);
        Assert.Empty(Moving(calm, 28, 0, 60_000));
        Assert.Equal(0, calm.Rung);
        // 一直 29 ms：量到第 3 帧起就算忙，再过 3 秒降
        var busy = New(1280, 720);
        Assert.Empty(Moving(busy, 29, 0, 3050));
        Assert.Single(Moving(busy, 29, 3100, 3100));
        Assert.Equal(1, busy.Rung);
    }

    [Fact]
    public void Busy_is_measured_against_the_frame_rate_in_use()
    {
        // 15 帧（控制端请这边少发，或者自定义了 15 帧）：一帧 66.7 ms，八成五是 56.7 ms。40 ms 在 30 帧时算忙，这里不算
        const double At15 = 1000.0 / 15;
        var calm = New(1280, 720);
        Assert.Empty(Moving(calm, 40, 0, 60_000, interval: At15));
        Assert.Equal(0, calm.Rung);
        var busy = New(1280, 720);
        Assert.Empty(Moving(busy, 60, 0, 3050, interval: At15));
        Assert.Equal(["自动画质：电脑忙不过来（采集加编码每帧 60 ms，一帧只有 67 ms），分辨率降到 960x540"], Moving(busy, 60, 3100, 3100, interval: At15));
    }

    [Fact]
    public void The_busy_timer_starts_over_when_frames_get_cheap_again()
    {
        var auto = New(1280, 720);
        // 每帧 60 ms 忙了 2 秒，轻了一阵：平均值降回门槛以下，从头数
        Assert.Empty(Moving(auto, 60, 0, 2000));
        Assert.Empty(Moving(auto, 5, 2050, 2500));
        Assert.Empty(Moving(auto, 40, 2550, 5500));
        // 再忙满 3 秒才降；"降之前每帧多久"也只算这一段（40 ms，前面那 2 秒的 60 ms 不算）
        Assert.Equal(["自动画质：电脑忙不过来（采集加编码每帧 40 ms，一帧只有 33 ms），分辨率降到 960x540"], Moving(auto, 40, 5550, 6500));
    }

    [Fact]
    public void A_pause_in_the_motion_starts_the_busy_timer_over()
    {
        // 拖了 1.5 秒窗口（每帧 40 ms），停了 20 秒，再动：不是"一直忙了 21 秒"，从再动起重新数满 3 秒
        var auto = New(1280, 720);
        Assert.Empty(Moving(auto, 40, 0, 1500));
        Assert.Null(auto.OnFrameCost(40, Interval, 21_500));
        Assert.Equal(0, auto.Rung);
        Assert.Empty(Moving(auto, 40, 21_550, 24_450));
        Assert.Single(Moving(auto, 40, 24_500, 24_500));

        // 一直在动时量的帧前后最多隔 300 ms：隔这么久不算停过
        var steady = New(1280, 720);
        Assert.Empty(Moving(steady, 40, 0, 1500));
        Assert.Empty(Moving(steady, 40, 1800, 3050));
        Assert.Single(Moving(steady, 40, 3100, 3100));
    }

    [Fact]
    public void A_second_cpu_step_needs_its_own_three_seconds()
    {
        // 1920×1200 上每帧 100 ms：3 秒多降到 1280×800；那里每帧 44 ms（像素少了一半多，用时跟着少：降得有用），
        // 还是忙，但要在那一级上重新量够 3 帧、再数满 3 秒才降到 960×600
        var auto = New(1920, 1200);
        Assert.Empty(Moving(auto, 100, 0, 3050));
        Assert.Single(Moving(auto, 100, 3100, 3100));
        Assert.Equal((1280, 800), auto.Size);
        Assert.Empty(Moving(auto, 44, 3150, 6200));
        Assert.Equal(["自动画质：电脑忙不过来（采集加编码每帧 44 ms，一帧只有 33 ms），分辨率降到 960x600"], Moving(auto, 44, 6250, 6250));
        Assert.Equal(((960, 600), AutoResolutionReason.Cpu), (auto.Size, auto.Reason));
    }

    [Fact]
    public void A_cpu_step_that_helps_stays()
    {
        // 1280×720 每帧 40 ms，3 秒多降到 960×540；那里每帧 20 ms（快了一半）：降得有用，留在 960×540
        var auto = New(1280, 720);
        Assert.Single(Moving(auto, 40, 0, 3100));
        Assert.Empty(Moving(auto, 20, 3150, 60_000));
        Assert.Equal(((960, 540), AutoResolutionReason.Cpu, RemoteStatsFlags.LoweredForCpu), (auto.Size, auto.Reason, auto.Flags));
    }

    [Fact]
    public void A_cpu_step_that_does_not_help_is_undone_and_not_tried_again()
    {
        // 慢在截屏上：1280×720 和 960×540 每帧都是 40 ms。3 秒多降，在 960×540 上跳过换级后的头两帧（3150、3200），
        // 从 3250 起量够 2 秒没快 → 退回 1280×720
        var auto = New(1280, 720);
        Assert.Single(Moving(auto, 40, 0, 3100));
        Assert.Empty(Moving(auto, 40, 3150, 5200));
        Assert.Equal(["自动画质：降了分辨率也没快多少（采集加编码每帧 40 → 40 ms，多半慢在截屏上），回到 1280x720"], Moving(auto, 40, 5250, 5250));
        Assert.Equal(((1280, 720), AutoResolutionReason.None, RemoteStatsFlags.None), (auto.Size, auto.Reason, auto.Flags));
        // 之后再忙多久也不因为电脑忙降了
        Assert.Empty(Moving(auto, 40, 5300, 70_000));
        Assert.Equal(0, auto.Rung);
    }

    [Fact]
    public void Cpu_steps_stay_off_until_reset_whatever_the_network_does()
    {
        // 截屏慢：1280×720 和 960×540 每帧都是 40 ms。降了、没用、退回来
        var auto = New(1280, 720);
        Assert.Equal(2, Moving(auto, 40, 0, 5250).Count);
        Assert.Equal(0, auto.Rung);
        // 网络慢降了一级、又好了升回 1280×720：之后画面一直在动、每帧 40 ms，也不再因为电脑忙降
        Assert.Single(Network(auto, 300, 5300, 10_300));
        Assert.Single(Network(auto, 5000, 10_400, 20_400, moving: false));
        Assert.Equal(0, auto.Rung);
        Assert.Empty(Moving(auto, 40, 20_450, 45_000));
        // 网络慢降了一级、换了线路退回来：也一样
        Assert.Single(Network(auto, 300, 45_100, 50_100));
        Assert.Equal("自动画质：分辨率回到 1280x720", auto.OnNetworkChanged(Interval, 50_200));
        Assert.Empty(Moving(auto, 40, 50_250, 75_000));
        Assert.Equal((0, RemoteStatsFlags.None), (auto.Rung, auto.Flags));
    }

    [Fact]
    public void Reset_lets_the_cpu_rule_try_again()
    {
        var auto = New(1280, 720);
        // 降了、没用、退回来
        Assert.Equal(2, Moving(auto, 40, 0, 5250).Count);
        Assert.Equal(0, auto.Rung);
        // 换了画质设置或屏幕：重新来，忙满 3 秒照样降
        auto.Reset(1280, 720, VideoSettings.Auto);
        Assert.Empty(Moving(auto, 40, 5300, 8350));
        Assert.Single(Moving(auto, 40, 8400, 8400));
        Assert.Equal((1, AutoResolutionReason.Cpu), (auto.Rung, auto.Reason));

        // 正在看降得有没有用时换了屏幕：不看了，新屏幕上从头数
        var mid = New(1280, 720);
        Assert.Single(Moving(mid, 40, 0, 3100));
        mid.Reset(1920, 1080, VideoSettings.Auto);
        Assert.Empty(Moving(mid, 40, 3150, 6200));
        Assert.Equal(["自动画质：电脑忙不过来（采集加编码每帧 40 ms，一帧只有 33 ms），分辨率降到 1280x720"], Moving(mid, 40, 6250, 6250));
    }

    [Fact]
    public void The_verdict_waits_for_frames_while_the_screen_is_still()
    {
        var auto = New(1280, 720);
        Assert.Single(Moving(auto, 40, 0, 3100));
        // 降完画面就停了：没有帧可量，一直等（网络够快也不升：还在一分钟里）
        Assert.Empty(Network(auto, 5000, 3200, 30_000, moving: false));
        Assert.Equal(1, auto.Rung);
        // 动了 1 秒、停了 20 秒、再动：停的那段不算，接着数，在动的时间加起来够 2 秒才下结论
        // （换级后的头两帧 30 050、30 100 不算：从 30 150 起，前一段量到 900 ms，后一段要到 52 150）
        Assert.Empty(Moving(auto, 40, 30_050, 31_050));
        Assert.Empty(Moving(auto, 40, 51_050, 52_100));
        Assert.Single(Moving(auto, 40, 52_150, 52_150));
        Assert.Equal(0, auto.Rung);
    }

    [Fact]
    public void The_verdict_needs_at_least_ten_frames()
    {
        // 降了之后每 300 ms 才量到一帧（还算一直在动：前后隔不到半秒）。换级后的头两帧不算，从 4000 那一帧起量：
        // 2 秒只有 7–8 帧，要等到第 10 帧（6700）
        var auto = New(1280, 720);
        Assert.Single(Moving(auto, 40, 0, 3100));
        for (var t = 3400L; t <= 6400; t += 300)
            Assert.Null(auto.OnFrameCost(40, Interval, t));
        Assert.Equal(1, auto.Rung);
        Assert.NotNull(auto.OnFrameCost(40, Interval, 6700));
        Assert.Equal(0, auto.Rung);
    }

    [Fact]
    public void No_second_cpu_step_before_the_first_one_is_judged()
    {
        // 1920×1200 上每帧 100 ms，3 秒多降到 1280×800。那里每 500 ms 才量到一帧（隔半秒还算一直在动），每帧还是 100 ms：
        // 量到第 3 帧起算忙，到第 9 帧正好忙满 3 秒，也先不降到 960×600；结论也从第 3 帧起量，等量够 10 帧（第 12 帧）
        // 才下——没用，退回 1920×1200
        var auto = New(1920, 1200);
        Assert.Single(Moving(auto, 100, 0, 3100));
        for (var t = 3600L; t <= 8600; t += 500)
            Assert.Null(auto.OnFrameCost(100, Interval, t));
        Assert.Equal((1280, 800), auto.Size);
        Assert.Equal("自动画质：降了分辨率也没快多少（采集加编码每帧 100 → 100 ms，多半慢在截屏上），回到 1920x1200", auto.OnFrameCost(100, Interval, 9100));
        Assert.Equal(((1920, 1200), RemoteStatsFlags.None), (auto.Size, auto.Flags));
    }

    [Fact]
    public void A_network_step_while_judging_drops_the_verdict()
    {
        var auto = New(1920, 1200);
        Assert.Equal(
            ["自动画质：电脑忙不过来（采集加编码每帧 100 ms，一帧只有 33 ms），分辨率降到 1280x800", "自动画质：网络慢（目标码率 300 kbps），分辨率降到 960x600"],
            CpuStepThenSlowNetwork(auto));
        // 之后画面一直在动、每帧还是 100 ms：不会再说"降了也没快多少"退回去
        Assert.Empty(Moving(auto, 100, 8250, 20_000));
        Assert.Equal(((960, 600), AutoResolutionReason.Network), (auto.Size, auto.Reason));
    }

    [Fact]
    public void A_dropped_verdict_does_not_keep_it_at_a_small_size()
    {
        var auto = New(1920, 1200);
        Assert.Equal(2, CpuStepThenSlowNetwork(auto).Count);
        // 网络好了、画面一直在动：那次没下结论的降不留"一分钟不升""升之前先估"（按像素估是 100 × 1.78 = 178 ms，永远升不上去），
        // 照常往回升。截屏慢，到了 1280×800 还是忙：可以再试一次降，这回量够了、没用，退回来不再试；最后回到 1920×1200
        var logs = Moving(auto, 100, 8250, 60_000, kbps: 5000);
        Assert.Equal("自动画质：分辨率回到 1920x1200", logs[^1]);
        Assert.Equal((0, AutoResolutionReason.None, RemoteStatsFlags.None), (auto.Rung, auto.Reason, auto.Flags));
    }

    [Fact]
    public void A_dropped_verdict_does_not_leave_its_size_marked_as_lowered_for_the_computer()
    {
        // 截屏慢：每一级每帧都是 40 ms。电脑忙降到 1280×800，还没下结论就因为网络降到 960×600
        var auto = New(1920, 1200);
        Assert.Equal(
            ["自动画质：电脑忙不过来（采集加编码每帧 40 ms，一帧只有 33 ms），分辨率降到 1280x800", "自动画质：网络慢（目标码率 300 kbps），分辨率降到 960x600"],
            CpuStepThenSlowNetwork(auto, 40));
        // 那次降不算了，1280×800 那一级也改记成网络慢降的（不然工具栏一直标着"电脑忙不过来，已降低分辨率"）。
        // 换了线路：两级都是网络慢降的，一路退回 1920×1200，不停在 1280×800
        Assert.Equal(RemoteStatsFlags.LoweredForNetwork, auto.Flags);
        Assert.Equal("自动画质：分辨率回到 1920x1200", auto.OnNetworkChanged(Interval, 8250));
        Assert.Equal((0, AutoResolutionReason.None, RemoteStatsFlags.None), (auto.Rung, auto.Reason, auto.Flags));
        // 之后画面一直在动，1500 kbps（不够从 1280×800 升回 1920×1200 的 2025）：1920×1200 上还是忙，再试一次降，量够了、没用，
        // 退回来不再试。最后不带"因为电脑忙降了"
        Assert.Equal(
            [
                "自动画质：电脑忙不过来（采集加编码每帧 40 ms，一帧只有 33 ms），分辨率降到 1280x800",
                "自动画质：降了分辨率也没快多少（采集加编码每帧 40 → 40 ms，多半慢在截屏上），回到 1920x1200",
            ],
            Moving(auto, 40, 8300, 70_000, kbps: 1500));
        Assert.Equal((0, RemoteStatsFlags.None), (auto.Rung, auto.Flags));
    }

    [Fact]
    public void Going_back_up_before_the_verdict_leaves_nothing_behind()
    {
        // 截屏慢：每一级每帧都是 40 ms。网络慢降到 1280×800；那里电脑忙，又降到 960×600，降完画面就停了、下不了结论
        var auto = New(1920, 1200);
        Network(auto, 850, 0, 5000);
        Assert.Single(Moving(auto, 40, 5050, 8150));
        // 一分钟过了、最近 10 秒也没量到，网络够快：先升回 1280×800，那次没下结论的降不算了
        Assert.Equal(["自动画质：分辨率回到 1280x800"], Network(auto, 2100, 8200, 68_200, moving: false));
        // 画面又动了：1280×800 上还是忙，再试一次降，量够了、没用，退回来不再试；之后照常升回 1920×1200
        // （没下结论的那次不留"升之前先估"：按像素估是 40 × 2.25 = 90 ms，永远升不上去）
        var logs = Moving(auto, 40, 68_250, 100_000, kbps: 5000);
        Assert.Equal("自动画质：分辨率回到 1920x1200", logs[^1]);
        Assert.Equal((0, RemoteStatsFlags.None), (auto.Rung, auto.Flags));
    }

    [Theory]
    [InlineData(0.84, false)]
    [InlineData(0.86, true)]
    public void A_cpu_step_has_to_save_fifteen_percent(double share, bool undone)
    {
        var auto = New(1280, 720);
        Assert.Single(Moving(auto, 40, 0, 3100));
        // 降之前每帧 40 ms：降到 34 ms 以内算有用
        Assert.Equal(undone ? 1 : 0, Moving(auto, 40 * share, 3150, 10_000).Count);
        Assert.Equal(undone ? 0 : 1, auto.Rung);
    }

    [Fact]
    public void The_verdict_is_the_average_of_what_was_measured_down_there()
    {
        // 降到 960×540 之后（换级后的头两帧不算，从 3250 起量）：头 1 秒每帧 20 ms、后 1 秒 46 ms，平均 32.7 ms，
        // 比降之前的 40 ms 快了 15 % 以上——算有用（不看指数平均：它这时已经贴着 46 了）
        var auto = New(1280, 720);
        Assert.Single(Moving(auto, 40, 0, 3100));
        Assert.Empty(Moving(auto, 20, 3150, 4250));
        Assert.Empty(Moving(auto, 46, 4300, 10_000));
        Assert.Equal((1, AutoResolutionReason.Cpu), (auto.Rung, auto.Reason));
    }

    [Fact]
    public void The_cost_before_a_step_is_the_average_of_the_busy_seconds()
    {
        // 1280×720 上一直每帧 40 ms，忙满 3 秒的最后 3 帧卡了一下（80 ms）：降之前每帧多久按忙的这 3 秒的平均算（42 ms），
        // 不按那一刻的指数平均（60 ms）
        var auto = New(1280, 720);
        Assert.Empty(Moving(auto, 40, 0, 2950));
        Assert.Equal(["自动画质：电脑忙不过来（采集加编码每帧 42 ms，一帧只有 33 ms），分辨率降到 960x540"], Moving(auto, 80, 3000, 3100));
        // 960×540 上每帧 37 ms：只快了 12 %，退回去（按 60 ms 算就成了快了 38 %，会留在 960×540）
        Assert.Equal(["自动画质：降了分辨率也没快多少（采集加编码每帧 42 → 37 ms，多半慢在截屏上），回到 1280x720"], Moving(auto, 37, 3150, 6000));
    }

    [Fact]
    public void The_cost_before_a_step_is_measured_at_that_size()
    {
        // 1920×1200 的屏幕，截屏占了大头：1920×1200 每帧 30 ms，1280×800 每帧 27 ms，降一级只快 10 %
        double Cost((int Width, int Height) size) => size.Width == 1920 ? 30 : 27;
        var auto = New(1920, 1200);
        // 网络慢降到 1280×800，那里画面一直在动（27 ms，不算忙）；网络好了，画面停着时回到 1920×1200。
        // 每帧用时的平均值按像素折算成了 27 × 2.25 = 60.75 ms
        Network(auto, 850, 0, 5000);
        Assert.Empty(Moving(auto, Cost, 5050, 8000));
        Assert.Single(Network(auto, 2100, 8100, 18_100, moving: false));
        // 打字：每 290 ms 量到一帧（还算一直在动）。忙不忙、降之前每帧多久都只看在 1920×1200 上真量到的 30 ms，
        // 不看折算过来的 60.75（打字时帧稀，平均值 3 秒里降不下来：按它算降之前是 32 ms，27 ms 就成了快了 15 % 以上）
        Assert.Equal(
            [
                "自动画质：电脑忙不过来（采集加编码每帧 30 ms，一帧只有 33 ms），分辨率降到 1280x800",
                "自动画质：降了分辨率也没快多少（采集加编码每帧 30 → 27 ms，多半慢在截屏上），回到 1920x1200",
            ],
            Moving(auto, Cost, 18_390, 48_000, kbps: 5000, every: 290));
        // 退回来以后，估往上升用的平均值也放回在 1920×1200 上量到的
        Assert.Equal(((1920, 1200), RemoteStatsFlags.None, 30.0), (auto.Size, auto.Flags, auto.CostMs));
    }

    [Theory]
    [InlineData(400)]
    [InlineData(450)]
    public void A_guess_from_a_smaller_size_does_not_make_it_busy(int every)
    {
        // 和上面一样网络慢降到 1280×800（那里每帧 24 ms）又回到 1920×1200：折算过来的平均值是 24 × 2.25 = 54 ms
        var auto = New(1920, 1200);
        Network(auto, 850, 0, 5000);
        Assert.Empty(Moving(auto, 24, 5050, 8000));
        Assert.Single(Network(auto, 2100, 8100, 18_100, moving: false));
        // 1920×1200 上其实每帧 25 ms（一帧的八成五是 28.3 ms，不忙）。打字时每 400–450 ms 量到一帧：
        // 折算过来的平均值要量十来帧才降到门槛以下，超过 3 秒——不能因此降
        Assert.Empty(Moving(auto, 25, 18_100 + every, 60_000, kbps: 5000, every: every));
        Assert.Equal((0, RemoteStatsFlags.None), (auto.Rung, auto.Flags));
    }

    [Fact]
    public void Each_size_is_measured_from_its_own_frames()
    {
        // 网络慢降到 1280×800；那里画面重了 2 秒（每帧 80 ms，忙，但不到 3 秒），之后停了。网络好了，回到 1920×1200
        var auto = New(1920, 1200);
        Network(auto, 850, 0, 5000);
        Assert.Empty(Moving(auto, 80, 5050, 7050));
        Assert.Single(Network(auto, 2100, 7100, 17_100, moving: false));
        // 1920×1200 上打字，每 450 ms 量到一帧、每帧 25 ms（不忙）：1280×800 上量到的 80 ms 不算这一级的（折不折算都不算），从头量，不降
        Assert.Empty(Moving(auto, 25, 17_550, 60_000, kbps: 5000, every: 450));
        Assert.Equal(0, auto.Rung);

        // 换了屏幕（或者画质设置）也一样：之前那块屏上量到的不算
        var other = New(1280, 720);
        Assert.Empty(Moving(other, 80, 0, 2000));
        other.Reset(1920, 1080, VideoSettings.Auto);
        Assert.Empty(Moving(other, 25, 2450, 60_000, every: 450));
        Assert.Equal(0, other.Rung);
    }

    [Fact]
    public void The_keyframe_after_a_size_change_does_not_make_an_idle_computer_busy()
    {
        // 1920×1200 的屏幕，打字：每 290 ms 量到一帧。平常每帧 1920×1200 上 28 ms、1280×800 上 27.5 ms、960×600 上 16 ms，
        // 都不到一帧的八成五（28.3 ms）；换了分辨率之后编码器重建，头一帧是关键帧，要平常的两倍
        double Steady((int Width, int Height) size) => size.Width switch { 1920 => 28, 1280 => 27.5, _ => 16 };
        var auto = New(1920, 1200);
        var logs = new List<string>();
        var last = auto.Size;
        void Typing(long from, long to, int kbps)
        {
            for (var t = from; t <= to; t += 290)
            {
                var cost = auto.Size == last ? Steady(auto.Size) : 2 * Steady(auto.Size);
                last = auto.Size;
                if (auto.OnFrameCost(cost, Interval, t) is { } cpu) logs.Add(cpu);
                if (auto.OnNetwork(kbps, true, Interval, t) is { } net) logs.Add(net);
            }
        }
        // 网络慢（850 kbps，画面在动）：5 秒多降到 1280×800。那里头一帧 55 ms，从它起算的平均值要 16 帧（打字时 4 秒多）
        // 才退到门槛以下——算进去就成了忙满 3 秒，再降到 960×600
        Typing(0, 39_900, 850);
        Assert.Equal(["自动画质：网络慢（目标码率 850 kbps），分辨率降到 1280x800"], logs);
        Assert.Equal(((1280, 800), AutoResolutionReason.Network), (auto.Size, auto.Reason));
        // 网络好了：10 秒后回到 1920×1200，那里的关键帧也不算成忙
        logs.Clear();
        Typing(40_000, 120_000, 5000);
        Assert.Equal(["自动画质：分辨率回到 1920x1200"], logs);
        Assert.Equal((0, RemoteStatsFlags.None), (auto.Rung, auto.Flags));
    }

    [Fact]
    public void An_undone_step_does_not_hold_back_the_network_rule()
    {
        // 网络慢降到 1280×800；那里每帧 40 ms（慢在截屏），又因为电脑忙降到 960×600，没用、退回 1280×800（还是网络慢降的那一级）
        var auto = New(1920, 1200);
        Network(auto, 850, 0, 5000);
        Assert.Single(Moving(auto, 40, 5100, 8200));
        Assert.Equal((960, 600), auto.Size);
        Assert.Empty(Moving(auto, 40, 8250, 10_300));
        Assert.Equal(["自动画质：降了分辨率也没快多少（采集加编码每帧 40 → 40 ms，多半慢在截屏上），回到 1280x800"], Moving(auto, 40, 10_350, 10_350));
        Assert.Equal(((1280, 800), AutoResolutionReason.Network, RemoteStatsFlags.LoweredForNetwork), (auto.Size, auto.Reason, auto.Flags));
        // 网络好了：照常 10 秒后回到 1920×1200。退回去的那次不留"一分钟不升""升之前先估"（按像素估本来就不准）
        Assert.Empty(Network(auto, 2100, 10_400, 20_300, moving: false));
        Assert.Equal(["自动画质：分辨率回到 1920x1200"], Network(auto, 2100, 20_400, 20_400, moving: false));
    }

    [Fact]
    public void An_undone_step_puts_back_what_an_earlier_step_set()
    {
        // 1920×1200 每帧 100 ms、1280×800 50 ms、960×600 48 ms：第一次降有用，第二次没快多少、退回 1280×800
        double Cost((int Width, int Height) size) => size.Width switch { 1920 => 100, 1280 => 50, _ => 48 };
        var auto = New(1920, 1200);
        var logs = Moving(auto, Cost, 0, 9000);
        Assert.Equal(3, logs.Count);
        Assert.Equal("自动画质：降了分辨率也没快多少（采集加编码每帧 50 → 48 ms，多半慢在截屏上），回到 1280x800", logs[2]);
        Assert.Equal(((1280, 800), AutoResolutionReason.Cpu, RemoteStatsFlags.LoweredForCpu), (auto.Size, auto.Reason, auto.Flags));
        // 网络够快、画面一直在动：第一次降留下的"一分钟不升"、之后的"升之前先估"（50 × 2.25 = 112 ms，远超一帧的六成）照样作数，
        // 留在 1280×800（1920×1200 上每帧 100 ms，升上去只有 10 帧）
        Assert.Empty(Moving(auto, Cost, 9050, 120_000, kbps: 5000));
        Assert.Equal(((1280, 800), RemoteStatsFlags.LoweredForCpu), (auto.Size, auto.Flags));
    }

    [Fact]
    public void Waits_a_minute_after_a_cpu_step_down_before_going_back_up()
    {
        var auto = New(1280, 720);
        Moving(auto, 40, 0, 3100);
        Assert.Equal((960, 540), auto.Size);
        // 降下来之后很轻松（960×540 每帧 8 ms，折算到 1280×720 是 14 ms，在一帧的六成以内）、网络也够快：一分钟之内照样不升
        Assert.Empty(Moving(auto, 8, 3150, 63_050, kbps: 5000));
        Assert.Equal(["自动画质：分辨率回到 1280x720"], Moving(auto, 8, 63_100, 63_100, kbps: 5000));
        Assert.Equal((0, RemoteStatsFlags.None), (auto.Rung, auto.Flags));
    }

    [Fact]
    public void Going_back_up_needs_the_bigger_size_to_fit_comfortably()
    {
        var auto = New(1280, 720);
        Moving(auto, 40, 0, 3100);
        // 960×540 每帧 15 ms 不算忙，但折算到 1280×720 是 26.7 ms，超过一帧的六成（20 ms）：升上去又会忙不过来，不升
        Assert.Empty(Moving(auto, 15, 3150, 120_000, kbps: 5000));
        Assert.Equal((960, 540), auto.Size);
        // 轻了（10 ms，折算 17.8 ms）就升
        Assert.Equal(["自动画质：分辨率回到 1280x720"], Moving(auto, 10, 120_050, 125_000, kbps: 5000));
    }

    [Fact]
    public void Frame_costs_from_a_few_seconds_ago_still_count_before_going_back_up()
    {
        var auto = New(1280, 720);
        Moving(auto, 40, 0, 3100);
        // 960×540 每帧 15 ms（折算到 1280×720 是 26.7 ms，超过一帧的六成），之后画面不动了
        Assert.Empty(Moving(auto, 15, 3150, 60_000, kbps: 5000));
        // 一分钟过了、网络也够快，但 9 秒前量到的还作数：不升
        Assert.Empty(Network(auto, 5000, 60_100, 69_000, moving: false));
        Assert.Equal((960, 540), auto.Size);
        // 超过 10 秒没量到才先升回去
        Assert.Empty(Network(auto, 5000, 69_100, 70_000, moving: false));
        Assert.Equal(["自动画质：分辨率回到 1280x720"], Network(auto, 5000, 70_100, 70_100, moving: false));
    }

    [Fact]
    public void A_still_screen_lets_it_try_the_bigger_size_again()
    {
        var auto = New(1280, 720);
        // 最后量到的那几帧很重（折算到 1280×720 也还是 40 ms），之后画面不动、没有帧可量
        Moving(auto, 40, 0, 3100);
        Assert.Empty(Network(auto, 5000, 3200, 63_000, moving: false));
        // 最近 10 秒没量到：一分钟后先升回去，真忙不过来会再降
        Assert.Equal(["自动画质：分辨率回到 1280x720"], Network(auto, 5000, 63_100, 63_100, moving: false));
    }

    [Fact]
    public void Changing_lines_undoes_what_the_old_line_was_too_slow_for()
    {
        var auto = New(1920, 1200);
        Network(auto, 850, 0, 5000);
        Assert.Equal(1, auto.Rung);
        Assert.Equal("自动画质：分辨率回到 1920x1200", auto.OnNetworkChanged(Interval, 5100));
        Assert.Equal((0, AutoResolutionReason.None), (auto.Rung, auto.Reason));
        // 计时也从头算
        Assert.Empty(Network(auto, 850, 5200, 10_100));
        Assert.Single(Network(auto, 850, 10_200, 10_200));
    }

    [Fact]
    public void Changing_lines_keeps_what_the_computer_could_not_handle()
    {
        var auto = New(1920, 1200);
        Moving(auto, 40, 0, 3100);
        Assert.Equal((1, AutoResolutionReason.Cpu), (auto.Rung, auto.Reason));
        Assert.Null(auto.OnNetworkChanged(Interval, 3120));
        Assert.Equal(1, auto.Rung);

        // 电脑忙降了一级（有用）、网络慢又降了一级：两样都报
        Moving(auto, 10, 3150, 8150, kbps: 300);
        Assert.Equal(((960, 600), AutoResolutionReason.Network), (auto.Size, auto.Reason));
        Assert.Equal(RemoteStatsFlags.LoweredForCpu | RemoteStatsFlags.LoweredForNetwork, auto.Flags);
        // 换了线路：网络慢降的那一级退回去（还在电脑忙之后的一分钟里，但那一级电脑本来就忙得过来），电脑忙降的不动
        Assert.Equal("自动画质：分辨率回到 1280x800", auto.OnNetworkChanged(Interval, 8200));
        Assert.Equal((1, AutoResolutionReason.Cpu, RemoteStatsFlags.LoweredForCpu), (auto.Rung, auto.Reason, auto.Flags));
    }

    [Fact]
    public void Right_after_a_step_the_estimate_for_going_back_is_what_was_measured_up_there()
    {
        var auto = New(1920, 1200);
        Moving(auto, 40, 0, 3100);
        // 1280×800 上每帧 15 ms，网络慢又降到 960×600；还没量到 960×600 的帧就换了线路
        Moving(auto, 15, 3150, 8150, kbps: 300);
        Assert.Equal((960, 600), auto.Size);
        // 降的时候平均值按像素数折算过：估回 1280×800 就是刚才在那里量到的 15 ms，在一帧的六成以内，退回去
        Assert.Equal("自动画质：分辨率回到 1280x800", auto.OnNetworkChanged(Interval, 8200));
    }

    [Fact]
    public void Changing_lines_does_not_go_back_up_to_a_size_the_computer_cannot_handle()
    {
        var auto = New(1920, 1200);
        Moving(auto, 40, 0, 3100);
        // 1280×800 上每帧 20 ms 不算忙；网络慢又降到 960×600
        Moving(auto, 20, 3150, 8150, kbps: 300);
        Assert.Equal(((960, 600), AutoResolutionReason.Network), (auto.Size, auto.Reason));
        // 960×600 上每帧 15 ms：折算到 1280×800 是 26.7 ms，超过一帧的六成
        Assert.Empty(Moving(auto, 15, 8200, 9100));
        // 换了线路：网络慢降的那一级本该退回去，但升上去电脑忙不过来——不动
        Assert.Null(auto.OnNetworkChanged(Interval, 9150));
        Assert.Equal(((960, 600), AutoResolutionReason.Network), (auto.Size, auto.Reason));
    }

    [Fact]
    public void Reset_starts_over_from_the_biggest_size()
    {
        var auto = New(1280, 720);
        Moving(auto, 40, 0, 3100);
        Assert.Equal(1, auto.Rung);
        auto.Reset(1920, 1080, VideoSettings.Auto);
        Assert.Equal(((1920, 1080), 0, AutoResolutionReason.None, RemoteStatsFlags.None), (auto.Size, auto.Rung, auto.Reason, auto.Flags));
        // 之前"一分钟不升""升之前先估"都不算了
        Assert.Single(Network(auto, 100, 3200, 8200));
        Assert.Single(Network(auto, 5000, 8300, 18_300));
        Assert.Equal((1920, 1080), auto.Size);
    }
}

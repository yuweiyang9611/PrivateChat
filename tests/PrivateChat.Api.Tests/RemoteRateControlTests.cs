using PrivateChat.Agent.Audio;
using PrivateChat.Agent.Session;

namespace PrivateChat.Api.Tests;

/// <summary>
/// 被控端的码率控制（NetworkEstimator）、画质规划（QualityPlanner）、响度拉平（AudioLeveler）：纯计算，源文件直接编进测试项目。
/// 线路用一个最简单的模型：固定带宽、固定单程延迟，数据先进先出；确认在画面到达后原路返回。
/// </summary>
public class RemoteRateControlTests
{
    private sealed class Link(int kbps, int oneWayMs, int decodeMs = 0)
    {
        private double _free;
        private double _decoded;
        private readonly PriorityQueue<(uint Ts, long At), long> _acks = new();
        private readonly Queue<(long PongAt, long SentAt)> _pongs = new();
        public int Kbps { get; set; } = kbps;
        public List<int> Delays { get; } = [];

        public void Send(uint ts, int bytes, long now)
        {
            _free = Math.Max(_free, now) + bytes * 8.0 / Kbps;
            var arrive = (long)_free + oneWayMs;
            Delays.Add((int)(arrive - now));
            // 控制端解码要一会儿，一帧一帧排着解；解出来才确认
            _decoded = Math.Max(_decoded, arrive) + decodeMs;
            _acks.Enqueue((ts, (long)_decoded + oneWayMs), (long)_decoded + oneWayMs);
        }

        public void Ping(long now) => _pongs.Enqueue(((long)Math.Max(_free, now) + 2 * oneWayMs, now));

        public void Deliver(NetworkEstimator net, long now)
        {
            while (_acks.TryPeek(out var ack, out var at) && at <= now)
            {
                _acks.Dequeue();
                net.OnAck(ack.Ts, ack.At);
            }
            while (_pongs.TryPeek(out var pong) && pong.PongAt <= now)
            {
                _pongs.Dequeue();
                net.OnProbe((int)(pong.PongAt - pong.SentAt), pong.PongAt);
            }
        }
    }

    /// <summary>
    /// 画面一直在变（每 50 ms 一帧）：帧的大小跟着预算走，但不小于 floorBytes（编码器把质量压到最低也有这么大）。
    /// 返回发出去的帧数。
    /// </summary>
    private static int Run(NetworkEstimator net, Link link, int fromMs, int toMs, int floorBytes, int? fixedBytes = null)
    {
        var sent = 0;
        for (long now = fromMs; now < toMs; now += 50)
        {
            link.Deliver(net, now);
            if (now % 500 == 0) link.Ping(now);
            net.Tick(now);
            if (!net.CanSend(now, 50)) continue;
            var bytes = fixedBytes ?? Math.Max(floorBytes, net.ChangeBudget(now, sustained: true, frameMs: 50));
            net.OnSent((uint)now + 1, bytes, now);
            link.Send((uint)now + 1, bytes, now);
            sent++;
        }
        return sent;
    }

    [Fact]
    public void Slow_link_brings_the_bitrate_down_and_delay_stays_bounded()
    {
        var net = new NetworkEstimator(100, 2500, 600);
        var link = new Link(300, 100);
        Run(net, link, 0, 5000, floorBytes: 3000);
        link.Delays.Clear();
        Run(net, link, 5000, 30_000, floorBytes: 3000);
        Assert.InRange(net.TargetKbps, 100, 450);
        // 稳定下来之后，一帧从发出到送达不超过一秒左右（旧逻辑在这种线路上会涨到十几秒）
        Assert.True(link.Delays.Max() < 1200, $"最大延迟 {link.Delays.Max()} ms");
        link.Delays.Sort();
        Assert.True(link.Delays[link.Delays.Count / 2] < 600, $"中位延迟 {link.Delays[link.Delays.Count / 2]} ms");
    }

    [Fact]
    public void High_latency_link_with_enough_bandwidth_keeps_full_frame_rate()
    {
        // 单程 300 ms（确认一来一回 600 ms）、带宽 4 Mbps；每帧 5 KB、每秒 20 帧 = 800 kbps。
        // 旧的"最多 4 帧没确认"在这里只有每秒 6 帧多
        var net = new NetworkEstimator(100, 2500, 1200);
        var link = new Link(4000, 300);
        Run(net, link, 0, 3000, 0, fixedBytes: 5000);
        var sent = Run(net, link, 3000, 13_000, 0, fixedBytes: 5000);
        Assert.True(sent >= 190, $"10 秒只发了 {sent} 帧");
        Assert.True(net.TargetKbps >= 1200, $"码率被错误地降到了 {net.TargetKbps}");
        Assert.InRange(net.MinRttMs, 600, 620);
    }

    [Fact]
    public void A_slow_decoder_is_not_mistaken_for_a_slow_network()
    {
        // 线路很快（单程 20 ms、5 Mbps），但控制端（比如旧手机）每帧要解 40 ms：每一帧的确认都晚 40 ms，这不是拥塞
        var net = new NetworkEstimator(100, 2500, 1200);
        var link = new Link(5000, 20, decodeMs: 40);
        Run(net, link, 0, 3000, 0, fixedBytes: 6000);
        var sent = Run(net, link, 3000, 13_000, 0, fixedBytes: 6000);
        Assert.True(net.TargetKbps >= 1200, $"码率被压到了 {net.TargetKbps}");
        Assert.True(sent >= 190, $"10 秒只发了 {sent} 帧");

        // 解码慢到跟不上帧率（每帧 80 ms，最多每秒 12 帧）：靠"没确认的帧数有上限"少发，而不是越堆越多
        net = new NetworkEstimator(100, 2500, 1200);
        link = new Link(5000, 20, decodeMs: 80);
        Run(net, link, 0, 3000, 0, fixedBytes: 6000);
        link.Delays.Clear();
        sent = Run(net, link, 3000, 13_000, 0, fixedBytes: 6000);
        Assert.InRange(sent, 100, 130);
        Assert.True(net.InFlight <= 8, $"还有 {net.InFlight} 帧没确认");
    }

    [Fact]
    public void Bitrate_climbs_back_when_the_link_gets_faster()
    {
        var net = new NetworkEstimator(100, 2500, 600);
        var link = new Link(300, 50);
        Run(net, link, 0, 10_000, floorBytes: 3000);
        var slow = net.TargetKbps;
        link.Kbps = 5000;
        Run(net, link, 10_000, 40_000, floorBytes: 3000);
        Assert.InRange(slow, 100, 450);
        Assert.True(net.TargetKbps > 1200, $"线路变快后 30 秒，码率才 {net.TargetKbps}");
    }

    [Fact]
    public void A_still_screen_does_not_raise_the_bitrate()
    {
        // 只有鼠标在动（每帧几百字节）：没有证据说明线路能传更多，不往上试
        var net = new NetworkEstimator(100, 2500, 600);
        var link = new Link(5000, 20);
        Run(net, link, 0, 20_000, 0, fixedBytes: 300);
        Assert.Equal(600, net.TargetKbps);
    }

    [Fact]
    public void Budget_is_charged_at_the_target_rate_and_cleared_when_everything_is_acknowledged()
    {
        var net = new NetworkEstimator(100, 2500, 800);
        Assert.True(net.CanSend(0, 50));
        // 800 kbps 下 350 ms 的突发额度 = 35 KB；100 ms 的补清晰额度里欠着账就没有了
        Assert.Equal(35_000, net.ChangeBudget(0, sustained: false, frameMs: 50));
        Assert.Equal(5_000, net.ChangeBudget(0, sustained: true, frameMs: 50));
        Assert.Equal(20_000, net.Credit(0));
        net.OnSent(1, 30_000, 0); // 按 800 kbps 要 300 ms 才"发完"
        Assert.False(net.CanSend(50, 50));
        Assert.Equal(0, net.Credit(50));
        Assert.True(net.CanSend(210, 50)); // 只欠 90 ms 了
        Assert.False(net.Idle(210));
        // 线路其实很快，30 ms 就确认了：路上是空的，不用再等
        net.OnAck(1, 30);
        Assert.True(net.CanSend(31, 50));
        Assert.True(net.Idle(31));
        Assert.Equal(0, net.InFlight);
    }

    [Fact]
    public void Frames_nobody_acknowledges_stop_the_sender_and_then_trigger_a_restart()
    {
        var net = new NetworkEstimator(100, 2500, 2500);
        net.OnProbe(40, 0);
        net.OnSent(1, 1000, 0);
        Assert.True(net.CanSend(300, 50));
        Assert.False(net.CanSend(1000, 50)); // 线路往返才 40 ms，这一帧一秒都没确认：先不发了
        Assert.False(net.Stalled(9000));
        Assert.True(net.Stalled(10_100)); // 十秒：当它丢了，调用方清空后从关键帧重新开始
        net.ForgetInFlight();
        Assert.True(net.CanSend(10_100, 50));
        // 换了画质档位：上限变了，目标跟着收进去
        net.SetMax(600);
        Assert.Equal(600, net.TargetKbps);
    }

    [Fact]
    public void Tiles_report_which_parts_of_the_frame_changed()
    {
        const int width = 200, height = 130; // 4 × 3 块，右边和下边的块不满 64
        Assert.Equal(12, FrameTiles.Count(width, height));
        var a = new byte[width * height * 4];
        var b = (byte[])a.Clone();
        var changed = new bool[12];
        Assert.Equal(12, FrameTiles.Diff(default, a, width, height, width * 4, changed)); // 没有上一帧：全变
        Assert.Equal(0, FrameTiles.Diff(a, b, width, height, width * 4, changed));
        b[(129 * width + 199) * 4 + 2] = 1; // 右下角最后一个像素
        b[(10 * width + 70) * 4] = 1;       // 第一行第二块
        Assert.Equal(2, FrameTiles.Diff(a, b, width, height, width * 4, changed));
        Assert.Equal([1, 11], Enumerable.Range(0, 12).Where(i => changed[i]));
    }

    private static (bool[] Mask, int Count) Area(QualityPlanner _, int tiles, int changed)
    {
        var mask = new bool[tiles];
        for (var i = 0; i < changed; i++) mask[i] = true;
        return (mask, changed);
    }

    [Fact]
    public void Big_changes_are_sent_blurry_and_small_ones_keep_the_picture_quality()
    {
        var planner = new QualityPlanner(1280, 800, ceiling: 70);
        var tiles = FrameTiles.Count(1280, 800);
        var (all, allCount) = Area(planner, tiles, tiles);
        var (few, fewCount) = Area(planner, tiles, 3);
        // 关键帧：预算小就糊，预算大就清楚，但不超过上限
        var tight = planner.Plan(all, allCount, key: true, sustained: false, changeBudget: 15_000, credit: 0);
        var roomy = planner.Plan(all, allCount, key: true, sustained: false, changeBudget: 500_000, credit: 0);
        Assert.InRange(tight, 0, 10);
        Assert.Equal(70, roomy);

        planner.Learn(tight, all, allCount, key: true, sustained: false, bytes: 30_000);
        Assert.Equal(tight, planner.Picture);
        Assert.False(planner.Sharp);
        // 画面还糊着的时候来了个小改动（打字）：不能因为"这点改动很便宜"就把质量拉到上限——
        // 质量是对整幅画面生效的，那样会把整屏重编一遍（实测一帧几十 KB），这一下操作的结果反而要等很久才看到
        Assert.Equal(tight, planner.Plan(few, fewCount, key: false, sustained: false, changeBudget: 60_000, credit: 400_000));
        // 画面一直在变（转圈的动画）等不到停下来：在补清晰的额度之内顺带补，额度不够就不补
        Assert.Equal(tight, planner.Plan(few, fewCount, key: false, sustained: true, changeBudget: 6_000, credit: 3_000));
        Assert.True(planner.Plan(few, fewCount, key: false, sustained: true, changeBudget: 6_000, credit: 400_000) > tight + 20);
    }

    [Fact]
    public void A_still_picture_is_refined_step_by_step_up_to_the_ceiling()
    {
        var planner = new QualityPlanner(1280, 800, ceiling: 70);
        var tiles = FrameTiles.Count(1280, 800);
        var (all, allCount) = Area(planner, tiles, tiles);
        planner.Learn(5, all, allCount, key: true, sustained: false, bytes: 32_000);

        // 额度太小迈不出一步：等着；线路空了很久就硬迈一小步（不然很慢的网永远补不清）
        Assert.Null(planner.Refine(credit: 500, force: false));
        Assert.Equal(11, planner.Refine(credit: 500, force: true));

        // 假装编码器：补清晰一步的大小 ≈ 整屏新内容（质量 50 时 86 KB）× 3.5% × 步长（封顶 55%），质量每高 1 乘 1.02
        static int Cost(int from, int to) => (int)(Math.Min(0.55, 0.035 * (to - from)) * 86_000 * Math.Pow(1.02, to - 50));
        var steps = new List<(int To, bool Forced)>();
        while (!planner.Sharp)
        {
            var before = planner.Picture;
            var to = planner.Refine(credit: 12_000, force: false);
            var forced = to is null;
            to ??= planner.Refine(credit: 12_000, force: true);
            Assert.True(to > before);
            var bytes = Cost(before, to!.Value);
            // 没被强迫的那些步，大小在额度附近
            if (!forced) Assert.True(bytes <= 12_000 * 1.3, $"{before} → {to} 用了 {bytes} 字节");
            planner.LearnRefine(to.Value, bytes);
            steps.Add((to.Value, forced));
            Assert.True(steps.Count < 40, "补不到头");
        }
        Assert.True(steps.Count >= 4, $"12 KB 的额度不该 {steps.Count} 步就补完");
        Assert.Contains(steps, s => !s.Forced);
        Assert.Equal(70, planner.Picture);
        Assert.True(planner.Sharp);
        Assert.Null(planner.Refine(credit: 1_000_000, force: true));

        // 补清晰之后只有一小块又变糊了：再补只算那一小块的开销，一步到位
        var (few, fewCount) = Area(planner, tiles, 4);
        planner.Learn(10, few, fewCount, key: false, sustained: false, bytes: 2_000);
        Assert.Equal(10, planner.Picture);
        Assert.Equal(70, planner.Refine(credit: 12_000, force: false));
    }

    [Fact]
    public void Sustained_motion_follows_what_the_last_frames_actually_cost()
    {
        var planner = new QualityPlanner(1280, 800, ceiling: 70);
        var tiles = FrameTiles.Count(1280, 800);
        var (all, allCount) = Area(planner, tiles, tiles);
        planner.Learn(70, all, allCount, key: true, sustained: false, bytes: 120_000);
        // 滚动：第一帧按"整屏新内容"估（很糊），之后几帧实际很便宜，质量跟着往上走
        var first = planner.Plan(all, allCount, key: false, sustained: false, changeBudget: 40_000, credit: 0);
        planner.Learn(first, all, allCount, key: false, sustained: false, bytes: 30_000);
        var q = first;
        for (var i = 0; i < 12; i++)
        {
            q = planner.Plan(all, allCount, key: false, sustained: true, changeBudget: 15_000, credit: 0);
            planner.Learn(q, all, allCount, key: false, sustained: true, bytes: 4_000);
        }
        Assert.True(first < 40, $"第一帧质量 {first}");
        Assert.True(q > first + 15, $"便宜的滚动帧没有把质量带上来：{first} → {q}");
    }

    [Fact]
    public void Quiet_audio_is_brought_up_to_a_normal_level_without_clipping()
    {
        const int frame = 960; // 20 ms @ 48 kHz
        var leveler = new AudioLeveler();
        short[] Tone(int amplitude, int offset)
        {
            var pcm = new short[frame * 2];
            for (var i = 0; i < frame; i++)
                pcm[i * 2] = pcm[i * 2 + 1] = (short)(Math.Sin((offset + i) * 2 * Math.PI * 440 / 48000) * amplitude);
            return pcm;
        }

        // 对方电脑音量开得很低：峰值只有 300（约 -40 dB）。两秒之后应该被拉到接近正常响度（最多放大 30 dB）
        var peak = 0;
        for (var n = 0; n < 100; n++)
        {
            var pcm = Tone(300, n * frame);
            leveler.Process(pcm, AudioLeveler.Peak(pcm));
            peak = AudioLeveler.Peak(pcm);
        }
        Assert.InRange(peak, 8000, 9600); // 300 × 31.6 ≈ 9480

        // 突然变响：这一包就得把增益降下来，不能削波
        var loud = Tone(20_000, 0);
        leveler.Process(loud, AudioLeveler.Peak(loud));
        Assert.InRange(AudioLeveler.Peak(loud), 15_000, 20_000);

        // 本来就够响的：原样通过（只放大不缩小）
        var normal = new AudioLeveler();
        var untouched = Tone(20_000, 0);
        var copy = (short[])untouched.Clone();
        normal.Process(untouched, AudioLeveler.Peak(untouched));
        Assert.Equal(copy, untouched);
    }
}

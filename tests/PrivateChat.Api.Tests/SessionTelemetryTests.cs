using System.Text.Json;
using System.Text.Json.Serialization;
using PrivateChat.Agent.Session;
using PrivateChat.Contracts.Remote;

namespace PrivateChat.Api.Tests;

/// <summary>
/// 被控端一次远程的遥测汇总：每秒样本的百分位（取第 ceil(p×n) 个）、两条线路各几秒、画质设置和编码大小各用了几秒、
/// 各种计数、相同的问题只留一条、长会话的样本减半、Final。
/// </summary>
public class SessionTelemetryTests
{
    /// <summary>和被控端的 ServerApi、服务器的 JSON 设置一样：camelCase、枚举写成字符串。</summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static readonly TelemetryStat Empty = new(0, 0, 0, 0, 0, 0, 0);

    private static SessionSecond Second(int fps = 30, int kbps = 2000, int target = 3000, double? captureMs = 5, double? encodeMs = 8, int cpu = 10,
        int rtt = 40, bool cpuBound = false, bool realtime = false, VideoSettings? settings = null, int width = 1920, int height = 1080, bool secure = false)
        => new(fps, kbps, target, captureMs, encodeMs, cpu, rtt, cpuBound, realtime, settings ?? VideoSettings.Auto, width, height, secure);

    [Fact]
    public void A_session_with_nothing_in_it_reports_zeros()
    {
        var report = new SessionTelemetry("0.8.6").Build(final: false);
        Assert.Equal("0.8.6", report.AgentVersion);
        Assert.False(report.Final);
        Assert.Equal((0, 0, 0), (report.VideoSeconds, report.RelaySeconds, report.RealtimeSeconds));
        Assert.Equal(("", false, ""), (report.Encoder, report.QualityMode, report.Capture));
        Assert.Empty(report.Settings);
        Assert.Empty(report.Sizes);
        Assert.All([report.SentFps, report.SentKbps, report.TargetKbps, report.CaptureMs, report.EncodeMs, report.CpuPercent, report.RttMs],
            s => Assert.Equal(Empty, s));
        // 没走快速线路：控制端那边的回报是 null，不是一堆 0
        Assert.Null(report.LossPermille);
        Assert.Null(report.ReceivedKbps);
        Assert.Null(report.JitterBufferMs);
        Assert.Null(report.ViewerFps);
        Assert.Equal(0, report.MaxFpsCaps + report.MinMaxFps + report.KeyFramesRequested + report.Stalls + report.RelaySendDrops
            + report.LineConnects + report.LineFailures + report.AutoNetworkSteps + report.AutoCpuSteps + report.AutoCpuUndos
            + report.CpuBoundSeconds + report.SecureDesktopSeconds);
        Assert.Empty(report.LineProblems);
        Assert.Empty(report.Errors);
    }

    [Fact]
    public void Percentiles_are_the_nearest_rank_over_the_per_second_samples()
    {
        var t = new SessionTelemetry("0.8.6");
        // 1..10 打乱了喂：P10 是第 1 个、P50 第 5 个、P90 第 9 个
        foreach (var fps in new[] { 7, 3, 10, 1, 5, 9, 2, 8, 4, 6 }) t.Sample(Second(fps: fps));
        Assert.Equal(new TelemetryStat(10, 5.5, 1, 1, 5, 9, 10), t.Build(false).SentFps);

        // 30 个：0.1×30 乘出来不能变成"第 4 个"
        var thirty = new SessionTelemetry("0.8.6");
        for (var i = 1; i <= 30; i++) thirty.Sample(Second(fps: i));
        Assert.Equal(new TelemetryStat(30, 15.5, 1, 3, 15, 27, 30), thirty.Build(false).SentFps);

        // 只有一个样本：全都是它；数保留一位小数
        var one = new SessionTelemetry("0.8.6");
        one.Sample(Second(captureMs: 12.34));
        Assert.Equal(new TelemetryStat(1, 12.3, 12.3, 12.3, 12.3, 12.3, 12.3), one.Build(false).CaptureMs);
    }

    [Fact]
    public void Seconds_are_split_by_line()
    {
        var t = new SessionTelemetry("0.8.6");
        for (var i = 0; i < 3; i++) t.Sample(Second(realtime: false));
        for (var i = 0; i < 2; i++) t.Sample(Second(realtime: true));
        var r = t.Build(false);
        Assert.Equal((5, 3, 2), (r.VideoSeconds, r.RelaySeconds, r.RealtimeSeconds));
    }

    [Fact]
    public void Settings_and_sizes_count_seconds_in_order_of_first_use()
    {
        var t = new SessionTelemetry("0.8.6");
        for (var i = 0; i < 3; i++) t.Sample(Second(settings: VideoSettings.Auto, width: 1920, height: 1080));
        for (var i = 0; i < 2; i++) t.Sample(Second(settings: VideoSettings.Smooth, width: 1280, height: 720));
        t.Sample(Second(settings: VideoSettings.Auto, width: 1600, height: 900));
        // 还没建编码器（0×0）：算进画质设置，不算进"编多大"
        t.Sample(Second(settings: VideoSettings.Auto, width: 0, height: 0));
        var r = t.Build(false);
        Assert.Equal([new TelemetrySettingsUse(RemoteVideoMode.Auto, 1920, 1200, 30, 8000, 5), new TelemetrySettingsUse(RemoteVideoMode.Smooth, 1280, 800, 30, 4000, 2)],
            r.Settings);
        Assert.Equal([new TelemetrySizeUse(1920, 1080, 3), new TelemetrySizeUse(1280, 720, 2), new TelemetrySizeUse(1600, 900, 1)], r.Sizes);
    }

    [Fact]
    public void Only_the_longest_used_sizes_are_reported_when_there_are_too_many()
    {
        var t = new SessionTelemetry("0.8.6");
        // 40 种大小，第 i 种用了 i+1 秒：留下用得最久的 32 种（第 8 到 39 种），还是按先后排
        for (var i = 0; i < 40; i++)
            for (var s = 0; s <= i; s++) t.Sample(Second(width: 100 + i * 2, height: 100));
        Assert.Equal(Enumerable.Range(8, SessionTelemetry.MaxUses).Select(i => new TelemetrySizeUse(100 + i * 2, 100, i + 1)), t.Build(false).Sizes);

        // 一样久的：先用到的优先；种类多到 64 种以后来的新的不记
        var many = new SessionTelemetry("0.8.6");
        for (var i = 0; i < 70; i++) many.Sample(Second(width: 100 + i * 2, height: 100));
        Assert.Equal(Enumerable.Range(0, SessionTelemetry.MaxUses).Select(i => new TelemetrySizeUse(100 + i * 2, 100, 1)), many.Build(false).Sizes);
        Assert.Equal(70, many.Build(false).VideoSeconds);
    }

    [Fact]
    public void Unknown_values_and_secure_desktop_seconds_are_left_out_of_the_stats()
    {
        var t = new SessionTelemetry("0.8.6");
        t.Sample(Second(fps: 30, captureMs: 5, encodeMs: 8, rtt: 40, cpuBound: true));
        // 画面不动：一帧没编（采集、编码没量到），往返时间也还没量到
        t.Sample(Second(fps: 0, kbps: 0, captureMs: null, encodeMs: null, rtt: 0));
        // 锁屏：一帧不发不是卡了，帧率、码率不算；目标码率、CPU、往返照算
        t.Sample(Second(fps: 0, kbps: 0, captureMs: null, encodeMs: null, rtt: 50, target: 600, secure: true));
        var r = t.Build(false);
        Assert.Equal((3, 1, 1), (r.VideoSeconds, r.SecureDesktopSeconds, r.CpuBoundSeconds));
        Assert.Equal(new TelemetryStat(2, 15, 0, 0, 0, 30, 30), r.SentFps);
        Assert.Equal(2, r.SentKbps.Samples);
        Assert.Equal(new TelemetryStat(1, 5, 5, 5, 5, 5, 5), r.CaptureMs);
        Assert.Equal(new TelemetryStat(1, 8, 8, 8, 8, 8, 8), r.EncodeMs);
        Assert.Equal(new TelemetryStat(2, 45, 40, 40, 40, 50, 50), r.RttMs);
        Assert.Equal(new TelemetryStat(3, 2200, 600, 600, 3000, 3000, 3000), r.TargetKbps);
        Assert.Equal(3, r.CpuPercent.Samples);
    }

    [Fact]
    public void Events_are_counted()
    {
        var t = new SessionTelemetry("0.8.6");
        t.OnAutoStep(SessionTelemetry.AutoStep.Network);
        t.OnAutoStep(SessionTelemetry.AutoStep.Network);
        t.OnAutoStep(SessionTelemetry.AutoStep.Cpu);
        t.OnAutoStep(SessionTelemetry.AutoStep.CpuUndo);
        for (var i = 0; i < 3; i++) t.OnKeyFrameRequested();
        t.OnStall();
        for (var i = 0; i < 4; i++) t.OnRelaySendDrop();
        t.OnLineConnected();
        t.OnLineConnected();
        var r = t.Build(false);
        Assert.Equal((2, 1, 1), (r.AutoNetworkSteps, r.AutoCpuSteps, r.AutoCpuUndos));
        Assert.Equal((3, 1, 4), (r.KeyFramesRequested, r.Stalls, r.RelaySendDrops));
        Assert.Equal((2, 0), (r.LineConnects, r.LineFailures));
    }

    [Fact]
    public void Only_new_or_tighter_frame_rate_caps_count()
    {
        var t = new SessionTelemetry("0.8.6");
        t.OnMaxFps(-1, 0);  // 新网页说它跟不上时会说：不限，不算
        t.OnMaxFps(0, 20);  // 限到 20：算
        t.OnMaxFps(20, 20); // 没变
        t.OnMaxFps(20, 12); // 限得更紧：算
        t.OnMaxFps(12, 25); // 缓过来了，放宽：不算
        t.OnMaxFps(25, 0);  // 不限了：不算
        t.OnMaxFps(-1, 15); // 换了控制端，它一来就限到 15：算
        var r = t.Build(false);
        Assert.Equal((3, 12), (r.MaxFpsCaps, r.MinMaxFps));
    }

    [Fact]
    public void Line_problems_and_errors_keep_one_line_per_reason_with_a_count()
    {
        var t = new SessionTelemetry("0.8.6");
        t.OnLineFailed("连接断开");
        t.OnLineFailed("没接通：超时");
        t.OnLineFailed("连接断开");
        // 异常的说明压成一行；文件路径换掉（里面可能有用户名）；太长的截短
        t.OnError("采集 / 编码出错：MFStartup 失败：0xC00D36E5\r\n   第二行");
        t.OnError(@"声音采集出错：Could not find file 'C:\Users\bob\AppData\Local\x.dll'.");
        t.OnError(@"数据通道断开：\\nas\share\bob\log.txt 不能写");
        t.OnError(new string('错', 500));
        t.OnError(new string('错', 600));
        var r = t.Build(false);
        Assert.Equal(3, r.LineFailures);
        Assert.Equal(["连接断开（2 次）", "没接通：超时"], r.LineProblems);
        Assert.Equal([
            "采集 / 编码出错：MFStartup 失败：0xC00D36E5 第二行",
            "声音采集出错：Could not find file '<路径>'.",
            "数据通道断开：<路径> 不能写",
            new string('错', SessionTelemetry.MaxNoteLength) + "（2 次）",
        ], r.Errors);
        Assert.DoesNotContain(r.Errors, e => e.Contains("bob"));
    }

    [Fact]
    public void Quoted_paths_are_scrubbed_whole_and_urls_are_left_alone()
    {
        var t = new SessionTelemetry("0.8.6");
        // 引号里的路径连空格一起换掉（单引号、双引号）；盘符后面是 / 的、file:///、\\服务器 也算；名字里的撇号不算引号结束
        t.OnError(@"出错：Access to the path 'C:\Users\John Smith\AppData\Local\PrivateChat\agent.log' is denied.");
        t.OnError(@"出错：Could not find file ""D:\My Documents\张三 的 报告.docx"".");
        t.OnError(@"出错：'C:\Users\zhangsan\OneDrive - Contoso\Desktop\工资 表.xlsx' 被占用");
        t.OnError("出错：不能加载 file:///C:/Users/bob/AppData/x.dll");
        t.OnError(@"出错：'C:\Users\O'Brien\x.dll'.");
        t.OnError(@"出错：C:/Users/bob/x.txt 和 '\\nas\share\John Smith\x.txt'");
        // 网址不是路径（https:// 的 s:/ 不是盘符）
        t.OnLineFailed("没接通：连不上 wss://rtc.example.com/v1/apps/x?a=1 （https://example.com/a/b）");
        var r = t.Build(false);
        Assert.Equal([
            "出错：Access to the path '<路径>' is denied.",
            "出错：Could not find file \"<路径>\".",
            "出错：'<路径>' 被占用",
            "出错：不能加载 <路径>",
            "出错：'<路径>'.",
            "出错：<路径> 和 '<路径>'",
        ], r.Errors);
        Assert.All(new[] { "John", "Smith", "张三", "报告", "zhangsan", "Contoso", "工资", "bob", "Brien", "nas" },
            name => Assert.DoesNotContain(r.Errors, e => e.Contains(name)));
        Assert.Equal(["没接通：连不上 wss://rtc.example.com/v1/apps/x?a=1 （https://example.com/a/b）"], r.LineProblems);

        // 探测结果里也用它（HResults.Clip）：显示器的设备名 \\.\DISPLAY1 不是路径，留着
        Assert.Equal(@"探测没做完，停在：桌面复制 \\.\DISPLAY1", SessionTelemetry.ScrubPaths(@"探测没做完，停在：桌面复制 \\.\DISPLAY1"));
    }

    [Fact]
    public void Too_many_different_problems_end_in_one_summary_line()
    {
        var t = new SessionTelemetry("0.8.6");
        for (var i = 0; i < 25; i++) t.OnError($"错误 {i}");
        t.OnError("错误 3");
        var errors = t.Build(false).Errors;
        Assert.Equal(SessionTelemetry.MaxNotes, errors.Count);
        Assert.Equal("错误 0", errors[0]);
        Assert.Equal("错误 3（2 次）", errors[3]);
        Assert.Equal("错误 18", errors[18]);
        Assert.Equal("另有 6 次别的问题", errors[19]);
    }

    [Fact]
    public void Receiver_reports_are_summarized_like_the_other_numbers()
    {
        var t = new SessionTelemetry("0.8.6");
        t.Sample(Second(realtime: true));
        t.OnReceiverReport(5, 2500, 40, 30);
        t.OnReceiverReport(15, 2300, 60, 28);
        var r = t.Build(false);
        Assert.Equal(new TelemetryStat(2, 10, 5, 5, 5, 15, 15), r.LossPermille);
        Assert.Equal(new TelemetryStat(2, 2400, 2300, 2300, 2300, 2500, 2500), r.ReceivedKbps);
        Assert.Equal(new TelemetryStat(2, 50, 40, 40, 40, 60, 60), r.JitterBufferMs);
        Assert.Equal(new TelemetryStat(2, 29, 28, 28, 28, 30, 30), r.ViewerFps);
    }

    [Fact]
    public void Long_sessions_keep_exact_counts_and_an_even_spread_of_samples()
    {
        var t = new SessionTelemetry("0.8.6");
        // 6 小时多一秒，每秒的数是第几秒：留下的样本减半了两次
        var n = 3 * SessionTelemetry.MaxKeptSamples + 1;
        for (var i = 0; i < n; i++) t.Sample(Second(fps: i));
        var fps = t.Build(false).SentFps;
        // 样本数、平均、最小、最大按全部样本算
        Assert.Equal((n, 0.0, n - 1.0, (n - 1) / 2.0), (fps.Samples, fps.Min, fps.Max, fps.Mean));
        // 百分位从留下的（均匀铺满整次远程）里取：和按全部样本取的只差几个
        Assert.InRange(fps.P10, 0.1 * n - 8, 0.1 * n + 8);
        Assert.InRange(fps.P50, 0.5 * n - 8, 0.5 * n + 8);
        Assert.InRange(fps.P90, 0.9 * n - 8, 0.9 * n + 8);
    }

    [Fact]
    public void Decimation_keeps_the_early_part_of_the_session_too()
    {
        var t = new SessionTelemetry("0.8.6");
        // 前 2 小时每秒 10 帧、后 2 小时每秒 30 帧：减半之后两段留下的一样多，不是只剩最近的
        for (var i = 0; i < SessionTelemetry.MaxKeptSamples; i++) t.Sample(Second(fps: 10));
        for (var i = 0; i < SessionTelemetry.MaxKeptSamples; i++) t.Sample(Second(fps: 30));
        Assert.Equal(new TelemetryStat(2 * SessionTelemetry.MaxKeptSamples, 20, 10, 10, 10, 30, 30), t.Build(false).SentFps);
    }

    [Fact]
    public void Final_marks_the_last_report_and_revision_tracks_changes()
    {
        var t = new SessionTelemetry("0.8.6");
        var start = t.Revision;
        Assert.False(t.Build(final: false).Final);
        Assert.True(t.Build(final: true).Final);
        // 出报告不算变化
        Assert.Equal(start, t.Revision);
        t.Sample(Second());
        var sampled = t.Revision;
        Assert.NotEqual(start, sampled);
        t.SetPipeline("Microsoft H.264（软件）", true, "GDI");
        var piped = t.Revision;
        Assert.NotEqual(sampled, piped);
        // 重建编码器、什么都没变：不算变化；放宽帧率限制不算一次限制，也不算变化
        t.SetPipeline("Microsoft H.264（软件）", true, "GDI");
        t.OnMaxFps(20, 25);
        Assert.Equal(piped, t.Revision);
        var report = t.Build(final: true);
        Assert.Equal(("Microsoft H.264（软件）", true, "GDI"), (report.Encoder, report.QualityMode, report.Capture));
        // 出两次一样（排序没动到汇总本身）
        Assert.Equal(JsonSerializer.Serialize(report, Json), JsonSerializer.Serialize(t.Build(final: true), Json));
    }

    [Fact]
    public void A_full_report_serializes_like_the_server_expects_and_stays_under_its_size_limit()
    {
        var t = new SessionTelemetry("0.8.6");
        // 每样都写满：画质设置、编码大小各 64 种（只报 32 种），出错、线路问题各 30 种很长的中文说明（只报 20 条），前面的还都出过两次
        for (var i = 0; i < 64; i++)
            t.Sample(Second(settings: new VideoSettings(RemoteVideoMode.Custom, 1000 + i, 800, 30, 8000, 85), width: 1000 + i * 2, height: 800, realtime: true));
        for (var round = 0; round < 2; round++)
            for (var i = 0; i < 30; i++)
            {
                t.OnError($"{i}{new string('错', 400)}");
                t.OnLineFailed($"{i}{new string('断', 400)}");
            }
        t.OnReceiverReport(5, 2500, 40, 30);
        var json = JsonSerializer.SerializeToUtf8Bytes(t.Build(final: true), Json);
        // 服务器收 64 KB 以内；汉字在 JSON 里写成 \uXXXX
        Assert.True(json.Length < 56 * 1024, $"{json.Length} 字节");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("final").GetBoolean());
        Assert.Equal("Custom", root.GetProperty("settings")[0].GetProperty("mode").GetString());
        Assert.Equal(SessionTelemetry.MaxUses, root.GetProperty("settings").GetArrayLength());
        Assert.Equal(SessionTelemetry.MaxNotes, root.GetProperty("errors").GetArrayLength());
        Assert.Equal(SessionTelemetry.MaxNotes, root.GetProperty("lineProblems").GetArrayLength());
        Assert.Equal(64, root.GetProperty("realtimeSeconds").GetInt32());
        Assert.Equal(1, root.GetProperty("lossPermille").GetProperty("samples").GetInt32());

        // 原样读得回来（服务器那边按同一份记录反序列化）
        var back = JsonSerializer.Deserialize<AgentSessionTelemetry>(json, Json)!;
        Assert.Equal(64, back.VideoSeconds);
        Assert.All(back.Errors, e => Assert.True(e.Length <= 300));
    }

    [Fact]
    public void The_switch_turns_it_off()
    {
        var before = Environment.GetEnvironmentVariable(SessionTelemetry.SwitchVariable);
        try
        {
            foreach (var off in new[] { "off", " OFF ", "0", "false" })
            {
                Environment.SetEnvironmentVariable(SessionTelemetry.SwitchVariable, off);
                Assert.True(SessionTelemetry.Disabled, off);
            }
            foreach (var on in new[] { null, "on", "1" })
            {
                Environment.SetEnvironmentVariable(SessionTelemetry.SwitchVariable, on);
                Assert.False(SessionTelemetry.Disabled, on);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(SessionTelemetry.SwitchVariable, before);
        }
    }
}

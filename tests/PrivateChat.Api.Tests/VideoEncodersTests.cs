using PrivateChat.Agent.Video;

namespace PrivateChat.Api.Tests;

/// <summary>
/// 选编码器里不碰 Windows 的那部分（Video/EncoderChoice.cs）：环境变量开关、硬件编码器的试用顺序（同厂商优先、出过错的跳过、
/// auto 只用探测验证过的）、质量 → QP 的换算和 AVEncVideoEncodeQP 的打包、等输出的时限、认"这一帧的输出"、SPS 里的 profile。
/// </summary>
public class VideoEncodersTests
{
    private const int Nvidia = 0x10DE, Amd = 0x1002, Intel = 0x8086;
    private static readonly Func<string, bool> None = _ => false;

    private static List<EncoderAttempt> Plan(EncoderSwitch mode, IReadOnlyList<ValidatedEncoder> validated, int captureVendor,
        IReadOnlyList<(string, int)>? enumerated = null, Func<string, bool>? excluded = null)
        => EncoderChoice.Plan(mode, validated, enumerated ?? [], captureVendor, excluded ?? None);

    [Theory]
    [InlineData(null, EncoderSwitch.Auto)]
    [InlineData("", EncoderSwitch.Auto)]
    [InlineData("auto", EncoderSwitch.Auto)]
    [InlineData("whatever", EncoderSwitch.Auto)]
    [InlineData("hardware", EncoderSwitch.Hardware)]
    [InlineData(" Hardware ", EncoderSwitch.Hardware)]
    [InlineData("hw", EncoderSwitch.Hardware)]
    [InlineData("software", EncoderSwitch.Software)]
    [InlineData("SW", EncoderSwitch.Software)]
    public void Parses_the_environment_switch(string? value, EncoderSwitch expected) => Assert.Equal(expected, EncoderChoice.ParseSwitch(value));

    [Theory]
    [InlineData(0, 44)]
    [InlineData(20, 38)]
    [InlineData(30, 35)]
    [InlineData(50, 30)]
    [InlineData(70, 24)]
    [InlineData(100, 15)]
    [InlineData(-5, 44)]
    [InlineData(250, 15)]
    public void Maps_quality_to_qp_on_the_software_encoders_scale(int quality, int qp) => Assert.Equal(qp, EncoderChoice.QpForQuality(quality));

    [Fact]
    public void Qp_never_goes_up_as_quality_goes_up_and_stays_in_range()
    {
        var previous = int.MaxValue;
        for (var q = 0; q <= 100; q++)
        {
            var qp = EncoderChoice.QpForQuality(q);
            Assert.InRange(qp, 10, 51);
            Assert.True(qp <= previous, $"质量 {q}：QP {qp} 比前一个 {previous} 大");
            previous = qp;
        }
        // 每 3.5 个质量差一个 QP：0–100 一共差 28～29 个
        Assert.InRange(EncoderChoice.QpForQuality(0) - EncoderChoice.QpForQuality(100), 28, 29);
    }

    [Fact]
    public void Packs_the_same_qp_into_all_four_16_bit_fields()
    {
        var packed = EncoderChoice.PackQp(26);
        Assert.Equal(0x001A_001A_001A_001AL, packed);
        Assert.Equal(26, packed & 0xFFFF); // 默认 QP：文档说编码器可能只看这一个
        Assert.Equal(51, EncoderChoice.PackQp(80) & 0xFFFF);
        Assert.Equal(0, EncoderChoice.PackQp(-3) & 0xFFFF);
    }

    [Theory]
    [InlineData(30, 100)]
    [InlineData(60, 100)]
    [InlineData(20, 150)]
    [InlineData(10, 300)]
    [InlineData(5, 600)]
    public void Waits_for_a_frame_at_least_100_ms_or_three_frame_intervals(int fps, int ms)
        => Assert.Equal(ms, EncoderChoice.FrameTimeout(fps).TotalMilliseconds, 3);

    [Theory]
    [InlineData(100, 5000, true)]
    [InlineData(1200, 5000, true)]
    [InlineData(1300, 5000, false)] // 不到 4 倍
    [InlineData(100, 900, false)]   // 太小：不像"补清晰"
    [InlineData(0, 1000, true)]
    public void Refine_has_to_be_clearly_bigger(int same, int refined, bool works) => Assert.Equal(works, EncoderChoice.RefineWorks(same, refined));

    [Fact]
    public void Refine_without_output_does_not_count() => Assert.False(EncoderChoice.RefineWorks(null, 50_000));

    [Fact]
    public void Names_round_trip_with_the_hardware_suffix()
    {
        var name = "Intel® Quick Sync Video H.264 Encoder MFT";
        Assert.Equal($"{name}（硬件）", EncoderChoice.DisplayName(name));
        Assert.Equal(name, EncoderChoice.BaseName(EncoderChoice.DisplayName(name)));
        Assert.Equal(name, EncoderChoice.BaseName(name));
    }

    [Fact]
    public void Software_switch_tries_no_hardware_at_all()
    {
        var validated = new[] { new ValidatedEncoder("Intel QSV", Intel, true, true) };
        Assert.Empty(Plan(EncoderSwitch.Software, validated, Intel, [("Intel QSV", Intel)]));
    }

    [Fact]
    public void Auto_without_validated_encoders_uses_software_even_if_hardware_is_listed()
        => Assert.Empty(Plan(EncoderSwitch.Auto, [], Intel, [("Intel QSV", Intel), ("NVIDIA H.264 Encoder MFT", Nvidia)]));

    [Fact]
    public void Auto_puts_the_capture_adapters_vendor_first_and_keeps_the_rest_in_order()
    {
        var validated = new[]
        {
            new ValidatedEncoder("NVIDIA H.264 Encoder MFT", Nvidia, false, true),
            new ValidatedEncoder("AMDh264Encoder", Amd, true, false),
            new ValidatedEncoder("Intel QSV", Intel, false, true),
        };
        Assert.Equal(["Intel QSV", "NVIDIA H.264 Encoder MFT", "AMDh264Encoder"], Plan(EncoderSwitch.Auto, validated, Intel).Select(a => a.Name));
        Assert.Equal(["AMDh264Encoder", "NVIDIA H.264 Encoder MFT", "Intel QSV"], Plan(EncoderSwitch.Auto, validated, Amd).Select(a => a.Name));
        // 不知道抓屏在哪张卡上：照探测的顺序
        Assert.Equal(["NVIDIA H.264 Encoder MFT", "AMDh264Encoder", "Intel QSV"], Plan(EncoderSwitch.Auto, validated, 0).Select(a => a.Name));
        Assert.All(Plan(EncoderSwitch.Auto, validated, 0), a => Assert.True(a.Validated));
    }

    [Fact]
    public void Auto_uses_only_the_modes_the_probe_verified_quality_first()
    {
        var validated = new[]
        {
            new ValidatedEncoder("A", Nvidia, true, true),
            new ValidatedEncoder("B", Intel, false, true),
            new ValidatedEncoder("C", Amd, true, false),
            new ValidatedEncoder("D", Amd, false, false), // 只能固定码率：auto 不用
        };
        var plan = Plan(EncoderSwitch.Auto, validated, 0);
        Assert.Equal(["A", "B", "C"], plan.Select(a => a.Name));
        Assert.Equal([HardwareRateControl.Quality, HardwareRateControl.Qp], plan[0].Modes);
        Assert.Equal([HardwareRateControl.Qp], plan[1].Modes);
        Assert.Equal([HardwareRateControl.Quality], plan[2].Modes);
    }

    [Fact]
    public void Skips_failed_encoders_and_duplicate_names()
    {
        // 这台笔记本上 Intel 的编码器 MFTEnumEx 列了两遍（两个适配器项），探测结果里也就有两条同名的
        var validated = new[]
        {
            new ValidatedEncoder("Intel QSV", Intel, false, true),
            new ValidatedEncoder("Intel QSV", Intel, false, true),
            new ValidatedEncoder("NVIDIA H.264 Encoder MFT（硬件）", Nvidia, false, true),
        };
        Assert.Equal(["Intel QSV", "NVIDIA H.264 Encoder MFT"], Plan(EncoderSwitch.Auto, validated, 0).Select(a => a.Name));
        var failed = new HashSet<string> { "Intel QSV" };
        Assert.Equal(["NVIDIA H.264 Encoder MFT"], Plan(EncoderSwitch.Auto, validated, Intel, excluded: failed.Contains).Select(a => a.Name));
        failed.Add("NVIDIA H.264 Encoder MFT");
        Assert.Empty(Plan(EncoderSwitch.Auto, validated, Intel, excluded: failed.Contains));
    }

    [Fact]
    public void Hardware_switch_tries_validated_first_then_everything_listed()
    {
        var validated = new[] { new ValidatedEncoder("Intel QSV", Intel, false, true) };
        var listed = new (string, int)[]
        {
            ("NVIDIA H.264 Encoder MFT", Nvidia),
            ("Intel QSV", Intel),
            ("Intel QSV", Intel),
            ("Microsoft AVC DX12 Encoder", EncoderChoice.MicrosoftVendorId),
            ("AMDh264Encoder", Amd),
        };
        var plan = Plan(EncoderSwitch.Hardware, validated, Nvidia, listed);
        Assert.Equal(["Intel QSV", "NVIDIA H.264 Encoder MFT", "AMDh264Encoder", "Microsoft AVC DX12 Encoder"], plan.Select(a => a.Name));
        Assert.Equal([true, false, false, false], plan.Select(a => a.Validated));
        Assert.Equal([HardwareRateControl.Qp], plan[0].Modes);
        Assert.Equal(EncoderChoice.UnverifiedModes, plan[1].Modes);
        Assert.Equal(HardwareRateControl.Cbr, plan[1].Modes[^1]);
    }

    [Fact]
    public void Hardware_switch_without_validation_orders_by_vendor_and_puts_microsofts_generic_encoder_last()
    {
        var listed = new (string, int)[]
        {
            ("Microsoft AVC DX12 Encoder", EncoderChoice.MicrosoftVendorId),
            ("NVIDIA H.264 Encoder MFT", Nvidia),
            ("Intel QSV", Intel),
        };
        // 这台笔记本：NVIDIA 建不起来（Optimus 上 ActivateObject 返回 E_UNEXPECTED）也只是排在前面、失败了接着试 Intel
        Assert.Equal(["NVIDIA H.264 Encoder MFT", "Intel QSV", "Microsoft AVC DX12 Encoder"], Plan(EncoderSwitch.Hardware, [], 0, listed).Select(a => a.Name));
        Assert.Equal(["Intel QSV", "NVIDIA H.264 Encoder MFT", "Microsoft AVC DX12 Encoder"], Plan(EncoderSwitch.Hardware, [], Intel, listed).Select(a => a.Name));
        Assert.Equal(["Intel QSV", "Microsoft AVC DX12 Encoder"],
            Plan(EncoderSwitch.Hardware, [], Intel, listed, new HashSet<string> { "NVIDIA H.264 Encoder MFT" }.Contains).Select(a => a.Name));
        Assert.Empty(Plan(EncoderSwitch.Hardware, [], Intel, []));
    }

    // 同名的几个实例（MFTEnumEx 每个适配器列一个）：instance 是"第几个"，failing 里的建不起来
    private static string? Start(EncoderSizeFailures failures, int width, int height, int[] instances, HashSet<int> failing, List<string> reasons)
        => failures.StartFirst("Intel QSV", width, height, instances,
            i => failing.Contains(i) ? throw new InvalidOperationException($"实例 {i} 起不来") : $"实例 {i}",
            ex => ex.Message, reasons.Add);

    [Fact]
    public void One_failing_instance_does_not_rule_out_the_size_when_a_same_named_one_starts()
    {
        var failures = new EncoderSizeFailures();
        var reasons = new List<string>();
        Assert.Equal("实例 2", Start(failures, 1920, 1080, [1, 2], [1], reasons));
        Assert.Equal(["实例 1 起不来"], reasons);
        Assert.False(failures.TryGet("Intel QSV", 1920, 1080, out _));
    }

    [Fact]
    public void A_size_is_ruled_out_only_when_every_same_named_instance_failed()
    {
        var failures = new EncoderSizeFailures();
        var reasons = new List<string>();
        Assert.Null(Start(failures, 3840, 2400, [1, 2], [1, 2], reasons));
        Assert.Equal(["实例 1 起不来", "实例 2 起不来"], reasons);
        Assert.True(failures.TryGet("Intel QSV", 3840, 2400, out var why));
        Assert.Equal("实例 2 起不来", why); // 最后那个的原因
        // 只记这个名字的这个尺寸
        Assert.False(failures.TryGet("Intel QSV", 1920, 1080, out _));
        Assert.False(failures.TryGet("AMDh264Encoder", 3840, 2400, out _));
        // 一个实例都没有：什么都不记
        Assert.Null(Start(failures, 1280, 720, [], [], reasons));
        Assert.False(failures.TryGet("Intel QSV", 1280, 720, out _));
    }

    [Fact]
    public void Starting_an_instance_clears_an_earlier_failure_at_that_size()
    {
        var failures = new EncoderSizeFailures();
        var reasons = new List<string>();
        Assert.Null(Start(failures, 1920, 1080, [1], [1], reasons));
        Assert.True(failures.TryGet("Intel QSV", 1920, 1080, out _));
        Assert.Equal("实例 1", Start(failures, 1920, 1080, [1, 2], [], reasons));
        Assert.False(failures.TryGet("Intel QSV", 1920, 1080, out _));
    }

    [Fact]
    public void Unverified_encoders_try_per_frame_qp_before_quality()
        => Assert.Equal([HardwareRateControl.Qp, HardwareRateControl.Quality, HardwareRateControl.Cbr], EncoderChoice.UnverifiedModes);

    private static readonly byte[] Sps = [0, 0, 0, 1, 0x67, 0x4D, 0x00, 0x1F, 0x95, 0xA8];
    private static readonly byte[] HighSps = [0, 0, 0, 1, 0x67, 0x64, 0x00, 0x28, 0xAC];
    private static readonly byte[] Pps = [0, 0, 0, 1, 0x68, 0xEE, 0x3C, 0x80];
    private static readonly byte[] Aud = [0, 0, 0, 1, 0x09, 0xF0];
    private static readonly byte[] Sei = [0, 0, 1, 0x06, 0x05, 0x01, 0x80];
    private static readonly byte[] Idr = [0, 0, 0, 1, 0x65, 0x88, 0x84, 0x21];
    private static readonly byte[] Slice = [0, 0, 1, 0x41, 0x9A, 0x02];

    [Fact]
    public void Tells_parameter_sets_from_pictures()
    {
        Assert.False(EncoderChoice.HasPicture([.. Aud, .. Sps, .. Pps]));
        Assert.False(EncoderChoice.HasPicture([.. Sei]));
        Assert.False(EncoderChoice.HasPicture([]));
        Assert.True(EncoderChoice.HasPicture([.. Aud, .. Sps, .. Pps, .. Idr]));
        Assert.True(EncoderChoice.HasPicture([.. Aud, .. Slice]));
    }

    [Fact]
    public void Reads_the_profile_from_the_sps()
    {
        Assert.Equal(77, EncoderChoice.Profile([.. Aud, .. Sps, .. Pps, .. Idr]));
        Assert.Equal(100, EncoderChoice.Profile([.. HighSps, .. Pps]));
        Assert.Null(EncoderChoice.Profile([.. Aud, .. Slice]));
        Assert.True(EncoderChoice.MainCompatible(77));
        Assert.True(EncoderChoice.MainCompatible(66));
        Assert.False(EncoderChoice.MainCompatible(100));
    }

    [Fact]
    public void Matches_outputs_to_frames_by_sample_time()
    {
        var pending = new PendingFrames();
        pending.Add(340_000);
        pending.Add(670_000);
        Assert.Equal(670_000, pending.Owner(670_000));
        Assert.Equal(340_000, pending.Owner(340_000));
        // 编码器把时间取整到毫秒：差 1 ms 以内认最近的那帧
        Assert.Equal(670_000, pending.Owner(669_999 - 5_000));
        Assert.Null(pending.Owner(500_000));
        // 没带时间：先进先出
        Assert.Equal(340_000, pending.Owner(null));
    }

    [Fact]
    public void Once_a_frame_is_out_earlier_frames_are_no_longer_waited_for()
    {
        var pending = new PendingFrames();
        pending.Add(100);
        pending.Add(200_000);
        pending.Add(400_000);
        pending.RemoveThrough(200_000);
        Assert.Equal(1, pending.Count);
        Assert.Null(pending.Owner(100));      // 更早那帧晚到的输出：认不出来（丢掉）
        Assert.Null(pending.Owner(200_000));  // 已经出过画面的那帧
        Assert.Equal(400_000, pending.Owner(400_000));
        pending.RemoveThrough(123);           // 不认识的时间：什么都不动
        Assert.Equal(1, pending.Count);
        pending.RemoveThrough(400_000);
        Assert.Equal(0, pending.Count);
        Assert.Null(pending.Owner(null));
    }

    [Fact]
    public void Keeps_only_the_last_few_frames_waiting()
    {
        var pending = new PendingFrames();
        for (var i = 1; i <= 20; i++) pending.Add(i * 1_000_000L);
        Assert.Equal(8, pending.Count);
        Assert.Null(pending.Owner(1_000_000));
        Assert.Equal(20_000_000, pending.Owner(20_000_000));
    }
}

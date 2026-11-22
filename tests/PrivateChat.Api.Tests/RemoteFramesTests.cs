using PrivateChat.Contracts.Remote;

namespace PrivateChat.Api.Tests;

public class RemoteFramesTests
{
    // 起始码有三字节、四字节两种；SPS(7) PPS(8) IDR(5)
    private static readonly byte[] KeyFrame =
    [
        0, 0, 0, 1, 0x67, 0x4D, 0x40, 0x1F, 0xAA,
        0, 0, 0, 1, 0x68, 0xEE, 0x3C, 0x80,
        0, 0, 1, 0x65, 0x88, 0x84, 0x00, 0x10,
    ];

    [Fact]
    public void Splits_annex_b_into_nal_units_with_either_start_code()
    {
        var units = H264.Split(KeyFrame);
        Assert.Equal([7, 8, 5], units.Select(u => u.Type(KeyFrame)));
        // 四字节起始码前的 0 不算进上一个 NAL；NAL 内部的 0 保留
        Assert.Equal(5, units[0].Length);
        Assert.Equal(0x10, KeyFrame[units[2].Offset + units[2].Length - 1]);
        Assert.True(H264.IsKeyFrame(KeyFrame));
        Assert.Equal("avc1.4D401F", H264.CodecString(KeyFrame));
        Assert.Equal(KeyFrame[..17], H264.ParameterSets(KeyFrame));
        Assert.False(H264.IsKeyFrame([0, 0, 1, 0x41, 0x9A]));
        Assert.Null(H264.ParameterSets([0, 0, 1, 0x41, 0x9A]));
    }

    [Fact]
    public void Control_frames_round_trip_and_garbage_is_ignored()
    {
        var move = Assert.IsType<ControlFrame.MouseMove>(RemoteFrames.ParseControl(RemoteFrames.MouseMove(0.25f, 1f)));
        Assert.Equal((0.25f, 1f), (move.X, move.Y));
        var key = Assert.IsType<ControlFrame.Key>(RemoteFrames.ParseControl(RemoteFrames.Key(0xE04B, true)));
        Assert.Equal((0xE04B, true), (key.ScanCode, key.Down));
        Assert.Equal(1234u, Assert.IsType<ControlFrame.FrameAck>(RemoteFrames.ParseControl(RemoteFrames.FrameAck(1234))).TimestampMs);
        Assert.Equal("你好", Assert.IsType<ControlFrame.Clipboard>(RemoteFrames.ParseControl(RemoteFrames.Clipboard("你好"))).Value);
        Assert.IsType<ControlFrame.RequestKeyFrame>(RemoteFrames.ParseControl([(byte)RemoteFrameType.RequestKeyFrame]));
        Assert.True(Assert.IsType<ControlFrame.SetAudio>(RemoteFrames.ParseControl(RemoteFrames.SetAudio(true))).On);
        Assert.False(Assert.IsType<ControlFrame.SetAudio>(RemoteFrames.ParseControl(RemoteFrames.SetAudio(false))).On);
        Assert.Equal(0x01020304u, Assert.IsType<ControlFrame.Pong>(RemoteFrames.ParseControl(RemoteFrames.Pong(0x01020304))).Sequence);
        Assert.Equal([0x1C, 4, 3, 2, 1], RemoteFrames.Pong(0x01020304));

        Assert.Null(RemoteFrames.ParseControl([]));
        Assert.Null(RemoteFrames.ParseControl([(byte)RemoteFrameType.MouseMove, 1, 2])); // 太短
        Assert.Null(RemoteFrames.ParseControl([(byte)RemoteFrameType.MouseButton, 9, 1])); // 没有这个键
        Assert.Null(RemoteFrames.ParseControl([(byte)RemoteFrameType.SpecialKey, 99]));
        Assert.Null(RemoteFrames.ParseControl([(byte)RemoteFrameType.VideoFrame, 0, 0, 0, 0, 1])); // 方向不对
        Assert.Null(RemoteFrames.ParseControl([(byte)RemoteFrameType.Pong, 1, 2])); // 太短
        Assert.Null(RemoteFrames.ParseControl(RemoteFrames.Ping(1))); // 探测包是被控端发的
    }

    [Fact]
    public void Agent_frames_have_the_documented_layout()
    {
        // 最后一个字节：支持的可选功能（0.7.0 加的；老网页只读前 8 个字节）
        Assert.Equal([0x01, 1, 0x80, 0x07, 0x38, 0x04, 20, 1, 0], RemoteFrames.VideoConfig(new VideoConfigFrame(RemoteVideoCodec.H264, 1920, 1080, 20, 1)));
        Assert.Equal(3, RemoteFrames.VideoConfig(new VideoConfigFrame(RemoteVideoCodec.H264, 1920, 1080, 20, 1, RemoteFeatures.Audio | RemoteFeatures.AdaptiveBitrate))[8]);
        // 位 3：认得 SetVideo（0.8.6 起）
        Assert.Equal(11, RemoteFrames.VideoConfig(new VideoConfigFrame(RemoteVideoCodec.H264, 1920, 1080, 30, 1, RemoteFeatures.Audio | RemoteFeatures.AdaptiveBitrate | RemoteFeatures.VideoSettings))[8]);
        var frame = RemoteFrames.VideoFrame(0x01020304, true, [9, 9]);
        Assert.Equal([0x02, 4, 3, 2, 1, 1, 9, 9], frame);
        Assert.Equal([0x04, 1, 0, 0x00, 0x05, 0xD0, 0x02, 1], RemoteFrames.Monitors([new MonitorInfo(0, 1280, 720, true)]));
        // 第 7–10 字节：目标码率、档位上限（0.7.0 加的）；再后面 5 个：每帧采集、编码用时（0.1 ms）和标志（0.8.6 加的，老网页只读前 11 个）
        Assert.Equal([0x06, 20, 0xE8, 0x03, 100, 85, 0, 0x58, 0x02, 0xC4, 0x09, 0, 0, 0, 0, 0], RemoteFrames.Stats(new StatsFrame(20, 1000, 140, 85, 600, 2500)));
        Assert.Equal([0x06, 20, 0xE8, 0x03, 100, 85, 0, 0x58, 0x02, 0x40, 0x1F, 0x7B, 0, 0xC8, 0x01, 6],
            RemoteFrames.Stats(new StatsFrame(20, 1000, 140, 85, 600, 8000, 123, 456, RemoteStatsFlags.LoweredForCpu | RemoteStatsFlags.CpuBound)));
        // 超出范围的数夹住，不回绕
        Assert.Equal([0, 0, 0xFF, 0xFF], RemoteFrames.Stats(new StatsFrame(0, 0, 0, 0, CaptureMs10: -5, EncodeMs10: 70_000))[11..15]);
        Assert.Equal([0x08, 4, 3, 2, 1, 1, 2, 7, 7], RemoteFrames.Audio(0x01020304, RemoteAudioCodec.Opus, 2, [7, 7]));
        Assert.Equal([0x09, 0x2A, 0, 0, 0], RemoteFrames.Ping(42));
        Assert.Equal([0x07, 1, (byte)'x'], RemoteFrames.Status(RemoteStatusCode.SecureDesktop, "x"));
        // 剪贴板超长：截断在字符边界上
        var huge = RemoteFrames.Clipboard(new string('好', RemoteFrames.MaxTextBytes));
        Assert.True(huge.Length - 1 <= RemoteFrames.MaxTextBytes);
        Assert.Equal(0, (huge.Length - 1) % 3);
    }

    [Fact]
    public void Only_input_frames_are_blocked_in_view_mode()
    {
        Assert.True(RemoteFrames.IsInput(RemoteFrameType.Key));
        Assert.True(RemoteFrames.IsInput(RemoteFrameType.Clipboard));
        Assert.True(RemoteFrames.IsInput(RemoteFrameType.Text));
        Assert.False(RemoteFrames.IsInput(RemoteFrameType.RequestKeyFrame));
        Assert.False(RemoteFrames.IsInput(RemoteFrameType.SetQuality));
        Assert.False(RemoteFrames.IsInput(RemoteFrameType.FrameAck));
        Assert.False(RemoteFrames.IsInput(RemoteFrameType.SelectMonitor));
        // 只看模式下也可以听声音、也要回探测包
        Assert.False(RemoteFrames.IsInput(RemoteFrameType.SetAudio));
        Assert.False(RemoteFrames.IsInput(RemoteFrameType.Pong));
        // 只看模式下也可以换线路、也要报接收情况
        Assert.False(RemoteFrames.IsInput(RemoteFrameType.SetTransport));
        Assert.False(RemoteFrames.IsInput(RemoteFrameType.ReceiverReport));
        // 只看模式下控制端也会跟不上
        Assert.False(RemoteFrames.IsInput(RemoteFrameType.MaxFps));
        // 只看模式下也可以换画质（中继只按 IsInput 过滤，照样转给被控端）
        Assert.False(RemoteFrames.IsInput(RemoteFrameType.SetVideo));
    }

    [Fact]
    public void SetVideo_carries_the_quality_menu()
    {
        // 档位、最大宽高（u16）、帧率（u8）、码率上限（u16 kbps）
        var bytes = RemoteFrames.SetVideo(RemoteVideoMode.Custom, 1280, 800, 15, 2000);
        Assert.Equal([0x20, 4, 0x00, 0x05, 0x20, 0x03, 15, 0xD0, 0x07], bytes);
        Assert.Equal(new ControlFrame.SetVideo(RemoteVideoMode.Custom, 1280, 800, 15, 2000), RemoteFrames.ParseControl(bytes));
        // 原始分辨率是 0×0，帧率、码率 0 = 这一档默认的（被控端再按档位补上）
        Assert.Equal(new ControlFrame.SetVideo(RemoteVideoMode.Original, 0, 0, 0, 0), RemoteFrames.ParseControl(RemoteFrames.SetVideo(RemoteVideoMode.Original, 0, 0, 0, 0)));
        Assert.Equal(new ControlFrame.SetVideo(RemoteVideoMode.Auto, 1920, 1200, 30, 8000), RemoteFrames.ParseControl([0x20, 0, 0x80, 0x07, 0xB0, 0x04, 30, 0x40, 0x1F]));
        // 超出范围的数夹住，不回绕
        Assert.Equal(new ControlFrame.SetVideo(RemoteVideoMode.Clear, 65535, 0, 255, 65535), RemoteFrames.ParseControl(RemoteFrames.SetVideo(RemoteVideoMode.Clear, 99_999, -1, 999, 99_999)));

        Assert.Null(RemoteFrames.ParseControl([0x20, 5, 0x00, 0x05, 0x20, 0x03, 15, 0xD0, 0x07])); // 没有这一档
        Assert.Null(RemoteFrames.ParseControl(bytes.AsSpan(0, 8))); // 太短
        Assert.Null(RemoteFrames.ParseControl([0x20]));
    }

    [Fact]
    public void MaxFps_tells_the_agent_how_many_frames_per_second_the_controller_can_take()
    {
        Assert.Equal([0x1F, 21], RemoteFrames.MaxFps(21));
        Assert.Equal(new ControlFrame.MaxFps(21), RemoteFrames.ParseControl(RemoteFrames.MaxFps(21)));
        // 0 是"不限了"
        Assert.Equal(new ControlFrame.MaxFps(0), RemoteFrames.ParseControl(RemoteFrames.MaxFps(0)));
        // 超出范围的数夹住，不回绕
        Assert.Equal([0x1F, 255], RemoteFrames.MaxFps(999));
        Assert.Equal([0x1F, 0], RemoteFrames.MaxFps(-3));
        Assert.Null(RemoteFrames.ParseControl([(byte)RemoteFrameType.MaxFps])); // 太短
    }

    [Fact]
    public void Transport_frames_have_the_documented_layout()
    {
        // 被控端 → 控制端：状态、第几次发布、说明
        Assert.Equal([0x0A, 1, 3], RemoteFrames.Transport(RemoteTransportState.Realtime, 3));
        Assert.Equal([0x0A, 2, 0, (byte)'x'], RemoteFrames.Transport(RemoteTransportState.RealtimeFailed, 0, "x"));
        Assert.Equal(7, RemoteFrames.Transport(RemoteTransportState.Realtime, 263)[2]); // 只有一个字节，转一圈回来

        // 控制端 → 被控端
        Assert.Equal([0x1D, 1], RemoteFrames.SetTransport(RemoteTransport.Realtime));
        Assert.Equal(RemoteTransport.Realtime, Assert.IsType<ControlFrame.SetTransport>(RemoteFrames.ParseControl(RemoteFrames.SetTransport(RemoteTransport.Realtime))).Transport);
        Assert.Equal(RemoteTransport.Relay, Assert.IsType<ControlFrame.SetTransport>(RemoteFrames.ParseControl(RemoteFrames.SetTransport(RemoteTransport.Relay))).Transport);
        Assert.Null(RemoteFrames.ParseControl([(byte)RemoteFrameType.SetTransport])); // 太短
        Assert.Null(RemoteFrames.ParseControl([(byte)RemoteFrameType.SetTransport, 9])); // 没有这条线路

        var bytes = RemoteFrames.ReceiverReport(25, 1800, 40, 17, 12);
        Assert.Equal([0x1E, 25, 0, 0x08, 0x07, 40, 0, 17, 12, 0], bytes);
        Assert.Equal(new ControlFrame.ReceiverReport(25, 1800, 40, 17, 12), RemoteFrames.ParseControl(bytes));
        // 超出范围的数夹住，不回绕
        Assert.Equal(new ControlFrame.ReceiverReport(1000, 65535, 65535, 255, 65535), RemoteFrames.ParseControl(RemoteFrames.ReceiverReport(5000, 99999, 99999, 999, 99999)));
        Assert.Null(RemoteFrames.ParseControl(bytes.AsSpan(0, 9))); // 太短
        Assert.Null(RemoteFrames.ParseControl(RemoteFrames.Transport(RemoteTransportState.Realtime, 1))); // 这是被控端发的
    }
}

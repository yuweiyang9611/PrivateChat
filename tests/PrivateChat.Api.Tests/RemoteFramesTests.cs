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

        Assert.Null(RemoteFrames.ParseControl([]));
        Assert.Null(RemoteFrames.ParseControl([(byte)RemoteFrameType.MouseMove, 1, 2])); // 太短
        Assert.Null(RemoteFrames.ParseControl([(byte)RemoteFrameType.MouseButton, 9, 1])); // 没有这个键
        Assert.Null(RemoteFrames.ParseControl([(byte)RemoteFrameType.SpecialKey, 99]));
        Assert.Null(RemoteFrames.ParseControl([(byte)RemoteFrameType.VideoFrame, 0, 0, 0, 0, 1])); // 方向不对
    }

    [Fact]
    public void Agent_frames_have_the_documented_layout()
    {
        Assert.Equal([0x01, 1, 0x80, 0x07, 0x38, 0x04, 20, 1], RemoteFrames.VideoConfig(new VideoConfigFrame(RemoteVideoCodec.H264, 1920, 1080, 20, 1)));
        var frame = RemoteFrames.VideoFrame(0x01020304, true, [9, 9]);
        Assert.Equal([0x02, 4, 3, 2, 1, 1, 9, 9], frame);
        Assert.Equal([0x04, 1, 0, 0x00, 0x05, 0xD0, 0x02, 1], RemoteFrames.Monitors([new MonitorInfo(0, 1280, 720, true)]));
        Assert.Equal([0x06, 20, 0xE8, 0x03, 100, 85, 0], RemoteFrames.Stats(new StatsFrame(20, 1000, 140, 85)));
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
    }
}

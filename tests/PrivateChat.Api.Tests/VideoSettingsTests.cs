using PrivateChat.Agent.Session;
using PrivateChat.Contracts.Remote;

namespace PrivateChat.Api.Tests;

/// <summary>画质设置：档位表、网页发来的数怎么夹、老网页的两档怎么对应、按屏幕算编多大。</summary>
public class VideoSettingsTests
{
    private static VideoSettings From(RemoteVideoMode mode, int maxWidth, int maxHeight, int fps, int maxKbps)
        => VideoSettings.From(new ControlFrame.SetVideo(mode, maxWidth, maxHeight, fps, maxKbps));

    [Fact]
    public void Presets_match_the_menu()
    {
        Assert.Equal(new VideoSettings(RemoteVideoMode.Auto, 1920, 1200, 30, 8000, 85), VideoSettings.Auto);
        Assert.Equal(new VideoSettings(RemoteVideoMode.Smooth, 1280, 800, 30, 4000, 70), VideoSettings.Smooth);
        Assert.Equal(new VideoSettings(RemoteVideoMode.Clear, 1920, 1200, 30, 8000, 85), VideoSettings.Clear);
        Assert.Equal(new VideoSettings(RemoteVideoMode.Original, 0, 0, 30, 16_000, 90), VideoSettings.Original);
        Assert.True(VideoSettings.Auto.AutoResolution);
        Assert.False(VideoSettings.Clear.AutoResolution);
        Assert.True(VideoSettings.Original.Native);
        Assert.False(VideoSettings.Clear.Native);
        Assert.Equal(["自动", "流畅", "清晰", "原画", "自定义"], Enum.GetValues<RemoteVideoMode>().Select(m => From(m, 0, 0, 0, 0).Name));
    }

    [Fact]
    public void The_presets_the_page_sends_come_out_the_same()
    {
        // 网页是数的来源：它按自己的档位表发，这边得到的和这边的表一样
        Assert.Equal(VideoSettings.Auto, VideoSettings.From(Parse(RemoteFrames.SetVideo(RemoteVideoMode.Auto, 1920, 1200, 30, 8000))));
        Assert.Equal(VideoSettings.Smooth, VideoSettings.From(Parse(RemoteFrames.SetVideo(RemoteVideoMode.Smooth, 1280, 800, 30, 4000))));
        Assert.Equal(VideoSettings.Clear, VideoSettings.From(Parse(RemoteFrames.SetVideo(RemoteVideoMode.Clear, 1920, 1200, 30, 8000))));
        Assert.Equal(VideoSettings.Original, VideoSettings.From(Parse(RemoteFrames.SetVideo(RemoteVideoMode.Original, 0, 0, 30, 16_000))));
        // 帧率、码率给 0 也一样
        Assert.Equal(VideoSettings.Original, VideoSettings.From(Parse(RemoteFrames.SetVideo(RemoteVideoMode.Original, 0, 0, 0, 0))));

        static ControlFrame.SetVideo Parse(byte[] bytes) => Assert.IsType<ControlFrame.SetVideo>(RemoteFrames.ParseControl(bytes));
    }

    [Fact]
    public void Custom_values_are_used_as_sent_within_limits()
    {
        Assert.Equal(new VideoSettings(RemoteVideoMode.Custom, 1280, 800, 15, 2000, 85), From(RemoteVideoMode.Custom, 1280, 800, 15, 2000));
        // 宽高有一个是 0 就是原始分辨率
        var native = From(RemoteVideoMode.Custom, 0, 1200, 30, 8000);
        Assert.Equal((0, 0, true), (native.MaxWidth, native.MaxHeight, native.Native));
        // 太小、太大的夹住
        var small = From(RemoteVideoMode.Custom, 100, 50, 30, 8000);
        Assert.Equal((320, 240), (small.MaxWidth, small.MaxHeight));
        var big = From(RemoteVideoMode.Custom, 8000, 5000, 30, 8000);
        Assert.Equal((3840, 2400), (big.MaxWidth, big.MaxHeight));
        // 帧率：0 = 30，最高 30（不开 60）
        Assert.Equal(30, From(RemoteVideoMode.Custom, 1920, 1200, 0, 8000).Fps);
        Assert.Equal(30, From(RemoteVideoMode.Custom, 1920, 1200, 60, 8000).Fps);
        Assert.Equal(5, From(RemoteVideoMode.Custom, 1920, 1200, 5, 8000).Fps);
        Assert.Equal(1, From(RemoteVideoMode.Custom, 1920, 1200, -3, 8000).Fps);
        // 码率上限：500–20000 kbps
        Assert.Equal(500, From(RemoteVideoMode.Custom, 1920, 1200, 30, 100).MaxKbps);
        Assert.Equal(20_000, From(RemoteVideoMode.Custom, 1920, 1200, 30, 50_000).MaxKbps);
        Assert.Equal(12_000, From(RemoteVideoMode.Custom, 1920, 1200, 30, 12_000).MaxKbps);
    }

    [Fact]
    public void Zero_bitrate_means_the_default_of_the_mode_and_the_ceiling_follows_the_mode()
    {
        Assert.Equal([8000, 4000, 8000, 16_000, 8000],
            Enum.GetValues<RemoteVideoMode>().Select(m => From(m, 1920, 1200, 30, 0).MaxKbps));
        Assert.Equal([85, 70, 85, 90, 85],
            Enum.GetValues<RemoteVideoMode>().Select(m => From(m, 1920, 1200, 30, 8000).Ceiling));
    }

    [Fact]
    public void Old_pages_get_the_nearest_preset()
    {
        Assert.Same(VideoSettings.Smooth, VideoSettings.FromLegacy(RemoteQualityPreset.Smooth));
        Assert.Same(VideoSettings.Clear, VideoSettings.FromLegacy(RemoteQualityPreset.Sharp));
    }

    [Fact]
    public void Box_fits_the_screen_without_upscaling()
    {
        Assert.Equal((1280, 720), VideoSettings.Smooth.Box(1920, 1080));
        Assert.Equal((1920, 1080), VideoSettings.Clear.Box(1920, 1080));
        Assert.Equal((1920, 1200), VideoSettings.Clear.Box(2560, 1600));
        // 不放大
        Assert.Equal((1280, 720), VideoSettings.Clear.Box(1280, 720));
        Assert.Equal((960, 540), From(RemoteVideoMode.Custom, 960, 600, 30, 8000).Box(1280, 720));
        // 原画：原始分辨率，但最大 3840×2400
        Assert.Equal((2560, 1440), VideoSettings.Original.Box(2560, 1440));
        Assert.Equal((3840, 2400), VideoSettings.Original.Box(3840, 2400));
        Assert.Equal((3840, 2160), VideoSettings.Original.Box(5120, 2880));
    }
}

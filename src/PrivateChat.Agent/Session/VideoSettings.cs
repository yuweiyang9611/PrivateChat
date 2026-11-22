using PrivateChat.Agent.Video;
using PrivateChat.Contracts.Remote;

namespace PrivateChat.Agent.Session;

/// <summary>
/// 画质设置：控制端"画质"菜单选的（SetVideo），或者老网页的两档（SetQuality）。
/// - 分辨率：等比缩到不超过 MaxWidth × MaxHeight（不放大）；两个都是 0 = 原始分辨率，但最大 3840×2400。
///   自动档的分辨率不固定，由 <see cref="Session.AutoResolution"/> 在这个框和更小的两级之间随网速、电脑性能调；其余档位固定。
/// - MaxKbps 是码率的天花板：网络不好时实际码率照样往下调，只是不会超过它。
/// - Ceiling：画面停下来以后补清晰补到的质量（<see cref="QualityPlanner"/>）。
/// 数以网页发来的为准（只夹到能接受的范围）；这里的档位表只用作默认值和老网页的 SetQuality。
/// </summary>
public sealed record VideoSettings(RemoteVideoMode Mode, int MaxWidth, int MaxHeight, int Fps, int MaxKbps, int Ceiling)
{
    /// <summary>帧率上限（全项目统一 30，不开 60）。</summary>
    public const int MaxFps = 30;
    /// <summary>原始分辨率也不超过这么大：再大编码器吃不消，码率也不够。</summary>
    public const int NativeMaxWidth = 3840, NativeMaxHeight = 2400;
    private const int MinWidth = 320, MinHeight = 240;
    private const int MinKbps = 500, MaxKbpsLimit = 20_000;

    /// <summary>默认（控制端什么都没说时）：框和清晰一样，分辨率随网速和电脑性能自动降。</summary>
    public static readonly VideoSettings Auto = new(RemoteVideoMode.Auto, 1920, 1200, MaxFps, 8000, 85);
    public static readonly VideoSettings Smooth = new(RemoteVideoMode.Smooth, 1280, 800, MaxFps, 4000, 70);
    public static readonly VideoSettings Clear = new(RemoteVideoMode.Clear, 1920, 1200, MaxFps, 8000, 85);
    public static readonly VideoSettings Original = new(RemoteVideoMode.Original, 0, 0, MaxFps, 16_000, 90);

    /// <summary>原始分辨率（最大 3840×2400）。</summary>
    public bool Native => MaxWidth == 0 || MaxHeight == 0;

    /// <summary>分辨率由 <see cref="Session.AutoResolution"/> 定（只有自动档）。</summary>
    public bool AutoResolution => Mode == RemoteVideoMode.Auto;

    /// <summary>日志里的名字。</summary>
    public string Name => Mode switch
    {
        RemoteVideoMode.Auto => "自动",
        RemoteVideoMode.Smooth => "流畅",
        RemoteVideoMode.Clear => "清晰",
        RemoteVideoMode.Original => "原画",
        _ => "自定义",
    };

    /// <summary>日志里的说明：分辨率、帧率、码率上限。</summary>
    public string Summary => $"{(Native ? "原始分辨率" : $"最大 {MaxWidth}x{MaxHeight}")}，{Fps} fps，码率上限 {MaxKbps} kbps"
        + (AutoResolution ? "（分辨率随网速和电脑性能自动调）" : "");

    /// <summary>控制端发来的设置，夹到能接受的范围：宽高有一个是 0 就是原始分辨率；帧率 0 = 30；码率 0 = 这一档默认的。</summary>
    public static VideoSettings From(ControlFrame.SetVideo f)
    {
        var native = f.MaxWidth == 0 || f.MaxHeight == 0;
        var width = native ? 0 : Math.Clamp(f.MaxWidth, MinWidth, NativeMaxWidth);
        var height = native ? 0 : Math.Clamp(f.MaxHeight, MinHeight, NativeMaxHeight);
        var fps = f.Fps == 0 ? MaxFps : Math.Clamp(f.Fps, 1, MaxFps);
        var kbps = f.MaxKbps == 0 ? DefaultKbps(f.Mode) : Math.Clamp(f.MaxKbps, MinKbps, MaxKbpsLimit);
        var ceiling = f.Mode switch { RemoteVideoMode.Smooth => Smooth.Ceiling, RemoteVideoMode.Original => Original.Ceiling, _ => Clear.Ceiling };
        return new VideoSettings(f.Mode, width, height, fps, kbps, ceiling);
    }

    /// <summary>老网页的两档：流畅 → 流畅档，清晰 → 清晰档。</summary>
    public static VideoSettings FromLegacy(RemoteQualityPreset preset) => preset == RemoteQualityPreset.Sharp ? Clear : Smooth;

    /// <summary>这块屏幕按这个设置编多大（等比缩、不放大、宽高取偶数）。</summary>
    public (int Width, int Height) Box(int screenWidth, int screenHeight) => Native
        ? FrameGeometry.Fit(screenWidth, screenHeight, NativeMaxWidth, NativeMaxHeight)
        : FrameGeometry.Fit(screenWidth, screenHeight, MaxWidth, MaxHeight);

    private static int DefaultKbps(RemoteVideoMode mode) => mode switch
    {
        RemoteVideoMode.Smooth => Smooth.MaxKbps,
        RemoteVideoMode.Original => Original.MaxKbps,
        _ => Clear.MaxKbps, // 自动、清晰、自定义（自定义第一次打开时就是 8 Mbps）
    };
}

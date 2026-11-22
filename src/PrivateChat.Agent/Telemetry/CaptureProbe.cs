using System.Diagnostics;
using PrivateChat.Agent.Session;
using PrivateChat.Agent.Video;
using PrivateChat.Contracts.Remote;

namespace PrivateChat.Agent.Telemetry;

/// <summary>
/// 截屏能力（只在探测子进程里跑）：
/// - 桌面复制在每块显示器上能不能用：用的就是远程时的 <see cref="DxgiScreenSource"/>，整条流程都走一遍——在这块显示器所在的那张卡上
///   建 D3D11 设备和视频处理器、DuplicateOutput1、取画面、显卡上缩放转 NV12、读回、画指针（指针正好在这块显示器上时）。
///   按原尺寸、缩到 1280×720、缩到三分之一（缩小超过 2 倍时显卡上分几步缩，见 <see cref="VideoConverter"/>）各截 10 帧，
///   每帧尺寸都对、没出异常、确实拿到过桌面画面、亮度在有限范围里（颜色范围设错了会一眼看出来）；
///   再把留着的桌面画面按另外三种方向各转一次（原尺寸、三分之一，见 <see cref="Orientations"/>：远程中途显示器转了方向就会用到显卡的旋转），
///   都没问题才算能用（Ok），远程时只有 Ok 的显示器才用桌面复制（见 <see cref="ScreenCaptures"/>）。FirstFrameMs：从建源到拿到第一帧（含建设备）。
///   新的显卡代码要是把进程带崩，崩的是这个子进程，Agent 照常用 GDI。
/// - GDI（桌面复制用不了时远程用的）：主显示器按原尺寸截 10 次（BitBlt）、缩到 1280×720 截 10 次（StretchBlt HALFTONE），各取平均。
///   用的就是远程时的 <see cref="GdiScreenSource"/>（含画鼠标、拷出像素）。
/// 不保存、不上报任何画面内容。
/// </summary>
internal static class CaptureProbe
{
    private const int DuplicationRounds = 10;
    private const int GdiRounds = 10;
    /// <summary>亮度明显超出有限范围的像素最多占这么多（缩放时边缘会冲出去一点点）。</summary>
    private const double MaxOutOfRange = 0.005;
    /// <summary>
    /// 别的方向转出来的平均亮度和现在这个方向的最多差这么多（同一幅画面，只是转了、缩了；Intel UHD、RTX 4060 上用真桌面实测：
    /// 原尺寸差 0.00，缩到三分之一最多差 0.5；转成全黑会差上百）。
    /// </summary>
    internal const double MaxMeanDrift = 4;

    /// <summary>每块接在桌面上的显示器试一次桌面复制；stage 报告正在试哪一块（探测超时的时候知道停在哪）。</summary>
    public static void Duplication(List<TelemetryDuplication> results, Action<string> stage)
    {
        foreach (var output in DxgiScreenSource.Outputs())
        {
            stage($"桌面复制 {output.DeviceName}");
            results.Add(TryDuplicate(output.DeviceName, output.Adapter));
        }
    }

    private static TelemetryDuplication TryDuplicate(string name, string adapterName)
    {
        TelemetryDuplication Fail(string error) => new(name, adapterName, false, null, HResults.Clip(error));

        try
        {
            var clock = Stopwatch.StartNew();
            using var source = new DxgiScreenSource();
            var screen = source.Screens().FirstOrDefault(s => string.Equals(s.DeviceName, name, StringComparison.OrdinalIgnoreCase));
            if (screen is null) return Fail("Screen.AllScreens 里没有这块显示器");
            var b = screen.Bounds;
            double? firstMs = null;
            var outOfRange = 0.0;
            var frames = 0;
            foreach (var (width, height) in new[] { FrameGeometry.Fit(b.Width, b.Height, b.Width, b.Height), FrameGeometry.Fit(b.Width, b.Height, 1280, 720), Third(b.Width, b.Height) })
            {
                for (var i = 0; i < DuplicationRounds; i++, frames++)
                {
                    var frame = source.Capture(screen, width, height);
                    firstMs ??= clock.Elapsed.TotalMilliseconds;
                    if (source.Problem is { } problem) return Fail(problem);
                    if (frame.Format != FrameFormat.Nv12 || frame.Width != width || frame.Height != height || frame.Data.Length < width * height * 3 / 2)
                        return Fail($"第 {frames + 1} 帧不对：要 {width}x{height} NV12，拿到 {frame.Width}x{frame.Height} {frame.Format}");
                    if (i == DuplicationRounds - 1 && source.Images > 0) outOfRange = Math.Max(outOfRange, OutOfRange(frame.Data.AsSpan(0, width * height)));
                    Thread.Sleep(15);
                }
            }
            if (source.Images == 0) return Fail($"建好了复制，{frames} 次都没拿到桌面画面");
            // 有限范围的亮度在 16–235；缩放时边缘会冲出去一点点。大片超出说明显卡按全范围输出了，和编码器声明的对不上（颜色会发灰 / 过曝）
            if (outOfRange > MaxOutOfRange) return Fail($"显卡转出来的 NV12 亮度不在有限范围里（{outOfRange:P1} 的像素超出 12–240）");
            if (Orientations(source) is { } turned) return Fail(turned);
            var (frameMs, frameP90, frameSize) = FrameCost(source);
            return new TelemetryDuplication(name, adapterName, true, Math.Round(firstMs ?? 0, 2), null, frameMs, frameP90, frameSize);
        }
        catch (Exception ex)
        {
            return Fail(HResults.Explain(ex));
        }
    }

    private const int FrameCostRounds = 20;

    /// <summary>
    /// 画面在动时每来一幅新画面要多久：把留着的桌面画面按现在的方向、自动档最大的尺寸（1920×1200 以内）连转 20 次 NV12、读回内存（第一次要建视频处理器，不算），
    /// 取平均和 p90。屏幕静止时截屏几乎不花时间，所以不靠"真等新画面"——这样不管探测时桌面动不动，量出来的都是一样的东西。
    /// 不包括取画面（AcquireNextFrame，零点几毫秒）和画指针。量不出来时三项都是 null（不影响"能用"）。
    /// </summary>
    private static (double? Avg, double? P90, string? Size) FrameCost(DxgiScreenSource source)
    {
        try
        {
            var (textureWidth, textureHeight) = source.DesktopSize;
            if (textureWidth <= 0 || textureHeight <= 0) return (null, null, null);
            var rotation = source.Rotation is >= 2 and <= 4 ? source.Rotation : 1;
            var (viewWidth, viewHeight) = CaptureTiles.Rotated(rotation, textureWidth, textureHeight);
            var (width, height) = FrameGeometry.Fit(viewWidth, viewHeight, VideoSettings.Auto.MaxWidth, VideoSettings.Auto.MaxHeight);
            var nv12 = new byte[width * height * 3 / 2];
            if (!source.ConvertRetained(rotation, width, height, nv12)) return (null, null, null);
            var times = new double[FrameCostRounds];
            for (var i = 0; i < times.Length; i++)
            {
                var clock = Stopwatch.StartNew();
                source.ConvertRetained(rotation, width, height, nv12);
                times[i] = clock.Elapsed.TotalMilliseconds;
            }
            Array.Sort(times);
            return (Math.Round(times.Average(), 2), Math.Round(times[(int)Math.Ceiling(times.Length * 0.9) - 1], 2), $"{width}x{height}");
        }
        catch (Exception)
        {
            return (null, null, null);
        }
    }

    /// <summary>缩到三分之一（保持比例）：缩小超过 2 倍，显卡上要分几步缩（<see cref="CaptureTiles.Passes"/>）。</summary>
    private static (int Width, int Height) Third(int width, int height) => FrameGeometry.Fit(width, height, Math.Max(2, width / 3), Math.Max(2, height / 3));

    /// <summary>
    /// 显示器现在这个方向之外的三种方向，也先在探测子进程里走一遍：远程中途显示器转了方向（竖着放、二合一自动旋转），会话里就会用到
    /// 显卡的旋转（GetVideoProcessorCaps、VideoProcessorSetStreamRotation、转着方向的 VideoProcessorBlt），探测要是没跑过，驱动出问题就是 Agent 进程崩。
    /// 把留着的桌面画面（和远程时同一个 <see cref="VideoConverter"/>）按每种方向转成宽高对调（90°、270°）的 NV12，原尺寸、三分之一（分几步缩）各一次：
    /// 亮度要在有限范围里，平均亮度要和现在这个方向原尺寸转出来的差不多（转成全黑、花屏能看出来）。有一种不行就返回原因（这块显示器不算验证过）。
    /// </summary>
    private static string? Orientations(DxgiScreenSource source)
    {
        var (textureWidth, textureHeight) = source.DesktopSize;
        if (textureWidth <= 0 || textureHeight <= 0) return "桌面画面没留下来，没法试别的方向";
        var current = source.Rotation is >= 2 and <= 4 ? source.Rotation : 1;
        var (viewWidth, viewHeight) = CaptureTiles.Rotated(current, textureWidth, textureHeight);
        var reference = Turn(source, current, FrameGeometry.Fit(viewWidth, viewHeight, viewWidth, viewHeight), out var problem);
        if (problem is not null) return problem;
        for (var rotation = 1; rotation <= 4; rotation++)
        {
            if (rotation == current) continue;
            var (width, height) = CaptureTiles.Rotated(rotation, textureWidth, textureHeight);
            foreach (var size in new[] { FrameGeometry.Fit(width, height, width, height), Third(width, height) })
            {
                var mean = Turn(source, rotation, size, out problem);
                if (problem is not null) return problem;
                if (Math.Abs(mean - reference) > MaxMeanDrift)
                    return $"显示器转 {Degrees(rotation)}° 时显卡转出来的 {size.Width}x{size.Height} 画面不对（平均亮度 {mean:F0}，现在的方向是 {reference:F0}）";
            }
        }
        return null;
    }

    /// <summary>留着的桌面画面按 rotation 转成 size 的 NV12，返回平均亮度；不对时 problem 是原因。</summary>
    private static double Turn(DxgiScreenSource source, int rotation, (int Width, int Height) size, out string? problem)
    {
        var (width, height) = size;
        var nv12 = new byte[width * height * 3 / 2];
        problem = null;
        try
        {
            if (!source.ConvertRetained(rotation, width, height, nv12))
            {
                problem = "桌面画面没留下来，没法试别的方向";
                return 0;
            }
        }
        catch (Exception ex)
        {
            problem = $"显示器转 {Degrees(rotation)}°、{width}x{height} 时：{HResults.Explain(ex)}";
            return 0;
        }
        var luma = nv12.AsSpan(0, width * height);
        var outside = OutOfRange(luma);
        if (outside > MaxOutOfRange)
        {
            problem = $"显示器转 {Degrees(rotation)}°、{width}x{height} 时显卡转出来的 NV12 亮度不在有限范围里（{outside:P1} 的像素超出 12–240）";
            return 0;
        }
        return Mean(luma);
    }

    /// <summary>DXGI_MODE_ROTATION（1 不转，2 / 3 / 4）→ 顺时针转了多少度。</summary>
    private static int Degrees(int rotation) => (rotation - 1) * 90;

    private static double Mean(ReadOnlySpan<byte> luma)
    {
        long sum = 0;
        foreach (var y in luma) sum += y;
        return luma.IsEmpty ? 0 : (double)sum / luma.Length;
    }

    /// <summary>Y 平面里明显超出有限范围（&lt; 12 或 &gt; 240）的像素占多少。</summary>
    private static double OutOfRange(ReadOnlySpan<byte> luma)
    {
        var count = 0;
        foreach (var y in luma)
            if (y is < 12 or > 240) count++;
        return luma.IsEmpty ? 0 : (double)count / luma.Length;
    }

    /// <summary>GDI 截主显示器：原尺寸一帧平均多少毫秒、缩到 1280×720（保持比例）一帧平均多少毫秒。</summary>
    public static (double Native, double Scaled) Gdi()
    {
        using var gdi = new GdiScreenSource();
        var screens = gdi.Screens();
        var screen = screens.FirstOrDefault(s => s.IsPrimary) ?? screens[0];
        var b = screen.Bounds;
        var native = Time(gdi, screen, b.Width, b.Height);
        var (w, h) = FrameGeometry.Fit(b.Width, b.Height, 1280, 720);
        var scaled = Time(gdi, screen, w, h);
        return (native, scaled);
    }

    private static double Time(GdiScreenSource gdi, ScreenInfo screen, int width, int height)
    {
        gdi.Capture(screen, width, height); // 第一次要建位图，不算
        var clock = Stopwatch.StartNew();
        for (var i = 0; i < GdiRounds; i++) gdi.Capture(screen, width, height);
        return Math.Round(clock.Elapsed.TotalMilliseconds / GdiRounds, 2);
    }
}

using System.Diagnostics;
using System.Runtime.InteropServices;
using PrivateChat.Agent.Video;
using PrivateChat.Contracts.Remote;

namespace PrivateChat.Agent.Telemetry;

/// <summary>
/// 截屏能力（只在探测子进程里跑）：
/// - 桌面复制（DXGI Desktop Duplication）在每块显示器上能不能用：在这块显示器所在的那张卡上建 D3D11 设备
///   （双显卡笔记本上必须是那张卡，否则 DuplicateOutput 失败）、DuplicateOutput、取一帧，记下成没成、错误码、多久拿到第一帧。
/// - GDI（现在远程用的）：主显示器按原尺寸截 10 次（BitBlt）、缩到 1280×720 截 10 次（StretchBlt HALFTONE），各取平均。
///   用的就是远程时的 <see cref="GdiScreenSource"/>（含画鼠标、拷出像素）。
/// 不保存、不上报任何画面内容。
/// </summary>
internal static class CaptureProbe
{
    private const int AcquireTimeoutMs = 500;
    private const int GdiRounds = 10;

    /// <summary>每块接在桌面上的显示器试一次桌面复制；stage 报告正在试哪一块（探测超时的时候知道停在哪）。</summary>
    public static void Duplication(List<TelemetryDuplication> results, Action<string> stage)
    {
        var factory = Dxgi.CreateFactory();
        try
        {
            for (var a = 0; a < 16; a++)
            {
                var hr = factory.EnumAdapters1(a, out var adapter);
                if (hr == Dxgi.ErrorNotFound) break;
                if (hr < 0) throw new COMException($"EnumAdapters1 失败：{HResults.Describe(hr)}", hr);
                try
                {
                    var adapterName = adapter.GetDesc1(out var desc) >= 0 ? desc.Description.Trim() : null;
                    for (var o = 0; o < 16 && adapter.EnumOutputs(o, out var output) >= 0; o++)
                    {
                        try
                        {
                            if (output.GetDesc(out var od) < 0 || od.AttachedToDesktop == 0) continue;
                            stage($"桌面复制 {od.DeviceName}");
                            results.Add(TryDuplicate(adapter, output, od.DeviceName, adapterName));
                        }
                        finally
                        {
                            Marshal.ReleaseComObject(output);
                        }
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(adapter);
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(factory);
        }
    }

    private static TelemetryDuplication TryDuplicate(IDXGIAdapter1 adapter, IDXGIOutput output, string name, string? adapterName)
    {
        TelemetryDuplication Fail(string what, int hr) => new(name, adapterName, false, null, $"{what}：{HResults.Describe(hr)}");

        IntPtr device = 0, context = 0;
        IDXGIOutputDuplication? duplication = null;
        var adapterPtr = Marshal.GetComInterfaceForObject<IDXGIAdapter1, IDXGIAdapter1>(adapter);
        try
        {
            var hr = Dxgi.D3D11CreateDevice(adapterPtr, Dxgi.DriverTypeUnknown, 0, Dxgi.CreateDeviceBgraSupport, 0, 0, Dxgi.D3D11SdkVersion,
                out device, out _, out context);
            if (hr < 0) return Fail("在这张显卡上建 D3D11 设备", hr);
            if (output is not IDXGIOutput1 output1) return Fail("显示器不支持 IDXGIOutput1", unchecked((int)0x80004002));
            var clock = Stopwatch.StartNew();
            hr = output1.DuplicateOutput(device, out duplication);
            if (hr < 0) return Fail("DuplicateOutput", hr);
            // 刚建好时第一帧（整个桌面）一般马上就有；没有就再等一次
            hr = duplication.AcquireNextFrame(AcquireTimeoutMs, out _, out var resource);
            if (hr == Dxgi.ErrorWaitTimeout) hr = duplication.AcquireNextFrame(AcquireTimeoutMs, out _, out resource);
            if (hr < 0) return Fail($"AcquireNextFrame（等了 {2 * AcquireTimeoutMs} ms）", hr);
            var ms = clock.Elapsed.TotalMilliseconds;
            if (resource != 0) Marshal.Release(resource);
            duplication.ReleaseFrame();
            return new TelemetryDuplication(name, adapterName, true, Math.Round(ms, 2), null);
        }
        finally
        {
            if (duplication is not null) Marshal.ReleaseComObject(duplication);
            if (context != 0) Marshal.Release(context);
            if (device != 0) Marshal.Release(device);
            Marshal.Release(adapterPtr);
        }
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

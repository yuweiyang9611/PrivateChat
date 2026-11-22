using System.Diagnostics;
using PrivateChat.Agent.Audio;
using PrivateChat.Agent.Video;
using PrivateChat.Contracts.Remote;

namespace PrivateChat.Agent;

/// <summary>
/// 本机自检：PrivateChat.Agent.exe --selftest [输出.h264] [--capture pattern|gdi|dxgi] [--encoder auto|hardware|software] [--size 宽x高] [--screen]
/// 采 60 帧（默认测试画面；--capture gdi / --screen 是 GDI 截真屏幕，--capture dxgi 是桌面复制——经远程时的那个工厂，
/// 桌面复制不能用时它自己退回 GDI，名字里写着为什么；--size 指定编码尺寸，默认主显示器缩到 1920×1080 以内）、
/// 按远程时的方式选编码器（--encoder，同 PRIVATECHAT_AGENT_ENCODER；
/// 自检不读探测结果，所以 auto 就是软件编码器）编出来，第 30 帧强制关键帧，打印用的是哪个编码器、哪种截屏、每帧大小和采集 / 转换 / 编码耗时，
/// 写出 Annex B 文件供 ffprobe 检查。硬件编码器中途出错时像远程时一样换软件编码器接着编（返回值记失败）。
/// 另外看两样这台电脑上说不准的东西（只打印，不影响返回值）：编码器能不能逐帧改质量、能不能采到电脑播放的声音。
/// 在单独的 MTA 线程上跑（Main 是 STA；远程时编码、截屏的线程是 MTA，硬件编码器的事件回调也要这样）。
/// </summary>
internal static class SelfTest
{
    /// <summary>和远程时的帧率上限一样。</summary>
    private const int SelfTestFps = 30;
    private const int Frames = 60;
    private const int KeyFrameAt = 30;
    private const int Kbps = 3000;

    public static int Run(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var code = 1;
        var thread = new Thread(() =>
        {
            try
            {
                code = RunOnThread(args);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"自检出错：{ex}");
                code = 1;
            }
        }) { Name = "selftest" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        thread.Join();
        return code;
    }

    private static string? Option(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static int RunOnThread(string[] args)
    {
        var output = args.SkipWhile(a => a != "--selftest").Skip(1).FirstOrDefault(a => !a.StartsWith("--")) ?? "selftest.h264";
        var capture = Option(args, "--capture")?.ToLowerInvariant() ?? (args.Contains("--screen") ? "gdi" : "pattern");
        if (Option(args, "--encoder") is { } encoderSwitch) Environment.SetEnvironmentVariable(VideoEncoders.SwitchVariable, encoderSwitch);
        Console.WriteLine($"编码器开关 {VideoEncoders.SwitchVariable}={Environment.GetEnvironmentVariable(VideoEncoders.SwitchVariable) ?? "（没设，auto）"}，截屏 --capture {capture}");
        ListHardwareEncoders();

        IFrameSource source;
        switch (capture)
        {
            case "dxgi":
                // 经远程时的工厂：dxgi = 探测没验证过也用桌面复制（出错退回 GDI，名字里写原因）
                Environment.SetEnvironmentVariable(ScreenCaptures.SwitchVariable, "dxgi");
                source = ScreenCaptures.Create(new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                break;
            case "gdi":
                source = new GdiScreenSource();
                break;
            case "pattern":
                source = new TestPatternSource();
                break;
            default:
                Console.WriteLine($"不认识的 --capture {capture}（pattern / gdi / dxgi）");
                return 2;
        }
        using var sourceScope = source;
        var screen = source.Screens().First(s => s.IsPrimary);
        var (width, height) = FrameGeometry.Fit(screen.Bounds.Width, screen.Bounds.Height, 1920, 1080);
        // --size 1366x768：指定编码尺寸（试宽不是 16 的倍数、或者老显卡编不了的大尺寸）；取偶数
        if (Option(args, "--size")?.Split('x', 'X') is [var sizeText, var heightText]
            && int.TryParse(sizeText, out var sizeWidth) && int.TryParse(heightText, out var sizeHeight) && sizeWidth >= 2 && sizeHeight >= 2)
            (width, height) = (sizeWidth & ~1, sizeHeight & ~1);
        // 和远程时一样先问这块显示器接在哪个厂商的卡上（同厂商的硬件编码器优先；桌面复制的来源这时也定了名字）
        var vendor = (source as IAdapterAwareSource)?.AdapterVendorId(screen) ?? 0;
        Console.WriteLine($"显示器 {screen.Bounds}（{screen.DeviceName ?? "-"}），编码 {width}x{height}，截屏 {source.Name}" +
                          (vendor != 0 ? $"（显卡厂商 0x{vendor:X4}）" : ""));
        var created = Stopwatch.StartNew();
        IVideoEncoder encoder = VideoEncoders.Create(width, height, SelfTestFps, Kbps, [], vendor, out var note);
        Console.WriteLine($"编码器 {encoder.Name}（{(encoder.QualityMode ? "逐帧定质量" : "固定码率")}{Mode(encoder)}，建起来用了 {created.Elapsed.TotalMilliseconds:F0} ms）" +
                          (note is null ? "" : $"；没用硬件的原因：{note}"));
        var failed = false;
        try
        {
            var nv12 = new byte[width * height * 3 / 2];
            using var file = File.Create(output);
            var total = Stopwatch.StartNew();
            double captureMs = 0, convertMs = 0, encodeMs = 0;
            long bytes = 0;
            var keys = new List<int>();
            var lastSourceName = source.Name;
            for (var i = 0; i < Frames; i++)
            {
                var sw = Stopwatch.StartNew();
                var frame = source.Capture(screen, width, height);
                var c = sw.Elapsed.TotalMilliseconds;
                if (source.Name != lastSourceName)
                {
                    lastSourceName = source.Name;
                    Console.WriteLine($"#{i}: 截屏换成了 {lastSourceName}");
                }
                sw.Restart();
                ReadOnlySpan<byte> input;
                if (frame.Format == FrameFormat.Nv12) input = frame.Data.AsSpan(0, width * height * 3 / 2);
                else
                {
                    Nv12.FromBgra(frame.Bgra, frame.Stride, frame.Width, frame.Height, nv12);
                    input = nv12;
                }
                var v = sw.Elapsed.TotalMilliseconds;
                sw.Restart();
                if (i == KeyFrameAt) encoder.RequestKeyFrame();
                EncodedFrame? encoded;
                try
                {
                    encoded = encoder.Encode(input, TimeSpan.FromMilliseconds(i * 1000.0 / SelfTestFps));
                }
                catch (EncoderFailedException ex)
                {
                    // 和远程时一样：记下来、本进程不再用它、换软件编码器从关键帧接着编
                    failed = true;
                    Console.WriteLine($"#{i}: 硬件编码器出错，换软件编码器：{ex.Message}");
                    VideoEncoders.MarkFailed(encoder.Name, ex.Message);
                    encoder.Dispose();
                    encoder = new H264Encoder(width, height, SelfTestFps, Kbps, quality: 30);
                    encoder.RequestKeyFrame();
                    encoded = encoder.Encode(input, TimeSpan.FromMilliseconds(i * 1000.0 / SelfTestFps));
                }
                var e = sw.Elapsed.TotalMilliseconds;
                captureMs += c;
                convertMs += v;
                encodeMs += e;
                var timing = $"采集 {c:F1} ms，转换 {v:F1} ms，编码 {e:F1} ms" + (frame.ChangedHint is { } hint ? $"，变化提示 {hint.Count(t => t)}/{hint.Length} 块" : "");
                if (encoded is not { } f)
                {
                    Console.WriteLine($"#{i}: 无输出；{timing}");
                    continue;
                }
                if (f.IsKey) keys.Add(i);
                bytes += f.Data.Length;
                var types = string.Join(",", H264.Split(f.Data).Select(n => n.Type(f.Data)));
                Console.WriteLine($"#{i}: {f.Data.Length} 字节 key={f.IsKey}{(f.IsKey || i < 2 ? $" NAL=[{types}] codec={H264.CodecString(f.Data)}" : "")}；{timing}");
                file.Write(f.Data);
            }
            Console.WriteLine($"关键帧：{string.Join(",", keys)}；共 {bytes / 1024} KB；采集 {captureMs / Frames:F1} ms/帧，转换 {convertMs / Frames:F1} ms/帧，编码 {encodeMs / Frames:F1} ms/帧；" +
                              $"总计 {total.ElapsedMilliseconds} ms；编码器 {encoder.Name}，截屏 {source.Name}");
            QualityMode(width, height, encoder);
            SoundWithin(TimeSpan.FromSeconds(5));
            return !failed && keys.Count >= 2 && keys[0] == 0 && keys.Contains(KeyFrameAt) ? 0 : 1;
        }
        finally
        {
            encoder.Dispose();
        }
    }

    private static string Mode(IVideoEncoder encoder) => encoder is HardwareH264Encoder hw
        ? $"，{hw.RateControl switch { HardwareRateControl.Quality => "按质量", HardwareRateControl.Qp => "逐帧 QP", _ => "固定码率" }}，厂商 0x{hw.VendorId:X4}，热身 {hw.WarmUpMs:F1} ms"
        : "";

    /// <summary>MFTEnumEx 列出来的硬件 H.264 编码器（名字、厂商）。</summary>
    private static void ListHardwareEncoders()
    {
        try
        {
            var all = Mft.H264Encoders(Mft.EnumHardware | Mft.EnumAsyncMft | Mft.EnumSortAndFilter);
            try
            {
                var hardware = all.Where(Mft.IsHardware).Select(a => $"{Mft.Name(a) ?? "（没有名字）"}（厂商 0x{Mft.VendorId(a):X4}）").ToList();
                Console.WriteLine(hardware.Count == 0 ? "硬件编码器：一个都没列出来" : $"硬件编码器：{string.Join("；", hardware)}");
            }
            finally
            {
                foreach (var a in all) System.Runtime.InteropServices.Marshal.ReleaseComObject(a);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"硬件编码器：列不出来（{ex.Message}）");
        }
    }

    /// <summary>
    /// 远程画面靠逐帧改质量来控制码率：同一幅画面，质量 20 的关键帧之后把质量调到 70，应该再出一个明显更大的"补清晰"帧。
    /// 画面是探测试编用的那种细碎的"文字"（纯色块调高质量也没什么可补的）。硬件编码器两种方式（按质量、逐帧 QP）各试一次
    /// （和探测一样：远程自动用的只是验证有效的那种），软件编码器试按质量。
    /// </summary>
    private static void QualityMode(int width, int height, IVideoEncoder used)
    {
        var pattern = new Telemetry.TestPattern(width, height);
        pattern.Render(0);
        if (used is not HardwareH264Encoder hw)
        {
            Refine("软件编码器", () => new H264Encoder(width, height, SelfTestFps, Kbps, quality: 20), pattern.Data);
            return;
        }
        foreach (var mode in new[] { HardwareRateControl.Quality, HardwareRateControl.Qp })
        {
            var label = mode == HardwareRateControl.Quality ? "按质量" : "逐帧 QP";
            List<IMFActivate> all;
            try
            {
                all = Mft.H264Encoders(Mft.EnumHardware | Mft.EnumAsyncMft | Mft.EnumSortAndFilter);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{label}：列不出编码器（{ex.Message}）");
                return;
            }
            try
            {
                if (all.FirstOrDefault(a => Mft.Name(a) == hw.FriendlyName) is not { } activate)
                {
                    Console.WriteLine($"{label}：列不出 {hw.FriendlyName} 了");
                    continue;
                }
                Refine($"{hw.Name}，{label}", () => new HardwareH264Encoder(activate, hw.FriendlyName, hw.VendorId, width, height, SelfTestFps, Kbps,
                    [mode], 20, ownsActivate: false), pattern.Data);
            }
            finally
            {
                foreach (var a in all) System.Runtime.InteropServices.Marshal.ReleaseComObject(a);
            }
        }
    }

    private static void Refine(string label, Func<IVideoEncoder> create, byte[] nv12)
    {
        try
        {
            using var encoder = create();
            if (!encoder.QualityMode)
            {
                Console.WriteLine($"逐帧改质量（{label}）：不接受（会退回固定码率，弱网下效果差一些）");
                return;
            }
            encoder.SetQuality(20);
            encoder.RequestKeyFrame();
            var key = encoder.Encode(nv12, TimeSpan.Zero)?.Data.Length;
            var same = encoder.Encode(nv12, TimeSpan.FromMilliseconds(1000.0 / SelfTestFps))?.Data.Length;
            encoder.SetQuality(70);
            var refined = encoder.Encode(nv12, TimeSpan.FromMilliseconds(2000.0 / SelfTestFps))?.Data.Length;
            Console.WriteLine($"逐帧改质量（{label}）：质量 20 的关键帧 {(key ?? 0) / 1024.0:F1} KB，画面不变再编一帧 {same?.ToString() ?? "无输出"} 字节，" +
                              $"质量调到 70 后 {(refined ?? 0) / 1024.0:F1} KB" +
                              (EncoderChoice.RefineWorks(same, refined) ? "（有效）" : "（没有反应：用这种方式码率控制会不准）"));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"逐帧改质量（{label}）：出错 {ex.Message}");
        }
    }

    /// <summary>
    /// <see cref="Sound"/>，最多等 limit：锁着屏时系统的声音接口可能一直不返回（以前在主线程上就这样卡住、自检不退出），
    /// 那就说一声接着往下（后台线程，进程退出时带走）。
    /// </summary>
    private static void SoundWithin(TimeSpan limit)
    {
        var thread = new Thread(Sound) { IsBackground = true, Name = "selftest-sound" };
        thread.Start();
        if (!thread.Join(limit)) Console.WriteLine($"声音：{limit.TotalSeconds:F0} 秒没采完（卡在系统的声音接口里；锁着屏时会这样）");
    }

    /// <summary>采半秒电脑正在播放的声音，编成 Opus。</summary>
    private static void Sound()
    {
        try
        {
            using var source = new WasapiLoopback();
            var pcm = new short[AudioFormat.SampleRate * AudioFormat.Channels];
            var points = 0;
            var peak = 0;
            var started = Stopwatch.StartNew();
            while (started.ElapsedMilliseconds < 500)
            {
                var got = source.Read(pcm.AsSpan(points * 2));
                peak = Math.Max(peak, AudioLeveler.Peak(pcm.AsSpan(points * 2, got * 2)));
                points += got;
                Thread.Sleep(10);
            }
            var packet = new byte[OpusPacketizer.MaxPacketBytes];
            var encoded = points >= AudioFormat.FrameSamples ? new OpusPacketizer(64000).Encode(pcm, packet) : 0;
            Console.WriteLine($"声音：{source.Description}；半秒采到 {points * 1000 / AudioFormat.SampleRate} ms，峰值 {peak}（0 = 这会儿没有声音在播）" +
                (encoded > 0 ? $"；Opus 一包 {encoded} 字节" : ""));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"声音：采不到（{ex.Message}）");
        }
    }
}

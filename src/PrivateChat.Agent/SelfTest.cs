using System.Diagnostics;
using PrivateChat.Agent.Audio;
using PrivateChat.Agent.Video;
using PrivateChat.Contracts.Remote;

namespace PrivateChat.Agent;

/// <summary>
/// 本机自检：PrivateChat.Agent.exe --selftest [输出.h264] [--screen]
/// 用测试画面（或真实屏幕）编 60 帧，第 30 帧强制关键帧，打印每帧大小、NAL 类型和耗时，写出 Annex B 文件供 ffprobe 检查。
/// 另外看两样这台电脑上说不准的东西（只打印，不影响返回值）：编码器接不接受"按质量"的模式、能不能采到电脑播放的声音。
/// </summary>
internal static class SelfTest
{
    public static int Run(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var output = args.SkipWhile(a => a != "--selftest").Skip(1).FirstOrDefault(a => !a.StartsWith("--")) ?? "selftest.h264";
        using IFrameSource source = args.Contains("--screen") ? new GdiScreenSource() : new TestPatternSource();
        var screen = source.Screens().First(s => s.IsPrimary);
        var (width, height) = FrameGeometry.Fit(screen.Bounds.Width, screen.Bounds.Height, 1920, 1080);
        Console.WriteLine($"显示器 {screen.Bounds}，编码 {width}x{height}");

        using var encoder = new H264Encoder(width, height, 20, 3000);
        var nv12 = new byte[width * height * 3 / 2];
        using var file = File.Create(output);
        var total = Stopwatch.StartNew();
        long captureMs = 0, convertMs = 0, encodeMs = 0, bytes = 0;
        var keys = new List<int>();
        for (var i = 0; i < 60; i++)
        {
            var sw = Stopwatch.StartNew();
            var frame = source.Capture(screen, width, height);
            captureMs += sw.ElapsedMilliseconds;
            sw.Restart();
            Nv12.FromBgra(frame.Bgra, frame.Stride, frame.Width, frame.Height, nv12);
            convertMs += sw.ElapsedMilliseconds;
            sw.Restart();
            if (i == 30) encoder.RequestKeyFrame();
            var encoded = encoder.Encode(nv12, TimeSpan.FromMilliseconds(i * 50));
            encodeMs += sw.ElapsedMilliseconds;
            if (encoded is not { } e)
            {
                Console.WriteLine($"#{i}: 无输出");
                continue;
            }
            if (e.IsKey) keys.Add(i);
            bytes += e.Data.Length;
            var types = string.Join(",", H264.Split(e.Data).Select(n => n.Type(e.Data)));
            if (i < 3 || e.IsKey || i % 20 == 0)
                Console.WriteLine($"#{i}: {e.Data.Length} 字节 key={e.IsKey} NAL=[{types}] codec={H264.CodecString(e.Data)}");
            file.Write(e.Data);
        }
        Console.WriteLine($"关键帧：{string.Join(",", keys)}；共 {bytes / 1024} KB；采集 {captureMs / 60.0:F1} ms/帧，转换 {convertMs / 60.0:F1} ms/帧，编码 {encodeMs / 60.0:F1} ms/帧；总计 {total.ElapsedMilliseconds} ms");
        QualityMode(width, height, nv12);
        Sound();
        return keys.Count >= 2 && keys[0] == 0 && keys.Contains(30) ? 0 : 1;
    }

    /// <summary>远程画面靠逐帧改质量来控制码率：同一幅画面，质量 20 的关键帧之后把质量调到 70，应该再出一个明显更大的"补清晰"帧。</summary>
    private static void QualityMode(int width, int height, byte[] nv12)
    {
        try
        {
            using var encoder = new H264Encoder(width, height, 20, 3000, quality: 20);
            if (!encoder.QualityMode)
            {
                Console.WriteLine("按质量模式：这台电脑的编码器不接受（会退回固定码率，弱网下效果差一些）");
                return;
            }
            var key = encoder.Encode(nv12, TimeSpan.Zero)?.Data.Length ?? 0;
            var same = encoder.Encode(nv12, TimeSpan.FromMilliseconds(50))?.Data.Length ?? 0;
            encoder.SetQuality(70);
            var refined = encoder.Encode(nv12, TimeSpan.FromMilliseconds(100))?.Data.Length ?? 0;
            Console.WriteLine($"按质量模式：质量 20 的关键帧 {key / 1024.0:F1} KB，画面不变再编一帧 {same} 字节，质量调到 70 后 {refined / 1024.0:F1} KB" +
                (refined > same * 4 ? "（逐帧改质量有效）" : "（改质量没有反应：这台电脑上码率控制会不准）"));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"按质量模式：出错 {ex.Message}");
        }
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

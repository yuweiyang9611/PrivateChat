using System.Diagnostics;
using PrivateChat.Agent.Video;
using PrivateChat.Contracts.Remote;

namespace PrivateChat.Agent;

/// <summary>
/// 本机自检：PrivateChat.Agent.exe --selftest [输出.h264] [--screen]
/// 用测试画面（或真实屏幕）编 60 帧，第 30 帧强制关键帧，打印每帧大小、NAL 类型和耗时，写出 Annex B 文件供 ffprobe 检查。
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
        return keys.Count >= 2 && keys[0] == 0 && keys.Contains(30) ? 0 : 1;
    }
}

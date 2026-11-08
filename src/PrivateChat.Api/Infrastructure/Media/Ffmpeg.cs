using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Options;

namespace PrivateChat.Api.Infrastructure.Media;

/// <summary>ffmpeg / ffprobe 子进程封装。容器镜像自带 ffmpeg；本机开发没装时派生任务会标记 Failed，前端回退播原件。</summary>
public sealed class Ffmpeg(IOptions<AppOptions> options, ILogger<Ffmpeg> logger)
{
    private readonly string _ffmpeg = options.Value.FfmpegPath;
    private readonly string _ffprobe = options.Value.FfprobePath;
    private bool? _available;

    public bool IsAvailable()
    {
        if (_available is { } v) return v;
        try
        {
            using var p = Process.Start(new ProcessStartInfo(_ffmpeg, "-version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            p!.WaitForExit(5000);
            _available = p.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            logger.LogWarning("找不到 ffmpeg（{Path}），音视频转码将跳过", _ffmpeg);
            _available = false;
        }
        return _available.Value;
    }

    /// <summary>媒体时长（秒），取不到返回 null。</summary>
    public async Task<double?> ProbeDurationAsync(string path, CancellationToken ct)
    {
        int code; string stdout;
        try { (code, stdout, _) = await RunAsync(_ffprobe, ["-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", path], TimeSpan.FromSeconds(30), ct); }
        catch (FfmpegException) { return null; }
        if (code != 0) return null;
        return double.TryParse(stdout.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    /// <summary>任意音频 → AAC 64 kbps m4a，faststart 便于边下边播。</summary>
    public async Task TranscodeAudioToM4aAsync(string input, string output, CancellationToken ct)
    {
        var (code, _, stderr) = await RunAsync(_ffmpeg,
            ["-y", "-hide_banner", "-loglevel", "error", "-i", input, "-vn", "-c:a", "aac", "-b:a", "64k", "-ar", "44100", "-ac", "1", "-movflags", "+faststart", "-f", "mp4", output],
            TimeSpan.FromMinutes(10), ct);
        if (code != 0) throw new FfmpegException($"ffmpeg 退出码 {code}: {stderr.Trim()}");
    }

    /// <summary>视频元数据（宽高、旋转、编码、时长、拍摄时间）；ffprobe 不可用或解析失败返回 null。</summary>
    public async Task<VideoProbe?> ProbeVideoAsync(string path, CancellationToken ct)
    {
        if (!IsAvailable()) return null;
        int code; string stdout;
        try { (code, stdout, _) = await RunAsync(_ffprobe, ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", path], TimeSpan.FromSeconds(60), ct); }
        catch (FfmpegException) { return null; }
        if (code != 0) return null;
        try { return MediaProbe.ParseFfprobe(stdout); }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException) { return null; }
    }

    /// <summary>抽一帧做封面（ffmpeg 默认按旋转元数据摆正）。</summary>
    public async Task ExtractFrameAsync(string input, string outputPng, double atSeconds, CancellationToken ct)
    {
        var (code, _, stderr) = await RunAsync(_ffmpeg,
            ["-y", "-hide_banner", "-loglevel", "error", "-ss", atSeconds.ToString("0.###", CultureInfo.InvariantCulture), "-i", input,
             "-frames:v", "1", "-f", "image2", "-c:v", "png", outputPng],
            TimeSpan.FromMinutes(2), ct);
        if (code != 0 || !File.Exists(outputPng)) throw new FfmpegException($"抽帧失败 {code}: {stderr.Trim()}");
    }

    /// <summary>转成各端都能播的 H.264 / AAC MP4，长边不超过 1280（"play-720p"），faststart 便于边下边播。</summary>
    public async Task TranscodeVideo720Async(string input, string output, CancellationToken ct)
    {
        var (code, _, stderr) = await RunAsync(_ffmpeg,
            ["-y", "-hide_banner", "-loglevel", "error", "-i", input,
             "-map", "0:v:0", "-map", "0:a:0?",
             // 长边限制 1280；H.264 要求宽高都是偶数，限制那一边向下取偶（-2 会自动取偶）
             "-vf", "scale='if(gt(iw,ih),trunc(min(1280,iw)/2)*2,-2)':'if(gt(iw,ih),-2,trunc(min(1280,ih)/2)*2)',format=yuv420p",
             "-c:v", "libx264", "-preset", "veryfast", "-crf", "23", "-profile:v", "high",
             "-c:a", "aac", "-b:a", "128k", "-ac", "2",
             "-movflags", "+faststart", "-f", "mp4", output],
            TimeSpan.FromMinutes(90), ct);
        if (code != 0) throw new FfmpegException($"视频转码失败 {code}: {stderr.Trim()}");
    }

    public async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(string exe, string[] args, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = new Process { StartInfo = psi };
        p.Start();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var stdoutTask = p.StandardOutput.ReadToEndAsync(cts.Token);
        var stderrTask = p.StandardError.ReadToEndAsync(cts.Token);
        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            // 超时按普通失败处理（调用方把这一档标记为 Failed 并保存，重启后不会反复重试）；只有真正停机才继续抛取消
            if (!ct.IsCancellationRequested) throw new FfmpegException($"{Path.GetFileName(exe)} 超时（{timeout.TotalMinutes:0} 分钟）");
            throw;
        }
        return (p.ExitCode, await stdoutTask, await stderrTask);
    }
}

public sealed class FfmpegException(string message) : Exception(message);

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
        var (code, stdout, _) = await RunAsync(_ffprobe, ["-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", path], TimeSpan.FromSeconds(30), ct);
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
            throw;
        }
        return (p.ExitCode, await stdoutTask, await stderrTask);
    }
}

public sealed class FfmpegException(string message) : Exception(message);

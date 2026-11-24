using System.Diagnostics;
using PrivateChat.Agent.Platform;

namespace PrivateChat.Agent.Telemetry;

/// <summary>探测子进程（--probe-hardware 文件）：普通模式下是 Agent 自己的子进程，服务模式下是服务以 SYSTEM 在控制台会话里起的。</summary>
internal interface IProbeProcess : IDisposable
{
    Task WaitForExitAsync(CancellationToken ct);

    int ExitCode { get; }

    /// <summary>杀掉；wait：等它真的退出（最多 5 秒）。不抛异常。</summary>
    void Kill(bool wait);
}

/// <summary>
/// HardwareTelemetry 在哪儿、怎么探测：
/// - StartProbe：起探测子进程，结果写到给的文件；
/// - ScreenLocked：控制台会话现在锁着屏 / 在安全桌面上（这时探到的截屏结果不准）；
/// - Fingerprint：便宜的指纹（版本、显卡驱动、显示器），见 <see cref="HardwareInfo.Fingerprint()"/>。
/// 普通模式（<see cref="Local"/>）都在自己的会话里；服务在会话 0，显示器、锁屏要看控制台会话（见 Service/AgentServiceHost）。
/// </summary>
internal sealed record TelemetryEnvironment(Func<string, IProbeProcess> StartProbe, Func<bool> ScreenLocked, Func<string> Fingerprint)
{
    public static TelemetryEnvironment Local { get; } = new(LocalProbeProcess.Start, HardwareInfo.ScreenLocked, HardwareInfo.Fingerprint);
}

/// <summary>
/// 普通模式：Agent 自己的子进程，低优先级，放进"句柄关了就全杀"的作业对象（<see cref="KillOnCloseJob"/>）：Agent 怎么没的它都跟着结束。
/// </summary>
internal sealed class LocalProbeProcess(Process process) : IProbeProcess
{
    private static bool _jobFailureLogged;

    public static IProbeProcess Start(string output)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("找不到自己的程序文件");
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        // dotnet PrivateChat.Agent.dll 这样跑的（开发时）：子进程也得这样跑
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(HardwareTelemetry).Assembly.Location);
        start.ArgumentList.Add("--probe-hardware");
        start.ArgumentList.Add(output);
        var process = Process.Start(start) ?? throw new InvalidOperationException("探测进程没有起来");
        try
        {
            KillOnCloseJob.Add(process);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // 子进程还有自己的 75 秒上限；Agent 正常退出时也会杀它
            if (!_jobFailureLogged) AgentLog.Warn($"探测子进程放不进作业对象（Agent 被强行结束时它不会跟着结束）：{ex.Message}");
            _jobFailureLogged = true;
        }
        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { } // 已经退出了
        return new LocalProbeProcess(process);
    }

    public Task WaitForExitAsync(CancellationToken ct) => process.WaitForExitAsync(ct);

    public int ExitCode => process.ExitCode;

    public void Kill(bool wait)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            if (wait) process.WaitForExit(5000);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
    }

    public void Dispose() => process.Dispose();
}

/// <summary>
/// 服务模式：服务以 SYSTEM 在控制台会话里起的（和远程时的工作进程同一个身份、同一个会话：验证的就是远程时会用的东西）。
/// 会话 0 的作业对象装不了它；服务怎么没的它自己有 75 秒上限，服务正常退出时会杀它。
/// </summary>
internal sealed class SessionProbeProcess(LaunchedProcess process) : IProbeProcess
{
    public static IProbeProcess Start(string output, IReadOnlyDictionary<string, string?> environment)
    {
        var session = ConsoleSession.ActiveId();
        if (session == ConsoleSession.None) throw new InvalidOperationException("控制台会话正在切换，等会儿再探测");
        return new SessionProbeProcess(SessionLauncher.StartAsSystem(session, Environment.ProcessPath!, ["--probe-hardware", output], environment: environment));
    }

    public Task WaitForExitAsync(CancellationToken ct) => process.WaitForExitAsync(ct);

    public int ExitCode => process.ExitCode ?? -1;

    public void Kill(bool wait)
    {
        process.Kill();
        if (wait) process.WaitForExit(5000);
    }

    public void Dispose() => process.Dispose();
}

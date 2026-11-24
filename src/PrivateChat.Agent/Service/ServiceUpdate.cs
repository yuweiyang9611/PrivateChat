using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using PrivateChat.Agent.Platform;

namespace PrivateChat.Agent.Service;

/// <summary>
/// 服务的自动升级：服务自己没法在运行中换掉自己的 exe 再"重启自己"，所以交给一个小进程：
/// 1. 服务下载、核对好新版本（ProgramData\…\update，只有 SYSTEM 和管理员能写），起 --apply-update（会话 0、SYSTEM、不在任何作业里），然后自己退出；
/// 2. --apply-update 等服务进程退出 → 把 Program Files 里的 exe 改名成 .old、新的放进去 → 启动服务 → 等新服务报"起来了"（<see cref="ServicePaths.ReadyEvent"/>），最多 60 秒；
/// 3. 新服务起不来：停掉它、换回旧的、再启动，并记下这个版本失败过（update-failed.json），6 小时内不再试它。
/// --apply-update 用的是旧版本的 exe（它自己就是 .old 那个文件）：新版本坏了也换得回来。
/// </summary>
internal static class ServiceUpdate
{
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan SkipFailedFor = TimeSpan.FromHours(6);
    private static string FailedPath => Path.Combine(ServicePaths.DataDir, "update-failed.json");

    /// <summary>服务里：起 --apply-update，之后服务该退出了（AgentService 收到 Restarting）。这个版本最近换失败过就抛异常（过一会儿再说）。</summary>
    public static void Begin(string staged, string version, string fromVersion)
    {
        if (RecentlyFailed(version) is { } when)
            throw new InvalidOperationException($"新版本 {version} 在 {when:HH:mm} 换过一次没起来，{SkipFailedFor.TotalHours:0} 小时内不再试");
        CheckSigned(staged, version, fromVersion);
        var start = new ProcessStartInfo(ServicePaths.ProgramExe) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Environment.SystemDirectory };
        foreach (var arg in new[] { "--apply-update", staged, version, fromVersion, Environment.ProcessId.ToString() }) start.ArgumentList.Add(arg);
        foreach (var (name, value) in ServicePaths.SystemChildEnvironment) start.Environment[name] = value;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("换版本的进程起不来");
        AgentLog.Info($"新版本 {version} 交给换版本的进程 {process.Id}，服务退出");
    }

    /// <summary>
    /// 服务以 SYSTEM 运行，只装我们签过名的版本（<see cref="ReleaseSignature"/>）：签名里的版本号要和清单的一样，而且比现在的新
    /// （服务器被人拿下也不能塞别的程序进来、也不能拿签过名的旧版本冒充新版本）。
    /// </summary>
    private static void CheckSigned(string staged, string version, string fromVersion)
    {
        var signed = ReleaseSignature.Verify(staged);
        if (signed.Version != version) throw new InvalidDataException($"签名里的版本是 {signed.Version}，清单说的是 {version}");
        if (!UpdateManifest.IsNewer(signed.Version, fromVersion)) throw new InvalidDataException($"签名里的版本 {signed.Version} 不比现在的 {fromVersion} 新");
    }

    /// <summary>新服务起来了：告诉在等的 --apply-update。</summary>
    public static void SignalReady()
    {
        if (EventWaitHandle.TryOpenExisting(ServicePaths.ReadyEvent, out var ready))
            using (ready) ready.Set();
    }

    /// <summary>--apply-update 新文件 新版本号 旧版本号 服务进程号。返回 0 = 换好了，1 = 换回了旧的。</summary>
    public static int Apply(string staged, string version, string fromVersion, int servicePid)
    {
        AgentLog.Init(ServicePaths.LogDir, console: false, fileName: "update.log");
        AgentLog.Info($"换版本：{fromVersion} → {version}");
        var target = ServicePaths.ProgramExe;
        var old = target + ".old";
        try
        {
            WaitForExit(servicePid, TimeSpan.FromSeconds(60));
            if (ServiceControl.State(ServicePaths.ServiceName) is { } state && state != ServiceControl.Stopped)
                ServiceControl.Stop(ServicePaths.ServiceName, TimeSpan.FromSeconds(30));
        }
        catch (Exception ex)
        {
            AgentLog.Error("等旧服务退出时出错", ex);
        }

        try
        {
            CheckSigned(staged, version, fromVersion);
        }
        catch (Exception ex)
        {
            AgentLog.Error("新版本没有有效的发布签名，不换", ex);
            TryStart();
            return 1;
        }
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, ServicePaths.ReadyEvent);
        try
        {
            if (File.Exists(old)) File.Delete(old);
        }
        catch (Exception)
        {
            old = $"{target}.{DateTime.Now.Ticks}.old";
        }
        try
        {
            File.Move(target, old); // 正在运行的 exe（就是这个进程自己）可以改名，不能覆盖
            File.Move(staged, target);
        }
        catch (Exception ex)
        {
            AgentLog.Error("换文件失败，启动原来的服务", ex);
            if (!File.Exists(target) && File.Exists(old)) File.Move(old, target);
            TryStart();
            return 1;
        }

        if (StartAndWait(ready))
        {
            AgentLog.Info($"新版本 {version} 起来了");
            try { File.Delete(FailedPath); }
            catch (Exception) { }
            return 0;
        }

        AgentLog.Warn($"新版本 {version} {ReadyTimeout.TotalSeconds:0} 秒没起来，换回 {fromVersion}");
        try
        {
            ServiceControl.Stop(ServicePaths.ServiceName, TimeSpan.FromSeconds(30));
            var bad = target + ".bad";
            if (File.Exists(bad)) File.Delete(bad);
            File.Move(target, bad);
            File.Move(old, target);
            File.WriteAllText(FailedPath, JsonSerializer.Serialize(new JsonObject { ["version"] = version, ["at"] = DateTimeOffset.Now.ToString("O") }));
        }
        catch (Exception ex)
        {
            AgentLog.Error("换回旧版本失败", ex);
        }
        TryStart();
        return 1;
    }

    private static bool StartAndWait(EventWaitHandle ready)
    {
        try
        {
            ServiceControl.Start(ServicePaths.ServiceName);
        }
        catch (Exception ex)
        {
            AgentLog.Error("启动新服务失败", ex);
            return false;
        }
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < ReadyTimeout)
        {
            if (ready.WaitOne(500)) return true;
            if (ServiceControl.State(ServicePaths.ServiceName) is null or ServiceControl.Stopped) return false;
        }
        return false;
    }

    private static void TryStart()
    {
        try { ServiceControl.Start(ServicePaths.ServiceName); }
        catch (Exception ex) { AgentLog.Error("启动服务失败", ex); }
    }

    private static DateTimeOffset? RecentlyFailed(string version)
    {
        try
        {
            if (!File.Exists(FailedPath)) return null;
            var failed = JsonNode.Parse(File.ReadAllText(FailedPath));
            if (failed?["version"]?.GetValue<string>() != version) return null;
            var at = DateTimeOffset.Parse(failed["at"]!.GetValue<string>());
            return DateTimeOffset.Now - at < SkipFailedFor ? at : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void WaitForExit(int pid, TimeSpan timeout)
    {
        var handle = Native.OpenProcess(Native.SYNCHRONIZE, false, (uint)pid);
        if (handle == IntPtr.Zero) return; // 已经没了
        try
        {
            Native.WaitForSingleObject(handle, (int)timeout.TotalMilliseconds);
        }
        finally
        {
            Native.CloseHandle(handle);
        }
    }

    private static class Native
    {
        public const uint SYNCHRONIZE = 0x00100000;

        [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
        [DllImport("kernel32.dll")] public static extern int WaitForSingleObject(IntPtr handle, int milliseconds);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr handle);
    }
}

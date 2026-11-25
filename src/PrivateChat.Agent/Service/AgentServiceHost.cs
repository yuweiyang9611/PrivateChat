using PrivateChat.Agent.Platform;
using PrivateChat.Agent.Telemetry;

namespace PrivateChat.Agent.Service;

/// <summary>
/// 装成系统服务后的路径和名字。程序在 Program Files（普通用户只能读），数据在 ProgramData（只有 SYSTEM 和管理员能动，见 Installer）。
/// </summary>
internal static class ServicePaths
{
    public const string ServiceName = "PrivateChatAgent";

    public static string ProgramDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PrivateChat", "Agent");
    public static string ProgramExe => Path.Combine(ProgramDir, "PrivateChat.Agent.exe");
    public static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PrivateChat", "Agent");
    public static string LogDir => Path.Combine(DataDir, "logs");
    /// <summary>单文件程序解压原生库的地方（DOTNET_BUNDLE_EXTRACT_BASE_DIR）：SYSTEM 进程别解压到普通用户能写的临时目录里。</summary>
    public static string RuntimeDir => Path.Combine(DataDir, "runtime");

    /// <summary>新服务起来了：--apply-update 等的就是它（见 <see cref="ServiceUpdate"/>）。</summary>
    public const string ReadyEvent = @"Global\PrivateChat.Agent.ServiceReady";

    /// <summary>SYSTEM 子进程（工作进程、探测）的环境变量：和服务一样解压到 ProgramData 里。</summary>
    public static IReadOnlyDictionary<string, string?> SystemChildEnvironment => new Dictionary<string, string?>
    {
        ["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = Environment.GetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR") ?? RuntimeDir,
    };
}

/// <summary>
/// 服务（PrivateChat.Agent.exe --service，SCM 起的，LocalSystem，会话 0）：就是普通模式的 <see cref="AgentService"/>，换掉几样（<see cref="AgentHooks"/>）：
/// 界面交给控制台会话里的界面进程（<see cref="HelperBridgeUi"/> + <see cref="HelperSupervisor"/>），远程在 SYSTEM 工作进程里跑（<see cref="WorkerSessionHost"/>），
/// 硬件探测在控制台会话里做、显示器用界面进程报的，新版本交给 --apply-update 换（<see cref="ServiceUpdate"/>）。
/// </summary>
internal static class AgentServiceHost
{
    /// <summary>
    /// 开发 / 自动化测试时用的跑法（--service-dev，和 --headless 一样的参数：--server、--data-dir、--name、--consent、--source pattern、--dry-input）：
    /// 不经 SCM、不装服务，同样的服务流程和子进程，只是子进程都以当前用户普通地起（<see cref="SessionLauncher.CurrentUserOnly"/>）、继承标准输出，
    /// 界面进程用无界面的 HeadlessUi（设备码、状态写到数据目录，同意由 --consent 决定）。只在 PRIVATECHAT_AGENT_TEST=1 时能用。
    /// </summary>
    public sealed record DevOptions(string DataDir, string Server, string Name, string Consent, bool UsePattern, string? DryInputPath);

    public static int Run()
    {
        AgentLog.Init(ServicePaths.LogDir, console: false, fileName: "service.log");
        return ServiceHost.Run(ServicePaths.ServiceName, (changes, ct) => RunCore(ServicePaths.DataDir, ServicePaths.LogDir, Environment.MachineName, dev: null, changes, ct));
    }

    public static int RunDev(DevOptions dev)
    {
        SessionLauncher.CurrentUserOnly = true;
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        AgentLog.Init(dev.DataDir, console: true, fileName: "service.log");
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        var changes = System.Threading.Channels.Channel.CreateUnbounded<ServiceHost.SessionChange>();
        try
        {
            return RunCore(dev.DataDir, dev.DataDir, dev.Name, dev, changes.Reader, cts.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
    }

    private static async Task<int> RunCore(string dataDir, string logDir, string name, DevOptions? dev,
        System.Threading.Channels.ChannelReader<ServiceHost.SessionChange> changes, CancellationToken ct)
    {
        {
            AgentLog.Info($"PrivateChat Agent {AgentService.Version} 服务启动{(dev is null ? "" : "（开发模式，不是真的服务）")}，数据目录 {dataDir}");
            if (dev is null) Installer.RecordVersion(AgentService.Version, Environment.ProcessPath!);
            var bridge = new HelperBridgeUi();
            var helpers = new HelperSupervisor(bridge, dev is null ? [] : ["--headless-ui", dataDir, "--consent", dev.Consent]);
            AgentService? agent = null;
            var telemetry = new TelemetryEnvironment(
                output => SessionProbeProcess.Start(output, ServicePaths.SystemChildEnvironment),
                ConsoleLocked,
                () => HardwareInfo.Fingerprint(bridge.Screens.Select(s => (s.DeviceName ?? "", s.IsPrimary))));
            var hooks = new AgentHooks(
                SessionFactory: (request, server) => new WorkerSessionHost(request,
                    () => new WorkerStart(request, server, agent!.ValidatedOutputs(), agent.ValidatedEncoders(), dev?.UsePattern ?? false, false, dev?.DryInputPath),
                    bridge, logDir),
                Telemetry: telemetry,
                Screens: () => bridge.Screens,
                ApplyUpdate: ServiceUpdate.Begin);
            agent = new AgentService(new AgentOptions(dataDir, name, UsePattern: false, DryInputPath: null, Headless: true), bridge, hooks);
            bridge.DisplayChanged += agent.DisplayChanged;
            // 开发模式没有安装程序：没设置过就直接用 --server（像 --headless 那样）
            if (dev is not null && !AgentConfig.Load(dataDir).HasServer) bridge.AdminSetup(dev.Server);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            agent.RestartRequested += stop.Cancel; // 新版本由 --apply-update 换上、重启服务：这个进程结束
            ServiceUpdate.SignalReady();
            var run = agent.RunAsync(stop.Token);
            var ui = helpers.RunAsync(changes, stop.Token);
            var admin = dev is null ? new AdminPipe(bridge, () => bridge.State).RunAsync(stop.Token) : Task.CompletedTask;
            try
            {
                await run;
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            await stop.CancelAsync();
            try { await Task.WhenAll(ui, admin); }
            catch (OperationCanceledException) { }
            AgentLog.Info("服务停止");
            return 0;
        }
    }

    /// <summary>控制台会话锁着屏、或者在安全桌面上（服务自己在会话 0，看的是控制台会话的锁屏标志）。</summary>
    private static bool ConsoleLocked()
    {
        var session = ConsoleSession.ActiveId();
        return session == ConsoleSession.None || ConsoleSession.Lock(session) != ConsoleSession.LockState.Unlocked;
    }
}

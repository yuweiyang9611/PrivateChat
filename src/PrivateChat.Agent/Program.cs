using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using PrivateChat.Agent.Ui;

namespace PrivateChat.Agent;

/// <summary>
/// PrivateChat.Agent.exe                         托盘程序（正常使用）
/// PrivateChat.Agent.exe --headless --server URL  无界面（自动化测试），另可加：
///     --data-dir DIR       配置与日志目录（默认 %LOCALAPPDATA%\PrivateChat\Agent）
///     --name NAME          报给服务器的电脑名（默认计算机名）
///     --source pattern     用测试画面代替真实屏幕（声音也换成测试音，不碰声卡）
///     --source busy        同上，但画面里有一块一直在滚动的"文字"，还带着时间码：弱网测试用（量得出端到端延迟）
///     --dry-input FILE     键鼠不注入系统，写到文件里
///     --pretend-version X  假装自己是版本 X（只在 PRIVATECHAT_AGENT_TEST=1 时生效）：测自动升级用
///     --consent accept|decline|ignore|wait   自动回复远程请求（accept 需要环境变量 PRIVATECHAT_AGENT_TEST=1；
///                          wait = 同意窗口一直挂着没人点，直到请求被取消或超时）
/// PrivateChat.Agent.exe --selftest [out.h264] [--capture dxgi|gdi|pattern] [--encoder auto|hardware|software]
///                          编码器 / 截屏自检：打印用了哪个编码器、哪种截屏和每帧耗时（见 SelfTest）
/// PrivateChat.Agent.exe --secure-desktop-test   锁屏 / UAC 实验：要管理员，临时装个服务看看以 SYSTEM 能不能截、点 UAC 提示和锁屏（见 Diagnostics/SecureDesktopTest）
/// PrivateChat.Agent.exe --probe-hardware OUT.json   探测显卡、编码器、截屏，结果写进 OUT.json 后退出（遥测用：Agent 连上服务器后
///                          自己在子进程里跑，见 Telemetry/HardwareTelemetry；也可以手动跑来看）。远程只用这里验证过的桌面复制和硬件编码器
/// 环境变量：
///     PRIVATECHAT_AGENT_CAPTURE=auto|dxgi|gdi   怎么截屏（默认 auto：最近一次探测验证过桌面复制的显示器上用桌面复制，别的用 GDI；
///                                             dxgi = 没验证过也用桌面复制；gdi = 一律 GDI。桌面复制中途出错都退回 GDI，远程不断。
///                                             见 Video/ScreenCaptures、docs/DESIGN.md"截屏和编码器怎么选"）
///     PRIVATECHAT_AGENT_ENCODER=auto|hardware|software   用哪个 H.264 编码器（默认 auto：最近一次探测里试编成功的硬件编码器，同一张显卡上的优先，
///                                             没有就用 Windows 自带的软件编码器；hardware = 没验证过也试显卡的硬件编码器；software = 一律软件。
///                                             硬件编码器起不来、中途出错都换回软件编码器，出过错的这个进程里不再用。见 Video/VideoEncoders）
///     PRIVATECHAT_AGENT_AUTOUPDATE=off        不自动升级（默认：服务器上的安装包比自己新就自动换，见 Updater）
///     PRIVATECHAT_AGENT_REALTIME=off          不用快速线路（画面和声音只走服务器中继；默认：服务器配了 Cloudflare Realtime 就可以用，由控制端决定）
///     PRIVATECHAT_AGENT_TLS=bundled|system   强制用程序自带的 TLS 1.3 / 系统的 TLS（默认：系统没有 TLS 1.3 时用自带的）
///     PRIVATECHAT_AGENT_TELEMETRY=off         不报遥测：不探测硬件、不上报（默认：连上服务器后报这台电脑的硬件和能力——没探测过、或者
///                                             版本 / 显卡驱动 / 显示器变了就先在子进程里探测一次，等启动后第一次查完更新、连上满 30 秒、
///                                             没在被远程时才探——每次远程再报性能汇总；
///                                             只有数字、型号和出错的说明，管理员在设备页的"诊断"里看，见 docs/DESIGN.md"远程协助遥测"）。
///                                             关了就没有探测验证过的桌面复制和硬件编码器：远程用 GDI + 软件编码器（除非上面两个强制）
///     PRIVATECHAT_AGENT_TEST_RTP_DROP=0.02   快速线路上故意丢掉这个比例的画面包（只在 PRIVATECHAT_AGENT_TEST=1 时生效，自动化测试用）
///     PRIVATECHAT_AGENT_TEST_CA=FILE.pem     自带的 TLS 只信任这个根证书（只在 PRIVATECHAT_AGENT_TEST=1 时生效，自动化测试用）
///     PRIVATECHAT_AGENT_TEST_ENCODE_DELAY_MS=45   每编一帧多等这么多毫秒 × 像素数 ÷ 1280×720，装成一台编码很慢的电脑（只在 PRIVATECHAT_AGENT_TEST=1 时生效，自动化测试用）
///     PRIVATECHAT_AGENT_TEST_CAPTURE_DELAY_MS=45  每截一次屏多等这么多毫秒（不管截多大），装成一台截屏很慢的电脑（只在 PRIVATECHAT_AGENT_TEST=1 时生效，自动化测试用）
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string? Option(string name)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        // 锁屏 / UAC 实验（Diagnostics/SecureDesktopTest）：窗口、临时服务、服务起的子进程
        switch (args)
        {
            case ["--secure-desktop-test", ..]:
                return Diagnostics.SecureDesktopTest.Launch(args);
            case [] when Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "").Contains("DesktopTest", StringComparison.OrdinalIgnoreCase):
                // 给测试电脑的那份改名叫 PrivateChatDesktopTest.exe：双击就是实验，不会变成普通的远程协助程序
                return Diagnostics.SecureDesktopTest.Launch(["--secure-desktop-test"]);
            // 装成系统服务后的几种进程（Service/）：服务本身、每次远程的工作进程、登录用户的界面进程、换版本
            case ["--service", ..]:
                SetProcessDpiAwarenessContext(-4);
                return Service.AgentServiceHost.Run();
            case ["--session-worker", var workerPipe, var workerService, var workerLogs, ..]:
                SetProcessDpiAwarenessContext(-4);
                return Service.SessionWorker.Run(workerPipe, int.Parse(workerService), workerLogs, console: args.Contains("--console"));
            case ["--helper", var helperPipe, var helperService, ..]:
            {
                SetProcessDpiAwarenessContext(-4);
                var headlessUi = Option("--headless-ui");
                return Service.HelperProcess.Run(helperPipe, int.Parse(helperService),
                    headlessUi is not null && Environment.GetEnvironmentVariable("PRIVATECHAT_AGENT_TEST") == "1" ? headlessUi : null, Option("--consent") ?? "ignore");
            }
            case ["--service-dev", ..] when Environment.GetEnvironmentVariable("PRIVATECHAT_AGENT_TEST") == "1":
                SetProcessDpiAwarenessContext(-4);
                return Service.AgentServiceHost.RunDev(new Service.AgentServiceHost.DevOptions(
                    Path.GetFullPath(Option("--data-dir") ?? "service-dev"), Option("--server") ?? "", Option("--name") ?? Environment.MachineName,
                    Option("--consent") ?? "ignore", Option("--source") is "pattern" or "busy", Option("--dry-input")));
            case ["--install" or "--uninstall" or "--configure", ..]:
                SetProcessDpiAwarenessContext(-4);
                return Service.Installer.Run(args);
            case ["--apply-update", var staged, var newVersion, var fromVersion, var oldService, ..]:
                return Service.ServiceUpdate.Apply(staged, newVersion, fromVersion, int.Parse(oldService));
            case ["--verify-signature", var signedFile, ..]:
                // 看一个 exe 末尾的发布签名（打印版本号和用的哪把钥匙，或者为什么不对）
                Console.OutputEncoding = Encoding.UTF8;
                try
                {
                    var signed = ReleaseSignature.Verify(signedFile);
                    Console.WriteLine($"签名有效：版本 {signed.Version}，钥匙 {signed.KeyId}");
                    return 0;
                }
                catch (Exception ex) when (ex is ReleaseSignatureException or InvalidDataException or IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
                {
                    Console.WriteLine($"签名无效：{ex.Message}");
                    return 1;
                }
            case ["--secure-desktop-test-dry", var dryReport, ..]:
                SetProcessDpiAwarenessContext(-4);
                return Diagnostics.SecureDesktopTest.Dry(dryReport);
            case ["--secure-desktop-test-service", var report, ..]:
                return Diagnostics.SecureDesktopTest.Service(report);
            case ["--secure-desktop-test-worker", var pipe, var servicePid, var label, var report, ..]:
                SetProcessDpiAwarenessContext(-4);
                return Diagnostics.SecureDesktopTest.Worker(pipe, int.Parse(servicePid), label, report);
            case ["--secure-desktop-test-helper", var pipe, var servicePid, var report, ..]:
                SetProcessDpiAwarenessContext(-4);
                return Diagnostics.SecureDesktopTest.Helper(pipe, int.Parse(servicePid), report);
        }

        // 探测子进程：不要界面、不要单实例锁、不写日志，做完就退出
        if (args is ["--probe-hardware", var probeOutput, ..])
        {
            SetProcessDpiAwarenessContext(-4); // 和远程时一样按物理像素看显示器
            return Telemetry.HardwareProbe.Run(probeOutput);
        }
        if (args.Contains("--selftest"))
        {
            SetProcessDpiAwarenessContext(-4);
            return SelfTest.Run(args);
        }

        string? Value(string name)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        var headless = args.Contains("--headless");
        if (headless) Console.OutputEncoding = Encoding.UTF8;
        // 这台电脑装了系统服务（管理员装的）：托盘由服务起，自己这个按用户装的老方式不再跑，开机自启也去掉
        if (!headless && ServiceInstalled())
        {
            try { Autostart.Enabled = false; }
            catch (Exception) { }
            return 0;
        }
        var dataDir = Path.GetFullPath(Value("--data-dir") ?? AgentConfig.DefaultDirectory);
        AgentLog.Init(dataDir, console: headless);

        // 每个数据目录只跑一个实例（正常使用就是每个 Windows 用户一个）
        var mutexName = @"Local\PrivateChat.Agent." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dataDir.ToLowerInvariant())))[..16];
        using var mutex = new Mutex(true, mutexName, out var first);
        var updatedFrom = Value("--updated-from");
        if (updatedFrom is not null)
        {
            // 自动升级后由旧版本启动的：先告诉它"我起来了"（它等到了才退出），再等它退出、把单实例的位置让出来
            Updater.SignalReady(dataDir);
            if (!first)
                try
                {
                    first = mutex.WaitOne(TimeSpan.FromSeconds(30));
                }
                catch (AbandonedMutexException)
                {
                    first = true; // 旧进程没来得及释放就退出了：位置已经是我们的
                }
        }
        if (!first)
        {
            if (!headless && !args.Contains("--autostart"))
                MessageBox.Show("远程协助程序已经在运行了，看看屏幕右下角的托盘图标。", "PrivateChat 远程协助");
            return 1;
        }

        var consent = Value("--consent") ?? "ignore";
        if (consent == "accept" && Environment.GetEnvironmentVariable("PRIVATECHAT_AGENT_TEST") != "1")
        {
            AgentLog.Warn("--consent accept 只在测试环境（PRIVATECHAT_AGENT_TEST=1）下生效");
            consent = "ignore";
        }
        var options = new AgentOptions(dataDir, Value("--name") ?? Environment.MachineName, Value("--source") is "pattern" or "busy", Value("--dry-input"), headless, Value("--source") == "busy");
        if (Value("--pretend-version") is { } pretend && Environment.GetEnvironmentVariable("PRIVATECHAT_AGENT_TEST") == "1") AgentService.PretendVersion = pretend;
        AgentLog.Info($"PrivateChat Agent {AgentService.Version} 启动（{(headless ? "无界面" : "托盘")}），数据目录 {dataDir}");
        if (updatedFrom is not null) AgentLog.Info($"已从 {updatedFrom} 自动升级到 {AgentService.Version}");
        using var cts = new CancellationTokenSource();

        if (headless)
        {
            SetProcessDpiAwarenessContext(-4);
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };
            var service = new AgentService(options, new HeadlessUi(dataDir, Value("--server"), consent));
            service.RestartRequested += cts.Cancel; // 自动升级：新版本已经起来了，这个进程结束
            try
            {
                service.RunAsync(cts.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) { }
            return 0;
        }

        ApplicationConfiguration.Initialize();
        using var tray = new TrayUi(dataDir);
        var agent = new AgentService(options, tray);
        tray.BindUpdateCheckWithProgress(agent.CheckForUpdateAsync, cts.Token);
        agent.RestartRequested += cts.Cancel; // 自动升级：新版本已经起来了，下面的 run 结束后托盘跟着退出
        tray.ExitRequested += async () =>
        {
            await agent.DisconnectLocallyAsync();
            await cts.CancelAsync();
            tray.Exit();
        };
        var run = Task.Run(() => agent.RunAsync(cts.Token));
        _ = run.ContinueWith(t =>
        {
            if (t.IsFaulted) AgentLog.Error("Agent 异常退出", t.Exception);
            tray.Exit();
        }, TaskScheduler.Default);
        // 跑着的时候管理员装上了系统服务：这个按用户运行的退出、去掉开机自启（不然两个同时在线，托盘也有两个）
        using var serviceWatch = new System.Threading.Timer(_ =>
        {
            if (!ServiceInstalled() || cts.IsCancellationRequested) return;
            AgentLog.Info("这台电脑装了远程协助的系统服务，按用户运行的这个退出");
            try { Autostart.Enabled = false; }
            catch (Exception) { }
            cts.Cancel();
        }, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        Application.Run(tray.Context);
        cts.Cancel();
        try { run.Wait(TimeSpan.FromSeconds(5)); }
        catch (AggregateException) { }
        return 0;
    }

    /// <summary>这台电脑装了远程协助的系统服务（问不到当没装：老方式照常跑）。</summary>
    private static bool ServiceInstalled()
    {
        try
        {
            return Platform.ServiceControl.State(Service.ServicePaths.ServiceName) is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(nint value);
}

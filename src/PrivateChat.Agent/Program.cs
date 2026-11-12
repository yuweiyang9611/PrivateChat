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
///     --source pattern     用测试画面代替真实屏幕
///     --dry-input FILE     键鼠不注入系统，写到文件里
///     --consent accept|decline|ignore   自动回复远程请求（accept 需要环境变量 PRIVATECHAT_AGENT_TEST=1）
/// PrivateChat.Agent.exe --selftest [out.h264] [--screen]   编码器自检
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
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
        var dataDir = Path.GetFullPath(Value("--data-dir") ?? AgentConfig.DefaultDirectory);
        AgentLog.Init(dataDir, console: headless);

        // 每个数据目录只跑一个实例（正常使用就是每个 Windows 用户一个）
        var mutexName = @"Local\PrivateChat.Agent." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dataDir.ToLowerInvariant())))[..16];
        using var mutex = new Mutex(true, mutexName, out var first);
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
        var options = new AgentOptions(dataDir, Value("--name") ?? Environment.MachineName, Value("--source") == "pattern", Value("--dry-input"), headless);
        AgentLog.Info($"PrivateChat Agent {AgentService.Version} 启动（{(headless ? "无界面" : "托盘")}），数据目录 {dataDir}");
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
        Application.Run(tray.Context);
        cts.Cancel();
        try { run.Wait(TimeSpan.FromSeconds(5)); }
        catch (AggregateException) { }
        return 0;
    }

    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(nint value);
}

using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using PrivateChat.Agent.Platform;
using PrivateChat.Agent.Ui;
using PrivateChat.Agent.Video;
using PrivateChat.Contracts.Remote;

namespace PrivateChat.Agent.Service;

/// <summary>
/// 界面进程（PrivateChat.Agent.exe --helper 管道名 服务进程号）：服务以登录用户的身份在控制台会话里起，跑的就是普通模式的托盘界面
/// （<see cref="TrayUi"/>，服务模式：没有"退出"和"开机自启"）。服务通过管道叫它显示状态、设备码、同意窗口、红条，读写剪贴板；
/// 它把"断开"、显示器变化报回去。"重新设置"要管理员：以管理员身份起 --configure（服务只认管理员给的服务器地址）。
/// 管道断了（服务停了、升级、换了控制台会话）就退出，服务会再起。日志在 %LOCALAPPDATA%\PrivateChat\Agent\helper.log。
/// </summary>
internal static class HelperProcess
{
    /// <param name="headlessDir">开发模式（--service-dev）：用无界面的 HeadlessUi，设备码、状态写到这个目录，同意由 consent 决定。</param>
    public static int Run(string pipe, int servicePid, string? headlessDir = null, string consent = "ignore")
    {
        var dataDir = headlessDir ?? AgentConfig.DefaultDirectory;
        if (headlessDir is not null) Console.OutputEncoding = System.Text.Encoding.UTF8;
        AgentLog.Init(dataDir, console: headlessDir is not null, fileName: "helper.log");
        AgentLog.Info($"界面进程启动（进程 {Environment.ProcessId}，会话 {ConsoleSession.CurrentProcessSession()}）");
        if (headlessDir is not null) return RunHeadless(pipe, servicePid, new HeadlessUi(headlessDir, null, consent));
        ApplicationConfiguration.Initialize();
        using var tray = new TrayUi(dataDir, serviceMode: true);
        PipeChannel channel;
        try
        {
            channel = PipeChannel.ConnectAsync(pipe, servicePid, TokenImpersonationLevel.Identification, 20_000, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            AgentLog.Error("连不上服务", ex);
            return 1;
        }
        var peer = new PipePeer(channel, (cmd, message) => Handle(tray, cmd, message, tray.Exit));
        tray.DisconnectRequested += () => _ = Notify("disconnect", null);
        tray.SetupRequested += Configure;
        EventHandler displayChanged = (_, _) => _ = Notify("screens", ScreenList());
        SystemEvents.DisplaySettingsChanged += displayChanged;
        _ = Notify("screens", ScreenList());
        _ = Task.Run(async () =>
        {
            await peer.RunAsync(CancellationToken.None);
            AgentLog.Info("和服务的管道断了，界面进程退出");
            tray.Exit();
        });
        Application.Run(tray.Context);
        SystemEvents.DisplaySettingsChanged -= displayChanged;
        channel.Dispose();
        return 0;

        async Task Notify(string cmd, JsonObject? body)
        {
            try { await peer.NotifyAsync(cmd, body, CancellationToken.None); }
            catch (Exception ex) { AgentLog.Warn($"发给服务失败（{cmd}）：{ex.Message}"); }
        }
    }

    /// <summary>开发模式：同样的管道协议，界面是 HeadlessUi（没有消息循环，管道断了就退出）。</summary>
    private static int RunHeadless(string pipe, int servicePid, IAgentUi ui)
    {
        try
        {
            using var channel = PipeChannel.ConnectAsync(pipe, servicePid, TokenImpersonationLevel.Identification, 20_000, CancellationToken.None).GetAwaiter().GetResult();
            var peer = new PipePeer(channel, (cmd, message) => Handle(ui, cmd, message, () => Environment.Exit(0)));
            ui.DisconnectRequested += () => _ = peer.NotifyAsync("disconnect", null, CancellationToken.None);
            _ = peer.NotifyAsync("screens", ScreenList(), CancellationToken.None);
            peer.RunAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            AgentLog.Error("界面进程出错", ex);
            return 1;
        }
        return 0;
    }

    private static readonly Dictionary<Guid, CancellationTokenSource> Consents = [];
    private static readonly Lock ConsentGate = new();

    /// <summary>服务发来的一条（托盘和无界面两种界面共用）。</summary>
    private static async Task<JsonObject?> Handle(IAgentUi ui, string cmd, JsonObject message, Action quit)
    {
        switch (cmd)
        {
            case "setState":
                ui.SetState(Enum.Parse<AgentState>(message["state"]!.GetValue<string>()), message["detail"]?.GetValue<string>() ?? "");
                return null;
            case "showDeviceCode":
                ui.ShowDeviceCode(message["code"]?.GetValue<string>(), message["server"]?.GetValue<string>());
                return null;
            case "showConnectionProblem":
                ui.ShowConnectionProblem(message["problem"] is JsonObject problem ? PipePeer.Body<ConnectionProblem>(problem) : null, message["server"]?.GetValue<string>());
                return null;
            case "showBanner":
                ui.ShowBanner(message["request"] is JsonObject banner ? PipePeer.Body<AgentSessionRequest>(banner) : null);
                return null;
            case "askConsent":
            {
                var request = PipePeer.Body<AgentSessionRequest>((JsonObject)message["request"]!);
                using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, request.ExpiresInSeconds)));
                lock (ConsentGate) Consents[request.SessionId] = cancel;
                try
                {
                    return new JsonObject { ["answer"] = await ui.AskConsentAsync(request, cancel.Token) };
                }
                finally
                {
                    lock (ConsentGate) Consents.Remove(request.SessionId);
                }
            }
            case "cancelConsent":
            {
                if (Guid.TryParse(message["sessionId"]?.GetValue<string>(), out var id))
                    lock (ConsentGate)
                        if (Consents.TryGetValue(id, out var cancel)) cancel.Cancel();
                return null;
            }
            case "readClipboard":
                return new JsonObject { ["text"] = await ui.ReadClipboardAsync() };
            case "writeClipboard":
                await ui.WriteClipboardAsync(message["text"]?.GetValue<string>() ?? "");
                return [];
            case "quit":
                quit();
                return null;
            default:
                throw new InvalidOperationException($"不认识的命令 {cmd}");
        }
    }

    /// <summary>显示器列表（物理像素：Program 设了 Per-Monitor V2），和普通模式下远程用的那份一样（编号 = Screen.AllScreens 的顺序）。</summary>
    private static JsonObject ScreenList() => new()
    {
        ["screens"] = new JsonArray([.. GdiScreenSource.List().Select(s => (JsonNode)new JsonObject
        {
            ["x"] = s.Bounds.X, ["y"] = s.Bounds.Y, ["width"] = s.Bounds.Width, ["height"] = s.Bounds.Height,
            ["primary"] = s.IsPrimary, ["device"] = s.DeviceName,
        })]),
    };

    /// <summary>"重新设置"：以管理员身份起 --configure（要电脑前的人点一次 UAC）。</summary>
    private static void Configure()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--configure") { UseShellExecute = true, Verb = "runas" });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            MessageBox.Show("重新设置要管理员同意（这台电脑的远程协助装成了系统服务，服务器地址只有管理员能改）。", "PrivateChat 远程协助");
        }
        catch (Exception ex)
        {
            AgentLog.Error("起不来设置程序", ex);
        }
    }
}

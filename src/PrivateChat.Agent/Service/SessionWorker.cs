using System.Collections.Frozen;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json.Nodes;
using PrivateChat.Agent.Audio;
using PrivateChat.Agent.Input;
using PrivateChat.Agent.Platform;
using PrivateChat.Agent.Session;
using PrivateChat.Agent.Ui;
using PrivateChat.Agent.Video;
using PrivateChat.Contracts.Remote;

namespace PrivateChat.Agent.Service;

/// <summary>
/// 服务开始一次远程时交给工作进程的东西（管道只有 SYSTEM 能连，设备令牌只在内存里过）。
/// UsePattern / BusyPattern / DryInputPath 是自动化测试用的（测试脚本扮演服务），工作进程只在 PRIVATECHAT_AGENT_TEST=1 时认。
/// </summary>
internal sealed record WorkerStart(
    AgentSessionRequest Request,
    ServerInfo Server,
    IReadOnlyList<string> ValidatedOutputs,
    IReadOnlyList<ValidatedEncoder> ValidatedEncoders,
    bool UsePattern = false,
    bool BusyPattern = false,
    string? DryInputPath = null);

/// <summary>
/// 工作进程（PrivateChat.Agent.exe --session-worker 管道名 服务进程号）：服务每次远程以 SYSTEM 在控制台会话里起一个，
/// 里面跑的就是普通模式下的 <see cref="SessionRunner"/>（截屏、编码、中继和快速线路、声音、键鼠），剪贴板经服务转给界面进程。
/// 服务说停（stop）、管道断了、服务进程没了，都收掉会话退出——不会留下一个没人管的 SYSTEM 进程在截屏。
/// 管道协议（<see cref="PipePeer"/>）：服务 → 工作进程 start{WorkerStart}、stop；工作进程 → 服务 finished{reason}（通知）、readClipboard、writeClipboard{text}。
/// </summary>
internal static class SessionWorker
{
    public static int Run(string pipe, int servicePid, string logDir, bool console = false)
    {
        if (console) Console.OutputEncoding = System.Text.Encoding.UTF8;
        AgentLog.Init(logDir, console, fileName: $"worker-{Environment.ProcessId}.log");
        AgentLog.Info($"工作进程启动（进程 {Environment.ProcessId}，会话 {ConsoleSession.CurrentProcessSession()}）");
        using var stop = new CancellationTokenSource();
        WatchService(servicePid, stop);
        SessionRunner? runner = null;
        var gate = new SemaphoreSlim(1, 1);
        try
        {
            using var channel = PipeChannel.ConnectAsync(pipe, servicePid, TokenImpersonationLevel.Impersonation, 20_000, stop.Token).GetAwaiter().GetResult();
            PipePeer? peer = null;
            peer = new PipePeer(channel, async (cmd, message) =>
            {
                switch (cmd)
                {
                    case "start":
                    {
                        var start = PipePeer.Body<WorkerStart>(message);
                        await gate.WaitAsync();
                        try
                        {
                            if (runner is not null) throw new InvalidOperationException("已经在远程了");
                            runner = Create(start, new WorkerUi(peer!));
                            runner.Start();
                        }
                        finally
                        {
                            gate.Release();
                        }
                        var started = runner;
                        _ = started.Finished.ContinueWith(async t =>
                        {
                            try { await peer!.NotifyAsync("finished", new JsonObject { ["reason"] = t.Result }, CancellationToken.None); }
                            catch (Exception) { }
                        }, TaskScheduler.Default);
                        AgentLog.Info($"远程会话开始：{start.Request.SessionId}（{start.Request.ControllerName}，{start.Request.Mode}）");
                        return [];
                    }
                    case "stop":
                        await StopAsync();
                        // 先让回答发出去再断
                        _ = Task.Delay(200).ContinueWith(_ => stop.Cancel(), TaskScheduler.Default);
                        return [];
                    default:
                        throw new InvalidOperationException($"不认识的命令 {cmd}");
                }
            });
            peer.RunAsync(stop.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AgentLog.Error("工作进程出错", ex);
        }
        catch (OperationCanceledException) { }
        StopAsync().GetAwaiter().GetResult();
        AgentLog.Info("工作进程退出");
        return 0;

        async Task StopAsync()
        {
            await gate.WaitAsync();
            try
            {
                if (runner is { } r)
                {
                    runner = null;
                    await r.DisposeAsync();
                }
            }
            finally
            {
                gate.Release();
            }
        }
    }

    private static SessionRunner Create(WorkerStart start, IAgentUi ui)
    {
        var test = Environment.GetEnvironmentVariable("PRIVATECHAT_AGENT_TEST") == "1";
        var pattern = test && (start.UsePattern || start.BusyPattern);
        var dryInput = test ? start.DryInputPath : null;
        var outputs = start.ValidatedOutputs.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        var input = new InputController(dryInput is not null ? new DryRunInputSink(dryInput) : new SendInputSink());
        IFrameSource Source() => !pattern ? ScreenCaptures.Create(outputs) : start.BusyPattern ? new BusyPatternSource() : new TestPatternSource();
        IAudioSource Audio() => pattern ? new TestToneSource() : new WasapiLoopback();
        return new SessionRunner(start.Request, start.Server, Source, () => start.ValidatedEncoders, Audio, input, ui);
    }

    /// <summary>服务进程没了就退出（会话 0 的作业对象装不了控制台会话里的进程，只能自己看着）。</summary>
    private static void WatchService(int servicePid, CancellationTokenSource stop)
    {
        var handle = Native.OpenProcess(Native.SYNCHRONIZE, false, (uint)servicePid);
        if (handle == IntPtr.Zero)
        {
            AgentLog.Warn($"打不开服务进程 {servicePid}（{Marshal.GetLastWin32Error()}），只靠管道断开来退出");
            return;
        }
        var thread = new Thread(() =>
        {
            Native.WaitForSingleObject(handle, -1);
            Native.CloseHandle(handle);
            AgentLog.Warn("服务进程没了，工作进程退出");
            stop.Cancel();
            // 收尾最多等 5 秒，还不走就直接退出
            Thread.Sleep(5000);
            Environment.Exit(2);
        }) { IsBackground = true, Name = "service-watch" };
        thread.Start();
    }

    /// <summary>工作进程里的"界面"：只有剪贴板用得上，经服务转给登录用户的界面进程（没人登录时读不到、写不了）。</summary>
    private sealed class WorkerUi(PipePeer peer) : IAgentUi
    {
        private static readonly TimeSpan ClipboardTimeout = TimeSpan.FromSeconds(5);

        public event Action? DisconnectRequested { add { } remove { } }
        public event Action? SetupRequested { add { } remove { } }

        public async Task<string?> ReadClipboardAsync()
            => (await peer.CallAsync("readClipboard", null, ClipboardTimeout, CancellationToken.None))["text"]?.GetValue<string>();

        public Task WriteClipboardAsync(string text)
            => peer.CallAsync("writeClipboard", new JsonObject { ["text"] = text }, ClipboardTimeout, CancellationToken.None);

        public Task<string?> AskSetupAsync(string? error, CancellationToken ct) => Task.FromResult<string?>(null);
        public void ShowDeviceCode(string? code, string? serverUrl) { }
        public void SetState(AgentState state, string detail) { }
        public void ShowConnectionProblem(ConnectionProblem? problem, string? serverUrl) { }
        public Task<bool?> AskConsentAsync(AgentSessionRequest request, CancellationToken ct) => Task.FromResult<bool?>(null);
        public void ShowBanner(AgentSessionRequest? request) { }
    }

    private static class Native
    {
        public const uint SYNCHRONIZE = 0x00100000;

        [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
        [DllImport("kernel32.dll")] public static extern int WaitForSingleObject(IntPtr handle, int milliseconds);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr handle);
    }
}

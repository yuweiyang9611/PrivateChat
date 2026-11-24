using System.Security.Cryptography;
using System.Text.Json.Nodes;
using PrivateChat.Agent.Platform;
using PrivateChat.Agent.Session;
using PrivateChat.Agent.Ui;
using PrivateChat.Agent.Video;
using PrivateChat.Contracts.Remote;

namespace PrivateChat.Agent.Service;

/// <summary>
/// 服务模式下的一次远程（<see cref="ISessionHost"/>）：以 SYSTEM 在现在的控制台会话里起一个工作进程（<see cref="SessionWorker"/>），
/// 管道连上后把会话交给它，转发剪贴板（经 ui，也就是界面进程），等它报结束。
/// 工作进程起不来、管道断了、进程没了，都算会话结束（原因写清楚，AgentService 会告诉服务器）。
/// </summary>
internal sealed class WorkerSessionHost(
    AgentSessionRequest request,
    Func<WorkerStart> startInfo,
    IAgentUi ui,
    string logDir) : ISessionHost
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(20);

    private readonly TaskCompletionSource<string> _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _cts = new();
    private Task _run = Task.CompletedTask;
    private LaunchedProcess? _process;
    private PipePeer? _peer;

    public Guid Id => request.SessionId;

    public Task<string> Finished => _finished.Task;

    public void Start() => _run = Task.Run(RunAsync);

    private void Finish(string reason) => _finished.TrySetResult(reason);

    private async Task RunAsync()
    {
        var ct = _cts.Token;
        try
        {
            var session = ConsoleSession.ActiveId();
            if (session == ConsoleSession.None)
            {
                Finish("这台电脑的屏幕正在切换用户，起不来远程");
                return;
            }
            var pipe = $"PrivateChat.Agent.W.{Convert.ToHexString(RandomNumberGenerator.GetBytes(16))}";
            var process = _process = SessionLauncher.StartAsSystem(session, Environment.ProcessPath!,
                ["--session-worker", pipe, Environment.ProcessId.ToString(), logDir, .. SessionLauncher.CurrentUserOnly ? new[] { "--console" } : []],
                environment: new Dictionary<string, string?> { ["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = Environment.GetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR") });
            AgentLog.Info($"远程 {request.SessionId}：工作进程 {process.Id}（会话 {session}）");
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connect.CancelAfter(ConnectTimeout);
            PipeChannel channel;
            try
            {
                channel = await PipeChannel.ListenAsync(pipe, PipeChannel.Security(SessionLauncher.SystemAccount), process.Id, connect.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                process.Kill();
                Finish($"工作进程 {ConnectTimeout.TotalSeconds:0} 秒没连上（退出码 {process.ExitCode?.ToString() ?? "还在跑"}）");
                return;
            }
            var peer = _peer = new PipePeer(channel, HandleAsync);
            var receive = peer.RunAsync(ct);
            await peer.CallAsync("start", PipePeer.With(startInfo()), CallTimeout, ct);
            var exited = process.WaitForExitAsync(ct);
            await Task.WhenAny(receive, exited);
            // 正常结束时工作进程先报 finished（通知）再退出；没报就没了 = 意外
            Finish(process.HasExited ? $"工作进程意外退出（退出码 {process.ExitCode}）" : "和工作进程的管道断了");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            AgentLog.Error($"远程 {request.SessionId}：工作进程出错", ex);
            Finish($"起不来工作进程：{ex.Message}");
        }
    }

    private async Task<JsonObject?> HandleAsync(string cmd, JsonObject message)
    {
        switch (cmd)
        {
            case "finished":
                Finish(message["reason"]?.GetValue<string>() ?? "ended");
                return null;
            case "readClipboard":
                return new JsonObject { ["text"] = await ui.ReadClipboardAsync() };
            case "writeClipboard":
                await ui.WriteClipboardAsync(message["text"]?.GetValue<string>() ?? "");
                return [];
            default:
                throw new InvalidOperationException($"不认识的命令 {cmd}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_peer is { } peer)
        {
            try { await peer.CallAsync("stop", null, TimeSpan.FromSeconds(8), CancellationToken.None); }
            catch (Exception ex) { AgentLog.Warn($"远程 {request.SessionId}：叫工作进程停下没回音：{ex.Message}"); }
        }
        await _cts.CancelAsync();
        if (_process is { } process)
        {
            if (!process.WaitForExit(5000))
            {
                AgentLog.Warn($"远程 {request.SessionId}：工作进程 {process.Id} 5 秒没退出，结束它");
                process.Kill();
            }
            process.Dispose();
        }
        try { await _run; }
        catch (Exception) { }
        if (_peer is { } p) await p.Channel.DisposeAsync();
        Finish("stopped");
        _cts.Dispose();
    }
}

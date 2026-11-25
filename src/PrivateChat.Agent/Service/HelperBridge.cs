using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using PrivateChat.Agent.Platform;
using PrivateChat.Agent.Ui;
using PrivateChat.Agent.Video;
using PrivateChat.Contracts.Remote;

namespace PrivateChat.Agent.Service;

/// <summary>
/// 服务这边的"界面"（<see cref="IAgentUi"/>）：服务在会话 0，自己没法显示任何东西，托盘、同意窗口、红条、设备码、剪贴板都转给
/// 控制台会话里以登录用户身份跑的界面进程（<see cref="HelperProcess"/>）。界面进程由 <see cref="HelperSupervisor"/> 起、看着、掉了重起。
/// - 状态类的（托盘状态、设备码、连不上的原因、红条）记下最后一次的，界面进程（重新）连上时补发——它晚到、重启过都不会漏；
/// - 同意：没有界面进程（没人登录、刚好在重启）就当没人回答，立刻返回 null（不让控制端白等 30 秒）；有就等它回答，请求取消时叫它收起窗口；
/// - 设置：服务的服务器地址决定它装哪个版本（以 SYSTEM 运行），不能让普通用户改——这里不问界面进程，等管理员设置（<see cref="Setup"/>）。
/// </summary>
internal sealed class HelperBridgeUi : IAgentUi
{
    private readonly Lock _gate = new();
    private PipePeer? _peer;
    private (AgentState State, string Detail) _state = (AgentState.Connecting, "正在启动");
    private (string? Code, string? Server) _deviceCode;
    private (ConnectionProblem? Problem, string? Server) _problem;
    private AgentSessionRequest? _banner;
    private readonly Channel<string> _setup = Channel.CreateUnbounded<string>();

    public event Action? DisconnectRequested;
    public event Action? SetupRequested;

    /// <summary>托盘上现在写的状态（管理管道的 status 用）。</summary>
    public string State => $"{_state.State}：{_state.Detail}";

    /// <summary>界面进程最近报的显示器（物理像素）；还没报过时空。</summary>
    public IReadOnlyList<ScreenInfo> Screens { get; private set; } = [];

    /// <summary>显示器变了（界面进程报的）。</summary>
    public event Action? DisplayChanged;

    /// <summary>界面进程连上了（peer）或断了（null）。连上时把记着的状态补发过去。</summary>
    public void Attach(PipePeer? peer)
    {
        lock (_gate) _peer = peer;
        if (peer is null) return;
        Notify("setState", new JsonObject { ["state"] = _state.State.ToString(), ["detail"] = _state.Detail });
        if (_deviceCode.Code is not null) Notify("showDeviceCode", new JsonObject { ["code"] = _deviceCode.Code, ["server"] = _deviceCode.Server });
        if (_problem.Problem is not null) Notify("showConnectionProblem", new JsonObject { ["problem"] = PipePeer.With(_problem.Problem), ["server"] = _problem.Server });
        if (_banner is not null) Notify("showBanner", new JsonObject { ["request"] = PipePeer.With(_banner) });
    }

    /// <summary>界面进程发来的通知（断开、显示器）。</summary>
    public Task<JsonObject?> HandleAsync(string cmd, JsonObject message)
    {
        switch (cmd)
        {
            case "disconnect":
                DisconnectRequested?.Invoke();
                break;
            case "screens":
                Screens = [.. (message["screens"]?.AsArray() ?? []).Select((s, i) => new ScreenInfo(i,
                    new Rectangle(s!["x"]!.GetValue<int>(), s["y"]!.GetValue<int>(), s["width"]!.GetValue<int>(), s["height"]!.GetValue<int>()),
                    s["primary"]?.GetValue<bool>() ?? false, s["device"]?.GetValue<string>()))];
                DisplayChanged?.Invoke();
                break;
            default:
                throw new InvalidOperationException($"不认识的命令 {cmd}");
        }
        return Task.FromResult<JsonObject?>(null);
    }

    /// <summary>
    /// 管理员（安装程序、--configure，经管理管道）给的配置串：交给 AskSetupAsync，并叫 AgentService 重新设置（它在等的话直接拿到）。
    /// 界面进程那边的"重新设置"不走这里（普通用户不能改服务器地址），它自己以管理员身份起 --configure。
    /// </summary>
    public void AdminSetup(string text)
    {
        // 先叫"重新设置"再交配置串：AgentService 正在等配置串时"重新设置"什么都不做（同步就判断完了），配置串到了它直接拿去用；
        // 连着服务器时先被打断、回到等配置串，再拿到这一份。反过来的话，等着的那边拿到配置串刚开始登记就被"重新设置"打断，又回去干等
        while (_setup.Reader.TryRead(out _)) { } // 只留最新的
        SetupRequested?.Invoke();
        _setup.Writer.TryWrite(text);
    }

    private PipePeer? Peer
    {
        get
        {
            lock (_gate) return _peer;
        }
    }

    private void Notify(string cmd, JsonObject body)
    {
        if (Peer is not { } peer) return;
        _ = Send();

        async Task Send()
        {
            try { await peer.NotifyAsync(cmd, body, CancellationToken.None); }
            catch (Exception ex) { AgentLog.Warn($"发给界面进程失败（{cmd}）：{ex.Message}"); }
        }
    }

    public async Task<string?> AskSetupAsync(string? error, CancellationToken ct)
    {
        if (error is { Length: > 0 }) AgentLog.Warn($"配置无效：{error}");
        AgentLog.Info("等管理员设置服务器（安装程序或\"重新设置\"）");
        return await _setup.Reader.ReadAsync(ct);
    }

    public void ShowDeviceCode(string? code, string? serverUrl)
    {
        _deviceCode = (code, serverUrl);
        Notify("showDeviceCode", new JsonObject { ["code"] = code, ["server"] = serverUrl });
    }

    public void SetState(AgentState state, string detail)
    {
        _state = (state, detail);
        Notify("setState", new JsonObject { ["state"] = state.ToString(), ["detail"] = detail });
    }

    public void ShowConnectionProblem(ConnectionProblem? problem, string? serverUrl)
    {
        _problem = (problem, serverUrl);
        Notify("showConnectionProblem", new JsonObject { ["problem"] = problem is null ? null : PipePeer.With(problem), ["server"] = serverUrl });
    }

    public async Task<bool?> AskConsentAsync(AgentSessionRequest request, CancellationToken ct)
    {
        if (Peer is not { } peer)
        {
            AgentLog.Info($"{request.ControllerName} 请求远程，但这台电脑前没有登录的人（或界面进程不在），没人能点同意");
            return null;
        }
        try
        {
            var reply = await peer.CallAsync("askConsent", new JsonObject { ["request"] = PipePeer.With(request) },
                TimeSpan.FromSeconds(Math.Max(1, request.ExpiresInSeconds) + 10), ct);
            return reply["answer"]?.GetValue<bool?>();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 请求取消了（对方取消、超时、改用 PIN）：叫界面进程收起窗口。不能用 ct.Register 发：取消时先跑的是 CallAsync 里等回答的那个回调，
            // 它让这里接着往下走、把后注册的回调撤掉了，通知就发不出去
            Notify("cancelConsent", new JsonObject { ["sessionId"] = request.SessionId.ToString() });
            return null;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or PipeCallException)
        {
            AgentLog.Warn($"同意窗口没有回答：{ex.Message}");
            return null;
        }
    }

    public void ShowBanner(AgentSessionRequest? request)
    {
        _banner = request;
        Notify("showBanner", new JsonObject { ["request"] = request is null ? null : PipePeer.With(request) });
    }

    public async Task<string?> ReadClipboardAsync()
    {
        if (Peer is not { } peer) return null;
        return (await peer.CallAsync("readClipboard", null, TimeSpan.FromSeconds(5), CancellationToken.None))["text"]?.GetValue<string>();
    }

    public async Task WriteClipboardAsync(string text)
    {
        if (Peer is not { } peer) return;
        await peer.CallAsync("writeClipboard", new JsonObject { ["text"] = text }, TimeSpan.FromSeconds(5), CancellationToken.None);
    }
}

/// <summary>
/// 让控制台会话里一直有一个界面进程：有人登录着就以他的身份起 --helper，连上管道（只给 SYSTEM 和这个用户，核对进程号和会话号）；
/// 它退出了、崩了就过一会儿再起（1、2、5、10、30 秒）；切换用户、注销、换了控制台会话就收掉旧的、给新会话起一个。
/// 没人登录（登录界面）时不起。
/// </summary>
internal sealed class HelperSupervisor(HelperBridgeUi bridge, IReadOnlyList<string> extraArgs)
{
    private static readonly int[] BackoffSeconds = [1, 2, 5, 10, 30];

    public async Task RunAsync(ChannelReader<ServiceHost.SessionChange> changes, CancellationToken ct)
    {
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            var session = ConsoleSession.ActiveId();
            if (session == ConsoleSession.None || ConsoleSession.UserName(session) is null)
            {
                await WaitForChange(changes, TimeSpan.FromSeconds(2), ct);
                continue;
            }
            var startedAt = Environment.TickCount64;
            try
            {
                await RunHelperAsync(session, changes, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                AgentLog.Warn($"界面进程（会话 {session}）：{ex.Message}");
            }
            finally
            {
                bridge.Attach(null);
            }
            // 跑了一分钟以上的算正常退出（注销、切换用户），从头计；一起来就掉的越等越久
            failures = Environment.TickCount64 - startedAt > 60_000 ? 0 : failures + 1;
            if (failures > 0) await WaitForChange(changes, TimeSpan.FromSeconds(BackoffSeconds[Math.Min(failures, BackoffSeconds.Length) - 1]), ct);
        }
    }

    /// <summary>起一个界面进程、连上、等到它退出 / 管道断了 / 控制台会话换了。</summary>
    private async Task RunHelperAsync(uint session, ChannelReader<ServiceHost.SessionChange> changes, CancellationToken ct)
    {
        var user = SessionUser(session);
        var pipe = $"PrivateChat.Agent.H.{session}.{Convert.ToHexString(RandomNumberGenerator.GetBytes(16))}";
        using var process = SessionLauncher.StartAsUser(session, Environment.ProcessPath!, ["--helper", pipe, Environment.ProcessId.ToString(), .. extraArgs]);
        AgentLog.Info($"界面进程 {process.Id}（会话 {session}）");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            connect.CancelAfter(TimeSpan.FromSeconds(30));
            PipeChannel channel;
            try
            {
                channel = await PipeChannel.ListenAsync(pipe, PipeChannel.Security(PipeChannel.LocalSystem, user), process.Id, connect.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"30 秒没连上（退出码 {process.ExitCode?.ToString() ?? "还在跑"}）");
            }
            await using (channel)
            {
                var peer = new PipePeer(channel, bridge.HandleAsync);
                var receive = peer.RunAsync(stop.Token);
                bridge.Attach(peer);
                var moved = WatchConsole(session, changes, stop.Token);
                await Task.WhenAny(receive, moved, process.WaitForExitAsync(stop.Token));
                if (moved.IsCompletedSuccessfully)
                {
                    AgentLog.Info($"控制台会话换了，收掉会话 {session} 的界面进程");
                    try { await peer.NotifyAsync("quit", null, CancellationToken.None); }
                    catch (Exception) { }
                }
            }
        }
        finally
        {
            await stop.CancelAsync();
            if (!process.WaitForExit(3000)) process.Kill();
        }
    }

    /// <summary>控制台会话不再是 session（换人、注销后没人了）时完成。</summary>
    private static async Task WatchConsole(uint session, ChannelReader<ServiceHost.SessionChange> changes, CancellationToken ct)
    {
        while (true)
        {
            await WaitForChange(changes, TimeSpan.FromSeconds(2), ct);
            var now = ConsoleSession.ActiveId();
            if (now == ConsoleSession.None) continue; // 正在切换：等它定下来
            if (now != session || ConsoleSession.UserName(session) is null) return;
        }
    }

    private static async Task WaitForChange(ChannelReader<ServiceHost.SessionChange> changes, TimeSpan timeout, CancellationToken ct)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(timeout);
        try
        {
            await changes.ReadAsync(wait.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        catch (ChannelClosedException) { await Task.Delay(timeout, ct); }
    }

    private static SecurityIdentifier SessionUser(uint session)
    {
        if (SessionLauncher.CurrentUserOnly) return SessionLauncher.SystemAccount;
        using var identity = SessionLauncher.UserIdentity(session);
        return identity.User ?? throw new InvalidOperationException("拿不到登录用户的 SID");
    }
}

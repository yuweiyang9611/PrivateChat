using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using PrivateChat.Agent.Audio;
using PrivateChat.Agent.Input;
using PrivateChat.Agent.Session;
using PrivateChat.Agent.Ui;
using PrivateChat.Agent.Video;
using PrivateChat.Contracts.Remote;

namespace PrivateChat.Agent;

/// <summary>启动参数（Program 解析命令行）。</summary>
public sealed record AgentOptions(string DataDir, string DeviceName, bool UsePattern, string? DryInputPath, bool Headless, bool BusyPattern = false);

/// <summary>
/// Agent 的主流程：
/// 没配置 → 要配置串；没令牌 → 设备码登记；有令牌 → 连 /hubs/agent 等远程请求（弹窗征求同意 / 无人值守直接开始），
/// 会话期间显示红条，本机随时可以断开。令牌被吊销 → 回到设备码登记。
/// </summary>
public sealed class AgentService
{
    public static string Version => PretendVersion ?? typeof(AgentService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0";
    /// <summary>自动化测试用（--pretend-version，只在 PRIVATECHAT_AGENT_TEST=1 时生效）：假装自己是个旧版本，好触发自动升级。</summary>
    public static string? PretendVersion { get; set; }
    /// <summary>自动升级换好了新版本、新版本已经起来：这个进程该退出了（Program 收到后结束运行）。</summary>
    public event Action? RestartRequested;

    private readonly AgentOptions _options;
    private readonly IAgentUi _ui;
    private readonly InputController _input;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Updater _updater;
    /// <summary>有理由现在就查一次更新（刚连上服务器——服务器多半刚部署过新版本）。</summary>
    private readonly SemaphoreSlim _updateSignal = new(0, 1);
    /// <summary>新版本已经启动、自己正要退出：新来的请求不接了（新版本连上服务器时会收到补发的）。</summary>
    private volatile bool _updating;
    private AgentConfig _config;
    private ServerInfo? _server;
    private SessionRunner? _runner;
    private (AgentState State, string Detail) _connectionState = (AgentState.Connecting, "正在连接");
    private CancellationTokenSource? _consent;
    private Guid? _consentFor;
    private CancellationTokenSource? _reset;
    /// <summary>最近结束的会话：迟到的"开始"通知（同意之后对方立刻挂了、重连补发）不再开红条。</summary>
    private readonly Queue<Guid> _endedOrder = new();
    private readonly HashSet<Guid> _ended = [];
    /// <summary>上一次记进日志的连接问题，和它之后又重复了几次。</summary>
    private string? _lastProblem;
    private int _sameProblem;
    private string? _transportLogged;

    /// <summary>试局域网直连通不通的超时（家里的服务器正常几毫秒就应答；不在家时别让每次连接都白等）。</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);
    /// <summary>走公网时多久看一次能不能改成局域网直连（另外网络一变就看）。</summary>
    private static readonly TimeSpan RouteCheckInterval = TimeSpan.FromMinutes(1);
    /// <summary>直连试得通、长连接却连不上时，这么久之内先不试直连，免得来回折腾。</summary>
    private static readonly TimeSpan DirectCooldown = TimeSpan.FromMinutes(2);
    private DateTime _skipDirectUntil;
    /// <summary>"现在就看看能不能直连"：网络变了、刚拿到直连入口时放一个信号。</summary>
    private readonly SemaphoreSlim _routeCheck = new(0, 1);

    public AgentService(AgentOptions options, IAgentUi ui)
    {
        _options = options;
        _ui = ui;
        _input = new InputController(options.DryInputPath is { } path ? new DryRunInputSink(path) : new SendInputSink());
        _config = AgentConfig.Load(options.DataDir);
        _updater = new Updater(options.DataDir);
        ui.DisconnectRequested += () => _ = DisconnectLocallyAsync();
        ui.SetupRequested += () => _reset?.Cancel();
    }

    private IFrameSource CreateSource() => _options.BusyPattern ? new BusyPatternSource() : _options.UsePattern ? new TestPatternSource() : new GdiScreenSource();

    /// <summary>电脑正在播放的声音；测试画面配测试音（一直响的 440 Hz），不碰声卡。</summary>
    private IAudioSource CreateAudioSource() => _options.UsePattern ? new TestToneSource() : new WasapiLoopback();

    public async Task RunAsync(CancellationToken ct)
    {
        _ = UpdateLoopAsync(ct);
        string? setupError = null;
        while (!ct.IsCancellationRequested)
        {
            using var reset = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _reset = reset;
            try
            {
                if (!_config.HasServer || setupError is not null)
                {
                    _ui.SetState(AgentState.NeedsSetup, "需要设置服务器");
                    var text = await _ui.AskSetupAsync(setupError, ct);
                    if (text is null)
                    {
                        // 关掉了设置窗口：等用户从托盘菜单再打开
                        await Task.Delay(Timeout.Infinite, reset.Token);
                        continue;
                    }
                    if (!AgentConfig.TryParseSetup(text, out var url, out var id, out var secret))
                    {
                        setupError = "配置串或服务器地址不对，请从网页「添加电脑」里复制";
                        continue;
                    }
                    setupError = null;
                    if (url != _config.ServerUrl) _config = new AgentConfig();
                    _config.ServerUrl = url;
                    _config.AccessClientId = id ?? "";
                    _config.AccessClientSecret = secret ?? "";
                    _config.Save(_options.DataDir);
                }
                if (_config.DeviceToken.Length == 0)
                {
                    await RegisterAsync(reset.Token);
                    continue;
                }
                if (await ConnectAsync(reset.Token) == Outcome.Revoked)
                {
                    AgentLog.Warn("设备已被移除（或令牌失效），回到设备码登记");
                    await StopRunnerAsync(revoked: true);
                    _config.DeviceTokenProtected = "";
                    _config.DirectUrl = _config.DirectPin = "";
                    _config.DeviceId = null;
                    _config.Save(_options.DataDir);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && reset.IsCancellationRequested)
            {
                // 托盘菜单"重新设置"
                _ui.ShowDeviceCode(null, null);
                _ui.ShowConnectionProblem(null, null);
                await StopRunnerAsync();
                setupError = "";
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // 其它意外（包括 HTTP 请求超时，它也是 OperationCanceledException）：稍等重来，不当成"重新设置"
                AgentLog.Warn($"出错，5 秒后重试：{ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
        await StopRunnerAsync();
    }

    // ---- 自动升级 ----

    /// <summary>
    /// 启动后、每次连上服务器时、之后每 6 小时查一次：服务器上的安装包比自己新就下载、核对、换掉自己并重启（见 <see cref="Updater"/>）。
    /// 正在被远程的时候不换，等结束了再说。
    /// </summary>
    private async Task UpdateLoopAsync(CancellationToken ct)
    {
        if (Updater.DisabledReason() is { } reason)
        {
            AgentLog.Info($"自动升级没有启用：{reason}");
            return;
        }
        _updater.CleanUp();
        var first = true;
        var wait = TimeSpan.FromSeconds(10);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _updateSignal.WaitAsync(wait, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (first)
            {
                // 刚升级完的话，旧进程这会儿才退干净：把它留下的文件再清一遍
                first = false;
                _ = Task.Delay(TimeSpan.FromSeconds(8), ct).ContinueWith(_ => _updater.CleanUp(), ct, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
            }
            wait = Updater.CheckInterval;
            if (!_config.HasServer) wait = TimeSpan.FromMinutes(1);
            else
                try
                {
                    var outcome = await TryUpdateAsync(ct);
                    if (outcome == UpdateOutcome.Restarting)
                    {
                        RestartRequested?.Invoke();
                        return;
                    }
                    if (outcome == UpdateOutcome.Deferred) wait = TimeSpan.FromMinutes(2);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    // 升级不成不影响使用：记下来，过半小时再试（连不上服务器这类原因，主流程那边已经在说了）
                    AgentLog.Warn($"自动升级没有成功，稍后再试：{ex.Message}");
                    wait = TimeSpan.FromMinutes(30);
                }
        }
    }

    private async Task<UpdateOutcome> TryUpdateAsync(CancellationToken ct)
    {
        var server = ServerFromConfig();
        var manifest = await _updater.FetchManifestAsync(server, ct);
        if (manifest is null || !UpdateManifest.IsNewer(manifest.Version, Version)) return UpdateOutcome.UpToDate;
        if (_runner is not null)
        {
            AgentLog.Info($"有新版本 {manifest.Version}，等这次远程结束后再升级");
            return UpdateOutcome.Deferred;
        }

        AgentLog.Info($"发现新版本 {manifest.Version}（现在是 {Version}），开始下载");
        var downloaded = await _updater.DownloadAsync(server, manifest, ct);
        AgentLog.Info($"新版本 {manifest.Version} 下载完成，大小和 SHA-256 核对无误");

        await _gate.WaitAsync(ct);
        try
        {
            if (_runner is not null) return UpdateOutcome.Deferred; // 下载的工夫开始了远程：文件留着，结束后直接换
            _updater.SwapAndRelaunch(downloaded, Version, Environment.GetCommandLineArgs()[1..]);
            _updating = true;
            _consent?.Cancel();
            AgentLog.Info($"已换成新版本 {manifest.Version} 并启动，这个进程退出");
            return UpdateOutcome.Restarting;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---- 设备码登记 ----

    private ServerInfo ServerFromConfig() => new(_config.ServerUrl, _config.AccessClientId, _config.AccessClientSecret, _config.DeviceToken);

    /// <summary>局域网直连这条路（服务器给过入口、而且是 https）：没有 Access 头，证书按指纹认。</summary>
    private ServerInfo? DirectFromConfig()
        => _config.DirectPin.Length > 0 && Uri.TryCreate(_config.DirectUrl, UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps
            ? new ServerInfo(_config.DirectUrl, "", "", _config.DeviceToken, _config.DirectPin)
            : null;

    private string OnlineText => _server?.IsDirect == true ? "在线（局域网直连）" : "在线";

    /// <summary>
    /// 连不上服务器：认出原因（加密握手失败的真正原因在异常的里层），写进日志。
    /// 每 10 秒重试一次，同一个原因只在第一次记全，之后每 30 次（约 5 分钟）再记一次，免得把日志刷满。
    /// </summary>
    private ConnectionProblem Diagnose(string what, Exception ex)
    {
        var server = new Uri(_config.ServerUrl);
        var problem = ConnectionDiagnosis.Describe(ex, server, TlsObserver.Take(), bundledTls: AgentTransport.ModeFor(server) == TlsMode.Bundled);
        var signature = problem.Summary + problem.Technical;
        if (signature != _lastProblem)
        {
            _lastProblem = signature;
            _sameProblem = 0;
        }
        else if (++_sameProblem % 30 != 0)
        {
            return problem;
        }
        AgentLog.Warn($"{what}：{problem.Summary}。{problem.Advice}");
        AgentLog.Warn($"  详细：{problem.Technical}");
        return problem;
    }

    /// <summary>走哪条路、用哪一种加密连服务器：第一次用到、或者变了的时候记一笔。</summary>
    private void LogTransport(ServerInfo? server = null, string? directProblem = null)
    {
        server ??= ServerFromConfig();
        var uri = new Uri(server.Url);
        var text = server.IsDirect
            ? $"局域网直连 {server.Url}：{AgentTransport.Describe(uri)}，只认服务器那张证书"
            : $"{server.Url}：{AgentTransport.Describe(uri)}{(directProblem is null ? "" : $"（局域网直连不通：{directProblem}）")}";
        if (text == _transportLogged) return;
        _transportLogged = text;
        AgentLog.Info($"连接方式 {text}");
    }

    // ---- 选路：在家时局域网直连，不在家走公网 ----

    /// <summary>这次走哪条路：局域网直连试得通就用它，否则走配置里的服务器地址（公网）。第二项是直连不通的原因。</summary>
    private async Task<(ServerInfo Server, string? DirectProblem)> ChooseRouteAsync(CancellationToken ct)
    {
        var server = ServerFromConfig();
        if (DirectFromConfig() is not { } direct) return (server, null);
        if (DateTime.UtcNow < _skipDirectUntil) return (server, "刚才连不上，过一会儿再试");
        return await ProbeAsync(direct, ct) is { } problem ? (server, problem) : (direct, null);
    }

    /// <summary>试一下局域网直连：通了返回 null，不通返回原因。</summary>
    private static async Task<string?> ProbeAsync(ServerInfo direct, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProbeTimeout);
        try
        {
            using var api = new ServerApi(direct);
            await api.ProbeAsync(timeout.Token);
            return null;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return timeout.IsCancellationRequested ? "超时" : Innermost(ex).Message;
        }
    }

    private static Exception Innermost(Exception ex)
    {
        while (ex.InnerException is { } inner) ex = inner;
        return ex;
    }

    private void CheckRouteSoon()
    {
        try
        {
            _routeCheck.Release();
        }
        catch (SemaphoreFullException)
        {
            // 已经有一个信号在等着了
        }
    }

    /// <summary>
    /// 走公网时盯着能不能改成局域网直连：网络变了（笔记本回到家、连上家里的 Wi-Fi）马上看，平时每分钟看一次。
    /// 正在被远程、正在问要不要同意时不换（换路要断开重连）。返回 = 该换了。走直连时什么都不做：直连断了自然会重连、重新选路。
    /// </summary>
    private async Task WatchRouteAsync(ServerInfo current, CancellationToken ct)
    {
        if (current.IsDirect)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return;
        }
        while (true)
        {
            // 网络刚变：等几秒，地址、路由就绪了再试
            if (await _routeCheck.WaitAsync(RouteCheckInterval, ct)) await Task.Delay(TimeSpan.FromSeconds(3), ct);
            if (_runner is not null || _consent is { IsCancellationRequested: false } || DateTime.UtcNow < _skipDirectUntil) continue;
            if (DirectFromConfig() is { } direct && await ProbeAsync(direct, ct) is null) return;
        }
    }

    /// <summary>向服务器要局域网直连的入口（地址 + 证书指纹）记下来；有了新的就马上看看能不能直连。</summary>
    private async Task RefreshDirectAsync(ServerInfo server, CancellationToken ct)
    {
        AgentDirectInfo? info;
        try
        {
            using var api = new ServerApi(server);
            info = await api.GetDirectAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            AgentLog.Warn($"查询局域网直连入口失败：{ex.Message}");
            return;
        }
        // 只认 https 的入口：直连靠的就是 TLS + 证书指纹
        var url = info is { Pin.Length: > 0 } && Uri.TryCreate(info.Url, UriKind.Absolute, out var parsed) && parsed.Scheme == Uri.UriSchemeHttps
            ? info.Url.TrimEnd('/')
            : "";
        var pin = url.Length > 0 ? info!.Pin : "";
        if (url == _config.DirectUrl && pin == _config.DirectPin) return;
        _config.DirectUrl = url;
        _config.DirectPin = pin;
        _config.Save(_options.DataDir);
        AgentLog.Info(url.Length > 0 ? $"服务器开了局域网直连：{url}" : "服务器没开局域网直连");
        if (url.Length == 0) return;
        _skipDirectUntil = default;
        CheckRouteSoon();
    }

    /// <summary>
    /// 发给服务器的请求（回复同意、挂断）：走现在这条路；局域网直连恰好断了（刚离开家）就改走公网再试一次。
    /// 服务器已经应答了的（ServerException）不重试。
    /// </summary>
    private async Task<T> CallAsync<T>(ServerInfo server, Func<ServerApi, Task<T>> call)
    {
        try
        {
            using var api = new ServerApi(server);
            return await call(api);
        }
        catch (Exception ex) when (server.IsDirect && ex is not ServerException)
        {
            AgentLog.Warn($"经局域网直连发请求失败，改走公网：{ex.Message}");
            using var api = new ServerApi(ServerFromConfig());
            return await call(api);
        }
    }

    private async Task RegisterAsync(CancellationToken ct)
    {
        LogTransport();
        using var api = new ServerApi(ServerFromConfig());
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            DeviceCode code;
            try
            {
                code = await api.RequestCodeAsync(_options.DeviceName, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                var problem = Diagnose("申请设备码失败", ex);
                _ui.ShowConnectionProblem(problem, _config.ServerUrl);
                _ui.SetState(AgentState.Registering, $"连不上服务器：{problem.Summary}（10 秒后重试）");
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
                continue;
            }
            _lastProblem = null;
            _ui.ShowConnectionProblem(null, null);
            AgentLog.Info("已申请设备码，等待在网页上确认");
            _ui.ShowDeviceCode(code.Code, _config.ServerUrl);
            _ui.SetState(AgentState.Registering, "等待在网页上输入设备码");
            var deadline = DateTime.UtcNow.AddSeconds(code.ExpiresInSeconds - 5);
            var pollFailing = false;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                DevicePoll? poll;
                try
                {
                    poll = await api.PollAsync(code, ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    // 设备码窗口开着：原因写在窗口下面那行状态里
                    var problem = Diagnose("查询设备码状态失败", ex);
                    _ui.SetState(AgentState.Registering, $"连不上服务器：{problem.Summary}，正在重试");
                    pollFailing = true;
                    continue;
                }
                if (pollFailing)
                {
                    pollFailing = false;
                    _lastProblem = null;
                    _ui.SetState(AgentState.Registering, "等待在网页上输入设备码");
                }
                if (poll is null || poll.Status == "expired") break;
                if (poll is { Status: "approved", DeviceToken: { } token })
                {
                    _config.DeviceToken = token;
                    _config.DeviceId = poll.DeviceId;
                    _config.Save(_options.DataDir);
                    _ui.ShowDeviceCode(null, null);
                    if (!_options.Headless) Autostart.EnableOnFirstRegistration(_options.DataDir);
                    AgentLog.Info($"已添加到家庭：设备 {poll.DeviceId}");
                    return;
                }
            }
        }
    }

    // ---- 在线 ----

    private enum Outcome { Revoked, Retry }

    /// <summary>
    /// SignalR 自带的重连试 1 分钟左右就放弃，交给外层：外层重新选路、重新 Start，能分辨出令牌是否已被吊销（401）。
    /// 局域网直连只重试两次（不到 10 秒）：多半是离开家了，早点交给外层改走公网。
    /// </summary>
    private sealed class RetryPolicy(bool direct) : IRetryPolicy
    {
        private readonly int[] _delays = direct ? [0, 2] : [0, 2, 5, 10, 20, 30];
        public TimeSpan? NextRetryDelay(RetryContext context)
            => context.PreviousRetryCount < _delays.Length ? TimeSpan.FromSeconds(_delays[context.PreviousRetryCount]) : null;
    }

    private async Task<Outcome> ConnectAsync(CancellationToken ct)
    {
        // 会话还在进行（直连断了、数据通道已经改走公网）：托盘照旧显示"正在被远程"
        await SetConnectionStateAsync(AgentState.Connecting, "正在连接");
        var (server, directProblem) = await ChooseRouteAsync(ct);
        _server = server;
        LogTransport(server, directProblem);
        var serverUri = new Uri(server.Url);
        // 系统没有 TLS 1.3 时：SignalR 的 HTTP 请求和 WebSocket 都经程序自带的 TLS 1.3（地址改成 http://，见 AgentTransport）；
        // 局域网直连时也自己建连接（证书按指纹认、不经代理）
        using var invoker = AgentTransport.CreateInvoker(serverUri, server.Pin);
        await using var hub = new HubConnectionBuilder()
            .WithUrl(AgentTransport.Address(new Uri(server.Url.TrimEnd('/') + "/hubs/agent")), o =>
            {
                if (server.AccessClientId.Length > 0)
                {
                    o.Headers["CF-Access-Client-Id"] = server.AccessClientId;
                    o.Headers["CF-Access-Client-Secret"] = server.AccessClientSecret;
                }
                o.AccessTokenProvider = () => Task.FromResult<string?>(server.DeviceToken);
                if (invoker is not null)
                {
                    o.HttpMessageHandlerFactory = _ => AgentTransport.CreateHandler(serverUri, server.Pin);
                    // 自己建 WebSocket：SignalR 默认的做法会走系统 TLS。请求头和令牌照它原来的样子带上
                    o.WebSocketFactory = async (context, cancel) =>
                    {
                        var socket = new System.Net.WebSockets.ClientWebSocket();
                        try
                        {
                            foreach (var (name, value) in context.Options.Headers) socket.Options.SetRequestHeader(name, value);
                            if (context.Options.AccessTokenProvider is { } provider && await provider() is { Length: > 0 } token)
                                socket.Options.SetRequestHeader("Authorization", $"Bearer {token}");
                            await socket.ConnectAsync(context.Uri, invoker, cancel);
                            return socket;
                        }
                        catch
                        {
                            socket.Dispose();
                            throw;
                        }
                    };
                }
                else
                {
                    // 校验结果不变，只多记一下没通过校验的证书（连不上时好说清楚原因）
                    o.HttpMessageHandlerFactory = handler =>
                    {
                        if (handler is HttpClientHandler h) h.ServerCertificateCustomValidationCallback = (_, cert, chain, errors) => TlsObserver.Validate(null, cert, chain, errors);
                        return handler;
                    };
                    o.WebSocketConfiguration = ws => ws.RemoteCertificateValidationCallback = TlsObserver.Validate;
                }
            })
            .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()))
            .WithAutomaticReconnect(new RetryPolicy(server.IsDirect))
            .Build();
        var revoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // 处理函数必须立刻返回（花括号写法，不把 Task 交回去）：SignalR 按顺序一个一个处理通知，会等上一个的 Task 结束。
        // 以前写成 r => _ = OnRequestedAsync(r)，这个表达式的值就是那个 Task——同意窗口开着的时候，
        // 后面的通知（对方取消了、又发了一次、用 PIN 直接连）全都排队，要等窗口被点掉或 30 秒超时才轮到
        hub.On<AgentSessionRequest>(AgentHubEvents.SessionRequested, r => { _ = OnRequestedAsync(r); });
        hub.On<AgentSessionEnded>(AgentHubEvents.SessionEnded, e => { _ = OnEndedAsync(e.SessionId, e.Reason); });
        hub.On(AgentHubEvents.Revoked, () => revoked.TrySetResult());
        hub.Reconnecting += _ => SetConnectionStateAsync(AgentState.Connecting, "网络断开，正在重新连接");
        hub.Reconnected += async _ =>
        {
            await SetConnectionStateAsync(AgentState.Online, OnlineText);
            await ReportAsync(hub);
        };
        hub.Closed += _ =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };

        try
        {
            await hub.StartAsync(ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            return Outcome.Revoked;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && server.IsDirect)
        {
            // 直连试得通、长连接却没连上（比如恰好在这时断了）：一会儿之内先不试直连，马上改走公网
            _skipDirectUntil = DateTime.UtcNow + DirectCooldown;
            AgentLog.Warn($"局域网直连连不上，改走公网：{Innermost(ex).Message}");
            return Outcome.Retry;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // 已经添加过的电脑：断网、服务器重启都会到这里，不弹窗，原因写在托盘上。10 秒后外层重新选路再连
            var problem = Diagnose("连接服务器失败", ex);
            await SetConnectionStateAsync(AgentState.Connecting, $"连不上服务器：{problem.Summary}");
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            return Outcome.Retry;
        }
        _lastProblem = null;
        AgentLog.Info(server.IsDirect ? "已连接服务器（局域网直连），等待远程请求" : "已连接服务器，等待远程请求");
        try { _updateSignal.Release(); } catch (SemaphoreFullException) { } // 顺便查一下有没有新版本
        await SetConnectionStateAsync(AgentState.Online, OnlineText);
        await ReportAsync(hub);
        EventHandler displayChanged = (_, _) => _ = ReportAsync(hub);
        if (!_options.Headless) SystemEvents.DisplaySettingsChanged += displayChanged;
        NetworkAddressChangedEventHandler networkChanged = (_, _) => CheckRouteSoon();
        NetworkChange.NetworkAddressChanged += networkChanged;
        using var watching = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            _ = RefreshDirectAsync(server, watching.Token);
            var betterRoute = WatchRouteAsync(server, watching.Token);
            var done = await Task.WhenAny(revoked.Task, closed.Task, betterRoute, Task.Delay(Timeout.Infinite, ct));
            await done; // 取消时抛出
            if (done == betterRoute)
            {
                // 回到家了：断开公网这条，外层重新选路就是局域网直连。正在进行的会话（万一恰好开始了）走它自己的数据通道，不受影响
                AgentLog.Info("能直连服务器了，改走局域网直连");
                await hub.StopAsync(CancellationToken.None);
                return Outcome.Retry;
            }
            // 走公网的长连接试了一分钟都没连上：正在进行的会话收掉（服务器那边早就结束了）。
            // 走直连的断了多半是离开了家：会话的数据通道自己会改走公网接着传，不跟着停
            if (done == revoked.Task) return Outcome.Revoked;
            if (!server.IsDirect) await StopRunnerAsync();
            return Outcome.Retry;
        }
        finally
        {
            await watching.CancelAsync();
            NetworkChange.NetworkAddressChanged -= networkChanged;
            if (!_options.Headless) SystemEvents.DisplaySettingsChanged -= displayChanged;
        }
    }

    private async Task ReportAsync(HubConnection hub)
    {
        try
        {
            using var source = CreateSource();
            var screens = source.Screens().Select(s => new AgentScreen(s.Bounds.Width, s.Bounds.Height, s.IsPrimary)).ToList();
            await hub.InvokeAsync(AgentHubEvents.Report, new AgentReport(Version, RuntimeInformation.OSDescription, screens));
        }
        catch (Exception ex)
        {
            AgentLog.Warn($"上报设备信息失败：{ex.Message}");
        }
    }

    // ---- 会话 ----

    private async Task OnRequestedAsync(AgentSessionRequest request)
    {
        CancellationTokenSource consent;
        await _gate.WaitAsync();
        try
        {
            if (_server is null || _updating || _ended.Contains(request.SessionId)) return;
            if (_runner is { } old && old.Id != request.SessionId) await StopRunnerCoreAsync(); // 服务端保证旧的已经结束
            if (request.Active)
            {
                // 不用同意就开始的（PIN、免同意名单）：之前还挂着的同意窗口是同一个人更早发的请求，
                // 服务器已经把它顶掉了，窗口跟着收起来，别让电脑前的人对着一个没用的窗口点
                _consent?.Cancel();
                if (_runner?.Id != request.SessionId) StartRunner(request);
                return;
            }
            _consent?.Cancel();
            // 用服务器给的相对秒数：这台电脑的时钟不准时，拿绝对时间减本机时间会让弹窗一闪就没
            consent = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, request.ExpiresInSeconds)));
            _consent = consent;
            _consentFor = request.SessionId;
        }
        finally
        {
            _gate.Release();
        }

        bool? answer;
        try
        {
            answer = await _ui.AskConsentAsync(request, consent.Token);
        }
        catch (OperationCanceledException)
        {
            answer = null;
        }
        if (answer is null || consent.IsCancellationRequested || _server is not { } server) return;

        try
        {
            var info = await CallAsync(server, api => api.ConsentAsync(request.SessionId, answer.Value, CancellationToken.None));
            AgentLog.Info($"已{(answer.Value ? "同意" : "拒绝")} {request.ControllerName} 的远程请求");
            if (answer.Value && info?.State == RemoteSessionState.Active)
            {
                await _gate.WaitAsync();
                try
                {
                    // 同意之后、开始之前对方已经挂断（SessionEnded 先到了）：不再开红条
                    if (_runner?.Id != request.SessionId && !_ended.Contains(request.SessionId) && !consent.IsCancellationRequested)
                        StartRunner(request with { Active = true });
                }
                finally
                {
                    _gate.Release();
                }
            }
        }
        catch (Exception ex)
        {
            AgentLog.Warn($"回复远程请求失败：{ex.Message}");
        }
    }

    private void StartRunner(AgentSessionRequest request)
    {
        // 走局域网直连时，数据通道连不上就改走公网（比如会话中途离开了家）
        var server = _server!;
        var runner = new SessionRunner(request, server, server.IsDirect ? ServerFromConfig() : null,
            CreateSource, CreateAudioSource, _input, _ui);
        _runner = runner;
        runner.Start();
        _ui.ShowBanner(request);
        _ui.SetState(AgentState.InSession, $"正在被 {request.ControllerName} 远程");
        AgentLog.Info($"远程会话开始：{request.SessionId}（{request.ControllerName}，{request.Mode}）");
        _ = WatchAsync(runner);
    }

    private async Task WatchAsync(SessionRunner runner)
    {
        var reason = await runner.Finished;
        var stoppedHere = false;
        await _gate.WaitAsync();
        try
        {
            if (_runner == runner)
            {
                AgentLog.Info($"远程会话结束：{reason}");
                MarkEnded(runner.Id);
                await StopRunnerCoreAsync();
                stoppedHere = true;
            }
        }
        finally
        {
            _gate.Release();
        }
        // 这边自己出的问题（采集失败、连不上）：告诉服务器结束，别让控制端干等 30 秒，记录里也是真实原因
        if (stoppedHere && reason is not ("ended" or "stopped") && _server is { } server)
        {
            try
            {
                await CallAsync(server, async api => { await api.EndAsync(runner.Id, CancellationToken.None); return true; });
            }
            catch (Exception ex)
            {
                AgentLog.Warn($"通知服务器结束会话失败：{ex.Message}");
            }
        }
    }

    private void MarkEnded(Guid id)
    {
        if (!_ended.Add(id)) return;
        _endedOrder.Enqueue(id);
        while (_endedOrder.Count > 64) _ended.Remove(_endedOrder.Dequeue());
    }

    private async Task OnEndedAsync(Guid sessionId, string reason)
    {
        await _gate.WaitAsync();
        try
        {
            MarkEnded(sessionId);
            if (_consentFor == sessionId) _consent?.Cancel();
            if (_runner?.Id == sessionId)
            {
                AgentLog.Info($"远程会话被结束：{reason}");
                await StopRunnerCoreAsync();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>本机用户要断开（红条按钮、快捷键、托盘菜单）：先停本地会话，再告诉服务端。</summary>
    public async Task DisconnectLocallyAsync()
    {
        Guid? id;
        await _gate.WaitAsync();
        try
        {
            id = _runner?.Id;
            if (id is { } running) MarkEnded(running);
            await StopRunnerCoreAsync();
        }
        finally
        {
            _gate.Release();
        }
        if (id is { } sessionId && _server is { } server)
        {
            try
            {
                await CallAsync(server, async api => { await api.EndAsync(sessionId, CancellationToken.None); return true; });
            }
            catch (Exception ex)
            {
                AgentLog.Warn($"通知服务器结束会话失败：{ex.Message}");
            }
        }
    }

    private async Task SetConnectionStateAsync(AgentState state, string detail)
    {
        await _gate.WaitAsync();
        try
        {
            _connectionState = (state, detail);
            if (_runner is null) _ui.SetState(state, detail);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StopRunnerAsync(bool revoked = false)
    {
        await _gate.WaitAsync();
        try
        {
            _consent?.Cancel();
            if (revoked)
            {
                _server = null;
                if (_runner is { } runner) MarkEnded(runner.Id);
                if (_consentFor is { } pending) MarkEnded(pending);
                _connectionState = (AgentState.Registering, "设备已被移除，正在重新登记");
            }
            await StopRunnerCoreAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StopRunnerCoreAsync()
    {
        if (_runner is not { } runner) return;
        _runner = null;
        _ui.ShowBanner(null);
        _ui.SetState(_connectionState.State, _connectionState.Detail);
        await runner.DisposeAsync();
    }
}

/// <summary>随用户登录启动：HKCU\…\Run（不需要管理员权限）。</summary>
public static class Autostart
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "PrivateChatAgent";

    public static bool Enabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(Key);
            return key?.GetValue(Name) is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(Key);
            if (value) key.SetValue(Name, $"\"{Environment.ProcessPath}\" --autostart");
            else key.DeleteValue(Name, throwOnMissingValue: false);
        }
    }

    /// <summary>第一次添加成功时默认打开自启（之后用户在托盘菜单里关掉的，不再自动打开）。</summary>
    public static void EnableOnFirstRegistration(string dataDir)
    {
        var marker = Path.Combine(dataDir, "autostart-initialized");
        if (File.Exists(marker)) return;
        try
        {
            Enabled = true;
            File.WriteAllText(marker, DateTime.Now.ToString("O"));
        }
        catch (Exception ex)
        {
            AgentLog.Warn($"设置开机自启失败：{ex.Message}");
        }
    }
}

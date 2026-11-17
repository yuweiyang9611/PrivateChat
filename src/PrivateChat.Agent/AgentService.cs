using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using PrivateChat.Agent.Input;
using PrivateChat.Agent.Session;
using PrivateChat.Agent.Ui;
using PrivateChat.Agent.Video;
using PrivateChat.Contracts.Remote;

namespace PrivateChat.Agent;

/// <summary>启动参数（Program 解析命令行）。</summary>
public sealed record AgentOptions(string DataDir, string DeviceName, bool UsePattern, string? DryInputPath, bool Headless);

/// <summary>
/// Agent 的主流程：
/// 没配置 → 要配置串；没令牌 → 设备码登记；有令牌 → 连 /hubs/agent 等远程请求（弹窗征求同意 / 无人值守直接开始），
/// 会话期间显示红条，本机随时可以断开。令牌被吊销 → 回到设备码登记。
/// </summary>
public sealed class AgentService
{
    public static string Version => typeof(AgentService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0";

    private readonly AgentOptions _options;
    private readonly IAgentUi _ui;
    private readonly InputController _input;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AgentConfig _config;
    private ServerInfo? _server;
    private SessionRunner? _runner;
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

    public AgentService(AgentOptions options, IAgentUi ui)
    {
        _options = options;
        _ui = ui;
        _input = new InputController(options.DryInputPath is { } path ? new DryRunInputSink(path) : new SendInputSink());
        _config = AgentConfig.Load(options.DataDir);
        ui.DisconnectRequested += () => _ = DisconnectLocallyAsync();
        ui.SetupRequested += () => _reset?.Cancel();
    }

    private IFrameSource CreateSource() => _options.UsePattern ? new TestPatternSource() : new GdiScreenSource();

    public async Task RunAsync(CancellationToken ct)
    {
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
                    _config.DeviceTokenProtected = "";
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

    // ---- 设备码登记 ----

    private ServerInfo ServerFromConfig() => new(_config.ServerUrl, _config.AccessClientId, _config.AccessClientSecret, _config.DeviceToken);

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

    /// <summary>用哪一种加密连服务器：第一次用到、或者换了服务器时记一笔。</summary>
    private void LogTransport()
    {
        var text = $"{_config.ServerUrl}：{AgentTransport.Describe(new Uri(_config.ServerUrl))}";
        if (text == _transportLogged) return;
        _transportLogged = text;
        AgentLog.Info($"连接方式 {text}");
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

    /// <summary>SignalR 自带的重连试 1 分钟左右就放弃，交给外层：外层重新 Start，能分辨出令牌是否已被吊销（401）。</summary>
    private sealed class RetryPolicy : IRetryPolicy
    {
        private static readonly int[] Delays = [0, 2, 5, 10, 20, 30];
        public TimeSpan? NextRetryDelay(RetryContext context)
            => context.PreviousRetryCount < Delays.Length ? TimeSpan.FromSeconds(Delays[context.PreviousRetryCount]) : null;
    }

    private async Task<Outcome> ConnectAsync(CancellationToken ct)
    {
        _server = ServerFromConfig();
        var server = _server;
        LogTransport();
        var serverUri = new Uri(server.Url);
        // 系统没有 TLS 1.3 时：SignalR 的 HTTP 请求和 WebSocket 都经程序自带的 TLS 1.3（地址改成 http://，见 AgentTransport）
        using var invoker = AgentTransport.CreateInvoker(serverUri);
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
                    o.HttpMessageHandlerFactory = _ => AgentTransport.CreateHandler(serverUri);
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
            .WithAutomaticReconnect(new RetryPolicy())
            .Build();
        var revoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.On<AgentSessionRequest>(AgentHubEvents.SessionRequested, r => _ = OnRequestedAsync(r));
        hub.On<AgentSessionEnded>(AgentHubEvents.SessionEnded, e => _ = OnEndedAsync(e.SessionId, e.Reason));
        hub.On(AgentHubEvents.Revoked, () => revoked.TrySetResult());
        hub.Reconnecting += _ =>
        {
            _ui.SetState(AgentState.Connecting, "网络断开，正在重新连接");
            return Task.CompletedTask;
        };
        hub.Reconnected += _ =>
        {
            _ui.SetState(_runner is null ? AgentState.Online : AgentState.InSession, "在线");
            return ReportAsync(hub);
        };
        hub.Closed += _ =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                _ui.SetState(AgentState.Connecting, "正在连接");
                await hub.StartAsync(ct);
                break;
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                return Outcome.Revoked;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // 已经添加过的电脑：断网、服务器重启都会到这里，不弹窗，原因写在托盘上
                var problem = Diagnose("连接服务器失败", ex);
                _ui.SetState(AgentState.Connecting, $"连不上服务器：{problem.Summary}");
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
            }
        }
        _lastProblem = null;
        AgentLog.Info("已连接服务器，等待远程请求");
        _ui.SetState(AgentState.Online, "在线");
        await ReportAsync(hub);
        EventHandler displayChanged = (_, _) => _ = ReportAsync(hub);
        if (!_options.Headless) SystemEvents.DisplaySettingsChanged += displayChanged;
        try
        {
            var done = await Task.WhenAny(revoked.Task, closed.Task, Task.Delay(Timeout.Infinite, ct));
            await done; // 取消时抛出
            await StopRunnerAsync();
            return done == revoked.Task ? Outcome.Revoked : Outcome.Retry;
        }
        finally
        {
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
            if (_ended.Contains(request.SessionId)) return;
            if (_runner is { } old && old.Id != request.SessionId) await StopRunnerCoreAsync(); // 服务端保证旧的已经结束
            if (request.Active)
            {
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
            using var api = new ServerApi(server);
            var info = await api.ConsentAsync(request.SessionId, answer.Value, CancellationToken.None);
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
        var runner = new SessionRunner(request, _server!, CreateSource, _input, _ui);
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
                using var api = new ServerApi(server);
                await api.EndAsync(runner.Id, CancellationToken.None);
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

    /// <summary>本机用户要断开（红条按钮、快捷键、托盘菜单）：先告诉服务端，再停。</summary>
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
                using var api = new ServerApi(server);
                await api.EndAsync(sessionId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                AgentLog.Warn($"通知服务器结束会话失败：{ex.Message}");
            }
        }
    }

    private async Task StopRunnerAsync()
    {
        await _gate.WaitAsync();
        try
        {
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
        _ui.SetState(AgentState.Online, "在线");
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

using System.Collections.Frozen;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using PrivateChat.Agent.Audio;
using PrivateChat.Agent.Input;
using PrivateChat.Agent.Session;
using PrivateChat.Agent.Telemetry;
using PrivateChat.Agent.Ui;
using PrivateChat.Agent.Video;
using PrivateChat.Contracts.Remote;

namespace PrivateChat.Agent;

/// <summary>
/// 装成系统服务时换掉的几样（见 Service/AgentServiceHost）：远程在哪儿跑（SYSTEM 工作进程）、硬件探测在哪儿做（控制台会话）、
/// 显示器从哪儿来（界面进程报的：服务在会话 0 里看不到真显示器）、新版本怎么换（交给 --apply-update 换文件、重启服务）。都是 null 就是普通模式。
/// </summary>
internal sealed record AgentHooks(
    Func<AgentSessionRequest, ServerInfo, ISessionHost>? SessionFactory = null,
    TelemetryEnvironment? Telemetry = null,
    Func<IReadOnlyList<ScreenInfo>>? Screens = null,
    Action<string, string, string>? ApplyUpdate = null);

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
    private readonly SemaphoreSlim _updateGate = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();
    private CancellationTokenSource? _updateSettings = new(); // 重新设置期间为 null，旧配置的检查与确认随之取消
    private (CancellationToken Settings, string Version, long Until)? _postponed;
    /// <summary>有理由现在就查一次更新（刚连上服务器——服务器多半刚部署过新版本）。</summary>
    private readonly SemaphoreSlim _updateSignal = new(0, 1);
    /// <summary>新版本已经启动、自己正要退出：新来的请求不接了（新版本连上服务器时会收到补发的）。</summary>
    private volatile bool _updating;
    private AgentConfig _config;
    private ServerInfo? _server;
    private ISessionHost? _runner;
    private readonly AgentHooks _hooks;
    /// <summary>现在连着的 hub（显示器变了要重报）。</summary>
    private volatile HubConnection? _hub;
    private (AgentState State, string Detail) _connectionState = (AgentState.Connecting, "正在连接");
    private CancellationTokenSource? _consent;
    private Guid? _consentFor;
    private CancellationTokenSource? _reset;
    /// <summary>主循环正在等配置串（<see cref="IAgentUi.AskSetupAsync"/>）：这时"重新设置"没什么可重来的，<see cref="RequestSetupAsync"/> 什么都不做。</summary>
    private volatile bool _askingSetup;
    /// <summary>最近结束的会话：迟到的"开始"通知（同意之后对方立刻挂了、重连补发）不再开红条。</summary>
    private readonly Queue<Guid> _endedOrder = new();
    private readonly HashSet<Guid> _ended = [];
    /// <summary>上一次记进日志的连接问题，和它之后又重复了几次。</summary>
    private string? _lastProblem;
    private int _sameProblem;
    private string? _transportLogged;
    /// <summary>遥测里"这台电脑的硬件和能力"：连上服务器就报（要探测先在子进程里探测）。关了遥测是 null。</summary>
    private readonly HardwareTelemetry? _telemetry;
    private readonly UpdateStatusReporter? _updateStatus;
    /// <summary>
    /// 启动后第一次查更新做完了（已是最新、暂缓、出错，或者没启用自动升级）：硬件探测等它再起子进程——
    /// 刚连上就发现新版本的话，探测到一半会被换版本打断，新版本还得重来。换成了新版本就一直不完成（这个进程马上退出）。
    /// </summary>
    private readonly TaskCompletionSource _firstUpdateCheck = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public AgentService(AgentOptions options, IAgentUi ui) : this(options, ui, new AgentHooks()) { }

    internal AgentService(AgentOptions options, IAgentUi ui, AgentHooks hooks)
    {
        _options = options;
        _ui = ui;
        _hooks = hooks;
        _input = new InputController(options.DryInputPath is { } path ? new DryRunInputSink(path) : new SendInputSink());
        _config = AgentConfig.Load(options.DataDir);
        _updater = new Updater(options.DataDir);
        if (SessionTelemetry.Disabled) AgentLog.Info($"遥测已用环境变量 {SessionTelemetry.SwitchVariable} 关闭：不探测硬件，也不上报");
        else
        {
            _telemetry = new HardwareTelemetry(options.DataDir, () => _runner is not null, _firstUpdateCheck.Task, hooks.Telemetry);
            _updateStatus = new UpdateStatusReporter(options.DataDir);
        }
        ui.DisconnectRequested += () => _ = DisconnectLocallyAsync();
        ui.SetupRequested += () => _ = RequestSetupAsync();
    }

    /// <summary>
    /// 真实屏幕：桌面复制只用在最近一次探测验证过的显示器上，别的用 GDI（<see cref="ScreenCaptures"/>，环境变量可以强制）。
    /// 构造很便宜（第一次采集才建 D3D 设备）：<see cref="ReportAsync"/> 也拿它列显示器。
    /// </summary>
    private IFrameSource CreateSource() => _options.BusyPattern ? new BusyPatternSource() : _options.UsePattern ? new TestPatternSource()
        : ScreenCaptures.Create(_telemetry?.ValidatedOutputs ?? FrozenSet<string>.Empty);

    /// <summary>最近一次探测里试编成功的硬件编码器：远程每次重建编码器时现问（<see cref="VideoEncoders"/>）。</summary>
    internal IReadOnlyList<ValidatedEncoder> ValidatedEncoders() => _telemetry?.ValidatedEncoders ?? [];

    /// <summary>最近一次探测里验证过桌面复制的显示器（服务模式下交给工作进程）。</summary>
    internal IReadOnlyList<string> ValidatedOutputs() => [.. _telemetry?.ValidatedOutputs ?? FrozenSet<string>.Empty];

    /// <summary>显示设置变了（插拔显示器、换主显示器）：验证过的先对一次指纹、重报显示器。服务模式下由界面进程报过来。</summary>
    internal void DisplayChanged()
    {
        _telemetry?.Recheck();
        if (_hub is { } hub) _ = ReportAsync(hub);
    }

    /// <summary>电脑正在播放的声音；测试画面配测试音（一直响的 440 Hz），不碰声卡。</summary>
    private IAudioSource CreateAudioSource() => _options.UsePattern ? new TestToneSource() : new WasapiLoopback();

    public async Task RunAsync(CancellationToken ct)
    {
        using var stopUpdates = ct.Register(() => _stopping.Cancel());
        if (_updateStatus is not null)
        {
            if (_config.HasServer && _config.DeviceId is { } device && _config.DeviceToken.Length > 0)
                _updateStatus.Connected(device, ServerFromConfig());
            _ = Task.Run(() => _updateStatus.RunAsync(ct));
        }
        _ = UpdateLoopAsync(ct);
        _telemetry?.Start(ct);
        string? setupError = null;
        while (!ct.IsCancellationRequested)
        {
            using var reset = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _reset = reset;
            try
            {
                if (!_config.HasServer || setupError is not null || _updateSettings is null)
                {
                    _ui.SetState(AgentState.NeedsSetup, "需要设置服务器");
                    string? text;
                    _askingSetup = true;
                    try { text = await _ui.AskSetupAsync(setupError, ct); }
                    finally { _askingSetup = false; }
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
                    await _gate.WaitAsync(ct);
                    try
                    {
                        if (_updating) return;
                        if (url != _config.ServerUrl) _config = new AgentConfig();
                        _config.ServerUrl = url;
                        _config.AccessClientId = id ?? "";
                        _config.AccessClientSecret = secret ?? "";
                        _config.Save(_options.DataDir);
                        _updateSettings ??= new CancellationTokenSource();
                    }
                    finally { _gate.Release(); }
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
                    _config.DeviceId = null;
                    _updateStatus?.Forget();
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

    private async Task RequestSetupAsync()
    {
        await _gate.WaitAsync();
        CancellationTokenSource? prior;
        Task cancelled;
        try
        {
            // 正在等配置串：已经是"重新设置"的状态了。装成服务时安装程序先叫"重新设置"、再把配置串交过来（HelperBridgeUi.AdminSetup）：
            // 这里要是照样取消，刚拿到配置串开始登记的那一轮会被打断、回去干等一个不会再来的配置串（2026-11-26 在虚拟机里装服务时卡在"正在启动"）
            if (_updating || _askingSetup) return;
            prior = _updateSettings;
            _updateSettings = null;
            _updateStatus?.Forget();
            cancelled = prior?.CancelAsync() ?? Task.CompletedTask;
            try { _reset?.Cancel(); } catch (ObjectDisposedException) { /* 主循环正在开始下一轮，会检查 _updateSettings。 */ }
        }
        finally { _gate.Release(); }
        try { await cancelled; }
        finally { prior?.Dispose(); }
    }

    /// <summary>托盘手动检查；找到可安装的新版后由调用方在界面线程确认。安装过程在后台运行。</summary>
    public Task<string> CheckForUpdateAsync(Func<string, long, CancellationToken, Task<bool>> confirmInstall, CancellationToken ct,
        IProgress<UpdateProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(confirmInstall);
        return Task.Run(async () =>
        {
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, _stopping.Token);
            var result = await TryUpdateAsync(confirmInstall, lifetime.Token, progress);
            return result.Message;
        }, ct);
    }

    private readonly record struct UpdateCheck(UpdateOutcome Outcome, string Message, TimeSpan? RetryAfter = null);

    /// <summary>
    /// 启动后、每次连上服务器时、之后每 6 小时查一次：服务器上的安装包比自己新就下载、核对、换掉自己并重启（见 <see cref="Updater"/>）。
    /// 正在被远程的时候不换，等结束了再说。
    /// </summary>
    private async Task UpdateLoopAsync(CancellationToken ct)
    {
        // 手动升级也会留下旧 exe；即使关了定时检查，也在取得单实例后清理。不能和检查、下载或换包并行。
        if (Updater.InstallationUnavailable() is null)
        {
            await _updateGate.WaitAsync(ct);
            try
            {
                if (_updating) return;
                if (!_updater.CleanUpOldExecutables())
                {
                    await Task.Delay(TimeSpan.FromSeconds(8), ct);
                    _updater.CleanUpOldExecutables();
                }
            }
            finally { _updateGate.Release(); }
        }
        if (Updater.DisabledReason() is { } reason)
        {
            AgentLog.Info($"自动升级没有启用：{reason}");
            _firstUpdateCheck.TrySetResult();
            return;
        }
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
            wait = Updater.CheckInterval;
            if (!_config.HasServer) wait = TimeSpan.FromMinutes(1);
            else
            {
                try
                {
                    var result = await TryUpdateAsync(null, ct);
                    if (result.Outcome == UpdateOutcome.Restarting) return;
                    if (result.Outcome == UpdateOutcome.Deferred) wait = result.RetryAfter ?? TimeSpan.FromMinutes(2);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    // 升级不成不影响使用：记下来，过半小时再试（连不上服务器这类原因，主流程那边已经在说了）
                    AgentLog.Warn($"自动升级没有成功，稍后再试：{ex.Message}");
                    wait = TimeSpan.FromMinutes(30);
                }
                _firstUpdateCheck.TrySetResult(); // 硬件探测在等这第一次（见 HardwareTelemetry）
            }
        }
    }

    private async Task<UpdateCheck> TryUpdateAsync(Func<string, long, CancellationToken, Task<bool>>? confirmInstall, CancellationToken ct,
        IProgress<UpdateProgress>? progress = null)
    {
        if (confirmInstall is null) await _updateGate.WaitAsync(ct);
        else if (!await _updateGate.WaitAsync(0, ct)) return new(UpdateOutcome.Deferred, "正在检查或安装更新，请稍后再试。");
        try
        {
            ct.ThrowIfCancellationRequested();
            ServerInfo server;
            CancellationToken settings;
            Action<AgentUpdateStatusReport>? report = null;
            await _gate.WaitAsync(ct);
            try
            {
                if (_updating) return new(UpdateOutcome.Restarting, "新版本已启动，正在重新打开。");
                if (!_config.HasServer) return new(UpdateOutcome.Deferred, "请先设置服务器，再检查更新。");
                if (_updateSettings is null) return new(UpdateOutcome.Deferred, "正在重新设置服务器，请设置后再检查更新。");
                settings = _updateSettings.Token;
                // 版本清单和安装包只需服务器地址与 Access 凭据。
                server = new ServerInfo(_config.ServerUrl, _config.AccessClientId, _config.AccessClientSecret, "");
                if (_updateStatus is not null && _config.DeviceId is { } id && _config.DeviceToken is { Length: > 0 } token)
                    report = _updateStatus.ForCheck(id, server with { DeviceToken = token });
            }
            finally { _gate.Release(); }
            using var active = CancellationTokenSource.CreateLinkedTokenSource(ct, settings);
            var trace = new UpdateCheckTrace(confirmInstall is null ? "auto" : "manual", Version, report, progress);
            trace.State("checking");
            try { return await CheckAndInstallAsync(server, settings, confirmInstall, active.Token, trace); }
            catch (OperationCanceledException) when (settings.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                trace.State("cancelled");
                return new(UpdateOutcome.Deferred, "服务器设置已变更，请重新检查更新。");
            }
            catch (OperationCanceledException) when (active.IsCancellationRequested) { trace.State("cancelled"); throw; }
            catch (Exception ex) { trace.Failed(ex); throw; }
        }
        finally { _updateGate.Release(); }
    }

    private async Task<UpdateCheck> CheckAndInstallAsync(ServerInfo server, CancellationToken settings,
        Func<string, long, CancellationToken, Task<bool>>? confirmInstall, CancellationToken ct, UpdateCheckTrace trace)
    {
        UpdateCheck Result(string state, UpdateOutcome outcome, string message, TimeSpan? retryAfter = null)
        {
            trace.State(state);
            return new(outcome, message, retryAfter);
        }
        var manifest = await _updater.FetchManifestAsync(server, ct);
        ct.ThrowIfCancellationRequested();
        if (manifest is null) return Result("noManifest", UpdateOutcome.UpToDate, "服务器尚未提供版本清单。");
        trace.Found(manifest.Version);
        var newer = UpdateManifest.IsNewer(manifest.Version, Version);
        if (confirmInstall is not null && Updater.InstallationUnavailable() is { } unavailable)
            return newer ? Result("unavailable", UpdateOutcome.UpToDate, $"有新版本 {manifest.Version}。{unavailable}，无法在此安装更新。")
                : Result("current", UpdateOutcome.UpToDate, $"当前已是最新版本（{Version}）。");
        _updater.CleanUpDownloads(newer ? manifest.Version : null);
        if (!newer) return Result("current", UpdateOutcome.UpToDate, $"当前已是最新版本（{Version}）。");
        if (confirmInstall is null && _postponed is { } later && later.Settings == settings && later.Version == manifest.Version)
        {
            var remaining = later.Until - Environment.TickCount64;
            if (remaining > 0) return Result("postponed", UpdateOutcome.Deferred, "已暂缓本次更新。", TimeSpan.FromMilliseconds(remaining));
        }
        UpdateCheck Busy() => Result("busy", UpdateOutcome.Deferred, $"有新版本 {manifest.Version}。正在远程协助，请在会话结束后更新。");
        if (Volatile.Read(ref _runner) is not null)
        {
            AgentLog.Info($"有新版本 {manifest.Version}，等这次远程结束后再升级");
            return Busy();
        }
        if (confirmInstall is not null)
        {
            trace.State("available");
            var confirmed = await confirmInstall(manifest.Version, manifest.Size, ct).WaitAsync(ct);
            ct.ThrowIfCancellationRequested();
            if (!confirmed)
            {
                _postponed = (settings, manifest.Version, Environment.TickCount64 + (long)TimeSpan.FromMinutes(30).TotalMilliseconds);
                return Result("postponed", UpdateOutcome.Deferred, $"已暂缓更新到 {manifest.Version}，可随时再次手动检查。");
            }
            if (Volatile.Read(ref _runner) is not null) return Busy();
        }

        ct.ThrowIfCancellationRequested();
        AgentLog.Info($"发现新版本 {manifest.Version}（现在是 {Version}），开始下载");
        var downloaded = await _updater.DownloadAsync(server, manifest, ct, trace);
        AgentLog.Info($"新版本 {manifest.Version} 安装包就绪，大小和 SHA-256 核对无误");

        await _gate.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (_runner is not null) return Busy(); // 下载的工夫开始了远程：文件留着，结束后直接换
            // 新版本马上要起来：正在跑的硬件探测先杀掉、删掉它的结果文件（新版本连上以后自己探测）；换不成就接着可以探测
            _telemetry?.Suspend();
            try
            {
                trace.Installing();
                if (_hooks.ApplyUpdate is { } apply) apply(downloaded, manifest.Version, Version);
                else _updater.SwapAndRelaunch(downloaded, Version, Environment.GetCommandLineArgs()[1..]);
            }
            catch
            {
                _telemetry?.Resume();
                throw;
            }
            _updating = true;
            _postponed = null;
            _consent?.Cancel();
            AgentLog.Info($"已换成新版本 {manifest.Version} 并启动，这个进程退出");
        }
        finally
        {
            _gate.Release();
        }
        RestartRequested?.Invoke();
        return new(UpdateOutcome.Restarting, $"已更新到 {manifest.Version}，正在重新打开。");
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
        // 会话还在进行时托盘照旧显示"正在被远程"（见 SetConnectionStateAsync）
        await SetConnectionStateAsync(AgentState.Connecting, "正在连接");
        var server = _server = ServerFromConfig();
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
        // 处理函数必须立刻返回（花括号写法，不把 Task 交回去）：SignalR 按顺序一个一个处理通知，会等上一个的 Task 结束。
        // 以前写成 r => _ = OnRequestedAsync(r)，这个表达式的值就是那个 Task——同意窗口开着的时候，
        // 后面的通知（对方取消了、又发了一次、用 PIN 直接连）全都排队，要等窗口被点掉或 30 秒超时才轮到
        hub.On<AgentSessionRequest>(AgentHubEvents.SessionRequested, r => { _ = OnRequestedAsync(r); });
        hub.On<AgentSessionEnded>(AgentHubEvents.SessionEnded, e => { _ = OnEndedAsync(e.SessionId, e.Reason); });
        hub.On(AgentHubEvents.Revoked, () => revoked.TrySetResult());
        hub.Reconnecting += _ => SetConnectionStateAsync(AgentState.Connecting, "网络断开，正在重新连接");
        hub.Reconnected += async _ =>
        {
            await SetConnectionStateAsync(AgentState.Online, "在线");
            await ReportAsync(hub);
            if (_config.DeviceId is { } id) _updateStatus?.Connected(id, server);
            _telemetry?.Connected(server);
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
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // 已经添加过的电脑：断网、服务器重启都会到这里，不弹窗，原因写在托盘上。10 秒后外层重新连
            var problem = Diagnose("连接服务器失败", ex);
            await SetConnectionStateAsync(AgentState.Connecting, $"连不上服务器：{problem.Summary}");
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            return Outcome.Retry;
        }
        _lastProblem = null;
        _hub = hub;
        AgentLog.Info("已连接服务器，等待远程请求");
        try { _updateSignal.Release(); } catch (SemaphoreFullException) { } // 顺便查一下有没有新版本
        await SetConnectionStateAsync(AgentState.Online, "在线");
        await ReportAsync(hub);
        if (_config.DeviceId is { } id) _updateStatus?.Connected(id, server);
        _telemetry?.Connected(server); // 硬件和能力走 REST，不走 hub（后台做，这里立刻返回）
        // 显示设置变了（插拔显示器、换主显示器，装显卡驱动时多半也有一次）：重报显示器；指纹变了的话验证过的先作废、没有远程时重探
        EventHandler displayChanged = (_, _) =>
        {
            _telemetry?.Recheck();
            _ = ReportAsync(hub);
        };
        if (!_options.Headless) SystemEvents.DisplaySettingsChanged += displayChanged;
        try
        {
            var done = await Task.WhenAny(revoked.Task, closed.Task, Task.Delay(Timeout.Infinite, ct));
            await done; // 取消时抛出
            // 长连接试了一分钟都没连上：正在进行的会话收掉（服务器那边早就结束了）
            if (done == revoked.Task) return Outcome.Revoked;
            await StopRunnerAsync();
            return Outcome.Retry;
        }
        finally
        {
            _hub = null;
            if (!_options.Headless) SystemEvents.DisplaySettingsChanged -= displayChanged;
            _telemetry?.Disconnected();
        }
    }

    private async Task ReportAsync(HubConnection hub)
    {
        try
        {
            IReadOnlyList<ScreenInfo> list;
            if (_hooks.Screens is { } provider) list = provider();
            else
            {
                using var source = CreateSource();
                list = source.Screens();
            }
            var screens = list.Select(s => new AgentScreen(s.Bounds.Width, s.Bounds.Height, s.IsPrimary)).ToList();
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
        var runner = _hooks.SessionFactory?.Invoke(request, _server!) ?? new SessionRunner(request, _server!, CreateSource, ValidatedEncoders, CreateAudioSource, _input, _ui);
        _runner = runner;
        // 建截屏、问验证过的编码器（runner.Start 之后在采集线程上）之前再对一次指纹：连着服务器时装了新显卡驱动、换了显示器，
        // 这次远程就别用旧探测验证过的（GDI + 软件编码器），远程结束后重探。放在 _runner 之后：叫醒的后台循环看得到正在远程
        _telemetry?.Recheck();
        _telemetry?.SessionStarted(); // 正在探测就停下（试编码器、桌面复制会和远程抢显卡），远程结束后再来
        runner.Start();
        _ui.ShowBanner(request);
        _ui.SetState(AgentState.InSession, $"正在被 {request.ControllerName} 远程");
        AgentLog.Info($"远程会话开始：{request.SessionId}（{request.ControllerName}，{request.Mode}）");
        _ = WatchAsync(runner);
    }

    private async Task WatchAsync(ISessionHost runner)
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
                using var api = new ServerApi(server);
                await api.EndAsync(sessionId, CancellationToken.None);
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
        _telemetry?.SessionEnded();
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

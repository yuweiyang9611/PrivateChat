using System.Collections.Frozen;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PrivateChat.Agent.Video;
using PrivateChat.Contracts.Remote;

namespace PrivateChat.Agent.Telemetry;

/// <summary>
/// 遥测里"这台电脑的硬件和能力"（<see cref="AgentTelemetrySnapshot"/>，docs/DESIGN.md"远程协助遥测"）：
/// - 探测在子进程里做（<see cref="HardwareProbe"/>：PrivateChat.Agent.exe --probe-hardware 文件）：试编码器、桌面复制要动显卡驱动，
///   驱动出问题（崩溃、卡死）只带走子进程。低优先级、60 秒超时（超时杀掉，结果里记"探测超时"；退出码不是 0 记"探测进程退出码 N"）；
///   子进程放在作业对象里（<see cref="KillOnCloseJob"/>），Agent 怎么没的它都跟着结束。
/// - 什么时候起子进程（见 <see cref="ReadyToProbeAsync"/>）：启动后自动升级的第一次检查做完了（已是最新、暂缓、出错，或者没启用自动升级），
///   而且这次连上服务器满 30 秒——刚升级完、服务器上又有更新的版本时，别探测到一半被换版本打断；
///   只在没有远程的时候跑，跑到一半开始远程就杀掉，远程结束后重来；自动升级换版本之前、Agent 退出时也杀掉（<see cref="Suspend"/>），结果文件删掉。
/// - 结果存在数据目录的 telemetry.json。之后每次要上报时先算一遍便宜的指纹（程序版本、显卡驱动、显示器，见 <see cref="HardwareInfo.Fingerprint"/>；
///   再加上探测做法的版本 <see cref="ProbeMethod"/>）：
///   和上次探测时一样就沿用上次的显卡 / 编码器 / 截屏结果，只刷新系统、内存、显示器这些字段，不再起子进程。探测失败（超时、崩溃）的结果也一样沿用，
///   免得每次连上都去碰一个会卡死的驱动；换了版本、换了驱动、换了显示器才重来。
/// - 锁着屏时探到的截屏结果不准：只临时用、不存盘，解锁后重探一次（见 <see cref="CurrentAsync"/>）。
/// - 每次连上服务器（和重新探测之后）PUT /api/remote/agent/telemetry（不走 SignalR：消息上限 32 KB，而且 Report 会让所有网页刷新设备列表）。
///   服务器是老版本（404 / 405）就安静地等下次连上再说；网络出错过一分钟再试，最多三次。
/// - 远程只用探测验证过的桌面复制和硬件编码器（<see cref="ValidatedOutputs"/>、<see cref="ValidatedEncoders"/>，docs/DESIGN.md"截屏和编码器怎么选"）：
///   新的原生路径先在子进程里走一遍，驱动出问题只带走子进程。远程开始前、显示设置变了时也对一次指纹（<see cref="Recheck"/>），变了就先不用。
/// PRIVATECHAT_AGENT_TELEMETRY=off 时 AgentService 不创建它（远程就一律 GDI + 软件编码器，除非用环境变量强制）。
/// </summary>
/// <param name="updateChecked">启动后自动升级的第一次检查做完了（见 AgentService.UpdateLoopAsync）；换成新版本了的话一直不完成。</param>
/// <param name="environment">在哪儿、怎么探测（null：普通模式，就在这个进程的会话里，见 <see cref="TelemetryEnvironment"/>）。</param>
internal sealed class HardwareTelemetry(string dataDir, Func<bool> sessionActive, Task updateChecked, TelemetryEnvironment? environment = null)
{
    private readonly TelemetryEnvironment _environment = environment ?? TelemetryEnvironment.Local;
    private const string CacheFile = "telemetry.json";
    private const int MaxAttempts = 3;
    /// <summary>服务器收 64 KB：超过这么多就把说明截短再报。</summary>
    private const int MaxUploadBytes = 60_000;
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(60);
    /// <summary>连上服务器满这么久才起探测子进程（每次连上都会查一次更新）。</summary>
    private static readonly TimeSpan ProbeAfterConnect = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan IdleRetry = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FailureRetry = TimeSpan.FromMinutes(1);
    /// <summary>锁屏时探到的结果先用这么久，之后（解锁了的话）重探一次。</summary>
    private static readonly TimeSpan ProvisionalFor = TimeSpan.FromMinutes(10);
    /// <summary>手上是临时结果时，每隔这么久看一眼该不该重探（不等下次连上服务器）。</summary>
    private static readonly TimeSpan ProvisionalCheck = TimeSpan.FromMinutes(1);
    /// <summary>桌面复制被拒的错误码（HResults.Describe 写出来的样子）：锁屏、安全桌面时才会这样。</summary>
    private const string AccessDenied = "0x80070005";
    /// <summary>探测子进程的结果文件：telemetry-probe-{Agent 的进程号}.json（写的时候先写 .tmp）。</summary>
    private static readonly Regex OutputName = new(@"^telemetry-probe-(\d+)\.json(?:\.tmp)?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    /// <summary>探测没做完的两种说法（写进 Problems 的开头）：这样的结果什么都不算验证过（见 <see cref="Publish"/>）。</summary>
    private const string TimeoutFailure = "探测超时";
    private const string ExitCodeFailure = "探测进程退出码 ";
    /// <summary>
    /// 探测做法的版本，算进指纹：探测怎么做变了，同一个程序版本号下存着的老结果也作废、重探一次。
    /// 2：桌面复制、硬件编码器经远程用的那几个类（DxgiScreenSource、HardwareH264Encoder）试过才算验证过——老做法的结果不能拿来放行。
    /// 3：桌面复制多量一项"画面在动时每幅新画面多久"（TelemetryDuplication.FrameMs），存着的老结果里没有，重探一次补上。
    /// </summary>
    private const string ProbeMethod = "probe 3";

    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly Lock _gate = new();
    private readonly string _output = Path.Combine(dataDir, $"telemetry-probe-{Environment.ProcessId}.json");
    private IProbeProcess? _probe;
    /// <summary>正在跑的探测被打断了（远程开始了、要换版本 / 退出了）：要记进日志的那句话。</summary>
    private string? _interrupted;
    /// <summary>要换版本 / 退出了：不再起新的探测（换版本没成功时 <see cref="Resume"/>）。</summary>
    private bool _suspended;
    private ServerInfo? _server;
    /// <summary>最近一次连上服务器的时候（Stopwatch 时间戳）。</summary>
    private long _connectedAt;
    private bool _uploadWanted;
    private int _attempts;
    private bool _oldServerLogged;
    private AgentTelemetrySnapshot? _snapshot;
    /// <summary>_snapshot 是临时的（锁着屏时探的，没存盘）：什么时候探的。只在后台循环里读写。</summary>
    private long? _provisionalSince;
    /// <summary>后台循环整份换掉（不可变），远程的采集线程随时读：不用锁。<see cref="Recheck"/> 从别的线程把它换成 None（CompareExchange）。</summary>
    private volatile Validated _validated = Validated.None;
    /// <summary>上次记进日志的验证结果（变了才再记）。只在后台循环里读写。</summary>
    private string? _validatedLogged;

    /// <summary>远程能用的显示器、硬件编码器；Fingerprint = 验证它们的那次探测的指纹（<see cref="Recheck"/> 拿来比；None 是 null）。</summary>
    private sealed record Validated(FrozenSet<string> Outputs, IReadOnlyList<ValidatedEncoder> Encoders, string? Fingerprint)
    {
        public static readonly Validated None = new(FrozenSet<string>.Empty, [], null);
    }

    /// <summary>
    /// 最近一次探测里整条桌面复制流程验证过的显示器（\\.\DISPLAYn，不分大小写）。还没有做完的探测（没探过、探测超时 / 崩溃 / 退出码不是 0）时是空的；
    /// 锁着屏时的临时结果也不算（桌面复制被拒，验证不了）。
    /// </summary>
    public IReadOnlySet<string> ValidatedOutputs => _validated.Outputs;

    /// <summary>
    /// 最近一次探测里经 HardwareH264Encoder 试编、每帧都出来了、而且能按质量或逐帧 QP 编的硬件编码器（按探测的顺序）。
    /// 没做完的探测时是空的；锁着屏时的临时结果照样算（编码不受锁屏影响）。
    /// </summary>
    public IReadOnlyList<ValidatedEncoder> ValidatedEncoders => _validated.Encoders;

    public void Start(CancellationToken ct)
    {
        // Agent 要退出了（托盘"退出"、Ctrl+C、自动升级换好了新版本）：正在跑的探测当场杀掉、删掉结果文件，
        // 不等后台循环（进程可能马上就没了）
        ct.Register(Suspend);
        _ = Task.Run(() => LoopAsync(ct), CancellationToken.None);
    }

    /// <summary>连上服务器了（第一次或重连）：上报一次（要探测就先探测）。立刻返回。</summary>
    public void Connected(ServerInfo server)
    {
        lock (_gate)
        {
            _server = server;
            _connectedAt = Stopwatch.GetTimestamp();
            _uploadWanted = true;
            _attempts = 0;
        }
        Wake();
    }

    public void Disconnected()
    {
        lock (_gate) _server = null;
    }

    /// <summary>远程开始了：正在跑的探测杀掉（远程结束后重来）。不等它退出（调用方拿着 AgentService 的锁）。</summary>
    public void SessionStarted()
    {
        lock (_gate)
        {
            if (_probe is not { } probe) return;
            _interrupted = "远程开始了，探测先停下，远程结束后再探测";
            probe.Kill(wait: false);
        }
    }

    public void SessionEnded() => Wake();

    /// <summary>
    /// 远程开始前（建截屏、问验证过的编码器之前）、显示设置变了时调：现算一遍便宜的指纹（注册表 + 显示器列表），
    /// 和验证结果所属的那次探测比。验证结果平时只在连上服务器（和重探）时对一次指纹，连着服务器的这几天里
    /// Windows 更新装了新显卡驱动（不用重启、连接也不断）的话，远程会在探测子进程没碰过的驱动上跑桌面复制和硬件编码器。
    /// 不一样就当场作废（这次远程用 GDI + 软件编码器），并要一次重探：后台循环在没有远程的时候探（连着服务器、连上满 30 秒；正在远程就等它结束）。
    /// 任何线程都能调，不抛异常；指纹算不出来也当变了（验证不了就别用）。
    /// </summary>
    public void Recheck()
    {
        if (_validated.Fingerprint is null) return; // 本来就没有验证过的
        string? now;
        try
        {
            now = Fingerprint();
        }
        catch (Exception ex)
        {
            AgentLog.Warn($"算不出这台电脑的指纹，探测验证过的先不用：{ex.Message}");
            now = null;
        }
        while (true)
        {
            var validated = _validated;
            if (validated.Fingerprint is null || validated.Fingerprint == now) return;
            // 后台循环刚好换了一份（新探测的）：拿新的再比一次
            if (ReferenceEquals(Interlocked.CompareExchange(ref _validated, Validated.None, validated), validated)) break;
        }
        AgentLog.Info("显卡驱动或显示器和上次探测时不一样了：探测验证过的桌面复制、硬件编码器先不用（远程用 GDI + 软件编码器），没有远程的时候重新探测");
        lock (_gate)
        {
            _uploadWanted = true;
            _attempts = 0;
        }
        Wake();
    }

    /// <summary>
    /// 自动升级要换版本了、Agent 要退出了：正在跑的探测杀掉（等它真的退出，最多 5 秒）、删掉它的结果文件，之后不再起新的探测，
    /// 直到 <see cref="Resume"/>（换版本没成功，接着跑）。调用方可能拿着 AgentService 的锁。
    /// </summary>
    public void Suspend()
    {
        lock (_gate)
        {
            _suspended = true;
            if (_probe is { } probe)
            {
                _interrupted = "要换新版本或者退出了，探测停下";
                probe.Kill(wait: true); // 拿着锁杀：ProbeAsync 收尾（Dispose）要等这把锁，杀的时候这个进程对象还在
            }
        }
        TryDelete(_output);
    }

    /// <summary>换版本没成功、这个进程接着跑：又可以探测了。</summary>
    public void Resume()
    {
        lock (_gate) _suspended = false;
        Wake();
    }

    private void Wake()
    {
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException) { }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        SweepOldOutputs();
        var wait = Timeout.InfiniteTimeSpan;
        while (true)
        {
            // 手上是锁屏时探的临时结果：每分钟看一眼该不该重探（解锁了、放了 10 分钟以上），不等下次连上服务器
            if (wait == Timeout.InfiniteTimeSpan && _provisionalSince is not null) wait = ProvisionalCheck;
            try
            {
                await _wake.WaitAsync(wait, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            wait = Timeout.InfiniteTimeSpan;
            if (ReprobeDue())
                lock (_gate)
                    if (!_uploadWanted)
                    {
                        _uploadWanted = true;
                        _attempts = 0;
                    }
            lock (_gate)
                if (!_uploadWanted || _server is null) continue;
            try
            {
                var snapshot = await CurrentAsync(ct);
                if (snapshot is null)
                {
                    wait = IdleRetry; // 正在远程（或探测刚被打断、等着探测时断开了）：过一会儿再看，远程结束、重新连上时也会叫醒
                    continue;
                }
                if (!await UploadAsync(snapshot, ct)) wait = Retry();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                AgentLog.Warn($"遥测（硬件和能力）出错：{ex.Message}");
                wait = Retry();
            }
        }
    }

    /// <summary>失败了：过一分钟再试，三次都不成就等下次连上服务器。</summary>
    private TimeSpan Retry()
    {
        lock (_gate)
        {
            if (++_attempts < MaxAttempts) return FailureRetry;
            _uploadWanted = false;
            return Timeout.InfiniteTimeSpan;
        }
    }

    /// <summary>
    /// 现在该报的快照：指纹没变就用上次的（刷新便宜的字段），变了就探测；null = 现在不能探测（正在远程、探测被打断、等的时候断开了）。
    /// 锁着屏也照样探测（这时探测最不打扰人），只是桌面复制会被拒（E_ACCESSDENIED）、GDI 截到的不是桌面：
    /// 这样的结果照报，但只算临时的——不存盘（Agent 重启后重来），放了 <see cref="ProvisionalFor"/> 以上、又没锁屏时重探一次（后台每分钟看一眼）。
    /// 每个临时结果只重探这一次：重探时锁着屏，得到的又是一个临时结果；没锁屏、桌面复制还是被拒，那就是这台电脑本来如此，照实存下来
    /// （错误码就在结果里），不再每 10 分钟探一遍。
    /// </summary>
    private async Task<AgentTelemetrySnapshot?> CurrentAsync(CancellationToken ct)
    {
        var fingerprint = Fingerprint();
        var known = _snapshot ?? LoadCache();
        var reprobe = ReprobeDue();
        if (known is not null && known.Fingerprint == fingerprint && !reprobe)
        {
            _snapshot = Refresh(known);
            Publish(_snapshot, provisional: _provisionalSince is not null);
            return _snapshot;
        }
        // 换了版本 / 驱动 / 显示器（或者从没探过）：上次验证过的不再算数，等这次探完
        if (known is not null && known.Fingerprint != fingerprint) _validated = Validated.None;
        if (sessionActive() || !await ReadyToProbeAsync(ct)) return null;
        var probed = await ProbeAsync(fingerprint, ct);
        if (probed is null) return null;
        _snapshot = probed;
        var locked = _environment.ScreenLocked() || probed.Problems.Contains(HardwareProbe.LockedProblem);
        var denied = probed.Capture.Duplication.Any(d => d.Error?.Contains(AccessDenied, StringComparison.Ordinal) == true);
        if (locked || (denied && !reprobe))
        {
            _provisionalSince = Stopwatch.GetTimestamp();
        }
        else
        {
            _provisionalSince = null;
            SaveCache(probed);
        }
        Publish(probed, provisional: _provisionalSince is not null);
        return probed;
    }

    /// <summary>这台电脑的指纹（<see cref="HardwareInfo.Fingerprint"/>）再加上探测做法的版本（<see cref="ProbeMethod"/>），还是 64 位十六进制。</summary>
    private string Fingerprint()
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{_environment.Fingerprint()}\n{ProbeMethod}"))).ToLowerInvariant();

    /// <summary>
    /// 按最新的快照定远程能用哪些显示器的桌面复制、哪些硬件编码器（<see cref="ValidatedOutputs"/>、<see cref="ValidatedEncoders"/>）：
    /// - 探测没做完（超时、崩溃、退出码不是 0、停在半路）：什么都不算——多半就是新的原生路径在驱动里出了事，远程别再去碰它；
    ///   这样的结果也会存盘沿用，换了版本 / 驱动 / 显示器才重探。
    /// - 显示器：桌面复制那一项 Ok（探测子进程用远程那个类采了几十帧、尺寸都对、没出错）。临时结果（锁着屏）不算。
    /// - 编码器：硬件的、试编每帧都出来了、没有出错，而且能按质量或逐帧 QP 编（只能固定码率的不自动用：画质规划靠逐帧定质量）。
    ///   按名字算（<see cref="ValidatedEncoders"/>）：同一个名字的每一项都得这样才算，能用的方式取它们都行的
    ///   （同厂商的两张卡——核显加独显——各列一个同名的编码器，远程时 VideoEncoders 按名字挑、挑到的可能是探测里没通过的那张卡上的）。
    /// </summary>
    private void Publish(AgentTelemetrySnapshot snapshot, bool provisional)
    {
        var finished = !snapshot.Problems.Any(p => p.StartsWith(HardwareProbe.UnfinishedPrefix, StringComparison.Ordinal)
            || p.StartsWith(TimeoutFailure, StringComparison.Ordinal) || p.StartsWith(ExitCodeFailure, StringComparison.Ordinal));
        var validated = !finished ? Validated.None : new Validated(
            provisional ? FrozenSet<string>.Empty
                : snapshot.Capture.Duplication.Where(d => d.Ok && d.Output.Length > 0).Select(d => d.Output).ToFrozenSet(StringComparer.OrdinalIgnoreCase),
            ValidEncoders(snapshot.Encoders),
            snapshot.Fingerprint);
        var before = _validated;
        _validated = validated;
        string text;
        if (!finished) text = "（探测没做完，都不算）远程用 GDI 截屏 + 软件编码器";
        else
        {
            var outputs = validated.Outputs.Count > 0 ? string.Join("、", validated.Outputs.Order(StringComparer.OrdinalIgnoreCase))
                : provisional ? "（锁着屏时的临时结果，不算）" : "（没有）";
            var encoders = validated.Encoders.Count > 0 ? string.Join("、", validated.Encoders.Select(e => e.Name)) : "（没有）";
            text = $"桌面复制 {outputs}，硬件编码器 {encoders}";
        }
        // 一样的话不再记；中途作废过（指纹变了）、重探后又是同样的结果，再记一次
        if (text == _validatedLogged && !(before.Fingerprint is null && validated.Fingerprint is not null)) return;
        _validatedLogged = text;
        AgentLog.Info($"探测验证过、远程可以用的：{text}");
    }

    /// <summary>
    /// 探测里的编码器 → 远程能自动用的硬件编码器（按第一次出现的顺序）：同名的（MFTEnumEx 每张卡列一个，名字一样）每一项都
    /// 试编通过才算，QualityMode / PerFrameQp 取它们都是 true 的，取完两样都不行也不算。
    /// </summary>
    internal static List<ValidatedEncoder> ValidEncoders(IEnumerable<TelemetryEncoder> encoders)
    {
        var result = new List<ValidatedEncoder>();
        foreach (var group in encoders.Where(e => e.Hardware).GroupBy(e => EncoderChoice.BaseName(e.Name), StringComparer.Ordinal))
        {
            if (group.Key.Length == 0) continue;
            var passed = group.All(e => e.Test is { Frames: > 0, Error: null } test && test.FramesOut == test.Frames);
            var quality = group.All(e => e.QualityMode);
            var qp = group.All(e => e.PerFrameQp);
            if (!passed || !(quality || qp)) continue;
            var first = group.First();
            result.Add(new ValidatedEncoder(first.Name, first.VendorId, quality, qp));
        }
        return result;
    }

    /// <summary>手上的临时结果放了 <see cref="ProvisionalFor"/> 以上、现在没锁屏：该重探了。</summary>
    private bool ReprobeDue()
        => _provisionalSince is { } since && Stopwatch.GetElapsedTime(since) > ProvisionalFor && !_environment.ScreenLocked();

    /// <summary>
    /// 起探测子进程之前先等：启动后自动升级的第一次检查做完（换成了新版本就一直等到退出），再等到这次连上服务器满 <see cref="ProbeAfterConnect"/>
    /// （每次连上都会查一次更新，见 AgentService.ConnectAsync；等的时候又重连了就从重连算起）。
    /// false = 等的这会儿断开了、或者要换版本 / 退出了。
    /// </summary>
    private async Task<bool> ReadyToProbeAsync(CancellationToken ct)
    {
        await updateChecked.WaitAsync(ct);
        while (true)
        {
            TimeSpan left;
            lock (_gate)
            {
                if (_server is null || _suspended) return false;
                left = ProbeAfterConnect - Stopwatch.GetElapsedTime(_connectedAt);
            }
            if (left <= TimeSpan.Zero) return true;
            await Task.Delay(left, ct);
        }
    }

    /// <summary>
    /// 以前的 Agent 进程（被结束任务、崩溃、升级前的旧版本）留下的探测结果文件删掉：都是没做完的探测，没人会再读。
    /// 同一个数据目录只跑一个 Agent（单实例锁），升级时旧进程换版本前已经杀掉自己的探测，所以别的进程号的都是剩下的。
    /// </summary>
    private void SweepOldOutputs()
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(dataDir, "telemetry-probe-*").ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }
        var mine = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        foreach (var file in files)
        {
            if (OutputName.Match(Path.GetFileName(file)) is not { Success: true } m || m.Groups[1].Value == mine) continue;
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private async Task<AgentTelemetrySnapshot?> ProbeAsync(string fingerprint, CancellationToken ct)
    {
        var output = _output;
        TryDelete(output);
        IProbeProcess process;
        lock (_gate)
        {
            if (sessionActive() || _suspended) return null;
            _interrupted = null;
            process = _environment.StartProbe(output);
            _probe = process;
        }
        AgentLog.Info("开始探测这台电脑的显卡、编码器和截屏（子进程，最多 60 秒）");
        var clock = Stopwatch.StartNew();
        string? failure = null;
        string? interrupted;
        try
        {
            var exited = process.WaitForExitAsync(ct);
            if (await Task.WhenAny(exited, Task.Delay(ProbeTimeout, ct)) != exited)
            {
                ct.ThrowIfCancellationRequested();
                process.Kill(wait: true);
                failure = TimeoutFailure;
            }
            else
            {
                await exited;
            }
            if (failure is null && process.ExitCode != 0) failure = $"{ExitCodeFailure}{ExitCodeText(process.ExitCode)}";
        }
        catch (OperationCanceledException)
        {
            process.Kill(wait: true); // Agent 要退出了
            TryDelete(output);
            throw;
        }
        finally
        {
            lock (_gate)
            {
                _probe = null;
                interrupted = _interrupted;
            }
            process.Dispose();
        }
        if (interrupted is not null)
        {
            TryDelete(output);
            AgentLog.Info(interrupted);
            return null;
        }

        var snapshot = ReadSnapshot(output);
        TryDelete(output);
        snapshot = snapshot is null ? Minimal($"{failure ?? "探测进程"}，什么结果都没写出来") : failure is null ? snapshot : WithFailure(snapshot, failure);
        // 用这边算的指纹：下次比的就是这一个（子进程算的一般一样，探测时插拔了显示器之类才会不同）
        snapshot = snapshot with { Fingerprint = fingerprint };
        var hardware = snapshot.Encoders.Count(e => e.Hardware);
        var duplicated = snapshot.Capture.Duplication.Count(d => d.Ok);
        var summary = $"探测完成（{clock.Elapsed.TotalSeconds:F1} 秒）：{snapshot.Gpus.Count} 张显卡，{snapshot.Encoders.Count} 个编码器（硬件 {hardware} 个），" +
                      $"桌面复制 {duplicated}/{snapshot.Capture.Duplication.Count} 块显示器能用";
        if (snapshot.Problems.Count == 0) AgentLog.Info(summary);
        else AgentLog.Warn($"{summary}；问题：{string.Join("；", snapshot.Problems)}");
        return snapshot;
    }

    private static string ExitCodeText(int code) => code is >= 0 and < 0x10000 ? code.ToString() : $"0x{code:X8}";

    /// <summary>超时 / 崩溃：子进程最后一条"探测没做完，停在：……"换成原因加停在哪一步。</summary>
    private static AgentTelemetrySnapshot WithFailure(AgentTelemetrySnapshot snapshot, string failure)
    {
        var problems = snapshot.Problems.ToList();
        string? stage = null;
        if (problems.Count > 0 && problems[^1].StartsWith(HardwareProbe.UnfinishedPrefix, StringComparison.Ordinal))
        {
            stage = problems[^1][HardwareProbe.UnfinishedPrefix.Length..];
            problems.RemoveAt(problems.Count - 1);
        }
        problems.Add(HResults.Clip(stage is null ? failure : $"{failure}（停在：{stage}）"));
        return snapshot with { Problems = problems };
    }

    /// <summary>子进程什么都没写出来：这边能便宜拿到的先报上（显卡、编码器、截屏都空着）。</summary>
    private static AgentTelemetrySnapshot Minimal(string problem)
    {
        var problems = new List<string> { HResults.Clip(problem) };
        T Read<T>(Func<T> read, T fallback)
        {
            try
            {
                return read();
            }
            catch (Exception ex)
            {
                problems.Add(HResults.Clip(ex.Message));
                return fallback;
            }
        }
        return new AgentTelemetrySnapshot(HardwareInfo.AgentVersion, DateTimeOffset.UtcNow, "",
            Read(HardwareInfo.Os, new TelemetryOs("", "", Environment.OSVersion.Version.Build, 0, "", Environment.Is64BitOperatingSystem)),
            Read(HardwareInfo.Cpu, new TelemetryCpu("", Environment.ProcessorCount, Environment.ProcessorCount, 0)),
            Read(HardwareInfo.MemoryBytes, 0L), [], Read(() => HardwareInfo.Displays([]), []), [], new TelemetryCapture([], null, null, null),
            HardwareInfo.RemoteDesktopSession(), problems);
    }

    /// <summary>沿用上次的探测结果：系统（可能装了更新）、内存、显示器（分辨率、缩放、刷新率）、是不是远程桌面会话重新读。</summary>
    private static AgentTelemetrySnapshot Refresh(AgentTelemetrySnapshot snapshot)
    {
        try
        {
            snapshot = snapshot with { Os = HardwareInfo.Os() };
        }
        catch (Exception) { }
        try
        {
            snapshot = snapshot with { MemoryBytes = HardwareInfo.MemoryBytes() };
        }
        catch (Exception) { }
        try
        {
            snapshot = snapshot with { Displays = HardwareInfo.Displays(snapshot.Gpus) };
        }
        catch (Exception) { }
        return snapshot with { RemoteDesktopSession = HardwareInfo.RemoteDesktopSession() };
    }

    private async Task<bool> UploadAsync(AgentTelemetrySnapshot snapshot, CancellationToken ct)
    {
        ServerInfo? server;
        lock (_gate) server = _server;
        if (server is null) return true; // 断开了：等下次连上
        using var api = new ServerApi(server);
        bool accepted;
        try
        {
            accepted = await api.PutTelemetryAsync(Fit(snapshot), ct);
        }
        catch (ServerException ex) when ((int)ex.Status is >= 400 and < 500)
        {
            // 服务器不收（格式不对、太大、没权限）：再试也一样，等下次连上
            AgentLog.Warn($"上报遥测（硬件和能力）被服务器拒绝：{ex.Message}");
            Done(server);
            return true;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            AgentLog.Warn($"上报遥测（硬件和能力）失败，稍后再试：{ex.Message}");
            return false;
        }
        Done(server);
        if (accepted) AgentLog.Info("已上报这台电脑的硬件和能力（遥测）");
        else if (!_oldServerLogged)
        {
            _oldServerLogged = true;
            AgentLog.Info("服务器还不收遥测（老版本），等下次连上再报");
        }
        return true;
    }

    /// <summary>这次连接的上报做完了（期间又重连了的话 Connected 会再要一次）。</summary>
    private void Done(ServerInfo server)
    {
        lock (_gate)
            if (ReferenceEquals(_server, server)) _uploadWanted = false;
    }

    /// <summary>JSON 超过 <see cref="MaxUploadBytes"/>（中文说明在 JSON 里每个字占 6 字节）：说明截短、问题只留前几条。</summary>
    private static AgentTelemetrySnapshot Fit(AgentTelemetrySnapshot snapshot)
    {
        if (JsonSerializer.SerializeToUtf8Bytes(snapshot, ServerApi.Json).Length <= MaxUploadBytes) return snapshot;
        static string? Short(string? text) => text is null ? null : HResults.Clip(text, 80);
        return snapshot with
        {
            Problems = [.. snapshot.Problems.Take(5).Select(p => Short(p)!)],
            Encoders = [.. snapshot.Encoders.Select(e => e with { Error = Short(e.Error), Test = e.Test is { } t ? t with { Error = Short(t.Error) } : null })],
            Capture = snapshot.Capture with { Error = Short(snapshot.Capture.Error), Duplication = [.. snapshot.Capture.Duplication.Select(d => d with { Error = Short(d.Error) })] },
        };
    }

    private AgentTelemetrySnapshot? LoadCache() => ReadSnapshot(Path.Combine(dataDir, CacheFile));

    private void SaveCache(AgentTelemetrySnapshot snapshot)
    {
        var path = Path.Combine(dataDir, CacheFile);
        try
        {
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(snapshot, HardwareProbe.FileJson));
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AgentLog.Warn($"保存探测结果失败（下次还会再探测）：{ex.Message}");
        }
    }

    private static AgentTelemetrySnapshot? ReadSnapshot(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<AgentTelemetrySnapshot>(File.ReadAllText(path), HardwareProbe.FileJson) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            File.Delete(path + ".tmp");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

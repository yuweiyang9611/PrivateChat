using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;
using PrivateChat.Agent.Audio;
using PrivateChat.Agent.Input;
using PrivateChat.Agent.Realtime;
using PrivateChat.Agent.Ui;
using PrivateChat.Agent.Video;
using PrivateChat.Contracts.Remote;
using HardwareInfo = PrivateChat.Agent.Telemetry.HardwareInfo;

namespace PrivateChat.Agent.Session;

/// <summary>
/// 一次远程会话的数据面：连 /ws/remote/{id}/agent，控制端在线时采集 → 编码 → 发送，收到的键鼠注入系统。
/// - 码率跟着网络走（<see cref="NetworkEstimator"/>）：控制端解码后回 FrameAck、对探测包回 Pong，据此估出线路能传多快，
///   发出去的数据按这个速度记账，账上欠多了就先不采——网络慢时少发几帧，延迟不会越积越高。
/// - 每一帧用什么质量由 <see cref="QualityPlanner"/> 定：画面大变时先糊一点马上送到，停下来再一步步补清晰。
/// - 画质：控制端的画质菜单（SetVideo，<see cref="VideoSettings"/>）定分辨率、帧率、码率上限；默认自动档，
///   分辨率随网速和这台电脑的性能调（<see cref="AutoResolution"/>）。每秒的统计里带上采集、编码一帧平均用了多久，控制端据此提示"对方电脑忙不过来"。
/// - 静止画面不编码（按 64×64 的块比较；桌面复制会说哪些区域变了，只比那几块）。
/// - 截屏、编码器（docs/DESIGN.md"截屏和编码器怎么选"）：默认只用这台电脑上探测验证过的桌面复制和硬件编码器，别的用 GDI + Windows 自带的软件编码器；
///   中途出错都换回 GDI / 软件编码器接着来（日志、遥测里记上原因），不结束远程。
/// - 声音：控制端要求时（SetAudio）采这台电脑正在播放的声音，Opus 编码后和画面走同一条连接。
/// - 断线（网络切换）30 秒内自动重连；服务端以 4001 关闭表示会话结束。
/// - 快速线路：控制端要求时（SetTransport）把画面和声音改经 Cloudflare Realtime 发（<see cref="RealtimePublisher"/>），
///   不再绕回家里的服务器；键鼠、剪贴板这些仍走上面那条连接。接不通、中途断了、控制端收不到，都自动回到中继。
/// - 遥测：这次远程的汇总（<see cref="SessionTelemetry"/>：帧率、码率、采集 / 编码用时、线路、出的问题）中途每 60 秒、结束时各报一次给服务器，
///   管理员在设备页的"诊断"里看。环境变量 PRIVATECHAT_AGENT_TELEMETRY=off 时不收集也不报。
/// </summary>
public sealed class SessionRunner : ISessionHost
{
    /// <summary>
    /// 帧率按画质设置的发（最高 30）：画面不动时不发，变化大时码率预算不够也会少发；控制端跟不上时它会请这边少发（MaxFps）。
    /// 控制端得先说一声它会这么做（连上后发一个 MaxFps），才按设置的帧率发；没说过的（还没更新的旧网页）照旧按以前流畅 / 清晰两档的帧率：
    /// 画面长边不超过 1280 时 20 帧，再大 15 帧。旧网页在慢设备上跟不上 30 帧又说不出来，中继线路会把它的确认来得晚当成网络堵，码率越压越低。
    /// </summary>
    private const int LegacyFps = 20, LegacyLargeFps = 15, LegacyLargeAbove = 1280;
    private const int MinKbps = 100;
    private const int StartKbps = 600;
    /// <summary>
    /// 没发过 SetVideo 的控制端（旧网页）在统计里看到的码率上限最多这么多：它按"目标码率低于上限的四成"提示网络较慢，
    /// 这样门槛是 2000 kbps，和新网页一样（以前流畅档报 2500、清晰档 6000）。
    /// </summary>
    private const int LegacyReportedMaxKbps = 5000;
    /// <summary>编码器不接受"按质量"时的老办法：变化停止后这么久里继续出帧，让固定码率的编码器把画质补上来（按时间算，不随帧率变）。</summary>
    private const int FallbackRefineMs = 400;
    /// <summary>控制端请求的每秒最多几帧（MaxFps）：-1 = 它没说过（旧网页，按老帧率），0 = 不限，其余是上限。换了控制端就作废。</summary>
    private volatile int _clientMaxFps = -1;
    private static readonly TimeSpan ReconnectWindow = TimeSpan.FromSeconds(30);

    /// <summary>设成 off 就不用快速线路（画面和声音只走中继）。</summary>
    public const string RealtimeSwitchVariable = "PRIVATECHAT_AGENT_REALTIME";
    /// <summary>自动化测试用（只在 PRIVATECHAT_AGENT_TEST=1 时生效）：快速线路上故意丢掉这个比例的画面包，看重发管不管用。</summary>
    public const string TestRtpDropVariable = "PRIVATECHAT_AGENT_TEST_RTP_DROP";
    /// <summary>自动化测试用（只在 PRIVATECHAT_AGENT_TEST=1 时生效）：快速线路上每隔这么多秒丢掉一个画面包，而且不补发（谁也补不回来的包）。</summary>
    public const string TestRtpLoseVariable = "PRIVATECHAT_AGENT_TEST_RTP_LOSE_EVERY";
    /// <summary>
    /// 自动化测试用（只在 PRIVATECHAT_AGENT_TEST=1 时生效）：装成一台编码很慢的电脑——每编一帧多等这么多毫秒 × 这一帧的像素数 ÷ 1280×720
    /// （算在编码用时里，最多 1000 ms）。编码的活随像素数变：分辨率降一级就省一截。
    /// </summary>
    public const string TestEncodeDelayVariable = "PRIVATECHAT_AGENT_TEST_ENCODE_DELAY_MS";
    /// <summary>
    /// 自动化测试用（只在 PRIVATECHAT_AGENT_TEST=1 时生效）：装成一台截屏很慢的电脑——每截一次屏多等这么多毫秒（算在采集用时里，最多 1000 ms），
    /// 不管截多大。像 GDI 截屏：按原始尺寸截、缩小还要另外缩放，降分辨率省不了它（桌面复制在显卡上缩放，不会这样）。
    /// </summary>
    public const string TestCaptureDelayVariable = "PRIVATECHAT_AGENT_TEST_CAPTURE_DELAY_MS";
    /// <summary>快速线路：发布之后这么久还没连上 Cloudflare 就算了。</summary>
    private static readonly TimeSpan LineConnectTimeout = TimeSpan.FromSeconds(8);
    /// <summary>接通之后控制端这么久还没说收到画面：它那边没接上，回中继。</summary>
    private const int LineConfirmMs = 12_000;
    /// <summary>控制端这么久没有汇报接收情况：它那边多半收不到了，回中继。</summary>
    private const int LineSilenceMs = 10_000;
    /// <summary>快速线路的连接断开这么久没恢复：回中继。</summary>
    private const int LineDisconnectMs = 5_000;
    /// <summary>
    /// 快速线路上每条轨道至少隔这么久发一个包：Cloudflare 会回收 30 秒没有数据的轨道，
    /// 而屏幕不动时本来一帧都不发、没声音时也不发声音包。
    /// </summary>
    private const int LineKeepAliveMs = 2_000;
    /// <summary>只有一个 TOC 字节的 Opus 包（CELT 全频带、20 ms、双声道、没有数据）：解出来是静音，用来保活声音轨道。</summary>
    private static readonly byte[] OpusSilence = [0xFC];
    /// <summary>遥测：中途每隔这么久报一次到现在为止的汇总（服务器按会话覆盖）。</summary>
    private const int TelemetryCheckpointMs = 60_000;
    /// <summary>遥测：中途那几份每份最多等这么久。</summary>
    private const int TelemetryCheckpointTimeoutMs = 15_000;
    /// <summary>遥测：结束时那一份（含一次重试）最多等这么久，到点就算了——不拖住会话的收尾。</summary>
    private const int TelemetryFinalBudgetMs = 3000;
    /// <summary>系统没在锁屏、桌面复制却一直说被拒（安全桌面）超过这么久：记一笔（那就不是锁屏，是这台电脑上桌面复制有问题）。</summary>
    private const int SourceSecureWarnMs = 10_000;
    /// <summary>后台关出错的硬件编码器超过这么久还没关完：记一笔。</summary>
    private const int DisposeSlowMs = 5000;
    /// <summary>
    /// 桌面复制建不起来、用着出了错，ScreenCapture 退回 GDI 后的名字的开头（"GDI（桌面复制不能用：DuplicateOutput1：…）"，见 Video/ScreenCaptures.cs）。
    /// 没验证过、强制 GDI 的名字不是这个开头（那不算出错）。
    /// </summary>
    private const string DdaFallbackPrefix = "GDI（桌面复制不能用";

    private readonly AgentSessionRequest _request;
    private readonly NormalDesktopGuard _desktop;
    private readonly ServerInfo _server;
    private readonly HttpMessageInvoker? _invoker;
    private readonly Func<IFrameSource> _sourceFactory;
    /// <summary>最近一次探测里试编成功的硬件编码器：每次重建编码器时现问（探测可能刚做完）。</summary>
    private readonly Func<IReadOnlyList<ValidatedEncoder>> _validatedEncoders;
    private readonly Func<IAudioSource> _audioFactory;
    private readonly InputController _input;
    private readonly IAgentUi _ui;
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource<string> _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly NetworkEstimator _net = new(MinKbps, VideoSettings.Auto.MaxKbps, StartKbps);
    private readonly ConcurrentDictionary<uint, long> _pings = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private Thread? _video;
    private Thread? _audio;
    private Task? _main;

    private volatile Connection? _connection;
    private volatile bool _peer;
    private volatile bool _announce;
    private volatile bool _keyRequested;
    private volatile bool _reconfigure;
    private volatile bool _audioOn;
    private volatile int _monitor = -1;
    /// <summary>画质设置：控制端什么都没说时是自动档。</summary>
    private volatile VideoSettings _settings = VideoSettings.Auto;
    /// <summary>这次会话里收到过 SetVideo：之后的 SetQuality 不理（新网页两个都发，后者是给老被控端看的）。</summary>
    private volatile bool _videoSettingsSeen;
    private volatile ScreenInfo? _current;
    private int _framesSent;
    private long _bytesSent;
    /// <summary>主路径上编过的帧：采集（含比较变化）、编码（含转 NV12）各用了多少 Stopwatch 刻度，一共几帧。统计线程每秒算平均。</summary>
    private long _captureTicks;
    private long _encodeTicks;
    private int _measuredFrames;
    /// <summary>采集线程写：自动档因为什么降了分辨率（<see cref="RemoteStatsFlags"/> 的低两位）、现在按每秒几帧发。</summary>
    private volatile int _autoFlags;
    private volatile int _fpsInUse;
    /// <summary>采集线程写，统计线程读（遥测）：现在编的画面多大（高 32 位是宽、低 32 位是高；没有编码器时是 0）、在不在锁屏 / 安全提示界面。</summary>
    private long _encodedSize;
    private volatile bool _secureDesktop;
    private volatile int _lineRttMs;
    private uint _pingSequence;
    private uint _clipboardSeq;
    private string? _clipboardText;
    /// <summary>正在写的剪贴板：之后的键鼠要等它写完（Ctrl+V 紧跟在剪贴板帧后面，不等就会粘出旧内容）。</summary>
    private Task _clipboardWrite = Task.CompletedTask;

    /// <summary>服务器能用快速线路、这台电脑也没关掉它：在 VideoConfig 里告诉控制端，由它决定用不用。</summary>
    private volatile bool _realtimeOffered;
    /** 已经问过服务器这次能不能走快速线路（不管问没问到）。 */
    private volatile bool _realtimeProbed;
    /** 等上面那个结果最晚等到什么时候（会话时钟的毫秒数）：到点了就先按"没有快速线路"发配置。 */
    private long _probeDeadline = long.MaxValue;
    private const int ProbeWaitMs = 2000;
    private volatile bool _resendConfig;
    /// <summary>已经接通的快速线路；null 表示画面和声音只走中继。</summary>
    private volatile Line? _line;
    private readonly Lock _lineGate = new();
    private PublisherOpening? _openingLine;
    private readonly PublisherIceRetry _iceRetry = new();
    // 服务端每个远程会话只保留一份发布登记。换 peer 时让已提交的请求结束后再提交新的。
    private readonly SemaphoreSlim _publishGate = new(1, 1);
    /// <summary>第几次接通（发给控制端，它据此知道要重新拉流）。</summary>
    private int _lineGeneration;
    private long _lastLineKeyAt = -10_000;
    private string _lastLinePath = "";

    /// <summary>这次远程的遥测汇总；环境变量 PRIVATECHAT_AGENT_TELEMETRY=off 时是 null（不收集、不上报）。</summary>
    private readonly SessionTelemetry? _telemetry = SessionTelemetry.Disabled ? null : new SessionTelemetry(AgentService.Version);
    /// <summary>每 60 秒报一次的那个循环（会话收尾时随 _cts 取消；最后一份要等它收掉再报）。</summary>
    private Task _telemetryLoop = Task.CompletedTask;
    /// <summary>服务器不收（老版本服务器、这次会话已经不收了）：这次远程里不再报。</summary>
    private volatile bool _telemetryStopped;
    /// <summary>上次报出去的那份是汇总的第几笔（<see cref="SessionTelemetry.Revision"/>）：没变就不报。</summary>
    private int _telemetrySent = -1;
    private int _telemetryFailureLogged;
    private int _telemetryFinalStarted;

    private sealed class PublisherOpening(SessionTelemetry.PublisherAttempt? telemetry)
    {
        public CancellationTokenSource Cancellation { get; } = new();
        public SessionTelemetry.PublisherAttempt? Telemetry { get; } = telemetry;
        public volatile RealtimePublisher? Publisher;

        public void Finish(SessionTelemetry.PublisherOutcome outcome)
            => Telemetry?.Finish(outcome, Publisher?.NominatedCandidateTypes ?? default);
    }

    private sealed class Line(RealtimePublisher publisher, RealtimeRateController rate, long readyAt)
    {
        public RealtimePublisher Publisher { get; } = publisher;
        public RealtimeRateController Rate { get; } = rate;
        public long ReadyAt { get; } = readyAt;
        /// <summary>
        /// 控制端说它已经在这条线路上收到画面了。在这之前画面两条线路都发、声音只走中继（控制端从中继切过去的那一两秒不黑屏、不重音）；
        /// 之后画面和声音只走这条线路。
        /// </summary>
        public volatile bool Confirmed;
    }

    private sealed class Connection(ClientWebSocket socket)
    {
        public ClientWebSocket Socket { get; } = socket;
        public Channel<byte[]> Outgoing { get; } = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    }

    public SessionRunner(AgentSessionRequest request, ServerInfo server,
        Func<IFrameSource> sourceFactory, Func<IReadOnlyList<ValidatedEncoder>> validatedEncoders, Func<IAudioSource> audioFactory,
        InputController input, IAgentUi ui)
    {
        _request = request;
        _desktop = new NormalDesktopGuard(request.Mode);
        _server = server;
        _invoker = AgentTransport.CreateInvoker(new Uri(server.Url));
        _sourceFactory = sourceFactory;
        _validatedEncoders = validatedEncoders;
        _audioFactory = audioFactory;
        _input = input;
        _ui = ui;
    }

    public Guid Id => _request.SessionId;
    public AgentSessionRequest Request => _request;

    /// <summary>会话自己结束了（服务端关闭、连不上），值是原因。</summary>
    public Task<string> Finished => _finished.Task;

    public void Start()
    {
        _clipboardSeq = _ui.ClipboardSequence;
        _main = Task.Run(RunAsync);
        _video = new Thread(VideoLoop) { IsBackground = true, Name = "remote-video" };
        _video.Start();
        _audio = new Thread(AudioLoop) { IsBackground = true, Name = "remote-audio" };
        _audio.Start();
        if (_telemetry is not null) _telemetryLoop = Task.Run(TelemetryLoopAsync);
    }

    private void Finish(string reason) => _finished.TrySetResult(reason);

    // ---- 连接 ----

    private async Task RunAsync()
    {
        var ct = _cts.Token;
        var lastUp = DateTime.UtcNow;
        var delay = 500;
        // 一开始就问服务器这次能不能走快速线路，和连数据通道同时进行：采集线程要等这个结果才发第一份 VideoConfig
        _ = ProbeRealtimeAsync(ct);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var socket = new ClientWebSocket();
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
                socket.Options.CollectHttpResponseDetails = true;
                _server.Apply(socket.Options);
                try
                {
                    // 系统没有 TLS 1.3 时经程序自带的 TLS 1.3 连（_invoker 不为空）
                    await socket.ConnectVia(_server.WebSocketUri($"/ws/remote/{Id}/agent"), _invoker, ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    var status = socket.HttpStatusCode;
                    socket.Dispose();
                    if (status is HttpStatusCode.NotFound or HttpStatusCode.Conflict or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    {
                        Finish("ended");
                        return;
                    }
                    if (DateTime.UtcNow - lastUp > ReconnectWindow)
                    {
                        _telemetry?.OnError("数据通道 30 秒都没连上，会话结束");
                        Finish("连不上服务器");
                        return;
                    }
                    AgentLog.Warn($"远程数据通道连接失败，{delay} ms 后重试：{ex.Message}");
                    _telemetry?.OnError($"数据通道连接失败：{ex.Message}");
                    await Task.Delay(delay, ct);
                    delay = Math.Min(delay * 2, 5000);
                    continue;
                }

                delay = 500;
                AgentLog.Info($"远程数据通道已连接：{Id}");
                var connection = new Connection(socket);
                _connection = connection;
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var sender = SendLoopAsync(connection, linked.Token);
                var stats = StatsLoopAsync(linked.Token);
                WebSocketCloseStatus? closed = null;
                try
                {
                    closed = await ReceiveLoopAsync(socket, ct);
                }
                catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException)
                {
                    AgentLog.Warn($"远程数据通道断开：{ex.Message}");
                    if (!ct.IsCancellationRequested) _telemetry?.OnError($"数据通道断开：{ex.Message}");
                }
                _connection = null;
                SetPeer(false);
                await linked.CancelAsync();
                await Task.WhenAll(sender, stats);
                socket.Dispose();
                lastUp = DateTime.UtcNow;
                if ((int?)closed is RelayCloseCodes.Ended or RelayCloseCodes.Replaced)
                {
                    Finish("ended");
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AgentLog.Error("远程会话出错", ex);
            _telemetry?.OnError($"远程会话出错：{ex.Message}");
        }
        finally
        {
            Finish("stopped");
        }
    }

    private async Task SendLoopAsync(Connection c, CancellationToken ct)
    {
        try
        {
            await foreach (var frame in c.Outgoing.Reader.ReadAllAsync(ct))
                await c.Socket.SendAsync(frame, WebSocketMessageType.Binary, true, ct);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException)
        {
            // 接收循环会发现连接断了
        }
    }

    /// <summary>放进当前连接的发送队列；队列满（网络发不动）或没连着时返回 false。</summary>
    private bool Send(byte[] frame) => _connection?.Outgoing.Writer.TryWrite(frame) ?? false;

    private async Task<WebSocketCloseStatus?> ReceiveLoopAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var count = 0;
            ValueWebSocketReceiveResult r;
            do
            {
                if (count == buffer.Length)
                {
                    if (buffer.Length >= 4 * RemoteFrames.MaxTextBytes) throw new WebSocketException("控制端发来的消息太大");
                    Array.Resize(ref buffer, buffer.Length * 2);
                }
                r = await socket.ReceiveAsync(buffer.AsMemory(count), ct);
                if (r.MessageType == WebSocketMessageType.Close) return socket.CloseStatus;
                count += r.Count;
            } while (!r.EndOfMessage);

            if (r.MessageType == WebSocketMessageType.Text)
            {
                try
                {
                    var notice = JsonSerializer.Deserialize<RelayNotice>(buffer.AsSpan(0, count), JsonSerializerOptions.Web);
                    if (notice?.Type == "peer") SetPeer(notice.Connected);
                }
                catch (JsonException) { }
                continue;
            }
            var frame = RemoteFrames.ParseControl(buffer.AsSpan(0, count));
            var generation = _desktop.Generation;
            if (frame is not (null or ControlFrame.FrameAck or ControlFrame.Pong or ControlFrame.Clipboard or ControlFrame.ReleaseAll) && !_clipboardWrite.IsCompleted)
                await Task.WhenAny(_clipboardWrite, Task.Delay(1000, ct));
            Handle(frame, generation);
        }
    }

    private void SetPeer(bool connected)
    {
        // 换了一条连接（或者对面走了）：在途的帧不会再确认，线路的往返时间也要重新量
        _net.ResetPath();
        _pings.Clear();
        _lineRttMs = 0;
        // 控制端被新标签页替换时，中继会直接通知新对端已连接。
        _audioOn = false;
        // 帧率限制是上一个控制端要的（它的设备慢）；新来的会不会说、要不要限，等它自己说
        _clientMaxFps = -1;
        lock (_desktop)
        {
            _desktop.ResetPeer();
            _peer = false;
            _input.ReleaseAll();
        }
        // 快速线路是上一个控制端要的：先回到中继，新来的想用会再说
        StopRealtime(null);
        if (connected)
        {
            _announce = true;
            _keyRequested = true;
            // 上一个控制端发过 SetVideo，不等于新来的也会发：顶掉它的可能是还没更新的旧网页（PWA 要点了更新才换），
            // 它只会发 SetQuality，不重新看的话它的流畅 / 清晰怎么点都不管用。画质设置本身留着（和以前一样，新来的想改会再说）。
            // 中继先发对端已连上的通知、再转发新控制端的帧：新网页的 SetVideo 一定在这之后到
            _videoSettingsSeen = false;
        }
        _peer = connected;
        AgentLog.Info(connected ? "控制端已连上，开始发画面" : "控制端不在，暂停采集");
        // 第一帧画面发出去之前先量一下线路：关键帧很大，它自己的往返时间里大半是传输时间，当不了基准
        if (connected) Ping();
    }

    private void Ping()
    {
        var sequence = Interlocked.Increment(ref _pingSequence);
        _pings[sequence] = _clock.ElapsedMilliseconds;
        if (!Send(RemoteFrames.Ping(sequence))) _pings.TryRemove(sequence, out _);
    }

    // ---- 控制端发来的帧 ----

    private void Handle(ControlFrame? frame, int generation)
    {
        switch (frame)
        {
            case null:
                return;
            case ControlFrame.FrameAck ack:
                _net.OnAck(ack.TimestampMs, _clock.ElapsedMilliseconds);
                return;
            case ControlFrame.Pong pong:
                if (_pings.TryRemove(pong.Sequence, out var sentAt))
                {
                    var now = _clock.ElapsedMilliseconds;
                    _lineRttMs = (int)(now - sentAt);
                    _net.OnProbe(_lineRttMs, now);
                }
                return;
            case ControlFrame.RequestKeyFrame:
                // 控制端丢掉了解码器里的帧，不会再确认它们：在途的清空，马上发关键帧
                _net.ForgetInFlight();
                _keyRequested = true;
                _telemetry?.OnKeyFrameRequested();
                if (VerboseLine) AgentLog.Info("快速线路：控制端经控制通道要关键帧");
                return;
            case ControlFrame.SetVideo v:
                var video = VideoSettings.From(v);
                _videoSettingsSeen = true;
                // 网页每次重连都把设置再发一遍：没变就什么都不动（自动档降过的分辨率、"一分钟不升"都留着）
                if (video == _settings)
                {
                    AgentLog.Info($"画质没变：{video.Name}");
                    return;
                }
                _settings = video;
                _reconfigure = true;
                AgentLog.Info($"画质改成 {video.Name}：{video.Summary}");
                return;
            case ControlFrame.SetQuality q:
                // 新网页先发 SetVideo、再发它给老被控端看（老的不认识 SetVideo）：收到过 SetVideo 就以那个为准
                if (_videoSettingsSeen) return;
                _settings = VideoSettings.FromLegacy(q.Preset);
                _reconfigure = true;
                return;
            case ControlFrame.MaxFps m:
                var before = _clientMaxFps;
                if (m.Fps != before)
                    AgentLog.Info(m switch
                    {
                        { Fps: 0 } => before < 0 ? "控制端跟不上时会说：按最高 30 帧发" : "控制端跟上了，帧率不再按它限制",
                        _ when before > 0 && m.Fps > before => $"控制端缓过来了，放开到每秒最多 {m.Fps} 帧",
                        _ => $"控制端解码跟不上，请这边每秒最多发 {m.Fps} 帧",
                    });
                _clientMaxFps = m.Fps;
                _telemetry?.OnMaxFps(before, m.Fps);
                // 要得比之前少：之前那些"确认来得晚"是它在排队解码，不是网络堵——因此降的码率补回来
                if (m.Fps > 0 && (before <= 0 || m.Fps < before)) _net.ReceiverWasBehind(_clock.ElapsedMilliseconds);
                return;
            case ControlFrame.SelectMonitor m:
                _monitor = m.Index;
                _reconfigure = true;
                return;
            case ControlFrame.SetAudio a:
                if (_audioOn != a.On) AgentLog.Info(a.On ? "控制端要听这台电脑的声音，开始采集" : "控制端关了声音");
                _audioOn = a.On;
                return;
            case ControlFrame.SetTransport t:
                if (t.Transport == RemoteTransport.Realtime) StartRealtime();
                else
                {
                    if (_line is not null) AgentLog.Info("控制端要求改回中继");
                    StopRealtime(RemoteTransportState.Relay);
                }
                return;
            case ControlFrame.ReceiverReport r:
                if (_line is { } reported)
                {
                    // 先记下这次汇报，再算"接上了"：CheckRealtime 在另一个线程上看"接上之后多久没汇报"，
                    // 顺序反过来的话，刚接上的那一瞬间会被它当成很久没汇报
                    var slowed = reported.Rate.OnReceiverReport(r.LossPermille, r.ReceivedKbps, r.JitterBufferMs, r.RttMs, _clock.ElapsedMilliseconds);
                    if (!reported.Confirmed && r.Fps > 0)
                    {
                        reported.Confirmed = true;
                        AgentLog.Info("控制端已经在快速线路上收到画面，画面和声音不再经中继");
                    }
                    // 遥测只记确认之后的汇报：之前它还没在这条线路上收到画面，帧率是 0，记进去像是卡住了
                    if (reported.Confirmed) _telemetry?.OnReceiverReport(r.LossPermille, r.ReceivedKbps, r.JitterBufferMs, r.Fps);
                    if (slowed is not null) AgentLog.Info($"快速线路降码率：{slowed}");
                    if (VerboseLine) AgentLog.Info($"快速线路汇报：丢包 {r.LossPermille / 10.0:0.#}%，收 {r.ReceivedKbps} kbps，缓冲 {r.JitterBufferMs} ms，{r.Fps} fps，往返 {r.RttMs} ms；发 {reported.Rate.SentKbps} kbps，目标 {reported.Rate.TargetKbps} kbps，重发 {reported.Publisher.Resent} 包");
                }
                return;
        }

        lock (_desktop)
        {
            if (frame is ControlFrame.ReleaseAll) _input.ReleaseAll();
            else if (_peer && !_cts.IsCancellationRequested && _desktop.CanControl(generation)) HandleInput(frame, generation);
        }
    }

    private void HandleInput(ControlFrame frame, int generation)
    {
        switch (frame)
        {
            case ControlFrame.MouseMove m when _current is { } screen:
                _input.MouseMove(screen.Bounds, m.X, m.Y);
                break;
            case ControlFrame.MouseButton b:
                _input.MouseButton(b.Button, b.Down);
                break;
            case ControlFrame.Wheel w:
                _input.Wheel(w.Dx, w.Dy);
                break;
            case ControlFrame.Key k:
                _input.Key(k.ScanCode, k.Down);
                break;
            case ControlFrame.Text t:
                _input.Text(t.Value);
                break;
            case ControlFrame.SpecialKey s:
                if (!_input.Special(s.Id)) Send(RemoteFrames.Status(RemoteStatusCode.Unsupported, "Ctrl+Alt+Del 只能在那台电脑上按"));
                break;
            case ControlFrame.Clipboard c:
                _clipboardWrite = WriteClipboardAsync(c.Value, generation);
                break;
        }
    }

    private bool CanUseClipboard(int generation)
    {
        lock (_desktop) return _peer && !_cts.IsCancellationRequested && _desktop.CanControl(generation);
    }

    private async Task WriteClipboardAsync(string text, int generation)
    {
        try
        {
            // 调用方已持有桌面状态锁；读剪贴板的轮询可能比写完后的 continuation 更早拿到新文字。
            _clipboardText = text;
            await _ui.WriteClipboardAsync(text, () => CanUseClipboard(generation));
            lock (_desktop)
            {
                if (!_desktop.CanControl(generation)) return;
                _clipboardSeq = _ui.ClipboardSequence; // 自己写的不再同步回去
            }
        }
        catch (Exception ex)
        {
            AgentLog.Warn($"写剪贴板失败：{ex.Message}");
        }
    }

    // ---- 快速线路：画面和声音经 Cloudflare Realtime，不经服务器中继 ----

    /// <summary>问一次服务器这次远程能不能走快速线路；能的话补发一次 VideoConfig 告诉控制端。</summary>
    private async Task ProbeRealtimeAsync(CancellationToken ct)
    {
        Interlocked.Exchange(ref _probeDeadline, _clock.ElapsedMilliseconds + ProbeWaitMs);
        if (Environment.GetEnvironmentVariable(RealtimeSwitchVariable)?.Trim().ToLowerInvariant() is "off" or "0" or "false")
        {
            AgentLog.Info($"快速线路已用环境变量 {RealtimeSwitchVariable} 关闭，画面只走中继");
            _realtimeProbed = true;
            return;
        }
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var api = new ServerApi(_server);
            // 只问有没有（采集线程在等这个结果）；真要接的时候再要 ICE 服务器
            var asked = _clock.ElapsedMilliseconds;
            var offered = await api.GetRealtimeAsync(Id, timeout.Token, withIce: false) is { Available: true };
            AgentLog.Info($"服务器{(offered ? "开了" : "没开")}快速线路（问了 {_clock.ElapsedMilliseconds - asked} ms）");
            if (!offered) return;
            _realtimeOffered = true;
            // 采集线程等到了这个结果的话，第一份配置里就带着；没等到（超过 2 秒）才需要补发
            _resendConfig = true;
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested) AgentLog.Warn($"问不到服务器有没有快速线路，这次只走中继：{ex.Message}");
        }
        finally
        {
            _realtimeProbed = true;
        }
    }

    private void StartRealtime()
    {
        PublisherOpening attempt;
        lock (_lineGate)
        {
            if (!_peer || _cts.IsCancellationRequested) return;
            if (_line is not null)
            {
                // 已经接通了（控制端可能没收到上一条通知）：再说一遍
                Send(RemoteFrames.Transport(RemoteTransportState.Realtime, _lineGeneration));
                return;
            }
            if (_openingLine is not null) return;
            if (!_realtimeOffered)
            {
                Send(RemoteFrames.Transport(RemoteTransportState.RealtimeFailed, _lineGeneration, "服务器没有开快速线路"));
                _telemetry?.OnLineFailed("服务器没有开快速线路");
                return;
            }
            _openingLine = attempt = new PublisherOpening(_telemetry?.BeginPublisherAttempt());
        }
        AgentLog.Info("控制端要走快速线路，开始接通");
        _ = Task.Run(() => OpenRealtimeAsync(attempt));
    }

    private async Task OpenRealtimeAsync(PublisherOpening attempt)
    {
        RealtimePublisher? publisher = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(attempt.Cancellation.Token, deadline.Token);
        IReadOnlyList<RemoteIceServer> servers = [];
        var stage = "ICE 配置";
        const string connecting = "ICE/DTLS 接通";
        try
        {
            timeout.Token.ThrowIfCancellationRequested();
            attempt.Telemetry?.Enter(SessionTelemetry.PublisherStage.IceConfig);
            using var api = new ServerApi(_server);
            var info = await api.GetRealtimeAsync(Id, timeout.Token);
            if (info is not { Available: true }) throw new PublisherStartupException("服务器没有开快速线路");
            servers = info.IceServers;
            bool relayOnly;
            lock (_lineGate)
            {
                if (_openingLine != attempt) return;
                relayOnly = _iceRetry.TakeRelayOnly(servers);
                attempt.Telemetry?.Enter(SessionTelemetry.PublisherStage.Gather, relayOnly);
            }
            stage = "收集本地候选";
            if (VerboseLine) LibraryLog.EnableIfAsked();
            publisher = new RealtimePublisher(servers, relayOnly);
            if (attempt.Telemetry is not null) attempt.Publisher = publisher;
            if (TestDropRate() is > 0 and var drop) publisher.DropForTest = _ => Random.Shared.NextDouble() < drop;
            if (TestLoseEveryMs() is > 0 and var every)
            {
                // 只在采集线程上调（发画面的那把锁里），不用再加锁
                var next = _clock.ElapsedMilliseconds + every;
                publisher.LoseForTest = sequence =>
                {
                    var now = _clock.ElapsedMilliseconds;
                    if (now < next) return false;
                    next = now + every;
                    AgentLog.Info($"快速线路：测试，故意丢掉一个画面包（序号 {sequence}），不补发");
                    return true;
                };
            }
            if (VerboseLine)
            {
                publisher.Trace = message => AgentLog.Info($"快速线路：{message}");
                publisher.TraceFrames = Environment.GetEnvironmentVariable("PRIVATECHAT_AGENT_REALTIME_FRAMES") == "1";
            }
            var offer = await publisher.CreateOfferAsync(timeout.Token);
            stage = "SFU 发布";
            attempt.Telemetry?.Enter(SessionTelemetry.PublisherStage.Publish);
            await _publishGate.WaitAsync(timeout.Token);
            RemoteRealtimePublishResponse answer;
            try
            {
                Task<RemoteRealtimePublishResponse> publish;
                lock (_lineGate)
                {
                    if (_openingLine != attempt) return;
                    timeout.Token.ThrowIfCancellationRequested();
                    // 请求已经交给服务器以后，即便换了 peer 也等这次登记返回；本次总时限和会话关闭仍会终止等待。
                    publish = api.PublishRealtimeAsync(Id, new RemoteRealtimePublishRequest(offer, publisher.Tracks), deadline.Token);
                }
                answer = await publish;
            }
            finally { _publishGate.Release(); }
            timeout.Token.ThrowIfCancellationRequested();
            stage = "应用 SFU 应答";
            attempt.Telemetry?.Enter(SessionTelemetry.PublisherStage.Handshake);
            publisher.SetAnswer(answer.Answer);
            stage = connecting;
            await publisher.WaitConnectedAsync(LineConnectTimeout, timeout.Token);

            // 码率从中继那边估到的起步（至少是起步码率），之后按这条线路自己的情况调
            var line = new Line(publisher, new RealtimeRateController(MinKbps, _net.MaxKbps, Math.Max(StartKbps, _net.TargetKbps)), _clock.ElapsedMilliseconds);
            lock (_lineGate)
            {
                if (_openingLine != attempt || !_peer || _cts.IsCancellationRequested) return;
                publisher.KeyFrameRequested += OnLineKeyRequest;
                publisher.ReceptionReported += r =>
                {
                    if (r.Video) line.Rate.OnUpstreamLoss(r.FractionLost, _clock.ElapsedMilliseconds);
                };
                publisher.StateChanged += state => OnLineState(line, state);
                attempt.Finish(SessionTelemetry.PublisherOutcome.Connected);
                _openingLine = null;
                _iceRetry.Reset();
                _lineGeneration = (_lineGeneration + 1) & 0xFF;
                _line = line;
                Send(RemoteFrames.Transport(RemoteTransportState.Realtime, _lineGeneration));
            }
            publisher = null; // 归 _line 管了
            AgentLog.Info("快速线路已接通（经 Cloudflare Realtime），等控制端接上");
            _telemetry?.OnLineConnected();
            // 登记事件之前连接可能已经断了
            if (line.Publisher.State is RealtimeLinkState.Failed or RealtimeLinkState.Closed) FailRealtime(line, "连接断开");
        }
        catch (Exception ex)
        {
            string reason;
            lock (_lineGate)
            {
                if (_openingLine != attempt || _cts.IsCancellationRequested) return;
                attempt.Finish(SessionTelemetry.PublisherOutcome.Failed);
                _openingLine = null;
                var timedOut = ex is TimeoutException || ex is OperationCanceledException && deadline.IsCancellationRequested;
                if (stage == connecting && timedOut && publisher?.IceChecksPending == true) _iceRetry.IceTimedOut(servers);
                var detail = ex switch
                {
                    PublisherStartupException => ex.Message,
                    TimeoutException when stage == connecting => ex.Message, // WaitConnectedAsync 自己生成的状态枚举
                    _ when timedOut => "超时",
                    ServerException se => $"HTTP {(int)se.Status}",
                    _ => ex.GetType().Name,
                };
                reason = $"{stage}：{detail}";
                if (publisher is not null) reason += $"；{publisher.CandidateSummary}";
                Send(RemoteFrames.Transport(RemoteTransportState.RealtimeFailed, _lineGeneration, reason));
            }
            AgentLog.Warn($"快速线路没接通，画面继续走中继：{reason}");
            _telemetry?.OnLineFailed($"没接通：{reason}");
        }
        finally
        {
            lock (_lineGate)
            {
                if (_openingLine == attempt)
                {
                    attempt.Finish(SessionTelemetry.PublisherOutcome.Cancelled);
                    _openingLine = null;
                }
                attempt.Cancellation.Dispose();
            }
            publisher?.Dispose();
        }
    }

    /// <summary>排查用：PRIVATECHAT_AGENT_REALTIME=verbose 时把控制端每秒的汇报都记进日志。</summary>
    private static readonly bool VerboseLine = Environment.GetEnvironmentVariable(RealtimeSwitchVariable)?.Trim().ToLowerInvariant() == "verbose";

    private static double TestDropRate()
        => Environment.GetEnvironmentVariable("PRIVATECHAT_AGENT_TEST") == "1"
            && double.TryParse(Environment.GetEnvironmentVariable(TestRtpDropVariable), System.Globalization.CultureInfo.InvariantCulture, out var rate) ? rate : 0;

    private static long TestLoseEveryMs()
        => Environment.GetEnvironmentVariable("PRIVATECHAT_AGENT_TEST") == "1"
            && double.TryParse(Environment.GetEnvironmentVariable(TestRtpLoseVariable), System.Globalization.CultureInfo.InvariantCulture, out var seconds) && seconds > 0
            ? (long)(seconds * 1000) : 0;

    /// <summary>测试用的"多等多少毫秒"钩子（见 <see cref="TestEncodeDelayVariable"/>、<see cref="TestCaptureDelayVariable"/>）：没开就是 0，最多 1000。</summary>
    private static int TestDelayMs(string variable)
        => Environment.GetEnvironmentVariable("PRIVATECHAT_AGENT_TEST") == "1"
            && int.TryParse(Environment.GetEnvironmentVariable(variable), System.Globalization.CultureInfo.InvariantCulture, out var ms) && ms > 0
            ? Math.Min(ms, 1000) : 0;

    /// <summary>
    /// 不走快速线路了，画面和声音回到中继（采集线程发现线路变了会从关键帧开始）。
    /// only 不为空时只在当前线路就是它时才停；state 不为空时告诉控制端。
    /// 返回这次是不是真摘下了一条线路（在 _lineGate 里定的：几个地方同时来停同一条线路，只有一个拿到 true）。
    /// </summary>
    private bool StopRealtime(RemoteTransportState? state, string reason = "", Line? only = null)
    {
        Line? line;
        int generation;
        lock (_lineGate)
        {
            if (only is not null && !ReferenceEquals(_line, only)) return false;
            _openingLine?.Finish(SessionTelemetry.PublisherOutcome.Cancelled);
            _openingLine?.Cancellation.Cancel();
            _openingLine = null;
            _iceRetry.Reset();
            line = _line;
            _line = null;
            generation = _lineGeneration;
        }
        // 关连接要一点时间，而且可能就是在它自己的回调里走到这儿的：换个线程关
        if (line is not null) _ = Task.Run(line.Publisher.Dispose);
        if (state is { } s) Send(RemoteFrames.Transport(s, generation, reason));
        return line is not null;
    }

    private void FailRealtime(Line line, string reason)
    {
        // 同一次断开可能几处同时发现（连接状态回调、断开 5 秒的计时、统计线程的检查、刚接通时的那次检查）：
        // 只有真把这条线路摘下来的那一处记日志、算一次失败
        if (!StopRealtime(RemoteTransportState.RealtimeFailed, reason, line)) return;
        AgentLog.Warn($"快速线路不能用了，画面改回中继：{reason}");
        _telemetry?.OnLineFailed(reason);
    }

    private void OnLineState(Line line, RealtimeLinkState state)
    {
        switch (state)
        {
            case RealtimeLinkState.Failed or RealtimeLinkState.Closed:
                FailRealtime(line, "连接断开");
                break;
            case RealtimeLinkState.Disconnected:
                // 网络抖一下可能自己恢复：等几秒还没好再回中继
                _ = Task.Delay(LineDisconnectMs, _cts.Token).ContinueWith(_ =>
                {
                    if (line.Publisher.State != RealtimeLinkState.Connected) FailRealtime(line, "连接断开超过 5 秒");
                }, TaskContinuationOptions.OnlyOnRanToCompletion);
                break;
        }
    }

    private void OnLineKeyRequest()
    {
        // 对面会连着要好几次：半秒内只出一个关键帧
        var now = _clock.ElapsedMilliseconds;
        if (now - Interlocked.Read(ref _lastLineKeyAt) < 500) return;
        Interlocked.Exchange(ref _lastLineKeyAt, now);
        _keyRequested = true;
        _telemetry?.OnKeyFrameRequested();
    }

    /// <summary>每半秒看一次快速线路还好不好：控制端一直没接上、或者很久没回报了，就回中继。</summary>
    private void CheckRealtime()
    {
        if (_line is not { } line) return;
        var now = _clock.ElapsedMilliseconds;
        line.Rate.SetUpstreamRtt(line.Publisher.RoundTripMs);
        if (VerboseLine && line.Publisher.Path is var path && path != _lastLinePath)
        {
            _lastLinePath = path;
            AgentLog.Info($"快速线路：走的地址 {path}");
        }
        if (!line.Confirmed && now - line.ReadyAt > LineConfirmMs) FailRealtime(line, "控制端没有接上快速线路");
        else if (line.Confirmed && now - Math.Max(line.Rate.LastReportAt, line.ReadyAt) > LineSilenceMs) FailRealtime(line, "控制端很久没有回报接收情况");
    }

    // ---- 统计、探测、剪贴板 ----

    private async Task StatsLoopAsync(CancellationToken ct)
    {
        var process = Process.GetCurrentProcess();
        var cpuBefore = process.TotalProcessorTime;
        var wallBefore = _clock.Elapsed;
        var framesBefore = Volatile.Read(ref _framesSent);
        var bytesBefore = Interlocked.Read(ref _bytesSent);
        var measuredBefore = Volatile.Read(ref _measuredFrames);
        var captureBefore = Interlocked.Read(ref _captureTicks);
        var encodeBefore = Interlocked.Read(ref _encodeTicks);
        var tick = 0;
        var lastTarget = 0;
        Line? lastLine = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(500, ct);
                if (_peer && _request.Mode == RemoteSessionMode.Control) await SyncClipboardAsync();
                if (_peer)
                {
                    // 没回来的探测包只留最近的（老版本网页不回 Pong）
                    var now = _clock.ElapsedMilliseconds;
                    foreach (var (sequence, sentAt) in _pings)
                        if (now - sentAt > 30_000) _pings.TryRemove(sequence, out _);
                    Ping();
                    CheckRealtime();
                }
                if (++tick % 2 != 0) continue;

                process.Refresh();
                var cpu = process.TotalProcessorTime;
                var wall = _clock.Elapsed;
                var frames = Volatile.Read(ref _framesSent);
                var bytes = Interlocked.Read(ref _bytesSent);
                var measured = Volatile.Read(ref _measuredFrames);
                var captureTicks = Interlocked.Read(ref _captureTicks);
                var encodeTicks = Interlocked.Read(ref _encodeTicks);
                var seconds = Math.Max(0.001, (wall - wallBefore).TotalSeconds);
                var cpuPercent = (int)((cpu - cpuBefore).TotalSeconds / seconds / Environment.ProcessorCount * 100);
                // 画面走哪条线路，码率和往返时间就报哪条的
                var line = _line is { Confirmed: true } confirmed ? confirmed : null;
                IRateController net = line is not null ? line.Rate : _net;
                if (!ReferenceEquals(line, lastLine))
                {
                    lastLine = line;
                    lastTarget = 0;
                }
                var target = net.TargetKbps;
                if (_peer)
                {
                    // 往返时间：探测包量到的线路延迟；老版本网页不回探测包，就用最近一帧画面从发出到确认的时间
                    var rtt = line is not null ? line.Rate.LastRttMs : _lineRttMs > 0 ? _lineRttMs : _net.LastRttMs;
                    var (captureMs10, encodeMs10, flags) = FrameCost(measured - measuredBefore, captureTicks - captureBefore, encodeTicks - encodeBefore);
                    // 没发过 SetVideo 的是旧网页：它按"目标码率低于上限的四成"提示网络较慢，上限 8000 时 3200 kbps 以下都算慢，
                    // 而画面不动时目标码率不往上涨，快网上也常年显示网络较慢。报给它的上限最多 5000：门槛是 2000 kbps，和新网页一样
                    var max = _videoSettingsSeen ? net.MaxKbps : Math.Min(net.MaxKbps, LegacyReportedMaxKbps);
                    var sentFps = (int)Math.Round((frames - framesBefore) / seconds);
                    var sentKbps = (int)((bytes - bytesBefore) * 8 / 1000 / seconds);
                    Send(RemoteFrames.Stats(new StatsFrame(sentFps, sentKbps, cpuPercent, rtt, target, max, captureMs10, encodeMs10, flags)));
                    if (_telemetry is { } telemetry)
                    {
                        // 这一秒一帧没编：采集、编码用时没有量到（不是 0 ms）
                        var costed = measured > measuredBefore;
                        var size = Interlocked.Read(ref _encodedSize);
                        telemetry.Sample(new SessionSecond(sentFps, sentKbps, target, costed ? captureMs10 / 10.0 : null, costed ? encodeMs10 / 10.0 : null,
                            cpuPercent, rtt, (flags & RemoteStatsFlags.CpuBound) != 0, line is not null, _settings,
                            (int)(size >> 32), (int)(size & 0xFFFF_FFFF), _secureDesktop));
                    }
                    // 码率明显变了记一笔：事后看日志能知道当时网络怎么样
                    if (lastTarget == 0 || Math.Abs(target - lastTarget) > lastTarget / 3)
                    {
                        AgentLog.Info($"网络：目标码率 {target} kbps（上限 {net.MaxKbps}），线路往返 {net.MinRttMs} ms{(line is not null ? "（快速线路）" : "")}");
                        lastTarget = target;
                    }
                }
                (cpuBefore, wallBefore, framesBefore, bytesBefore) = (cpu, wall, frames, bytes);
                (measuredBefore, captureBefore, encodeBefore) = (measured, captureTicks, encodeTicks);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// 这一秒里平均每编一帧，采集、编码各用了多久（0.1 ms），和统计里该带的标志：自动档降没降分辨率、为什么降；
    /// 编了至少 5 帧、而且采集加编码平均占了一帧时间的八成五以上，就是这台电脑忙不过来了（CpuBound）。
    /// </summary>
    private (int CaptureMs10, int EncodeMs10, RemoteStatsFlags Flags) FrameCost(int frames, long captureTicks, long encodeTicks)
    {
        var flags = (RemoteStatsFlags)_autoFlags;
        if (frames <= 0) return (0, 0, flags);
        var captureMs = captureTicks * 1000.0 / Stopwatch.Frequency / frames;
        var encodeMs = encodeTicks * 1000.0 / Stopwatch.Frequency / frames;
        if (frames >= 5 && _fpsInUse is > 0 and var fps && captureMs + encodeMs >= AutoResolution.BusyShare * 1000.0 / fps)
            flags |= RemoteStatsFlags.CpuBound;
        return ((int)Math.Round(captureMs * 10), (int)Math.Round(encodeMs * 10), flags);
    }

    /// <summary>这台电脑上复制了新文字 → 发给控制端。会话开始前剪贴板里已有的内容不发（可能是密码）。</summary>
    private async Task SyncClipboardAsync()
    {
        int generation;
        lock (_desktop)
        {
            generation = _desktop.Generation;
            if (!CanUseClipboard(generation)) return;
            var seq = _ui.ClipboardSequence;
            if (seq == _clipboardSeq) return;
            _clipboardSeq = seq;
        }
        try
        {
            var text = await _ui.ReadClipboardAsync(() => CanUseClipboard(generation));
            lock (_desktop)
            {
                if (!CanUseClipboard(generation) || string.IsNullOrEmpty(text) || text == _clipboardText) return;
                _clipboardText = text;
                Send(RemoteFrames.Clipboard(text));
            }
        }
        catch (Exception ex)
        {
            AgentLog.Warn($"读剪贴板失败：{ex.Message}");
        }
    }

    // ---- 采集 + 编码（独立线程：截屏和编码器都是同步调用；硬件编码器也是等到这一帧的输出才返回）----

    private void VideoLoop()
    {
        IFrameSource? source = null;
        IVideoEncoder? encoder = null;
        QualityPlanner? planner = null;
        // 上一帧（比较变化用）：只和同一种格式、同样每行字节数的帧比（截屏中途从桌面复制退回 GDI 时，NV12 换成了 BGRA）
        var previous = Array.Empty<byte>();
        var previousFormat = FrameFormat.Bgra;
        var previousStride = 0;
        var hasPrevious = false;
        var changed = Array.Empty<bool>();
        // 锁屏期间快速线路保活时重编的那一帧（NV12）：BGRA 的来源每编一帧都在这里转好；桌面复制直接给 NV12，进锁屏时才从最后一帧拷过来
        var nv12 = Array.Empty<byte>();
        var fallbackRefine = 0;
        // 下一帧最早什么时候采：按小数毫秒排（30 fps 是 33.3 ms 一帧，取整成 33 ms 实际会到 30.3 fps，超过上限）
        double nextDue = 0;
        long lastDesktopCheck = -10_000;
        var lastCheckedPeer = -1;
        long lastChangeAt = -10_000;
        long lastSentAt = -10_000;
        uint lastTimestamp = 0;
        // 自动档的分辨率；autoFor / autoScreen：它是按哪份设置、哪块屏幕排的级（设置的值或屏幕变了就从最大的一级重新开始；
        // 按值比：网页重连时重发的同一份设置不算变。离开自动档时忘掉 autoFor，之后再回到自动档也从头开始）
        var auto = new AutoResolution();
        VideoSettings? autoFor = null;
        ScreenInfo? autoScreen = null;
        // 上一次采集时画面有没有变：网络很慢时两次采集之间会隔好几百毫秒，"最近 300 ms 变过"不够用来判断画面还在不在动
        var changedLastTime = false;
        // 现在这个编码器是按哪份设置建的
        VideoSettings? built = null;
        var secure = false;
        Line? lastLine = null;
        IRateController? lastNet = null;
        var lastQuality = 0;
        var nv12Valid = false;
        IReadOnlyList<ScreenInfo> announced = [];
        // 最近一次采到的画面和它的屏幕（画面是来源的缓冲区，下一次采集前有效）：进锁屏时留作保活的那一帧；桌面复制被拒时拿来重试
        CapturedFrame? lastFrame = null;
        ScreenInfo? lastScreen = null;
        // 这次远程里硬件编码器出过错：之后只用软件编码器（之后每次重建也是）
        var softwareOnly = false;
        // 下一轮照常重建一遍编码器（锁屏时临时换上了软件编码器：解锁后发配置、记日志、换新的画质规划）
        var rebuildWanted = false;
        string? encoderNote = null;
        // 日志、遥测里记的截屏方式（来源最近一次采集实际用的；同一块显示器上中途变了——桌面复制出错退回 GDI——再记一笔）
        var pipelineCapture = "";
        // 系统没在锁屏、桌面复制却说是安全桌面：从什么时候起（-1 = 不是这样），记过没有
        long sourceSecureSince = -1;
        var sourceSecureWarned = false;

        // 建编码器：这次远程里硬件出过错就直接用软件的；否则按探测验证过的挑（同一张显卡上的优先），挑不上是软件的（note 说为什么）
        IVideoEncoder CreateEncoder(int width, int height, VideoSettings settings, ScreenInfo screen)
        {
            if (softwareOnly) return new H264Encoder(width, height, settings.Fps, settings.MaxKbps, quality: 30);
            var validated = _validatedEncoders();
            // 桌面复制第一次采集之后才知道这块显示器接在哪张卡上；不知道是 0
            var vendor = (source as IAdapterAwareSource)?.AdapterVendorId(screen) ?? 0;
            var created = VideoEncoders.Create(width, height, settings.Fps, settings.MaxKbps, validated, vendor, out var note);
            if (note != encoderNote)
            {
                encoderNote = note;
                if (note is not null)
                {
                    AgentLog.Info($"编码器：{note}");
                    // 探测验证过的硬件编码器这次起不来才是要查的问题（没有验证过的、用环境变量指定了软件编码器的都不算；
                    // 和 VideoEncoders 认环境变量的写法一样，software / sw 都算）
                    var forcedSoftware = EncoderChoice.ParseSwitch(Environment.GetEnvironmentVariable(VideoEncoders.SwitchVariable)) == EncoderSwitch.Software;
                    if (!created.IsHardware && validated.Count > 0 && !forcedSoftware) _telemetry?.OnError($"硬件编码器没用上：{note}");
                }
            }
            return created;
        }

        // 硬件编码器出错（驱动出错、超时）：记下原因，这个编码器本进程里不再选，这次远程之后只用软件编码器；不结束远程，下一帧是关键帧。
        // 出错的那个放到后台线程上关：连着超时多半是它在驱动里卡住了，关它（END_OF_STREAM、ShutdownObject）也可能卡住，
        // 在采集线程上关就一直建不了软件编码器、画面停住。之后采集线程不会再碰它（MF 的对象在线程池的 MTA 线程上关没问题）
        void HardwareEncoderFailed(IVideoEncoder failed, Exception ex)
        {
            var reason = ex is EncoderFailedException ? ex.Message : $"{ex.GetType().Name}：{ex.Message}";
            if (ex is EncoderFailedException) AgentLog.Warn($"硬件编码器 {failed.Name} 出错，这次远程改用软件编码器：{reason}");
            else AgentLog.Error($"硬件编码器 {failed.Name} 出了意外的错，这次远程改用软件编码器", ex);
            _telemetry?.OnError($"硬件编码器 {failed.Name} 出错，改用软件编码器：{reason}");
            VideoEncoders.MarkFailed(failed.Name, reason);
            softwareOnly = true;
            DisposeInBackground(failed, $"出错的硬件编码器 {failed.Name}");
            Interlocked.Exchange(ref _encodedSize, 0);
            _keyRequested = true;
        }

        var encodeDelayMs = TestDelayMs(TestEncodeDelayVariable);
        if (encodeDelayMs > 0) AgentLog.Info($"测试：每编一帧多等 {encodeDelayMs} ms × 像素数 ÷ 1280×720，最多 1000 ms（{TestEncodeDelayVariable}，装成编码很慢的电脑）");
        var captureDelayMs = TestDelayMs(TestCaptureDelayVariable);
        if (captureDelayMs > 0) AgentLog.Info($"测试：每截一次屏多等 {captureDelayMs} ms，不管截多大（{TestCaptureDelayVariable}，装成截屏很慢的电脑）");
        // 系统计时器默认 15.6 ms 一跳，Sleep(几毫秒) 会睡到下一跳：每帧都晚到下一跳，30 fps 实际只有二十出头。会话期间调到 1 ms
        var fineTimer = timeBeginPeriod(1) == 0;
        try
        {
            source = _sourceFactory();
            // 抓的是真屏幕（GDI、桌面复制）才查锁屏；测试画面不查
            var realScreen = source.IsRealScreen;
            while (!_cts.IsCancellationRequested)
            {
                if (!_peer || _connection is null)
                {
                    DisposeQuietly(encoder, "编码器");
                    encoder = null;
                    Interlocked.Exchange(ref _encodedSize, 0);
                    hasPrevious = false;
                    sourceSecureSince = -1;
                    Thread.Sleep(50);
                    continue;
                }
                // 第一份 VideoConfig 就要说清这次能不能走快速线路：控制端选了快速线路时只等那条线路的画面，
                // 不先从中继出画面再换过去。问服务器一般几百毫秒；问不到也最多等 2 秒，之后问到了再补发一次配置
                if (!_realtimeProbed && _clock.ElapsedMilliseconds < Interlocked.Read(ref _probeDeadline))
                {
                    Thread.Sleep(20);
                    continue;
                }

                var settings = _settings;
                // 离开了自动档：忘掉它是按哪份设置排的级，之后再切回自动档从最大的一级重新开始（放在这里：下面几处提前 continue 的轮次也算看到了）
                if (!settings.AutoResolution) autoFor = null;
                // 控制端说它跟不上时按它说的帧率发；它没说过会说（旧网页）就按以前的帧率发（看现在编的画面有多大）。
                // 编码器按设置的帧率建，不跟着变：按质量编码时那只是名义上的帧率（极少数只能按固定码率编的电脑上，
                // 发得比名义帧率少时每帧还是按名义帧率分码率，实际码率会低一些）
                var longSide = encoder is null ? 0 : Math.Max(encoder.Width, encoder.Height);
                var fps = _clientMaxFps switch
                {
                    < 0 => Math.Min(settings.Fps, longSide <= LegacyLargeAbove ? LegacyFps : LegacyLargeFps),
                    0 => settings.Fps,
                    var cap => Math.Min(cap, settings.Fps),
                };
                _fpsInUse = fps;
                var frameInterval = 1000.0 / fps;
                // 码率预算、在途帧数按整毫秒算就够了
                var frameMs = (int)Math.Round(frameInterval);
                var now = _clock.ElapsedMilliseconds;
                if (now < nextDue)
                {
                    Thread.Sleep((int)Math.Min(Math.Ceiling(nextDue - now), 20));
                    continue;
                }
                nextDue = Math.Max(nextDue + frameInterval, now);

                // 锁屏 / UAC 提示：截不到桌面（GDI 截到黑屏，桌面复制被拒），告诉控制端原因
                var peer = _desktop.PeerGeneration;
                if (peer != lastCheckedPeer || now - lastDesktopCheck >= 1000)
                {
                    lastDesktopCheck = now;
                    lastCheckedPeer = peer;
                    // 输入桌面不是普通桌面（UAC、Ctrl+Alt+Del），或者这个会话锁着屏（Windows 11 的锁屏有时输入桌面还是 Default，
                    // 见 HardwareInfo.SessionLocked）；再加上截屏自己知道的：桌面复制被拒（E_ACCESSDENIED），它比这个每秒一次的检查先知道
                    var locked = realScreen && HardwareInfo.ScreenLocked();
                    // 系统已经不锁屏了、桌面复制还说被拒：它得再采一次才知道现在能不能用了（采到的不要；解锁后从整幅比较重新开始）
                    if (realScreen && !locked && secure && source.SecureDesktop && lastScreen is { } retry && lastFrame is { } last)
                        source.Capture(retry, last.Width, last.Height);
                    var s = realScreen && (locked || source.SecureDesktop);
                    lock (_desktop)
                    {
                        if (!_peer || peer != _desktop.PeerGeneration) continue;
                        if (_desktop.Observe(s))
                        {
                            if (s) _input.ReleaseAll();
                            // 只同步恢复普通桌面之后新复制的内容。
                            else _clipboardSeq = _ui.ClipboardSequence;
                            _clipboardText = null;
                        }
                        _secureDesktop = s;
                        _desktop.Report(blocked => Send(RemoteFrames.Status(
                            blocked ? RemoteStatusCode.SecureDesktop : RemoteStatusCode.Ok, blocked ? "对方电脑在锁屏或系统安全提示界面" : "")));
                    }
                    if (s && !locked)
                    {
                        if (sourceSecureSince < 0) sourceSecureSince = now;
                        else if (!sourceSecureWarned && now - sourceSecureSince >= SourceSecureWarnMs)
                        {
                            sourceSecureWarned = true;
                            AgentLog.Warn($"系统没在锁屏，截屏（{source.Name}）却一直说在安全桌面、被拒：控制端那边一直显示锁屏");
                            _telemetry?.OnError("系统没在锁屏，桌面复制却一直被拒");
                        }
                    }
                    else sourceSecureSince = -1;
                    if (s != secure)
                    {
                        secure = s;
                        if (s)
                        {
                            // 留下锁屏前的最后一帧给快速线路保活：桌面复制给的 NV12 是来源的缓冲区（下一次采集就变了），拷一份。
                            // BGRA 的来源编每一帧时已经转在 nv12 里了
                            if (lastFrame is { Format: FrameFormat.Nv12 } kept && encoder is not null && kept.Width == encoder.Width && kept.Height == encoder.Height)
                            {
                                var keptSize = kept.Width * kept.Height * 3 / 2;
                                if (nv12.Length < keptSize) nv12 = new byte[keptSize];
                                kept.Data.AsSpan(0, keptSize).CopyTo(nv12);
                                nv12Valid = true;
                            }
                        }
                        else
                        {
                            _keyRequested = true;
                            // 锁屏期间可能为了让桌面复制重试采过（那几次的变化提示没用上）：整幅重新比较
                            hasPrevious = false;
                        }
                    }
                }
                if (secure)
                {
                    // 锁屏期间不采集。快速线路上隔一阵把锁屏前的那一帧再编一次发出去（内容没变，很小），不然轨道会被 Cloudflare 回收。
                    // 对面要关键帧（丢的包补不回来了）、或者这条线路是锁屏期间才接上的（那头的解码器是空的）：马上发，而且编成关键帧，
                    // 不然后面这些帧它一帧都解不出来，过一会儿就当线路断了
                    if (_line is { } held && encoder is not null && nv12Valid)
                    {
                        var wantKey = _keyRequested || !ReferenceEquals(held, lastLine);
                        if (wantKey || now - lastSentAt >= LineKeepAliveMs)
                        {
                            if (wantKey)
                            {
                                lastLine = held;
                                _keyRequested = false;
                            }
                            var again = Math.Max((uint)now, lastTimestamp + 1);
                            try
                            {
                                if (wantKey) encoder.RequestKeyFrame();
                                if (encoder.Encode(nv12.AsSpan(0, encoder.Width * encoder.Height * 3 / 2), TimeSpan.FromMilliseconds(again)) is { } same)
                                {
                                    lastTimestamp = again;
                                    held.Publisher.SendVideo(again, same.Data);
                                }
                            }
                            catch (Exception ex) when (encoder.IsHardware)
                            {
                                // 锁屏期间还得接着保活：先换上同样大小的软件编码器（下一次保活是关键帧），解锁后再照常重建一遍
                                var (heldWidth, heldHeight, heldFps) = (encoder.Width, encoder.Height, encoder.Fps);
                                HardwareEncoderFailed(encoder, ex);
                                encoder = null;
                                encoder = new H264Encoder(heldWidth, heldHeight, heldFps, built?.MaxKbps ?? settings.MaxKbps, quality: 30);
                                Interlocked.Exchange(ref _encodedSize, ((long)heldWidth << 32) | (uint)heldHeight);
                                rebuildWanted = true;
                            }
                            lastSentAt = now;
                        }
                    }
                    continue;
                }

                // 快速线路接通后先两条线路都发画面；控制端确认在那边收到了，就只走快速线路，码率也改由它那套来定
                var line = _line;
                var lineOnly = line is { Confirmed: true };
                IRateController net = lineOnly ? line!.Rate : _net;
                if (!ReferenceEquals(line, lastLine))
                {
                    // 线路变了：新线路那头的解码器是空的，回到中继时那边也早就不同步了，都从关键帧开始
                    lastLine = line;
                    _keyRequested = true;
                    if (line is null) _net.ForgetInFlight();
                }
                if (!ReferenceEquals(net, lastNet))
                {
                    // 码率改由另一条线路的那一套来定：之前那条线路"一直很慢"的结论不作数了，自动档因为网络慢降的分辨率退回去重新判断
                    lastNet = net;
                    if (settings.AutoResolution && settings == autoFor && auto.OnNetworkChanged(frameInterval, now) is { } back)
                    {
                        AgentLog.Info(back);
                        _telemetry?.OnAutoStep(SessionTelemetry.AutoStep.Network);
                    }
                }

                net.Tick(now);
                if (net.Stalled(now))
                {
                    // 最早那帧十秒都没确认：控制端多半丢了它，从关键帧重新开始
                    net.ForgetInFlight();
                    _keyRequested = true;
                    _telemetry?.OnStall();
                }
                // 码率预算用完了、或者前面的帧还堵在路上：先不采，等一等（延迟不累积）
                if (!net.CanSend(now, frameMs)) continue;

                var screens = source.Screens();
                var screen = screens.FirstOrDefault(s => s.Index == _monitor) ?? screens.FirstOrDefault(s => s.IsPrimary) ?? screens[0];
                if (settings.AutoResolution)
                {
                    // 自动档：换了设置或屏幕就从最大的一级重新排；之后网络持续很慢降一级、够快了再升回去（见 AutoResolution）
                    if (settings != autoFor || screen != autoScreen)
                    {
                        auto.Reset(screen.Bounds.Width, screen.Bounds.Height, settings);
                        (autoFor, autoScreen) = (settings, screen);
                    }
                    // 画面在不在动：上一次采集时变了，或者最近 300 ms 里变过（和下面的 sustained 一样）
                    var moving = changedLastTime || now - lastChangeAt < 300;
                    if (auto.OnNetwork(net.TargetKbps, moving, frameInterval, now) is { } step)
                    {
                        AgentLog.Info(step);
                        _telemetry?.OnAutoStep(SessionTelemetry.AutoStep.Network);
                    }
                }
                _autoFlags = settings.AutoResolution ? (int)auto.Flags : 0;
                var (width, height) = settings.AutoResolution ? auto.Size : settings.Box(screen.Bounds.Width, screen.Bounds.Height);

                // 采集（含比较变化）、编码（含转 NV12）各用了多久：统计里报给控制端，自动档据此判断这台电脑忙不忙得过来。
                // 先采集、再（需要时）重建编码器：桌面复制第一次采集时才知道这块显示器接在哪张卡上（编码器挑同一张卡上的），
                // 日志里也要写这一帧实际是怎么截的（桌面复制建不起来、这一帧已经退回 GDI 的话写的是 GDI）
                var captureStart = Stopwatch.GetTimestamp();
                var frame = source.Capture(screen, width, height);
                // 测试钩子：截屏慢，截多大都一样慢
                if (captureDelayMs > 0) Thread.Sleep(captureDelayMs);
                var captureTicks = Stopwatch.GetTimestamp() - captureStart;
                (lastFrame, lastScreen) = (frame, screen);
                var captureName = source.Name;
                if (pipelineCapture.Length > 0 && captureName != pipelineCapture && screen == _current)
                {
                    // 同一块显示器上截屏方式变了：桌面复制出错（显卡驱动重置、同时复制桌面的程序太多……），这次远程退回了 GDI
                    // （换了显示器时变了不算：那块可能本来就没验证过桌面复制）
                    AgentLog.Warn($"截屏改用 {captureName}（之前是 {pipelineCapture}）");
                    _telemetry?.OnError($"截屏改用 {captureName}");
                    pipelineCapture = captureName;
                    if (encoder is not null) _telemetry?.SetPipeline(encoder.Name, encoder.QualityMode, pipelineCapture);
                }

                if (rebuildWanted || encoder is null || planner is null || _reconfigure || _announce || !ReferenceEquals(settings, built)
                    || encoder.Width != width || encoder.Height != height || encoder.Fps != settings.Fps || screen != _current)
                {
                    _reconfigure = false;
                    rebuildWanted = false;
                    built = settings;
                    // 先关旧的再建新的（GeForce 上同时开着的硬件编码会话有上限）；建不起来抛出时 finally 里别再关一次
                    DisposeQuietly(encoder, "编码器");
                    encoder = null;
                    encoder = CreateEncoder(width, height, settings, screen);
                    // 这次远程第一次采集、刚换了显示器时桌面复制就建不起来（同时复制桌面的程序太多、视频处理器建不起来……），
                    // 上面"同一块显示器上变了"那一处不管（之前没记过、或者是另一块显示器）：在这里记进遥测的出错。
                    // 名字和上次记的一样就不再记（中途退回的上面已经记过；退回后整个这次远程都是这个名字，换显示器、重连都不变）。
                    // 日志不用再写：ScreenCapture 退回时记过原因，下面"编码 …"那一行最后也写着截屏方式
                    if (captureName != pipelineCapture && captureName.StartsWith(DdaFallbackPrefix, StringComparison.Ordinal))
                        _telemetry?.OnError($"截屏改用 {captureName}");
                    pipelineCapture = captureName;
                    Interlocked.Exchange(ref _encodedSize, ((long)width << 32) | (uint)height);
                    _telemetry?.SetPipeline(encoder.Name, encoder.QualityMode, pipelineCapture);
                    planner = new QualityPlanner(width, height, settings.Ceiling);
                    changed = new bool[FrameTiles.Count(width, height)];
                    _net.SetMax(settings.MaxKbps);
                    line?.Rate.SetMax(settings.MaxKbps);
                    net.ForgetInFlight();
                    _current = screen;
                    hasPrevious = false;
                    nv12Valid = false;
                    _resendConfig = false;
                    Send(RemoteFrames.VideoConfig(new VideoConfigFrame(RemoteVideoCodec.H264, width, height, settings.Fps, screen.Index, Features())));
                    if (_announce || !screens.SequenceEqual(announced))
                    {
                        Send(RemoteFrames.Monitors([.. screens.Select(s => new MonitorInfo(s.Index, s.Bounds.Width, s.Bounds.Height, s.IsPrimary))]));
                        announced = screens;
                    }
                    _announce = false;
                    _keyRequested = true;
                    var lowered = !settings.AutoResolution ? "" : auto.Reason switch
                    {
                        AutoResolutionReason.Network => "（网络慢，降了分辨率）",
                        AutoResolutionReason.Cpu => "（电脑忙不过来，降了分辨率）",
                        _ => "",
                    };
                    // 编码器、截屏方式加在最后（e2e 按前面那一截认这一行）
                    AgentLog.Info($"编码 {width}x{height} {settings.Fps} fps，码率上限 {settings.MaxKbps} kbps，显示器 {screen.Index}，画质 {settings.Name}{lowered}" +
                        (encoder.QualityMode ? "" : "（这台电脑的编码器不支持按质量，用固定码率）") + $"，编码器 {encoder.Name}，截屏 {pipelineCapture}");
                }
                else if (_resendConfig)
                {
                    // 问到服务器有快速线路时第一份配置已经发出去了（问得太慢，没等到）：补发一次，控制端才知道有这条线路
                    _resendConfig = false;
                    AgentLog.Info("补发一次画面配置：服务器有快速线路");
                    Send(RemoteFrames.VideoConfig(new VideoConfigFrame(RemoteVideoCodec.H264, width, height, settings.Fps, screen.Index, Features())));
                }

                // 比较变化（算在采集里）。来源说了哪些块可能变了就只比那几块（桌面复制）；没有上一帧时整幅都算变了，用不着它
                var diffStart = Stopwatch.GetTimestamp();
                if (frame.Format != previousFormat || frame.Stride != previousStride) hasPrevious = false;
                var nv12Frame = frame.Format == FrameFormat.Nv12;
                var length = nv12Frame ? width * height * 3 / 2 : frame.Stride * frame.Height;
                ReadOnlySpan<byte> current = frame.Data.AsSpan(0, length);
                ReadOnlySpan<byte> before = hasPrevious ? previous.AsSpan(0, length) : default;
                ReadOnlySpan<bool> hint = hasPrevious && frame.ChangedHint is { } given && given.Length == changed.Length ? given : default;
                var changedTiles = nv12Frame
                    ? FrameTiles.DiffNv12(before, current, width, height, changed, hint)
                    : FrameTiles.Diff(before, current, width, height, frame.Stride, changed, hint);
                captureTicks += Stopwatch.GetTimestamp() - diffStart;
                changedLastTime = changedTiles > 0;
                var key = _keyRequested;
                var sustained = now - lastChangeAt < 300;
                var refining = false;
                var quality = 0;
                // 快速线路上屏幕不动也要隔一阵发一帧（内容没变，编出来很小），不然 Cloudflare 会把这条轨道回收掉
                var keepAlive = line is not null && now - lastSentAt >= LineKeepAliveMs;
                var learn = true;
                int? bitrate = null;
                if (encoder.QualityMode)
                {
                    if (key || changedTiles > 0)
                    {
                        quality = planner.Plan(changed, changedTiles, key, sustained, net.ChangeBudget(now, sustained && !key, frameMs), net.Credit(now));
                    }
                    else if (now - lastChangeAt >= 150 && net.InFlight == 0
                        && planner.Refine(net.Credit(now), force: net.Idle(now) && now - lastSentAt >= 1000) is { } to)
                    {
                        // 画面停了：等前面的帧都确认了（线路是空的）再补清晰，一步一步来
                        quality = to;
                        refining = true;
                    }
                    else if (keepAlive)
                    {
                        quality = lastQuality;
                        learn = false;
                    }
                    else continue;
                }
                else
                {
                    if (changedTiles == 0 && fallbackRefine <= 0 && !key && !keepAlive) continue;
                    fallbackRefine = changedTiles > 0 ? (int)Math.Ceiling(FallbackRefineMs / frameInterval) : Math.Max(0, fallbackRefine - 1);
                    bitrate = Math.Clamp(net.TargetKbps, MinKbps, settings.MaxKbps);
                }
                if (changedTiles > 0)
                {
                    lastChangeAt = now;
                    if (previous.Length < length) previous = new byte[length];
                    current.CopyTo(previous);
                    (previousFormat, previousStride) = (frame.Format, frame.Stride);
                    hasPrevious = true;
                }

                // 编码器的输入：桌面复制在显卡上已经转好了 NV12（BT.709、16–235，和编码器的输入类型一样），直接编；BGRA 在这里转（算在编码里）
                var size = width * height * 3 / 2;
                var convertStart = Stopwatch.GetTimestamp();
                ReadOnlySpan<byte> input;
                if (nv12Frame)
                {
                    input = current;
                    nv12Valid = false; // nv12 里不是这一帧了（进锁屏时再从最后一帧拷）
                }
                else
                {
                    if (nv12.Length < size) nv12 = new byte[size];
                    Nv12.FromBgra(frame.Bgra, frame.Stride, width, height, nv12);
                    input = nv12.AsSpan(0, size);
                    nv12Valid = true;
                }
                var encodeTicks = Stopwatch.GetTimestamp() - convertStart;
                // 时间戳也是这一帧的编号：控制端按它确认，必须一帧比一帧大
                var ts = Math.Max((uint)_clock.ElapsedMilliseconds, lastTimestamp + 1);
                var encodeStart = Stopwatch.GetTimestamp();
                EncodedFrame? output;
                try
                {
                    if (key)
                    {
                        _keyRequested = false;
                        encoder.RequestKeyFrame();
                    }
                    if (bitrate is { } kbps) encoder.SetBitrate(kbps);
                    encoder.SetQuality(quality);
                    output = encoder.Encode(input, TimeSpan.FromMilliseconds(ts));
                }
                catch (Exception ex) when (encoder.IsHardware)
                {
                    // 这一帧不发了；下一轮马上重建成软件编码器（不等出错的那个关完），从关键帧开始
                    HardwareEncoderFailed(encoder, ex);
                    encoder = null;
                    continue;
                }
                lastQuality = quality;
                // 测试钩子：编码的活随像素数变，1280×720 上多等的就是设的那么多
                if (encodeDelayMs > 0)
                    Thread.Sleep((int)Math.Min(Math.Round(encodeDelayMs * ((double)width * height / (1280 * 720))), 1000));
                encodeTicks += Stopwatch.GetTimestamp() - encodeStart;
                Interlocked.Add(ref _captureTicks, captureTicks);
                Interlocked.Add(ref _encodeTicks, encodeTicks);
                Interlocked.Increment(ref _measuredFrames);
                // 自动档只看画面持续在动时的帧：偶尔一帧（打字、鼠标）慢一点不要紧。
                // 电脑忙降了一级、或者降了发现没快多少又退回去：下一轮按新的大小重建编码器
                var rungBefore = auto.Rung;
                if (settings.AutoResolution && changedTiles > 0 && sustained
                    && auto.OnFrameCost((captureTicks + encodeTicks) * 1000.0 / Stopwatch.Frequency, frameInterval, now) is { } cpuStep)
                {
                    AgentLog.Info(cpuStep);
                    _autoFlags = (int)auto.Flags;
                    // 降到了更小的一级是"电脑忙降的"，回到了更大的一级是"降了没用退回去"
                    _telemetry?.OnAutoStep(auto.Rung > rungBefore ? SessionTelemetry.AutoStep.Cpu : SessionTelemetry.AutoStep.CpuUndo);
                }
                if (output is not { } encoded)
                {
                    // 硬件编码器这一帧没等到（超时、它没要输入）：这一帧已经记成了 previous，下一轮比出来没变化，
                    // 画面不动（画质也补到顶了、中继上没有保活）就一直不再编，控制端一直看不到这次的变化。
                    // 下一轮整幅编一个关键帧重发（硬件编码器自己也会强制关键帧，画质规划按关键帧算）；连着几次这样它会抛出、换软件编码器。
                    // 软件编码器的 null 不一样：输出下一次调用时就出来了
                    if (encoder.IsHardware) _keyRequested = true;
                    continue;
                }
                lastTimestamp = ts;
                // 两条线路都按"中继上这一帧的大小"记账（6 字节帧头 + 数据），画质规划学到的东西换线路后还能用
                var bytes = 6 + encoded.Data.Length;
                if (encoder.QualityMode && learn)
                {
                    if (refining) planner.LearnRefine(quality, bytes);
                    else planner.Learn(quality, changed, changedTiles, key || encoded.IsKey, sustained, bytes);
                }
                line?.Publisher.SendVideo(ts, encoded.Data);
                if (VerboseLine && line is not null && encoded.IsKey) AgentLog.Info($"快速线路：发了一个关键帧，{encoded.Data.Length} 字节（时间戳 {ts}）");
                if (!lineOnly &&!Send(RemoteFrames.VideoFrame(ts, encoded.IsKey, encoded.Data)))
                {
                    // 发送队列满了（网络发不动）：这一帧丢了，后面的 P 帧解不出来，下一帧从关键帧开始
                    _keyRequested = true;
                    _telemetry?.OnRelaySendDrop();
                    continue;
                }
                net.OnSent(ts, bytes, now);
                lastSentAt = now;
                Interlocked.Increment(ref _framesSent);
                Interlocked.Add(ref _bytesSent, bytes);
            }
        }
        catch (Exception ex)
        {
            AgentLog.Error("采集 / 编码出错", ex);
            _telemetry?.OnError($"采集 / 编码出错：{ex.Message}");
            Send(RemoteFrames.Status(RemoteStatusCode.CaptureFailed, ex.Message));
            Thread.Sleep(300); // 让这条状态先发出去
            Finish("采集失败");
        }
        finally
        {
            if (fineTimer) timeEndPeriod(1);
            // 这里抛出去就是整个程序崩掉（线程上没人接）：显卡驱动关的时候出错也只记一笔
            DisposeQuietly(encoder, "编码器");
            DisposeQuietly(source, "截屏");
        }
    }

    /// <summary>关掉编码器 / 截屏：硬件编码器、桌面复制关的时候驱动可能出错，记一笔就算了。</summary>
    private static void DisposeQuietly(IDisposable? disposable, string what)
    {
        if (disposable is null) return;
        try
        {
            disposable.Dispose();
        }
        catch (Exception ex)
        {
            AgentLog.Warn($"关掉{what}时出错：{ex.Message}");
        }
    }

    /// <summary>在线程池上关（出错的硬件编码器：关它可能卡在驱动里，别让采集线程等）；5 秒还没关完记一笔，之后关完了再记一笔。</summary>
    private static void DisposeInBackground(IDisposable disposable, string what)
    {
        var dispose = Task.Run(() => DisposeQuietly(disposable, what));
        _ = Task.Run(async () =>
        {
            if (await Task.WhenAny(dispose, Task.Delay(DisposeSlowMs)) == dispose) return;
            AgentLog.Warn($"关掉{what}过了 {DisposeSlowMs / 1000} 秒还没关完（多半卡在显卡驱动里）：远程照常用软件编码器");
            await dispose;
            AgentLog.Info($"{what}关完了");
        });
    }

    private RemoteFeatures Features()
        => RemoteFeatures.Audio | RemoteFeatures.AdaptiveBitrate | RemoteFeatures.VideoSettings | (_realtimeOffered ? RemoteFeatures.Realtime : RemoteFeatures.None);

    // ---- 声音（独立线程：采集是同步调用，10 ms 取一次）----

    private void AudioLoop()
    {
        long lastLineAudioAt = -10_000;
        // 快速线路上的声音轨道也要保活：没开声音、或者电脑上很久没声音时，隔一阵发一个静音包
        void KeepLineAudioAlive()
        {
            var now = _clock.ElapsedMilliseconds;
            if (_line is not { } line || now - lastLineAudioAt < LineKeepAliveMs) return;
            lastLineAudioAt = now;
            line.Publisher.SendAudio((uint)now, OpusSilence);
        }

        IAudioSource? source = null;
        OpusPacketizer? opus = null;
        var leveler = new AudioLeveler();
        var pcm = new short[AudioFormat.SampleRate * AudioFormat.Channels]; // 最多攒 1 秒
        var packet = new byte[OpusPacketizer.MaxPacketBytes];
        const int frameValues = AudioFormat.FrameSamples * AudioFormat.Channels;
        var filled = 0;
        var silentPackets = 0;
        long timestamp = 0;
        var failures = 0;
        var complained = false;
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                KeepLineAudioAlive();
                if (!_audioOn || !_peer || _connection is null)
                {
                    source?.Dispose();
                    source = null;
                    filled = 0;
                    Thread.Sleep(100);
                    continue;
                }
                try
                {
                    if (source is null)
                    {
                        source = _audioFactory();
                        AgentLog.Info($"声音采集已打开{(source is WasapiLoopback w ? "：" + w.Description : "")}");
                        failures = 0;
                    }
                    filled += source.Read(pcm.AsSpan(filled * AudioFormat.Channels));
                }
                catch (Exception ex)
                {
                    // 没有播放设备、设备被拔掉、默认设备换了：过一会儿重新打开；一直不行就告诉控制端一次
                    source?.Dispose();
                    source = null;
                    filled = 0;
                    if (++failures == 1)
                    {
                        AgentLog.Warn($"声音采集出错，稍后重试：{ex.Message}");
                        _telemetry?.OnError($"声音采集出错：{ex.Message}");
                    }
                    if (failures == 3 && !complained)
                    {
                        complained = true;
                        Send(RemoteFrames.Status(RemoteStatusCode.Unsupported, "对方电脑上没有可用的播放设备，传不了声音"));
                    }
                    Thread.Sleep(failures < 3 ? 500 : 3000);
                    continue;
                }

                while (filled >= AudioFormat.FrameSamples)
                {
                    var frame = pcm.AsSpan(0, frameValues);
                    var peak = AudioLeveler.Peak(frame);
                    // 没有声音（或只有量化噪声）超过 200 ms 就不发了：安静的时候不占流量
                    silentPackets = peak <= 2 ? silentPackets + 1 : 0;
                    if (silentPackets <= 10)
                    {
                        var now = _clock.ElapsedMilliseconds;
                        // 时间戳按包数往前走（每包 20 ms）；停过一阵之后从当前时间重新起算，控制端据此知道中间断过
                        timestamp = timestamp == 0 || now - timestamp > 500 ? now : timestamp + AudioFormat.FrameMs;
                        leveler.Process(frame, peak);
                        opus ??= new OpusPacketizer(64000);
                        // 控制端确认接上快速线路之后声音只走它；之前只走中继（两边同时放会听到两遍）
                        var line = _line is { Confirmed: true } confirmed ? confirmed : null;
                        IRateController net = line is not null ? line.Rate : _net;
                        // 网络慢的时候声音也省一点
                        opus.Bitrate = net.TargetKbps >= 800 ? 64000 : 32000;
                        var written = opus.Encode(frame, packet);
                        if (line is not null)
                        {
                            line.Publisher.SendAudio((uint)timestamp, packet.AsSpan(0, written).ToArray());
                            lastLineAudioAt = now;
                            net.OnSentUntracked(written, now);
                            Interlocked.Add(ref _bytesSent, written);
                        }
                        else
                        {
                            var payload = RemoteFrames.Audio((uint)timestamp, RemoteAudioCodec.Opus, AudioFormat.Channels, packet.AsSpan(0, written));
                            if (Send(payload))
                            {
                                net.OnSentUntracked(payload.Length, now);
                                Interlocked.Add(ref _bytesSent, payload.Length);
                            }
                        }
                    }
                    filled -= AudioFormat.FrameSamples;
                    Array.Copy(pcm, frameValues, pcm, 0, filled * AudioFormat.Channels);
                }
                Thread.Sleep(10);
            }
        }
        catch (Exception ex)
        {
            // 声音出问题不影响画面和键鼠
            AgentLog.Error("声音线程出错", ex);
            _telemetry?.OnError($"声音线程出错：{ex.Message}");
        }
        finally
        {
            source?.Dispose();
        }
    }

    // ---- 遥测：这次远程的汇总报给服务器（docs/DESIGN.md"远程协助遥测"）----

    /// <summary>每 60 秒把到现在为止的汇总报一次（会话收尾时随 _cts 取消，最后那份由 <see cref="DisposeAsync"/> 报）。</summary>
    private async Task TelemetryLoopAsync()
    {
        var ct = _cts.Token;
        try
        {
            while (!_telemetryStopped && !ct.IsCancellationRequested)
            {
                await Task.Delay(TelemetryCheckpointMs, ct);
                await ReportCheckpointAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            TelemetryFailed(ex);
        }
    }

    /// <summary>中途的一份：上次报过之后什么都没变（控制端一直没连着）就不报；失败了等下一分钟。</summary>
    private async Task ReportCheckpointAsync(CancellationToken ct)
    {
        if (_telemetry is not { } telemetry || _telemetryStopped) return;
        var revision = telemetry.Revision;
        if (revision == _telemetrySent) return;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TelemetryCheckpointTimeoutMs);
            await SendTelemetryAsync(telemetry.Build(final: false), timeout.Token);
            _telemetrySent = revision;
        }
        catch (Exception ex)
        {
            // 会话在收尾（取消了）不算失败
            if (!ct.IsCancellationRequested) TelemetryFailed(ex);
        }
    }

    /// <summary>
    /// 会话收尾时的最后一份（Final）：先等中途那份收掉（它随 _cts 取消了；不等的话它要是晚到服务器，会把最后这份盖掉），
    /// 再报；网络出错（不是服务器明确拒绝）隔一下重试一次。一共最多 <see cref="TelemetryFinalBudgetMs"/>，到点就算了。不抛异常。
    /// </summary>
    private async Task ReportFinalAsync()
    {
        if (_telemetry is not { } telemetry) return;
        try
        {
            using var budget = new CancellationTokenSource(TelemetryFinalBudgetMs);
            await _telemetryLoop.WaitAsync(budget.Token);
            if (_telemetryStopped) return;
            var report = telemetry.Build(final: true);
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await SendTelemetryAsync(report, budget.Token);
                    return;
                }
                catch (Exception ex) when (attempt == 1 && !budget.IsCancellationRequested && ex is not ServerException { Status: < HttpStatusCode.InternalServerError })
                {
                    await Task.Delay(200, budget.Token);
                }
            }
        }
        catch (Exception ex)
        {
            TelemetryFailed(ex);
        }
    }

    /// <summary>报一份。服务器不收（老版本服务器、这次会话已经不收了）时这次远程里不再报；网络、服务器出错照常抛。</summary>
    private async Task SendTelemetryAsync(AgentSessionTelemetry report, CancellationToken ct)
    {
        using var api = new ServerApi(_server);
        if (await api.PutSessionTelemetryAsync(Id, report, ct)) return;
        _telemetryStopped = true;
        AgentLog.Info("服务器不收这次远程的遥测（服务器是老版本，或者这次会话已经不收了），这次远程里不再报");
    }

    /// <summary>报遥测失败：每次远程只记一条日志（服务器连不上时每分钟一条没有意思），不影响会话。</summary>
    private void TelemetryFailed(Exception ex)
    {
        if (Interlocked.Exchange(ref _telemetryFailureLogged, 1) != 0) return;
        AgentLog.Warn($"上报这次远程的遥测失败（这次远程里再失败不再记）：{(ex is OperationCanceledException ? "超时" : ex.Message)}");
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        StopRealtime(null); // 最后一份遥测要包含刚才取消的接通尝试。
        // 遥测的最后一份：换个线程报（这里可能是界面线程），和下面关连接、等线程同时进行，自己最多 3 秒
        var telemetry = _telemetry is not null && Interlocked.Exchange(ref _telemetryFinalStarted, 1) == 0
            ? Task.Run(ReportFinalAsync) : Task.CompletedTask;
        _input.ReleaseAll();
        if (_connection?.Socket is { State: WebSocketState.Open } socket)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", timeout.Token);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException) { }
        }
        if (_main is not null) await Task.WhenAny(_main, Task.Delay(3000));
        _video?.Join(TimeSpan.FromSeconds(3));
        _audio?.Join(TimeSpan.FromSeconds(3));
        // 它自己有 3 秒的期限、不抛异常；多半在上面等线程的时候就报完了
        await telemetry;
        Finish("stopped");
    }

    [DllImport("winmm.dll")] private static extern int timeBeginPeriod(int milliseconds);
    [DllImport("winmm.dll")] private static extern int timeEndPeriod(int milliseconds);
}

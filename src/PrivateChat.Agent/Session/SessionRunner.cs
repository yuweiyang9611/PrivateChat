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

namespace PrivateChat.Agent.Session;

/// <summary>
/// 一次远程会话的数据面：连 /ws/remote/{id}/agent，控制端在线时采集 → 编码 → 发送，收到的键鼠注入系统。
/// - 码率跟着网络走（<see cref="NetworkEstimator"/>）：控制端解码后回 FrameAck、对探测包回 Pong，据此估出线路能传多快，
///   发出去的数据按这个速度记账，账上欠多了就先不采——网络慢时少发几帧，延迟不会越积越高。
/// - 每一帧用什么质量由 <see cref="QualityPlanner"/> 定：画面大变时先糊一点马上送到，停下来再一步步补清晰。
/// - 静止画面不编码（按 64×64 的块比较）。
/// - 声音：控制端要求时（SetAudio）采这台电脑正在播放的声音，Opus 编码后和画面走同一条连接。
/// - 断线（网络切换）30 秒内自动重连；服务端以 4001 关闭表示会话结束。
/// - 快速线路：控制端要求时（SetTransport）把画面和声音改经 Cloudflare Realtime 发（<see cref="RealtimePublisher"/>），
///   不再绕回家里的服务器；键鼠、剪贴板这些仍走上面那条连接。接不通、中途断了、控制端收不到，都自动回到中继。
/// </summary>
public sealed class SessionRunner : IAsyncDisposable
{
    /// <summary>画质档位。MaxKbps 是码率上限（实际用多少看网络和画面），Ceiling 是静止后补到的质量。</summary>
    /// <summary>LegacyFps：控制端没说过它会在跟不上时告诉这边（还没更新的旧网页）时用的帧率，就是以前的帧率。</summary>
    private readonly record struct Preset(int MaxWidth, int MaxHeight, int Fps, int LegacyFps, int MaxKbps, int Ceiling);

    /// <summary>
    /// 流畅：最大 1280×800、最高 30 fps、上限 2.5 Mbps、质量 70（QP 24）；清晰：最大 1920×1200、最高 30 fps、上限 6 Mbps、质量 85（QP 20）。
    /// 30 帧是上限：画面不动时不发，变化大时码率预算不够也会少发；控制端跟不上时它会请这边少发（MaxFps）。
    /// 控制端得先说一声它会这么做（连上后发一个 MaxFps），才按 30 帧发；没说过的（还没更新的旧网页）照旧按 20 / 15 帧：
    /// 旧网页在慢设备上跟不上 30 帧又说不出来，中继线路会把它的确认来得晚当成网络堵，码率越压越低。
    /// </summary>
    private static readonly Preset[] Presets = [new(1280, 800, 30, 20, 2500, 70), new(1920, 1200, 30, 15, 6000, 85)];
    private const int MinKbps = 100;
    private const int StartKbps = 600;
    /// <summary>
    /// 流畅档在网络很慢时自动降到这个尺寸：这个编码器把质量压到最低，1280×800 的整屏变化一帧也要几十 KB，
    /// 300 kbps 的线路上就是一两秒一帧；像素少一半，同样的码率帧率高一倍。码率连续 5 秒低于 400 kbps 时降，连续 10 秒高于 900 kbps 时恢复。
    /// 清晰档不降（选它的人要的就是分辨率）。
    /// </summary>
    private static readonly (int Width, int Height) WeakSize = (960, 600);
    private const int WeakBelowKbps = 400;
    private const int WeakAboveKbps = 900;
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

    private readonly AgentSessionRequest _request;
    private readonly ServerInfo _server;
    private readonly HttpMessageInvoker? _invoker;
    private readonly Func<IFrameSource> _sourceFactory;
    private readonly Func<IAudioSource> _audioFactory;
    private readonly InputController _input;
    private readonly IAgentUi _ui;
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource<string> _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly NetworkEstimator _net = new(MinKbps, Presets[0].MaxKbps, StartKbps);
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
    private volatile int _quality;
    private volatile ScreenInfo? _current;
    private int _framesSent;
    private long _bytesSent;
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
    private bool _lineStarting;
    /// <summary>每次要接、要停都加一：进行到一半的那次据此知道自己作废了。</summary>
    private int _lineAttempt;
    /// <summary>第几次接通（发给控制端，它据此知道要重新拉流）。</summary>
    private int _lineGeneration;
    private long _lastLineKeyAt = -10_000;
    private string _lastLinePath = "";

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
        Func<IFrameSource> sourceFactory, Func<IAudioSource> audioFactory, InputController input, IAgentUi ui)
    {
        _request = request;
        _server = server;
        _invoker = AgentTransport.CreateInvoker(new Uri(server.Url));
        _sourceFactory = sourceFactory;
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
        _clipboardSeq = Desktop.ClipboardSequence;
        _main = Task.Run(RunAsync);
        _video = new Thread(VideoLoop) { IsBackground = true, Name = "remote-video" };
        _video.Start();
        _audio = new Thread(AudioLoop) { IsBackground = true, Name = "remote-audio" };
        _audio.Start();
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
                        Finish("连不上服务器");
                        return;
                    }
                    AgentLog.Warn($"远程数据通道连接失败，{delay} ms 后重试：{ex.Message}");
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
            if (frame is not (null or ControlFrame.FrameAck or ControlFrame.Pong or ControlFrame.Clipboard) && !_clipboardWrite.IsCompleted)
                await Task.WhenAny(_clipboardWrite, Task.Delay(1000, ct));
            Handle(frame);
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
        _input.ReleaseAll();
        // 快速线路是上一个控制端要的：先回到中继，新来的想用会再说
        StopRealtime(null);
        if (connected)
        {
            _announce = true;
            _keyRequested = true;
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

    private void Handle(ControlFrame? frame)
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
                if (VerboseLine) AgentLog.Info("快速线路：控制端经控制通道要关键帧");
                return;
            case ControlFrame.SetQuality q:
                _quality = (int)q.Preset;
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
                    if (slowed is not null) AgentLog.Info($"快速线路降码率：{slowed}");
                    if (VerboseLine) AgentLog.Info($"快速线路汇报：丢包 {r.LossPermille / 10.0:0.#}%，收 {r.ReceivedKbps} kbps，缓冲 {r.JitterBufferMs} ms，{r.Fps} fps，往返 {r.RttMs} ms；发 {reported.Rate.SentKbps} kbps，目标 {reported.Rate.TargetKbps} kbps，重发 {reported.Publisher.Resent} 包");
                }
                return;
        }

        // 以下都是输入：只看模式下不执行（中继已经丢掉一遍，这里再防一道）
        if (_request.Mode != RemoteSessionMode.Control) return;
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
                _clipboardWrite = WriteClipboardAsync(c.Value);
                break;
            case ControlFrame.ReleaseAll:
                _input.ReleaseAll();
                break;
        }
    }

    private async Task WriteClipboardAsync(string text)
    {
        try
        {
            _clipboardText = text;
            await _ui.WriteClipboardAsync(text);
            _clipboardSeq = Desktop.ClipboardSequence; // 自己写的不再同步回去
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
        int attempt;
        lock (_lineGate)
        {
            if (_line is not null)
            {
                // 已经接通了（控制端可能没收到上一条通知）：再说一遍
                Send(RemoteFrames.Transport(RemoteTransportState.Realtime, _lineGeneration));
                return;
            }
            if (_lineStarting) return;
            if (!_realtimeOffered)
            {
                Send(RemoteFrames.Transport(RemoteTransportState.RealtimeFailed, _lineGeneration, "服务器没有开快速线路"));
                return;
            }
            _lineStarting = true;
            attempt = ++_lineAttempt;
        }
        AgentLog.Info("控制端要走快速线路，开始接通");
        _ = Task.Run(() => OpenRealtimeAsync(attempt));
    }

    private async Task OpenRealtimeAsync(int attempt)
    {
        RealtimePublisher? publisher = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            using var api = new ServerApi(_server);
            var info = await api.GetRealtimeAsync(Id, timeout.Token);
            if (info is not { Available: true }) throw new InvalidOperationException("服务器没有开快速线路");

            if (VerboseLine) LibraryLog.EnableIfAsked();
            publisher = new RealtimePublisher(info.IceServers);
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
            var offer = await publisher.CreateOfferAsync();
            // 收集候选要一会儿。这期间这次接通作废了的话（控制端改了主意、换了连接，可能已经在接下一次）就不要再去登记：
            // 服务器上一次会话只留一份登记，晚到的这份会把后来那次已经接通的线路顶掉
            lock (_lineGate)
                if (attempt != _lineAttempt) return;
            var answer = await api.PublishRealtimeAsync(Id, new RemoteRealtimePublishRequest(offer, publisher.Tracks), timeout.Token);
            publisher.SetAnswer(answer.Answer);
            await publisher.WaitConnectedAsync(LineConnectTimeout, timeout.Token);

            // 码率从中继那边估到的起步（至少是起步码率），之后按这条线路自己的情况调
            var line = new Line(publisher, new RealtimeRateController(MinKbps, _net.MaxKbps, Math.Max(StartKbps, _net.TargetKbps)), _clock.ElapsedMilliseconds);
            int generation;
            lock (_lineGate)
            {
                if (attempt != _lineAttempt || !_peer)
                {
                    // 等的这会儿控制端走了、或者改主意了：这条连接不要了（finally 里收掉）
                    if (attempt == _lineAttempt) _lineStarting = false;
                    return;
                }
                publisher.KeyFrameRequested += OnLineKeyRequest;
                publisher.ReceptionReported += r =>
                {
                    if (r.Video) line.Rate.OnUpstreamLoss(r.FractionLost, _clock.ElapsedMilliseconds);
                };
                publisher.StateChanged += state => OnLineState(line, state);
                _lineStarting = false;
                generation = _lineGeneration = (_lineGeneration + 1) & 0xFF;
                _line = line;
            }
            publisher = null; // 归 _line 管了
            Send(RemoteFrames.Transport(RemoteTransportState.Realtime, generation));
            AgentLog.Info("快速线路已接通（经 Cloudflare Realtime），等控制端接上");
            // 登记事件之前连接可能已经断了
            if (line.Publisher.State is RealtimeLinkState.Failed or RealtimeLinkState.Closed) FailRealtime(line, "连接断开");
        }
        catch (Exception ex)
        {
            bool mine;
            lock (_lineGate)
            {
                mine = attempt == _lineAttempt;
                if (mine) _lineStarting = false;
            }
            if (!mine || _cts.IsCancellationRequested) return;
            var reason = ex is OperationCanceledException ? "超时" : ex.Message;
            if (reason.Length > 120) reason = reason[..120];
            AgentLog.Warn($"快速线路没接通，画面继续走中继：{reason}");
            Send(RemoteFrames.Transport(RemoteTransportState.RealtimeFailed, _lineGeneration, reason));
        }
        finally
        {
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

    /// <summary>
    /// 不走快速线路了，画面和声音回到中继（采集线程发现线路变了会从关键帧开始）。
    /// only 不为空时只在当前线路就是它时才停；state 不为空时告诉控制端。
    /// </summary>
    private void StopRealtime(RemoteTransportState? state, string reason = "", Line? only = null)
    {
        Line? line;
        int generation;
        lock (_lineGate)
        {
            if (only is not null && !ReferenceEquals(_line, only)) return;
            _lineAttempt++; // 正在接的那次作废
            _lineStarting = false;
            line = _line;
            _line = null;
            generation = _lineGeneration;
        }
        // 关连接要一点时间，而且可能就是在它自己的回调里走到这儿的：换个线程关
        if (line is not null) _ = Task.Run(line.Publisher.Dispose);
        if (state is { } s) Send(RemoteFrames.Transport(s, generation, reason));
    }

    private void FailRealtime(Line line, string reason)
    {
        if (!ReferenceEquals(_line, line)) return;
        AgentLog.Warn($"快速线路不能用了，画面改回中继：{reason}");
        StopRealtime(RemoteTransportState.RealtimeFailed, reason, line);
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
                    Send(RemoteFrames.Stats(new StatsFrame((int)Math.Round((frames - framesBefore) / seconds), (int)((bytes - bytesBefore) * 8 / 1000 / seconds), cpuPercent, rtt, target, net.MaxKbps)));
                    // 码率明显变了记一笔：事后看日志能知道当时网络怎么样
                    if (lastTarget == 0 || Math.Abs(target - lastTarget) > lastTarget / 3)
                    {
                        AgentLog.Info($"网络：目标码率 {target} kbps（上限 {net.MaxKbps}），线路往返 {net.MinRttMs} ms{(line is not null ? "（快速线路）" : "")}");
                        lastTarget = target;
                    }
                }
                (cpuBefore, wallBefore, framesBefore, bytesBefore) = (cpu, wall, frames, bytes);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>这台电脑上复制了新文字 → 发给控制端。会话开始前剪贴板里已有的内容不发（可能是密码）。</summary>
    private async Task SyncClipboardAsync()
    {
        var seq = Desktop.ClipboardSequence;
        if (seq == _clipboardSeq) return;
        _clipboardSeq = seq;
        try
        {
            var text = await _ui.ReadClipboardAsync();
            if (string.IsNullOrEmpty(text) || text == _clipboardText) return;
            _clipboardText = text;
            Send(RemoteFrames.Clipboard(text));
        }
        catch (Exception ex)
        {
            AgentLog.Warn($"读剪贴板失败：{ex.Message}");
        }
    }

    // ---- 采集 + 编码（独立线程：GDI 和编码器都是同步调用）----

    private void VideoLoop()
    {
        IFrameSource? source = null;
        H264Encoder? encoder = null;
        QualityPlanner? planner = null;
        var previous = Array.Empty<byte>();
        var hasPrevious = false;
        var changed = Array.Empty<bool>();
        var nv12 = Array.Empty<byte>();
        var fallbackRefine = 0;
        // 下一帧最早什么时候采：按小数毫秒排（30 fps 是 33.3 ms 一帧，取整成 33 ms 实际会到 30.3 fps，超过上限）
        double nextDue = 0;
        long lastDesktopCheck = -10_000;
        long lastChangeAt = -10_000;
        long lastSentAt = -10_000;
        uint lastTimestamp = 0;
        var weak = false;
        long? slowSince = null;
        long? fastSince = null;
        var secure = false;
        Line? lastLine = null;
        IRateController? lastNet = null;
        var lastQuality = 0;
        var nv12Valid = false;
        IReadOnlyList<ScreenInfo> announced = [];
        // 系统计时器默认 15.6 ms 一跳，Sleep(几毫秒) 会睡到下一跳：每帧都晚到下一跳，30 fps 实际只有二十出头。会话期间调到 1 ms
        var fineTimer = timeBeginPeriod(1) == 0;
        try
        {
            source = _sourceFactory();
            var realScreen = source is GdiScreenSource;
            while (!_cts.IsCancellationRequested)
            {
                if (!_peer || _connection is null)
                {
                    encoder?.Dispose();
                    encoder = null;
                    hasPrevious = false;
                    // 控制端重新连上来时重新检查一遍锁屏状态并告诉它（不然它只看到"正在连接"）
                    secure = false;
                    lastDesktopCheck = -10_000;
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

                var preset = Presets[Math.Clamp(_quality, 0, Presets.Length - 1)];
                // 控制端说它跟不上时按它说的帧率发；它没说过会说（旧网页）就按以前的帧率发。
                // 编码器按档位的帧率建，不跟着变：按质量编码时那只是名义上的帧率（极少数只能按固定码率编的电脑上，
                // 发得比名义帧率少时每帧还是按名义帧率分码率，实际码率会低一些）
                var fps = _clientMaxFps switch { < 0 => preset.LegacyFps, 0 => preset.Fps, var cap => Math.Min(cap, preset.Fps) };
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

                // 锁屏 / UAC 提示：截到的是黑屏，告诉控制端原因
                if (realScreen && now - lastDesktopCheck >= 1000)
                {
                    lastDesktopCheck = now;
                    var s = !Desktop.IsInputDesktopNormal();
                    if (s != secure)
                    {
                        secure = s;
                        Send(RemoteFrames.Status(s ? RemoteStatusCode.SecureDesktop : RemoteStatusCode.Ok, s ? "对方电脑在锁屏或系统安全提示界面" : ""));
                        if (!s) _keyRequested = true;
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
                                encoder.RequestKeyFrame();
                            }
                            var again = Math.Max((uint)now, lastTimestamp + 1);
                            if (encoder.Encode(nv12, TimeSpan.FromMilliseconds(again)) is { } same)
                            {
                                lastTimestamp = again;
                                held.Publisher.SendVideo(again, same.Data);
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
                    // 码率改由另一条线路的那一套来定：之前那条线路"一直很慢"的结论不作数了，分辨率从头判断
                    lastNet = net;
                    weak = false;
                    slowSince = fastSince = null;
                }

                net.Tick(now);
                if (net.Stalled(now))
                {
                    // 最早那帧十秒都没确认：控制端多半丢了它，从关键帧重新开始
                    net.ForgetInFlight();
                    _keyRequested = true;
                }
                // 码率预算用完了、或者前面的帧还堵在路上：先不采，等一等（延迟不累积）
                if (!net.CanSend(now, frameMs)) continue;

                // 网络持续很慢：流畅档降一档分辨率（见 WeakSize）
                var target = net.TargetKbps;
                slowSince = target < WeakBelowKbps ? slowSince ?? now : null;
                fastSince = target > WeakAboveKbps ? fastSince ?? now : null;
                if (!weak && slowSince is { } slow && now - slow >= 5000) weak = true;
                else if (weak && fastSince is { } fast && now - fast >= 10_000) weak = false;
                var shrink = weak && _quality == (int)RemoteQualityPreset.Smooth;

                var screens = source.Screens();
                var screen = screens.FirstOrDefault(s => s.Index == _monitor) ?? screens.FirstOrDefault(s => s.IsPrimary) ?? screens[0];
                var (width, height) = FrameGeometry.Fit(screen.Bounds.Width, screen.Bounds.Height,
                    shrink ? WeakSize.Width : preset.MaxWidth, shrink ? WeakSize.Height : preset.MaxHeight);
                if (encoder is null || planner is null || _reconfigure || _announce || encoder.Width != width || encoder.Height != height || encoder.Fps != preset.Fps || screen != _current)
                {
                    _reconfigure = false;
                    encoder?.Dispose();
                    encoder = new H264Encoder(width, height, preset.Fps, preset.MaxKbps, quality: 30);
                    planner = new QualityPlanner(width, height, preset.Ceiling);
                    changed = new bool[FrameTiles.Count(width, height)];
                    _net.SetMax(preset.MaxKbps);
                    line?.Rate.SetMax(preset.MaxKbps);
                    net.ForgetInFlight();
                    _current = screen;
                    hasPrevious = false;
                    nv12Valid = false;
                    _resendConfig = false;
                    Send(RemoteFrames.VideoConfig(new VideoConfigFrame(RemoteVideoCodec.H264, width, height, preset.Fps, screen.Index, Features())));
                    if (_announce || !screens.SequenceEqual(announced))
                    {
                        Send(RemoteFrames.Monitors([.. screens.Select(s => new MonitorInfo(s.Index, s.Bounds.Width, s.Bounds.Height, s.IsPrimary))]));
                        announced = screens;
                    }
                    _announce = false;
                    _keyRequested = true;
                    AgentLog.Info($"编码 {width}x{height} {preset.Fps} fps，码率上限 {preset.MaxKbps} kbps，显示器 {screen.Index}" +
                        (shrink ? "（网络慢，降了分辨率）" : "") + (encoder.QualityMode ? "" : "（这台电脑的编码器不支持按质量，用固定码率）"));
                }
                else if (_resendConfig)
                {
                    // 问到服务器有快速线路时第一份配置已经发出去了（问得太慢，没等到）：补发一次，控制端才知道有这条线路
                    _resendConfig = false;
                    AgentLog.Info("补发一次画面配置：服务器有快速线路");
                    Send(RemoteFrames.VideoConfig(new VideoConfigFrame(RemoteVideoCodec.H264, width, height, preset.Fps, screen.Index, Features())));
                }

                var frame = source.Capture(screen, width, height);
                var length = frame.Stride * frame.Height;
                var changedTiles = FrameTiles.Diff(hasPrevious ? previous.AsSpan(0, length) : default, frame.Bgra.AsSpan(0, length), width, height, frame.Stride, changed);
                var key = _keyRequested;
                var sustained = now - lastChangeAt < 300;
                var refining = false;
                var quality = 0;
                // 快速线路上屏幕不动也要隔一阵发一帧（内容没变，编出来很小），不然 Cloudflare 会把这条轨道回收掉
                var keepAlive = line is not null && now - lastSentAt >= LineKeepAliveMs;
                var learn = true;
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
                    encoder.SetBitrate(Math.Clamp(net.TargetKbps, MinKbps, preset.MaxKbps));
                }
                if (changedTiles > 0)
                {
                    lastChangeAt = now;
                    if (previous.Length < length) previous = new byte[length];
                    frame.Bgra.AsSpan(0, length).CopyTo(previous);
                    hasPrevious = true;
                }

                var size = width * height * 3 / 2;
                if (nv12.Length < size) nv12 = new byte[size];
                Nv12.FromBgra(frame.Bgra, frame.Stride, width, height, nv12);
                nv12Valid = true;
                if (key)
                {
                    _keyRequested = false;
                    encoder.RequestKeyFrame();
                }
                encoder.SetQuality(quality);
                lastQuality = quality;
                // 时间戳也是这一帧的编号：控制端按它确认，必须一帧比一帧大
                var ts = Math.Max((uint)_clock.ElapsedMilliseconds, lastTimestamp + 1);
                if (encoder.Encode(nv12, TimeSpan.FromMilliseconds(ts)) is not { } encoded) continue;
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
            Send(RemoteFrames.Status(RemoteStatusCode.CaptureFailed, ex.Message));
            Thread.Sleep(300); // 让这条状态先发出去
            Finish("采集失败");
        }
        finally
        {
            if (fineTimer) timeEndPeriod(1);
            encoder?.Dispose();
            source?.Dispose();
        }
    }

    private RemoteFeatures Features()
        => RemoteFeatures.Audio | RemoteFeatures.AdaptiveBitrate | (_realtimeOffered ? RemoteFeatures.Realtime : RemoteFeatures.None);

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
                    if (++failures == 1) AgentLog.Warn($"声音采集出错，稍后重试：{ex.Message}");
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
        }
        finally
        {
            source?.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _input.ReleaseAll();
        StopRealtime(null);
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
        Finish("stopped");
    }

    [DllImport("winmm.dll")] private static extern int timeBeginPeriod(int milliseconds);
    [DllImport("winmm.dll")] private static extern int timeEndPeriod(int milliseconds);
}

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;
using PrivateChat.Agent.Audio;
using PrivateChat.Agent.Input;
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
/// </summary>
public sealed class SessionRunner : IAsyncDisposable
{
    /// <summary>画质档位。MaxKbps 是码率上限（实际用多少看网络和画面），Ceiling 是静止后补到的质量。</summary>
    private readonly record struct Preset(int MaxWidth, int MaxHeight, int Fps, int MaxKbps, int Ceiling);

    /// <summary>流畅：最大 1280×800、20 fps、上限 2.5 Mbps、质量 70（QP 24）；清晰：最大 1920×1200、15 fps、上限 6 Mbps、质量 85（QP 20）。</summary>
    private static readonly Preset[] Presets = [new(1280, 800, 20, 2500, 70), new(1920, 1200, 15, 6000, 85)];
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
    /// <summary>编码器不接受"按质量"时的老办法：变化停止后再补这么多帧，让固定码率的编码器把画质补上来。</summary>
    private const int FallbackRefineFrames = 8;
    private static readonly TimeSpan ReconnectWindow = TimeSpan.FromSeconds(30);

    private readonly AgentSessionRequest _request;
    private ServerInfo _server;
    /// <summary>走局域网直连时的退路（公网）：直连连不上就改走它，这次会话剩下的都走公网。</summary>
    private ServerInfo? _fallback;
    private HttpMessageInvoker? _invoker;
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

    private sealed class Connection(ClientWebSocket socket)
    {
        public ClientWebSocket Socket { get; } = socket;
        public Channel<byte[]> Outgoing { get; } = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    }

    public SessionRunner(AgentSessionRequest request, ServerInfo server, ServerInfo? fallback,
        Func<IFrameSource> sourceFactory, Func<IAudioSource> audioFactory, InputController input, IAgentUi ui)
    {
        _request = request;
        _server = server;
        _fallback = fallback;
        _invoker = AgentTransport.CreateInvoker(new Uri(server.Url), server.Pin);
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
                    if (_fallback is { } fallback)
                    {
                        // 局域网直连连不上（比如笔记本刚离开家）：马上改走公网，不占重连的时间
                        AgentLog.Warn($"远程数据通道经局域网直连连不上，改走公网：{ex.Message}");
                        _invoker?.Dispose();
                        _server = fallback;
                        _fallback = null;
                        _invoker = AgentTransport.CreateInvoker(new Uri(fallback.Url), fallback.Pin);
                        continue;
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
                AgentLog.Info($"远程数据通道已连接：{Id}{(_server.IsDirect ? "（局域网直连）" : "")}");
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
        _input.ReleaseAll();
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
                return;
            case ControlFrame.SetQuality q:
                _quality = (int)q.Preset;
                _reconfigure = true;
                return;
            case ControlFrame.SelectMonitor m:
                _monitor = m.Index;
                _reconfigure = true;
                return;
            case ControlFrame.SetAudio a:
                if (_audioOn != a.On) AgentLog.Info(a.On ? "控制端要听这台电脑的声音，开始采集" : "控制端关了声音");
                _audioOn = a.On;
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
                }
                if (++tick % 2 != 0) continue;

                process.Refresh();
                var cpu = process.TotalProcessorTime;
                var wall = _clock.Elapsed;
                var frames = Volatile.Read(ref _framesSent);
                var bytes = Interlocked.Read(ref _bytesSent);
                var seconds = Math.Max(0.001, (wall - wallBefore).TotalSeconds);
                var cpuPercent = (int)((cpu - cpuBefore).TotalSeconds / seconds / Environment.ProcessorCount * 100);
                var target = _net.TargetKbps;
                if (_peer)
                {
                    // 往返时间：探测包量到的线路延迟；老版本网页不回探测包，就用最近一帧画面从发出到确认的时间
                    var rtt = _lineRttMs > 0 ? _lineRttMs : _net.LastRttMs;
                    Send(RemoteFrames.Stats(new StatsFrame((int)Math.Round((frames - framesBefore) / seconds), (int)((bytes - bytesBefore) * 8 / 1000 / seconds), cpuPercent, rtt, target, _net.MaxKbps)));
                    // 码率明显变了记一笔：事后看日志能知道当时网络怎么样
                    if (lastTarget == 0 || Math.Abs(target - lastTarget) > lastTarget / 3)
                    {
                        AgentLog.Info($"网络：目标码率 {target} kbps（上限 {_net.MaxKbps}），线路往返 {_net.MinRttMs} ms");
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
        long nextDue = 0;
        long lastDesktopCheck = -10_000;
        long lastChangeAt = -10_000;
        long lastSentAt = -10_000;
        uint lastTimestamp = 0;
        var weak = false;
        long? slowSince = null;
        long? fastSince = null;
        var secure = false;
        IReadOnlyList<ScreenInfo> announced = [];
        // 系统计时器默认 15.6 ms 一跳，Sleep(几毫秒) 会睡到下一跳：20 fps 实际只有 16。会话期间调到 1 ms
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

                var preset = Presets[Math.Clamp(_quality, 0, Presets.Length - 1)];
                var frameMs = 1000 / preset.Fps;
                var now = _clock.ElapsedMilliseconds;
                if (now < nextDue)
                {
                    Thread.Sleep((int)Math.Min(nextDue - now, 20));
                    continue;
                }
                nextDue = Math.Max(nextDue + frameMs, now);

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
                if (secure) continue;

                _net.Tick(now);
                if (_net.Stalled(now))
                {
                    // 最早那帧十秒都没确认：控制端多半丢了它，从关键帧重新开始
                    _net.ForgetInFlight();
                    _keyRequested = true;
                }
                // 码率预算用完了、或者前面的帧还堵在路上：先不采，等一等（延迟不累积）
                if (!_net.CanSend(now, frameMs)) continue;

                // 网络持续很慢：流畅档降一档分辨率（见 WeakSize）
                var target = _net.TargetKbps;
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
                    _net.ForgetInFlight();
                    _current = screen;
                    hasPrevious = false;
                    Send(RemoteFrames.VideoConfig(new VideoConfigFrame(RemoteVideoCodec.H264, width, height, preset.Fps, screen.Index, RemoteFeatures.Audio | RemoteFeatures.AdaptiveBitrate)));
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

                var frame = source.Capture(screen, width, height);
                var length = frame.Stride * frame.Height;
                var changedTiles = FrameTiles.Diff(hasPrevious ? previous.AsSpan(0, length) : default, frame.Bgra.AsSpan(0, length), width, height, frame.Stride, changed);
                var key = _keyRequested;
                var sustained = now - lastChangeAt < 300;
                var refining = false;
                var quality = 0;
                if (encoder.QualityMode)
                {
                    if (key || changedTiles > 0)
                    {
                        quality = planner.Plan(changed, changedTiles, key, sustained, _net.ChangeBudget(now, sustained && !key, frameMs), _net.Credit(now));
                    }
                    else
                    {
                        // 画面停了：等前面的帧都确认了（线路是空的）再补清晰，一步一步来
                        if (now - lastChangeAt < 150 || _net.InFlight > 0) continue;
                        if (planner.Refine(_net.Credit(now), force: _net.Idle(now) && now - lastSentAt >= 1000) is not { } to) continue;
                        quality = to;
                        refining = true;
                    }
                }
                else
                {
                    if (changedTiles == 0 && fallbackRefine <= 0 && !key) continue;
                    fallbackRefine = changedTiles > 0 ? FallbackRefineFrames : fallbackRefine - 1;
                    encoder.SetBitrate(Math.Clamp(_net.TargetKbps, MinKbps, preset.MaxKbps));
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
                if (key)
                {
                    _keyRequested = false;
                    encoder.RequestKeyFrame();
                }
                encoder.SetQuality(quality);
                // 时间戳也是这一帧的编号：控制端按它确认，必须一帧比一帧大
                var ts = Math.Max((uint)_clock.ElapsedMilliseconds, lastTimestamp + 1);
                if (encoder.Encode(nv12, TimeSpan.FromMilliseconds(ts)) is not { } encoded) continue;
                lastTimestamp = ts;
                var payload = RemoteFrames.VideoFrame(ts, encoded.IsKey, encoded.Data);
                if (encoder.QualityMode)
                {
                    if (refining) planner.LearnRefine(quality, payload.Length);
                    else planner.Learn(quality, changed, changedTiles, key || encoded.IsKey, sustained, payload.Length);
                }
                if (!Send(payload))
                {
                    // 发送队列满了（网络发不动）：这一帧丢了，后面的 P 帧解不出来，下一帧从关键帧开始
                    _keyRequested = true;
                    continue;
                }
                _net.OnSent(ts, payload.Length, now);
                lastSentAt = now;
                Interlocked.Increment(ref _framesSent);
                Interlocked.Add(ref _bytesSent, payload.Length);
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

    // ---- 声音（独立线程：采集是同步调用，10 ms 取一次）----

    private void AudioLoop()
    {
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
                        // 网络慢的时候声音也省一点
                        opus.Bitrate = _net.TargetKbps >= 800 ? 64000 : 32000;
                        var written = opus.Encode(frame, packet);
                        var payload = RemoteFrames.Audio((uint)timestamp, RemoteAudioCodec.Opus, AudioFormat.Channels, packet.AsSpan(0, written));
                        if (Send(payload))
                        {
                            _net.OnSentUntracked(payload.Length, now);
                            Interlocked.Add(ref _bytesSent, payload.Length);
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

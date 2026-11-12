using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using PrivateChat.Agent.Input;
using PrivateChat.Agent.Ui;
using PrivateChat.Agent.Video;
using PrivateChat.Contracts.Remote;

namespace PrivateChat.Agent.Session;

/// <summary>
/// 一次远程会话的数据面：连 /ws/remote/{id}/agent，控制端在线时采集 → 编码 → 发送，收到的键鼠注入系统。
/// - 流控：每帧带时间戳，控制端解码后回 FrameAck；未确认的超过 4 帧就不再采集，延迟不会越积越高。
/// - 静止画面不编码（逐字节比较）；变化停止后再补几帧，让 CBR 编码器把画质补上来。
/// - 断线（网络切换）30 秒内自动重连；服务端以 4001 关闭表示会话结束。
/// </summary>
public sealed class SessionRunner : IAsyncDisposable
{
    /// <summary>流畅：最大 1280×800、20 fps、1.5 Mbps；清晰：最大 1920×1200、15 fps、4 Mbps。</summary>
    private static readonly (int MaxWidth, int MaxHeight, int Fps, int Kbps)[] Presets = [(1280, 800, 20, 1500), (1920, 1200, 15, 4000)];
    private const int MaxInFlight = 4;
    private const int RefineFrames = 8;
    private static readonly TimeSpan ReconnectWindow = TimeSpan.FromSeconds(30);
    private static readonly long AckTimeoutTicks = Stopwatch.Frequency * 3;

    private readonly AgentSessionRequest _request;
    private readonly ServerInfo _server;
    private readonly Func<IFrameSource> _sourceFactory;
    private readonly InputController _input;
    private readonly IAgentUi _ui;
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource<string> _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<uint, long> _inFlight = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private Thread? _video;
    private Task? _main;

    private volatile Connection? _connection;
    private volatile bool _peer;
    private volatile bool _announce;
    private volatile bool _keyRequested;
    private volatile bool _reconfigure;
    private volatile int _monitor = -1;
    private volatile int _quality;
    private volatile ScreenInfo? _current;
    private int _framesSent;
    private long _bytesSent;
    private volatile int _rttMs;
    private uint _clipboardSeq;
    private string? _clipboardText;
    /// <summary>正在写的剪贴板：之后的键鼠要等它写完（Ctrl+V 紧跟在剪贴板帧后面，不等就会粘出旧内容）。</summary>
    private Task _clipboardWrite = Task.CompletedTask;

    private sealed class Connection(ClientWebSocket socket)
    {
        public ClientWebSocket Socket { get; } = socket;
        public Channel<byte[]> Outgoing { get; } = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    }

    public SessionRunner(AgentSessionRequest request, ServerInfo server, Func<IFrameSource> sourceFactory, InputController input, IAgentUi ui)
    {
        _request = request;
        _server = server;
        _sourceFactory = sourceFactory;
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
                    await socket.ConnectAsync(_server.WebSocketUri($"/ws/remote/{Id}/agent"), ct);
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
            if (frame is not (null or ControlFrame.FrameAck or ControlFrame.Clipboard) && !_clipboardWrite.IsCompleted)
                await Task.WhenAny(_clipboardWrite, Task.Delay(1000, ct));
            Handle(frame);
        }
    }

    private void SetPeer(bool connected)
    {
        _inFlight.Clear();
        if (connected)
        {
            _announce = true;
            _keyRequested = true;
        }
        else
        {
            _input.ReleaseAll();
        }
        _peer = connected;
        AgentLog.Info(connected ? "控制端已连上，开始发画面" : "控制端不在，暂停采集");
    }

    // ---- 控制端发来的帧 ----

    private void Handle(ControlFrame? frame)
    {
        switch (frame)
        {
            case null:
                return;
            case ControlFrame.FrameAck ack:
                if (_inFlight.TryRemove(ack.TimestampMs, out var sentAt))
                    _rttMs = (int)((_clock.ElapsedTicks - sentAt) * 1000 / Stopwatch.Frequency);
                foreach (var ts in _inFlight.Keys)
                    if (ts < ack.TimestampMs) _inFlight.TryRemove(ts, out _);
                return;
            case ControlFrame.RequestKeyFrame:
                // 控制端丢掉了解码器里的帧，不会再确认它们：在途窗口清空，马上发关键帧
                _inFlight.Clear();
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

    // ---- 统计、剪贴板 ----

    private async Task StatsLoopAsync(CancellationToken ct)
    {
        var process = Process.GetCurrentProcess();
        var cpuBefore = process.TotalProcessorTime;
        var wallBefore = _clock.Elapsed;
        var framesBefore = Volatile.Read(ref _framesSent);
        var bytesBefore = Interlocked.Read(ref _bytesSent);
        var tick = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(500, ct);
                if (_peer && _request.Mode == RemoteSessionMode.Control) await SyncClipboardAsync();
                if (++tick % 2 != 0) continue;

                process.Refresh();
                var cpu = process.TotalProcessorTime;
                var wall = _clock.Elapsed;
                var frames = Volatile.Read(ref _framesSent);
                var bytes = Interlocked.Read(ref _bytesSent);
                var seconds = Math.Max(0.001, (wall - wallBefore).TotalSeconds);
                var cpuPercent = (int)((cpu - cpuBefore).TotalSeconds / seconds / Environment.ProcessorCount * 100);
                if (_peer)
                    Send(RemoteFrames.Stats(new StatsFrame((int)Math.Round((frames - framesBefore) / seconds), (int)((bytes - bytesBefore) * 8 / 1000 / seconds), cpuPercent, _rttMs)));
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
        byte[]? previous = null;
        var nv12 = Array.Empty<byte>();
        var refine = 0;
        long nextDue = 0;
        long lastDesktopCheck = -10_000;
        var secure = false;
        IReadOnlyList<ScreenInfo> announced = [];
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
                    previous = null;
                    // 控制端重新连上来时重新检查一遍锁屏状态并告诉它（不然它只看到"正在连接"）
                    secure = false;
                    lastDesktopCheck = -10_000;
                    Thread.Sleep(50);
                    continue;
                }

                var (maxWidth, maxHeight, fps, kbps) = Presets[Math.Clamp(_quality, 0, Presets.Length - 1)];
                var now = _clock.ElapsedMilliseconds;
                if (now < nextDue)
                {
                    Thread.Sleep((int)Math.Min(nextDue - now, 20));
                    continue;
                }
                nextDue = Math.Max(nextDue + 1000 / fps, now);

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

                // 流控：未确认的帧太多就先不采；确认丢了（超过 3 秒）就从关键帧重新开始
                if (_inFlight.Count >= MaxInFlight && !_keyRequested)
                {
                    var oldest = _inFlight.Values.DefaultIfEmpty(long.MaxValue).Min();
                    if (_clock.ElapsedTicks - oldest < AckTimeoutTicks) continue;
                    _inFlight.Clear();
                    _keyRequested = true;
                }

                var screens = source.Screens();
                var screen = screens.FirstOrDefault(s => s.Index == _monitor) ?? screens.FirstOrDefault(s => s.IsPrimary) ?? screens[0];
                var (width, height) = FrameGeometry.Fit(screen.Bounds.Width, screen.Bounds.Height, maxWidth, maxHeight);
                if (encoder is null || _reconfigure || _announce || encoder.Width != width || encoder.Height != height || encoder.Fps != fps || screen != _current)
                {
                    _reconfigure = false;
                    encoder?.Dispose();
                    encoder = new H264Encoder(width, height, fps, kbps);
                    _current = screen;
                    previous = null;
                    _inFlight.Clear();
                    Send(RemoteFrames.VideoConfig(new VideoConfigFrame(RemoteVideoCodec.H264, width, height, fps, screen.Index)));
                    if (_announce || !screens.SequenceEqual(announced))
                    {
                        Send(RemoteFrames.Monitors([.. screens.Select(s => new MonitorInfo(s.Index, s.Bounds.Width, s.Bounds.Height, s.IsPrimary))]));
                        announced = screens;
                    }
                    _announce = false;
                    AgentLog.Info($"编码 {width}x{height} {fps} fps {kbps} kbps，显示器 {screen.Index}");
                }

                var frame = source.Capture(screen, width, height);
                var length = frame.Stride * frame.Height;
                var changed = previous is null || !frame.Bgra.AsSpan(0, length).SequenceEqual(previous.AsSpan(0, length));
                var key = _keyRequested;
                if (!changed && refine <= 0 && !key) continue;
                refine = changed ? RefineFrames : refine - 1;
                if (changed)
                {
                    if (previous is null || previous.Length < length) previous = new byte[length];
                    frame.Bgra.AsSpan(0, length).CopyTo(previous);
                }

                var size = width * height * 3 / 2;
                if (nv12.Length < size) nv12 = new byte[size];
                Nv12.FromBgra(frame.Bgra, frame.Stride, width, height, nv12);
                if (key)
                {
                    _keyRequested = false;
                    encoder.RequestKeyFrame();
                }
                var ts = (uint)_clock.ElapsedMilliseconds;
                if (encoder.Encode(nv12, TimeSpan.FromMilliseconds(ts)) is not { } encoded) continue;
                var payload = RemoteFrames.VideoFrame(ts, encoded.IsKey, encoded.Data);
                if (!Send(payload))
                {
                    // 发送队列满了（网络发不动）：这一帧丢了，后面的 P 帧解不出来，下一帧从关键帧开始
                    _keyRequested = true;
                    continue;
                }
                _inFlight[ts] = _clock.ElapsedTicks;
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
            encoder?.Dispose();
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
        Finish("stopped");
    }
}

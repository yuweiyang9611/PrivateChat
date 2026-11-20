using PrivateChat.Contracts.Remote;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace PrivateChat.Agent.Realtime;

/// <summary>对面对我们发的一路流的接收情况（RTCP 接收报告里的一项）。</summary>
public readonly record struct RealtimeReception(bool Video, double FractionLost, int PacketsLost, uint Jitter);

public enum RealtimeLinkState { Connecting, Connected, Disconnected, Failed, Closed }

/// <summary>
/// 快速线路的发送端：一条 WebRTC 连接，把已经编好的 H.264 画面和 Opus 声音发给 Cloudflare Realtime（SFU），只发不收。
/// 信令不在这里：调用方拿 <see cref="CreateOfferAsync"/> 的 offer 去换 answer（经我们自己的服务器），再交给 <see cref="SetAnswer"/>。
/// Cloudflare 不支持 trickle，所以 offer 等 ICE 候选收集完才给出来。
/// 丢包：对面报丢了哪些包（NACK），库会把留着的原样再发一遍；它已经不留的，从这里自己留的那份里补发。
/// </summary>
public sealed class RealtimePublisher : IDisposable
{
    private const int VideoPayloadType = 96;
    private const int AudioPayloadType = 111;
    private const int VideoClock = 90000;
    /// <summary>自己留着准备补发的包数（2 的幂）。20 fps、每帧十来个包时大约是最近 5 秒。</summary>
    private const int ResendSlots = 1024;
    /// <summary>发出去超过这么久的包不再补发：对面早就改要关键帧了。</summary>
    private const long ResendWindowMs = 2000;

    private readonly record struct SentPacket(ushort Sequence, byte[] Data, uint Timestamp, bool Marker, long SentAt);

    private readonly RTCPeerConnection _pc;
    private readonly VideoStream _video;
    private readonly TaskCompletionSource _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _send = new();
    private readonly SentPacket[] _sent = new SentPacket[ResendSlots];
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    // 序号自己编：补发时要用原来的序号
    private ushort _videoSequence = (ushort)Random.Shared.Next(1, 0x7FFF);
    private ushort _audioSequence = (ushort)Random.Shared.Next(1, 0x7FFF);
    private long _resentHere;
    private volatile bool _up;
    private int _disposed;

    /// <summary>对面（SFU 替订阅端转达）要一个关键帧：PLI 或 FIR。</summary>
    public event Action? KeyFrameRequested;
    /// <summary>SFU 的接收报告：我们发过去的某一路，最近一段丢了多大比例、累计丢了多少个包。</summary>
    public event Action<RealtimeReception>? ReceptionReported;
    public event Action<RealtimeLinkState>? StateChanged;

    public RealtimePublisher(IReadOnlyList<RemoteIceServer> iceServers)
    {
        var config = new RTCConfiguration
        {
            iceServers = [.. iceServers.SelectMany(s => s.Urls.Select(url => new RTCIceServer { urls = url, username = s.Username, credential = s.Credential }))],
            bundlePolicy = RTCBundlePolicy.max_bundle,
            // 要用 SAVPF 这个 profile，对面才会发 NACK、PLI 这类反馈
            X_UseRtpFeedbackProfile = true,
        };
        _pc = new RTCPeerConnection(config);
        // 画面是 Main profile；level 只是名义上的，接收端按 level-asymmetry-allowed 收更大的画面
        _pc.addTrack(new MediaStreamTrack(
            new VideoFormat(VideoCodecsEnum.H264, VideoPayloadType, VideoClock, "level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=4d001f"),
            MediaStreamStatusEnum.SendOnly));
        _pc.addTrack(new MediaStreamTrack(
            new SIPSorceryMedia.Abstractions.AudioFormat(AudioCodecsEnum.OPUS, AudioPayloadType, Audio.AudioFormat.SampleRate, Audio.AudioFormat.Channels, "minptime=10;useinbandfec=1;stereo=1;sprop-stereo=1"),
            MediaStreamStatusEnum.SendOnly));
        _video = _pc.VideoStream;

        _pc.onconnectionstatechange += OnState;
        _pc.OnReceiveReport += OnReport;
        _video.OnNackReceived += OnNack;
    }

    public bool Connected => _up;

    private volatile int _state = (int)RealtimeLinkState.Connecting;
    public RealtimeLinkState State => (RealtimeLinkState)_state;

    /// <summary>应对面的要求重发过多少个包（库重发的加上这里补发的）。</summary>
    public long Resent => _video.RetransmissionMetrics.PacketsRetransmitted + Interlocked.Read(ref _resentHere);

    /// <summary>到 SFU 的往返时间（取自 RTCP），还没量到时是 0。</summary>
    public int RoundTripMs => (int)(_video.RtcpSession?.RoundTripTime.TotalMilliseconds ?? 0);

    /// <summary>offer 里的两条轨道（给服务器登记用）。<see cref="CreateOfferAsync"/> 之后才有。</summary>
    public IReadOnlyList<RemoteRealtimeTrack> Tracks { get; private set; } = [];

    public async Task<string> CreateOfferAsync()
    {
        // 等候选收集完会阻塞（没有 STUN / TURN 时很快，有的话等它们回话或超时）
        var offer = await Task.Run(() => _pc.createOffer(new RTCOfferOptions { X_WaitForIceGatheringToComplete = true }));
        await _pc.setLocalDescription(offer);
        Tracks = ParseTracks(offer.sdp);
        return offer.sdp;
    }

    public void SetAnswer(string sdp)
    {
        var result = _pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = sdp });
        if (result != SetDescriptionResultEnum.OK) throw new InvalidOperationException($"对面的 answer 用不了：{result}");
    }

    /// <summary>等连接建好（ICE 通了、DTLS 握完手）。超时或连接失败抛异常。</summary>
    public async Task WaitConnectedAsync(TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            await _connected.Task.WaitAsync(timeout, ct);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"{timeout.TotalSeconds:0} 秒没连上（ICE {_pc.iceConnectionState}，连接 {_pc.connectionState}）");
        }
    }

    /// <summary>发一帧画面（Annex B）。时间戳是会话时钟的毫秒数。</summary>
    public void SendVideo(uint timestampMs, ReadOnlySpan<byte> annexB)
    {
        if (!_up) return;
        var packets = H264Rtp.Packetize(annexB);
        var timestamp = unchecked(timestampMs * (VideoClock / 1000));
        var now = _clock.ElapsedMilliseconds;
        try
        {
            lock (_send)
                foreach (var packet in packets)
                {
                    var sequence = _videoSequence++;
                    _sent[sequence % ResendSlots] = new SentPacket(sequence, packet.Data, timestamp, packet.Marker, now);
                    if (DropForTest?.Invoke(sequence) == true) continue;
                    _pc.SendRtpRaw(SDPMediaTypesEnum.video, packet.Data, timestamp, packet.Marker ? 1 : 0, VideoPayloadType, sequence);
                }
        }
        catch (Exception ex)
        {
            SendFailed(ex);
        }
    }

    /// <summary>
    /// 往连接上发东西出了错：多半是连接正在关（另一个线程在收这条线路），也可能是库里的问题。
    /// 不往外抛：调用的是采集线程、声音线程、库自己的线程，抛出去轻则这次远程结束、声音没了，重则整个程序退出。
    /// 当作线路断了，画面回中继。
    /// </summary>
    private void SendFailed(Exception ex)
    {
        if (!_up) return; // 已经断了 / 正在关：不用再报
        _up = false;
        if (Volatile.Read(ref _disposed) != 0) return;
        AgentLog.Warn($"快速线路发送出错：{ex.GetType().Name} {ex.Message}");
        Changed(RealtimeLinkState.Failed);
    }

    /// <summary>测试用：返回 true 的包第一次不发出去（模拟丢包，看重发管不管用）。</summary>
    public Func<ushort, bool>? DropForTest { get; set; }

    /// <summary>发一个 Opus 包（20 ms）。时间戳和画面是同一个时钟的毫秒数。</summary>
    public void SendAudio(uint timestampMs, byte[] opus)
    {
        if (!_up) return;
        var timestamp = unchecked(timestampMs * (uint)(Audio.AudioFormat.SampleRate / 1000));
        try
        {
            lock (_send) _pc.SendRtpRaw(SDPMediaTypesEnum.audio, opus, timestamp, 0, AudioPayloadType, _audioSequence++);
        }
        catch (Exception ex)
        {
            SendFailed(ex);
        }
    }

    /// <summary>对面报丢的包里，库已经不留的那些（notKept）：这里还留着、也不太旧的就按原样再发一次。</summary>
    private void OnNack(IReadOnlyList<ushort> lost, IReadOnlyList<ushort> notKept)
    {
        if (notKept.Count == 0 || !_up) return;
        var now = _clock.ElapsedMilliseconds;
        try
        {
            lock (_send)
                foreach (var sequence in notKept)
                {
                    var packet = _sent[sequence % ResendSlots];
                    if (packet.Data is null || packet.Sequence != sequence || now - packet.SentAt > ResendWindowMs) continue;
                    _pc.SendRtpRaw(SDPMediaTypesEnum.video, packet.Data, packet.Timestamp, packet.Marker ? 1 : 0, VideoPayloadType, sequence);
                    Interlocked.Increment(ref _resentHere);
                }
        }
        catch (Exception ex)
        {
            // 这里是库的线程：异常漏出去会让整个程序退出
            SendFailed(ex);
        }
    }

    private void OnState(RTCPeerConnectionState state)
    {
        switch (state)
        {
            case RTCPeerConnectionState.connected:
                _up = true;
                _connected.TrySetResult();
                Changed(RealtimeLinkState.Connected);
                break;
            case RTCPeerConnectionState.disconnected:
                Changed(RealtimeLinkState.Disconnected);
                break;
            case RTCPeerConnectionState.failed:
                _up = false;
                _connected.TrySetException(new InvalidOperationException("连接没建起来（ICE 或 DTLS 失败）"));
                Changed(RealtimeLinkState.Failed);
                break;
            case RTCPeerConnectionState.closed:
                _up = false;
                _connected.TrySetException(new InvalidOperationException("连接已关闭"));
                Changed(RealtimeLinkState.Closed);
                break;
        }
    }

    private void Changed(RealtimeLinkState state)
    {
        _state = (int)state;
        StateChanged?.Invoke(state);
    }

    private void OnReport(System.Net.IPEndPoint remote, SDPMediaTypesEnum media, RTCPCompoundPacket report)
    {
        // 一个 RTCP 包里可以有好几条反馈；要关键帧的只可能是冲着唯一的那条画面轨道来的，不用看它标的是哪条流
        if (report.FeedbackMessages.Any(f => f.Header.PacketType == RTCPReportTypesEnum.PSFB
                && f.Header.PayloadFeedbackMessageType is PSFBFeedbackTypesEnum.PLI or PSFBFeedbackTypesEnum.FIR))
            KeyFrameRequested?.Invoke();

        var video = _video.LocalTrack?.Ssrc;
        foreach (var sample in report.ReceiverReport?.ReceptionReports ?? report.SenderReport?.ReceptionReports ?? [])
            ReceptionReported?.Invoke(new RealtimeReception(sample.SSRC == video, sample.FractionLost / 256.0, sample.PacketsLost, sample.Jitter));
    }

    /// <summary>从 SDP 里按顺序取出每个 m= 段的种类和 mid。</summary>
    internal static List<RemoteRealtimeTrack> ParseTracks(string sdp)
    {
        var tracks = new List<RemoteRealtimeTrack>();
        string? kind = null;
        foreach (var raw in sdp.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("m=", StringComparison.Ordinal)) kind = line[2..].Split(' ')[0];
            else if (kind is "video" or "audio" && line.StartsWith("a=mid:", StringComparison.Ordinal))
            {
                tracks.Add(new RemoteRealtimeTrack(line[6..], kind));
                kind = null;
            }
        }
        return tracks;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _up = false;
        _pc.onconnectionstatechange -= OnState;
        _pc.OnReceiveReport -= OnReport;
        _video.OnNackReceived -= OnNack;
        try { _pc.Close("disposed"); } catch (Exception) { /* 关的时候出错不要紧 */ }
        _pc.Dispose();
    }
}

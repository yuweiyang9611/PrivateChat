using System.Buffers.Binary;
using System.Text;

namespace PrivateChat.Contracts.Remote;

/// <summary>
/// 远程控制数据面帧类型（docs/DESIGN.md 附录 C）。
/// WebSocket 二进制消息，一条消息一帧：第 1 字节为类型，其余为载荷，数值小端。
/// 前端的同一份定义在 web/src/features/remote/protocol.ts。
/// </summary>
public enum RemoteFrameType : byte
{
    // Agent → 控制端
    VideoConfig = 0x01,
    VideoFrame = 0x02,
    Cursor = 0x03,      // 预留：v1 指针直接画进画面
    Monitors = 0x04,
    Clipboard = 0x05,   // 双向
    Stats = 0x06,
    Status = 0x07,
    Audio = 0x08,       // 对方电脑正在播放的声音
    Ping = 0x09,        // 量线路往返时间的探测包，控制端原样回 Pong
    Transport = 0x0A,   // 画面和声音现在走哪条线路（见 RemoteTransportState）

    // 控制端 → Agent
    MouseMove = 0x10,
    MouseButton = 0x11,
    Wheel = 0x12,
    Key = 0x13,
    SelectMonitor = 0x14,
    SetQuality = 0x15,
    RequestKeyFrame = 0x16,
    SpecialKey = 0x17,
    Text = 0x18,
    FrameAck = 0x19,
    ReleaseAll = 0x1A,
    SetAudio = 0x1B,
    Pong = 0x1C,
    SetTransport = 0x1D,    // 让画面和声音改走哪条线路
    ReceiverReport = 0x1E,  // 快速线路下，控制端每秒报一次自己收得怎么样
    MaxFps = 0x1F,          // 控制端解码 / 画画跟不上：请被控端每秒最多发几帧（0 = 不限）。老版本被控端不认识，直接忽略
    SetVideo = 0x20,        // 画质菜单：档位 + 分辨率 / 帧率 / 码率上限（被控端 0.8.6 起；收到过它之后不再理 SetQuality）
}

/// <summary>
/// 画面和声音走哪条线路。Relay：经我们自己的服务器中继（WebSocket，一直有）；
/// Realtime：经 Cloudflare Realtime（WebRTC，不回家里的服务器）。键鼠、剪贴板这些控制帧始终走中继。
/// </summary>
public enum RemoteTransport : byte { Relay = 0, Realtime = 1 }

/// <summary>
/// 被控端报告的线路状态。Realtime：已经发布好，控制端可以去拉流了（每发布一次 Generation 加一）；
/// RealtimeFailed：快速线路没起来或中途断了，画面已经改回中继（说明里是原因）。
/// </summary>
public enum RemoteTransportState : byte { Relay = 0, Realtime = 1, RealtimeFailed = 2 }

public enum RemoteVideoCodec : byte { H264 = 1 }

/// <summary>声音的编码：Opus，48 kHz，一帧 20 ms（浏览器用 WebCodecs 的 AudioDecoder 解）。</summary>
public enum RemoteAudioCodec : byte { Opus = 1 }

/// <summary>被控端支持的可选功能（VideoConfig 的最后一个字节；老版本没有这个字节，按 0 算）。</summary>
[Flags]
public enum RemoteFeatures : byte
{
    None = 0,
    /// <summary>能传电脑上播放的声音（控制端发 SetAudio 打开）。</summary>
    Audio = 1,
    /// <summary>码率跟着网络自动调（Stats 里带目标码率；会发 Ping 量线路延迟）。</summary>
    AdaptiveBitrate = 2,
    /// <summary>画面和声音可以改走 Cloudflare Realtime（控制端发 SetTransport 切换）。服务器没配 Realtime 时不带这一位。</summary>
    Realtime = 4,
    /// <summary>认得 SetVideo（画质菜单：自动 / 流畅 / 清晰 / 原画 / 自定义）。0.8.6 起总是带着；没有这一位的老被控端只认 SetQuality。</summary>
    VideoSettings = 8,
}

/// <summary>老的两档画质（SetQuality）。被控端 0.8.6 起换成 SetVideo，这个只留给老版本：流畅 → 流畅档，清晰 → 清晰档。</summary>
public enum RemoteQualityPreset : byte { Smooth = 0, Sharp = 1 }

/// <summary>画质菜单的档位（SetVideo）。只有自动会随网速和电脑性能改分辨率；其余的分辨率固定，码率仍在上限以下随网络调。</summary>
public enum RemoteVideoMode : byte { Auto = 0, Smooth = 1, Clear = 2, Original = 3, Custom = 4 }

/// <summary>Stats 的最后一个字节（被控端 0.8.6 起）。</summary>
[Flags]
public enum RemoteStatsFlags : byte
{
    None = 0,
    /// <summary>自动档因为网络慢降了分辨率。</summary>
    LoweredForNetwork = 1,
    /// <summary>自动档因为电脑忙不过来（采集加编码赶不上帧率）降了分辨率。</summary>
    LoweredForCpu = 2,
    /// <summary>这一秒里编了至少 5 帧，平均每帧采集加编码用掉了一帧时间的八成五以上：这台电脑是瓶颈。</summary>
    CpuBound = 4,
}

public enum RemoteSpecialKey : byte { CtrlAltDel = 1, Win = 2, PrintScreen = 3, TaskManager = 4, AltTab = 5 }

/// <summary>Agent 告诉控制端的状态：画面暂停的原因等。</summary>
public enum RemoteStatusCode : byte { Ok = 0, SecureDesktop = 1, CaptureFailed = 2, Unsupported = 3 }

public enum RemoteMouseButton : byte { Left = 0, Middle = 1, Right = 2, Back = 3, Forward = 4 }

public enum RemoteSessionMode { View, Control }

/// <summary>
/// 这次远程是怎么开始的：Prompt 电脑前的人点了同意；Unattended 输了设备的无人值守 PIN；
/// Trusted 控制的人在这台电脑的免同意名单里。Trusted 只存在于服务端和网页：发给 Agent 时按 Unattended 说
/// （已经装出去的 Agent 不认识新值，而它只关心要不要弹窗）。
/// </summary>
public enum RemoteConsentType { Prompt, Unattended, Trusted }

public enum RemoteSessionState { Pending, Active, Ended }

public sealed record VideoConfigFrame(RemoteVideoCodec Codec, int Width, int Height, int Fps, int Monitor, RemoteFeatures Features = RemoteFeatures.None);
public sealed record MonitorInfo(int Index, int Width, int Height, bool IsPrimary);
/// <summary>
/// TargetKbps：按网络状况定的码率上限；MaxKbps：当前画质档位的上限。两者差得多说明网络慢、已自动降画质。
/// CaptureMs10 / EncodeMs10：这一秒里平均每编一帧，采集（含比较变化）/ 编码（含转 NV12）用了多久，单位 0.1 ms（0.8.6 起）。
/// </summary>
public sealed record StatsFrame(int Fps, int Kbps, int CpuPercent, int RttMs, int TargetKbps = 0, int MaxKbps = 0,
    int CaptureMs10 = 0, int EncodeMs10 = 0, RemoteStatsFlags Flags = RemoteStatsFlags.None);

/// <summary>控制端发来的一帧（解析后）。</summary>
public abstract record ControlFrame
{
    public sealed record MouseMove(float X, float Y) : ControlFrame;
    public sealed record MouseButton(RemoteMouseButton Button, bool Down) : ControlFrame;
    public sealed record Wheel(short Dx, short Dy) : ControlFrame;
    public sealed record Key(ushort ScanCode, bool Down) : ControlFrame;
    public sealed record SelectMonitor(int Index) : ControlFrame;
    public sealed record SetQuality(RemoteQualityPreset Preset) : ControlFrame;
    public sealed record RequestKeyFrame : ControlFrame;
    public sealed record SpecialKey(RemoteSpecialKey Id) : ControlFrame;
    public sealed record Text(string Value) : ControlFrame;
    public sealed record Clipboard(string Value) : ControlFrame;
    public sealed record FrameAck(uint TimestampMs) : ControlFrame;
    public sealed record ReleaseAll : ControlFrame;
    public sealed record SetAudio(bool On) : ControlFrame;
    public sealed record Pong(uint Sequence) : ControlFrame;
    public sealed record SetTransport(RemoteTransport Transport) : ControlFrame;
    /// <summary>
    /// 快速线路下控制端的接收情况：最近一秒丢包的千分比、收到的码率、浏览器里画面缓冲了多久、解出的帧率、
    /// 浏览器到 SFU 的往返时间。被控端据此调码率（SFU 只管转发，不会告诉我们订阅端的网络怎么样）。
    /// </summary>
    public sealed record ReceiverReport(int LossPermille, int ReceivedKbps, int JitterBufferMs, int Fps, int RttMs) : ControlFrame;
    /// <summary>
    /// 控制端自己解码、画画跟不上：请被控端每秒最多发这么多帧（0 = 不限）。
    /// 中继线路靠"画出来才确认"控制发多快，控制端慢的时候确认会越来越晚，看起来和网络堵了一样；
    /// 由它直接说一声，被控端就少发几帧，而不是把码率越压越低。
    /// </summary>
    public sealed record MaxFps(int Fps) : ControlFrame;
    /// <summary>
    /// 画质菜单：档位、最大宽高（都是 0 = 原始分辨率）、帧率（0 = 30）、码率上限 kbps（0 = 这一档默认的）。
    /// 数是网页定的，被控端只夹到能接受的范围（见 Agent 的 VideoSettings.From）。
    /// </summary>
    public sealed record SetVideo(RemoteVideoMode Mode, int MaxWidth, int MaxHeight, int Fps, int MaxKbps) : ControlFrame;
}

public static class RemoteFrames
{
    /// <summary>剪贴板、文字输入的上限（UTF-8 字节）。</summary>
    public const int MaxTextBytes = 1 << 20;

    /// <summary>控制端发来的、会改变被控电脑状态的帧：只看模式下中继和 Agent 都丢弃。</summary>
    public static bool IsInput(RemoteFrameType type) => type is RemoteFrameType.MouseMove or RemoteFrameType.MouseButton
        or RemoteFrameType.Wheel or RemoteFrameType.Key or RemoteFrameType.SpecialKey or RemoteFrameType.Text
        or RemoteFrameType.Clipboard or RemoteFrameType.ReleaseAll;

    public static byte[] VideoConfig(VideoConfigFrame f)
    {
        var b = new byte[9];
        b[0] = (byte)RemoteFrameType.VideoConfig;
        b[1] = (byte)f.Codec;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), (ushort)f.Width);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), (ushort)f.Height);
        b[6] = (byte)f.Fps;
        b[7] = (byte)f.Monitor;
        b[8] = (byte)f.Features;
        return b;
    }

    public static byte[] VideoFrame(uint timestampMs, bool isKey, ReadOnlySpan<byte> annexB)
    {
        var b = new byte[6 + annexB.Length];
        b[0] = (byte)RemoteFrameType.VideoFrame;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(1), timestampMs);
        b[5] = isKey ? (byte)1 : (byte)0;
        annexB.CopyTo(b.AsSpan(6));
        return b;
    }

    public static byte[] Monitors(IReadOnlyList<MonitorInfo> monitors)
    {
        var b = new byte[2 + monitors.Count * 6];
        b[0] = (byte)RemoteFrameType.Monitors;
        b[1] = (byte)monitors.Count;
        for (var i = 0; i < monitors.Count; i++)
        {
            var m = monitors[i];
            var s = b.AsSpan(2 + i * 6);
            s[0] = (byte)m.Index;
            BinaryPrimitives.WriteUInt16LittleEndian(s[1..], (ushort)m.Width);
            BinaryPrimitives.WriteUInt16LittleEndian(s[3..], (ushort)m.Height);
            s[5] = m.IsPrimary ? (byte)1 : (byte)0;
        }
        return b;
    }

    /// <summary>16 字节：老网页只读前 11 个（到 maxKbps），后面的采集 / 编码耗时和标志是 0.8.6 加的。</summary>
    public static byte[] Stats(StatsFrame f)
    {
        var b = new byte[16];
        b[0] = (byte)RemoteFrameType.Stats;
        b[1] = (byte)Math.Clamp(f.Fps, 0, 255);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), (ushort)Math.Clamp(f.Kbps, 0, ushort.MaxValue));
        b[4] = (byte)Math.Clamp(f.CpuPercent, 0, 100);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(5), (ushort)Math.Clamp(f.RttMs, 0, ushort.MaxValue));
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(7), (ushort)Math.Clamp(f.TargetKbps, 0, ushort.MaxValue));
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(9), (ushort)Math.Clamp(f.MaxKbps, 0, ushort.MaxValue));
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(11), (ushort)Math.Clamp(f.CaptureMs10, 0, ushort.MaxValue));
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(13), (ushort)Math.Clamp(f.EncodeMs10, 0, ushort.MaxValue));
        b[15] = (byte)f.Flags;
        return b;
    }

    /// <summary>探测包：不带画面，控制端收到马上回 Pong，量到的就是线路本身的往返时间。</summary>
    public static byte[] Ping(uint sequence)
    {
        var b = new byte[5];
        b[0] = (byte)RemoteFrameType.Ping;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(1), sequence);
        return b;
    }

    /// <summary>一个声音包：时间戳（毫秒，和视频帧同一个时钟）、编码、声道数、编码后的数据。</summary>
    public static byte[] Audio(uint timestampMs, RemoteAudioCodec codec, int channels, ReadOnlySpan<byte> packet)
    {
        var b = new byte[7 + packet.Length];
        b[0] = (byte)RemoteFrameType.Audio;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(1), timestampMs);
        b[5] = (byte)codec;
        b[6] = (byte)channels;
        packet.CopyTo(b.AsSpan(7));
        return b;
    }

    public static byte[] Status(RemoteStatusCode code, string message = "") => WithText(RemoteFrameType.Status, message, (byte)code);

    /// <summary>线路状态：状态、第几次发布（控制端据此知道要不要重新拉流）、说明。</summary>
    public static byte[] Transport(RemoteTransportState state, int generation, string detail = "")
    {
        var text = Encoding.UTF8.GetBytes(detail);
        var b = new byte[3 + text.Length];
        b[0] = (byte)RemoteFrameType.Transport;
        b[1] = (byte)state;
        b[2] = (byte)generation;
        text.CopyTo(b.AsSpan(3));
        return b;
    }

    public static byte[] Clipboard(string text) => WithText(RemoteFrameType.Clipboard, text);

    /// <summary>类型字节 + 可选前缀字节 + UTF-8 文本（超过上限时截断在字符边界上）。</summary>
    private static byte[] WithText(RemoteFrameType type, string text, byte? prefix = null)
    {
        var head = prefix is null ? 1 : 2;
        var bytes = Encoding.UTF8.GetBytes(text);
        var length = bytes.Length;
        if (length > MaxTextBytes)
        {
            length = MaxTextBytes;
            while (length > 0 && (bytes[length] & 0xC0) == 0x80) length--; // 不切断多字节字符
        }
        var b = new byte[head + length];
        b[0] = (byte)type;
        if (prefix is { } p) b[1] = p;
        bytes.AsSpan(0, length).CopyTo(b.AsSpan(head));
        return b;
    }

    /// <summary>解析控制端发来的一帧；格式不对或不是控制端该发的类型返回 null（Agent 直接忽略）。</summary>
    public static ControlFrame? ParseControl(ReadOnlySpan<byte> b)
    {
        if (b.Length == 0) return null;
        var p = b[1..];
        return (RemoteFrameType)b[0] switch
        {
            RemoteFrameType.MouseMove when p.Length >= 8 => new ControlFrame.MouseMove(
                BinaryPrimitives.ReadSingleLittleEndian(p), BinaryPrimitives.ReadSingleLittleEndian(p[4..])),
            RemoteFrameType.MouseButton when p.Length >= 2 && p[0] <= 4 => new ControlFrame.MouseButton((RemoteMouseButton)p[0], p[1] != 0),
            RemoteFrameType.Wheel when p.Length >= 4 => new ControlFrame.Wheel(
                BinaryPrimitives.ReadInt16LittleEndian(p), BinaryPrimitives.ReadInt16LittleEndian(p[2..])),
            RemoteFrameType.Key when p.Length >= 3 => new ControlFrame.Key(BinaryPrimitives.ReadUInt16LittleEndian(p), p[2] != 0),
            RemoteFrameType.SelectMonitor when p.Length >= 1 => new ControlFrame.SelectMonitor(p[0]),
            RemoteFrameType.SetQuality when p.Length >= 1 && p[0] <= 1 => new ControlFrame.SetQuality((RemoteQualityPreset)p[0]),
            RemoteFrameType.RequestKeyFrame => new ControlFrame.RequestKeyFrame(),
            RemoteFrameType.SpecialKey when p.Length >= 1 && Enum.IsDefined((RemoteSpecialKey)p[0]) => new ControlFrame.SpecialKey((RemoteSpecialKey)p[0]),
            RemoteFrameType.Text when p.Length <= MaxTextBytes => new ControlFrame.Text(Encoding.UTF8.GetString(p)),
            RemoteFrameType.Clipboard when p.Length <= MaxTextBytes => new ControlFrame.Clipboard(Encoding.UTF8.GetString(p)),
            RemoteFrameType.FrameAck when p.Length >= 4 => new ControlFrame.FrameAck(BinaryPrimitives.ReadUInt32LittleEndian(p)),
            RemoteFrameType.ReleaseAll => new ControlFrame.ReleaseAll(),
            RemoteFrameType.SetAudio when p.Length >= 1 => new ControlFrame.SetAudio(p[0] != 0),
            RemoteFrameType.Pong when p.Length >= 4 => new ControlFrame.Pong(BinaryPrimitives.ReadUInt32LittleEndian(p)),
            RemoteFrameType.SetTransport when p.Length >= 1 && p[0] <= 1 => new ControlFrame.SetTransport((RemoteTransport)p[0]),
            RemoteFrameType.MaxFps when p.Length >= 1 => new ControlFrame.MaxFps(p[0]),
            RemoteFrameType.SetVideo when p.Length >= 8 && p[0] <= 4 => new ControlFrame.SetVideo((RemoteVideoMode)p[0],
                BinaryPrimitives.ReadUInt16LittleEndian(p[1..]), BinaryPrimitives.ReadUInt16LittleEndian(p[3..]), p[5],
                BinaryPrimitives.ReadUInt16LittleEndian(p[6..])),
            RemoteFrameType.ReceiverReport when p.Length >= 9 => new ControlFrame.ReceiverReport(
                BinaryPrimitives.ReadUInt16LittleEndian(p), BinaryPrimitives.ReadUInt16LittleEndian(p[2..]),
                BinaryPrimitives.ReadUInt16LittleEndian(p[4..]), p[6], BinaryPrimitives.ReadUInt16LittleEndian(p[7..])),
            _ => null,
        };
    }

    // 下面是控制端方向的编码（测试和 Agent 自检用；浏览器那份在 protocol.ts）
    public static byte[] MouseMove(float x, float y)
    {
        var b = new byte[9];
        b[0] = (byte)RemoteFrameType.MouseMove;
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(1), x);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(5), y);
        return b;
    }

    public static byte[] Key(ushort scanCode, bool down) =>
        [(byte)RemoteFrameType.Key, (byte)scanCode, (byte)(scanCode >> 8), down ? (byte)1 : (byte)0, 0];

    public static byte[] Pong(uint sequence)
    {
        var b = new byte[5];
        b[0] = (byte)RemoteFrameType.Pong;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(1), sequence);
        return b;
    }

    public static byte[] SetAudio(bool on) => [(byte)RemoteFrameType.SetAudio, on ? (byte)1 : (byte)0];

    public static byte[] SetTransport(RemoteTransport transport) => [(byte)RemoteFrameType.SetTransport, (byte)transport];

    public static byte[] ReceiverReport(int lossPermille, int receivedKbps, int jitterBufferMs, int fps, int rttMs)
    {
        var b = new byte[10];
        b[0] = (byte)RemoteFrameType.ReceiverReport;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(1), (ushort)Math.Clamp(lossPermille, 0, 1000));
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(3), (ushort)Math.Clamp(receivedKbps, 0, ushort.MaxValue));
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(5), (ushort)Math.Clamp(jitterBufferMs, 0, ushort.MaxValue));
        b[7] = (byte)Math.Clamp(fps, 0, 255);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(8), (ushort)Math.Clamp(rttMs, 0, ushort.MaxValue));
        return b;
    }

    public static byte[] MaxFps(int fps) => [(byte)RemoteFrameType.MaxFps, (byte)Math.Clamp(fps, 0, 255)];

    public static byte[] SetVideo(RemoteVideoMode mode, int maxWidth, int maxHeight, int fps, int maxKbps)
    {
        var b = new byte[9];
        b[0] = (byte)RemoteFrameType.SetVideo;
        b[1] = (byte)mode;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), (ushort)Math.Clamp(maxWidth, 0, ushort.MaxValue));
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), (ushort)Math.Clamp(maxHeight, 0, ushort.MaxValue));
        b[6] = (byte)Math.Clamp(fps, 0, 255);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(7), (ushort)Math.Clamp(maxKbps, 0, ushort.MaxValue));
        return b;
    }

    public static byte[] FrameAck(uint timestampMs)
    {
        var b = new byte[5];
        b[0] = (byte)RemoteFrameType.FrameAck;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(1), timestampMs);
        return b;
    }
}

/// <summary>Agent 的 SignalR 通道（/hubs/agent）上的方法名与载荷。</summary>
public static class AgentHubEvents
{
    /// <summary>服务端 → Agent：有人请求远程（Active=false 时要弹窗征求同意；无人值守时 Active=true，直接连数据面）。</summary>
    public const string SessionRequested = "SessionRequested";
    /// <summary>服务端 → Agent：会话结束（对方挂断、超时、被吊销……），收起弹窗 / 红条，断开数据面。</summary>
    public const string SessionEnded = "SessionEnded";
    /// <summary>服务端 → Agent：设备令牌已吊销，删掉本地令牌、回到设备码登录。</summary>
    public const string Revoked = "Revoked";
    /// <summary>Agent → 服务端：上报版本、系统、显示器。</summary>
    public const string Report = "Report";
}

/// <summary>ExpiresInSeconds：等同意还剩几秒（相对时间，被控电脑的时钟不准也不影响）。</summary>
public sealed record AgentSessionRequest(Guid SessionId, string ControllerName, RemoteSessionMode Mode, RemoteConsentType Consent, DateTimeOffset ExpiresAt, bool Active, int ExpiresInSeconds);
public sealed record AgentSessionEnded(Guid SessionId, string Reason);
public sealed record AgentScreen(int Width, int Height, bool Primary);
public sealed record AgentReport(string Version, string Os, IReadOnlyList<AgentScreen> Screens);

/// <summary>ICE 服务器，和浏览器的 RTCIceServer 一个形状。</summary>
public sealed record RemoteIceServer(IReadOnlyList<string> Urls, string? Username = null, string? Credential = null);

/// <summary>
/// 快速线路（画面和声音经 Cloudflare Realtime）的信令。被控端和网页都不直接碰 Cloudflare 的接口，SDP 经我们的服务器转手：
/// GET  /api/remote/sessions/{id}/realtime             → <see cref="RemoteRealtimeInfo"/>（两边都能调）
/// POST /api/remote/sessions/{id}/realtime/publish     被控端：<see cref="RemoteRealtimePublishRequest"/> → <see cref="RemoteRealtimePublishResponse"/>
/// POST /api/remote/sessions/{id}/realtime/pull        控制端：→ <see cref="RemoteRealtimePullResponse"/>（SFU 给的 offer）
/// POST /api/remote/sessions/{id}/realtime/renegotiate 控制端：<see cref="RemoteRealtimeAnswerRequest"/>
/// </summary>
public sealed record RemoteRealtimeInfo(bool Available, IReadOnlyList<RemoteIceServer> IceServers);

/// <summary>一条轨道：它在 SDP 里的 mid 和种类（video / audio）。</summary>
public sealed record RemoteRealtimeTrack(string Mid, string Kind);
public sealed record RemoteRealtimePublishRequest(string Offer, IReadOnlyList<RemoteRealtimeTrack> Tracks);
public sealed record RemoteRealtimePublishResponse(string Answer);
public sealed record RemoteRealtimePullResponse(string Offer, IReadOnlyList<RemoteRealtimeTrack> Tracks);
public sealed record RemoteRealtimeAnswerRequest(string Answer);

/// <summary>中继发给两端的文本消息（WebSocket 文本帧，JSON）：对端连上 / 断开。</summary>
public sealed record RelayNotice(string Type, bool Connected);

public static class RelayCloseCodes
{
    /// <summary>同一角色从别处连上来了，这条旧连接被顶掉。</summary>
    public const int Replaced = 4000;
    /// <summary>会话已结束。</summary>
    public const int Ended = 4001;
}

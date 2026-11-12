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
}

public enum RemoteVideoCodec : byte { H264 = 1 }

public enum RemoteQualityPreset : byte { Smooth = 0, Sharp = 1 }

public enum RemoteSpecialKey : byte { CtrlAltDel = 1, Win = 2, PrintScreen = 3, TaskManager = 4, AltTab = 5 }

/// <summary>Agent 告诉控制端的状态：画面暂停的原因等。</summary>
public enum RemoteStatusCode : byte { Ok = 0, SecureDesktop = 1, CaptureFailed = 2, Unsupported = 3 }

public enum RemoteMouseButton : byte { Left = 0, Middle = 1, Right = 2, Back = 3, Forward = 4 }

public enum RemoteSessionMode { View, Control }

public enum RemoteConsentType { Prompt, Unattended }

public enum RemoteSessionState { Pending, Active, Ended }

public sealed record VideoConfigFrame(RemoteVideoCodec Codec, int Width, int Height, int Fps, int Monitor);
public sealed record MonitorInfo(int Index, int Width, int Height, bool IsPrimary);
public sealed record StatsFrame(int Fps, int Kbps, int CpuPercent, int RttMs);

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
        var b = new byte[8];
        b[0] = (byte)RemoteFrameType.VideoConfig;
        b[1] = (byte)f.Codec;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), (ushort)f.Width);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), (ushort)f.Height);
        b[6] = (byte)f.Fps;
        b[7] = (byte)f.Monitor;
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

    public static byte[] Stats(StatsFrame f)
    {
        var b = new byte[7];
        b[0] = (byte)RemoteFrameType.Stats;
        b[1] = (byte)Math.Clamp(f.Fps, 0, 255);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), (ushort)Math.Clamp(f.Kbps, 0, ushort.MaxValue));
        b[4] = (byte)Math.Clamp(f.CpuPercent, 0, 100);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(5), (ushort)Math.Clamp(f.RttMs, 0, ushort.MaxValue));
        return b;
    }

    public static byte[] Status(RemoteStatusCode code, string message = "") => WithText(RemoteFrameType.Status, message, (byte)code);

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

/// <summary>中继发给两端的文本消息（WebSocket 文本帧，JSON）：对端连上 / 断开。</summary>
public sealed record RelayNotice(string Type, bool Connected);

public static class RelayCloseCodes
{
    /// <summary>同一角色从别处连上来了，这条旧连接被顶掉。</summary>
    public const int Replaced = 4000;
    /// <summary>会话已结束。</summary>
    public const int Ended = 4001;
}

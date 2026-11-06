namespace PrivateChat.Contracts.Remote;

/// <summary>
/// 远程控制数据面帧类型（docs/DESIGN.md 附录 C）。
/// 二进制帧：第 1 字节为类型，其余为载荷，数值小端。
/// </summary>
public enum RemoteFrameType : byte
{
    // Agent → 控制端
    VideoConfig = 0x01,
    VideoFrame = 0x02,
    Cursor = 0x03,
    Monitors = 0x04,
    Clipboard = 0x05, // 双向
    Stats = 0x06,

    // 控制端 → Agent
    MouseMove = 0x10,
    MouseButton = 0x11,
    Wheel = 0x12,
    Key = 0x13,
    SelectMonitor = 0x14,
    SetQuality = 0x15,
    RequestKeyFrame = 0x16,
    SpecialKey = 0x17,
}

public enum RemoteVideoCodec : byte { H264 = 1 }

public enum RemoteQualityPreset : byte { Smooth = 0, Sharp = 1 }

public enum RemoteSpecialKey : byte { CtrlAltDel = 1, WinKey = 2, PrintScreen = 3 }

public enum RemoteSessionMode { View, Control }

public enum RemoteConsentType { Prompt, Unattended }

public sealed record VideoConfigFrame(RemoteVideoCodec Codec, ushort Width, ushort Height, byte Fps);
public sealed record VideoFrameHeader(uint TimestampMs, bool IsKeyFrame);
public sealed record CursorFrame(float X, float Y, byte ShapeId);
public sealed record MonitorInfo(byte Index, ushort Width, ushort Height, bool IsPrimary);
public sealed record StatsFrame(byte Fps, ushort Kbps, byte CpuPercent);
public sealed record MouseMoveFrame(float X, float Y);
public sealed record MouseButtonFrame(byte Button, bool Down);
public sealed record WheelFrame(short Dx, short Dy);
public sealed record KeyFrame(ushort ScanCode, bool Down, byte Modifiers);

/// <summary>控制面（SignalR / REST）共享的 DTO。</summary>
public sealed record RemoteDeviceDto(Guid Id, string Name, string Platform, bool Online, DateTimeOffset? LastSeenAt, bool RemoteCapable);
public sealed record RemoteSessionDto(Guid Id, Guid ControllerUserId, Guid TargetDeviceId, RemoteSessionMode Mode, RemoteConsentType Consent, DateTimeOffset StartedAt, DateTimeOffset? EndedAt);

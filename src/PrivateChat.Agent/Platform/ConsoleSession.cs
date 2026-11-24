using System.Runtime.InteropServices;

namespace PrivateChat.Agent.Platform;

/// <summary>
/// 物理控制台会话（坐在电脑前的人用的那个 Windows 会话；不是远程桌面会话）。服务在会话 0 里，要靠这里找到人在哪个会话、锁没锁屏。
/// WTSGetActiveConsoleSessionId 在快速切换用户、注销的一瞬间会是 0xFFFFFFFF（<see cref="None"/>）：当"暂时不知道"，别当成没人。
/// </summary>
internal static class ConsoleSession
{
    public const uint None = 0xFFFFFFFF;

    public enum LockState { Unknown, Locked, Unlocked }

    public static uint ActiveId() => Native.WTSGetActiveConsoleSessionId();

    public static uint CurrentProcessSession() => Native.ProcessIdToSessionId((uint)Environment.ProcessId, out var session) ? session : None;

    /// <summary>
    /// 这个会话锁没锁屏（WTSSessionInfoEx 的 SessionFlags；Windows 10 起 0 = 锁着、1 = 没锁）。
    /// Windows 11 锁屏时输入桌面的名字有时还是 "Default"，要靠这个才看得出来。问不到时 Unknown。
    /// </summary>
    public static LockState Lock(uint sessionId)
    {
        if (!Native.WTSQuerySessionInformationW(IntPtr.Zero, sessionId, Native.WTSSessionInfoEx, out var buffer, out _) || buffer == 0) return LockState.Unknown;
        try
        {
            // WTSINFOEXW：Level（DWORD），8 字节处起 WTSINFOEX_LEVEL1_W：SessionId、SessionState、SessionFlags
            if (Marshal.ReadInt32(buffer) != 1) return LockState.Unknown;
            return Marshal.ReadInt32(buffer, 16) switch
            {
                Native.WTS_SESSIONSTATE_LOCK => LockState.Locked,
                Native.WTS_SESSIONSTATE_UNLOCK => LockState.Unlocked,
                _ => LockState.Unknown,
            };
        }
        finally
        {
            Native.WTSFreeMemory(buffer);
        }
    }

    /// <summary>这个会话里登录的用户（"DOMAIN\user"）；没人登录（登录界面）时 null。</summary>
    public static string? UserName(uint sessionId)
    {
        var user = QueryString(sessionId, Native.WTSUserName);
        if (string.IsNullOrEmpty(user)) return null;
        var domain = QueryString(sessionId, Native.WTSDomainName);
        return string.IsNullOrEmpty(domain) ? user : $@"{domain}\{user}";
    }

    /// <summary>会话的连接状态（WTS_CONNECTSTATE_CLASS：0 Active、1 Connected、4 Disconnected……）；问不到时 -1。</summary>
    public static int ConnectState(uint sessionId)
    {
        if (!Native.WTSQuerySessionInformationW(IntPtr.Zero, sessionId, Native.WTSConnectState, out var buffer, out _) || buffer == 0) return -1;
        try
        {
            return Marshal.ReadInt32(buffer);
        }
        finally
        {
            Native.WTSFreeMemory(buffer);
        }
    }

    private static string? QueryString(uint sessionId, int infoClass)
    {
        if (!Native.WTSQuerySessionInformationW(IntPtr.Zero, sessionId, infoClass, out var buffer, out _) || buffer == 0) return null;
        try
        {
            return Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            Native.WTSFreeMemory(buffer);
        }
    }

    private static class Native
    {
        public const int WTSUserName = 5;
        public const int WTSDomainName = 7;
        public const int WTSConnectState = 8;
        public const int WTSSessionInfoEx = 25;
        public const int WTS_SESSIONSTATE_LOCK = 0;
        public const int WTS_SESSIONSTATE_UNLOCK = 1;

        [DllImport("kernel32.dll")] public static extern uint WTSGetActiveConsoleSessionId();
        [DllImport("kernel32.dll")] public static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);
        [DllImport("wtsapi32.dll", SetLastError = true)] public static extern bool WTSQuerySessionInformationW(IntPtr server, uint sessionId, int infoClass, out IntPtr buffer, out int bytes);
        [DllImport("wtsapi32.dll")] public static extern void WTSFreeMemory(IntPtr memory);
    }
}

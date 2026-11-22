using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using PrivateChat.Agent.Ui;
using PrivateChat.Contracts.Remote;

namespace PrivateChat.Agent.Telemetry;

/// <summary>
/// 这台电脑的系统、处理器、内存、显卡、显示器（遥测的"这台电脑"那一块）。只读注册表和系统信息，不用 WMI。
/// 除 <see cref="Gpus"/> 以外都很便宜，Agent 进程里也直接调（沿用上次探测结果时刷新这些字段、算指纹）；
/// <see cref="Gpus"/> 要问显卡驱动的版本（会把驱动的用户态部分载进进程），只在探测子进程里调。
/// </summary>
internal static class HardwareInfo
{
    /// <summary>
    /// 程序真实的版本号（不管 --pretend-version）：指纹按它算，Agent 和探测子进程算出来才一样。
    /// </summary>
    public static string AgentVersion { get; } =
        typeof(HardwareInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0";

    private const string DisplayClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    public static TelemetryOs Os()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var build = int.TryParse(key?.GetValue("CurrentBuild") as string, out var b) ? b : Environment.OSVersion.Version.Build;
        var product = (key?.GetValue("ProductName") as string)?.Trim() ?? "Windows";
        // Windows 11 的 ProductName 还写着 "Windows 10 ……"：按版本号改过来
        if (build >= 22000 && product.StartsWith("Windows 10", StringComparison.Ordinal)) product = "Windows 11" + product["Windows 10".Length..];
        var display = key?.GetValue("DisplayVersion") as string ?? key?.GetValue("ReleaseId") as string ?? "";
        var revision = key?.GetValue("UBR") is int ubr ? ubr : 0;
        var edition = key?.GetValue("EditionID") as string ?? "";
        return new TelemetryOs(product, display, build, revision, edition, Environment.Is64BitOperatingSystem);
    }

    public static TelemetryCpu Cpu()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        var name = string.Join(' ', (key?.GetValue("ProcessorNameString") as string ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var mhz = key?.GetValue("~MHz") is int m ? m : 0;
        var (cores, threads) = CoresAndThreads();
        return new TelemetryCpu(name, cores, threads, mhz);
    }

    /// <summary>物理核心数、逻辑处理器数（GetLogicalProcessorInformationEx 的每个 RelationProcessorCore 是一个核心，它的掩码里几位就是几个线程）。</summary>
    private static (int Cores, int Threads) CoresAndThreads()
    {
        var length = 0;
        Native.GetLogicalProcessorInformationEx(Native.RelationProcessorCore, IntPtr.Zero, ref length);
        if (length <= 0) return (Environment.ProcessorCount, Environment.ProcessorCount);
        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (!Native.GetLogicalProcessorInformationEx(Native.RelationProcessorCore, buffer, ref length))
                return (Environment.ProcessorCount, Environment.ProcessorCount);
            int cores = 0, threads = 0;
            for (var offset = 0; offset < length;)
            {
                var record = buffer + offset;
                var size = Marshal.ReadInt32(record, 4);
                if (size <= 0) break;
                if (Marshal.ReadInt32(record) == Native.RelationProcessorCore)
                {
                    cores++;
                    // PROCESSOR_RELATIONSHIP 从第 8 字节起：Flags、EfficiencyClass、Reserved[20]、GroupCount（第 30 字节），GroupMask[] 从第 32 字节起，每个 16 字节
                    var groups = Marshal.ReadInt16(record, 30);
                    for (var g = 0; g < groups; g++) threads += BitOperations.PopCount((ulong)Marshal.ReadInt64(record, 32 + g * 16));
                }
                offset += size;
            }
            return cores > 0 ? (cores, Math.Max(threads, cores)) : (Environment.ProcessorCount, Environment.ProcessorCount);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public static long MemoryBytes()
    {
        var status = new Native.MEMORYSTATUSEX { dwLength = Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        return Native.GlobalMemoryStatusEx(ref status) ? (long)status.ullTotalPhys : 0;
    }

    /// <summary>程序跑在远程桌面（RDP）会话里：桌面复制用不了，GDI 截屏也慢。</summary>
    public static bool RemoteDesktopSession() => Native.GetSystemMetrics(Native.SM_REMOTESESSION) != 0;

    /// <summary>锁着屏、或者正在系统安全提示界面（UAC、Ctrl+Alt+Del）：这时探到的截屏结果不准。</summary>
    public static bool ScreenLocked() => SessionLocked() || !Desktop.IsInputDesktopNormal();

    /// <summary>
    /// 这个 Windows 会话锁着屏（WTSSessionInfoEx 的 SessionFlags；Windows 10 起 0 = 锁着）。
    /// Windows 11 锁屏时 OpenInputDesktop 有时还是 "Default"（Desktop.IsInputDesktopNormal 看不出来），桌面复制却已经 E_ACCESSDENIED：两个都要看。
    /// 问不到时当没锁。
    /// </summary>
    public static bool SessionLocked()
    {
        if (!Native.ProcessIdToSessionId(Environment.ProcessId, out var session)) return false;
        if (!Native.WTSQuerySessionInformationW(IntPtr.Zero, session, Native.WTSSessionInfoEx, out var buffer, out _) || buffer == 0) return false;
        try
        {
            // WTSINFOEXW：Level（DWORD），8 字节处起 WTSINFOEX_LEVEL1_W：SessionId、SessionState、SessionFlags
            return Marshal.ReadInt32(buffer) == 1 && Marshal.ReadInt32(buffer, 16) == Native.WTS_SESSIONSTATE_LOCK;
        }
        finally
        {
            Native.WTSFreeMemory(buffer);
        }
    }

    /// <summary>显卡（DXGI）：名字、厂商 / 设备号、驱动版本、显存、接在它上面的显示器。会载入显卡驱动，只在探测子进程里调。</summary>
    public static List<TelemetryGpu> Gpus()
    {
        var gpus = new List<TelemetryGpu>();
        var factory = Dxgi.CreateFactory();
        try
        {
            for (var i = 0; i < 16; i++)
            {
                var hr = factory.EnumAdapters1(i, out var adapter);
                if (hr == Dxgi.ErrorNotFound) break;
                if (hr < 0) throw new COMException($"EnumAdapters1 失败：{HResults.Describe(hr)}", hr);
                try
                {
                    if (adapter.GetDesc1(out var desc) < 0) continue;
                    var outputs = new List<string>();
                    for (var o = 0; o < 16 && adapter.EnumOutputs(o, out var output) >= 0; o++)
                    {
                        try
                        {
                            if (output.GetDesc(out var od) >= 0 && od.AttachedToDesktop != 0) outputs.Add(od.DeviceName);
                        }
                        finally
                        {
                            Marshal.ReleaseComObject(output);
                        }
                    }
                    gpus.Add(new TelemetryGpu(desc.Description.Trim(), desc.VendorId, desc.DeviceId, Dxgi.DriverVersion(adapter),
                        (long)desc.DedicatedVideoMemory, (desc.Flags & Dxgi.AdapterFlagSoftware) != 0, outputs));
                }
                finally
                {
                    Marshal.ReleaseComObject(adapter);
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(factory);
        }
        return gpus;
    }

    /// <summary>
    /// 显示器：名字、分辨率（物理像素：进程是 Per-Monitor V2）、刷新率、缩放、是不是主显示器、接在哪张显卡上（按 gpus 的 Outputs 对名字）。
    /// </summary>
    public static List<TelemetryDisplay> Displays(IReadOnlyList<TelemetryGpu> gpus)
    {
        var displays = new List<TelemetryDisplay>();
        foreach (var screen in Screen.AllScreens)
        {
            var b = screen.Bounds;
            var mode = new Native.DEVMODEW { dmSize = (short)Marshal.SizeOf<Native.DEVMODEW>() };
            // 0 和 1 是"硬件默认"，当成不知道
            var refresh = Native.EnumDisplaySettingsW(screen.DeviceName, Native.ENUM_CURRENT_SETTINGS, ref mode) && mode.dmDisplayFrequency > 1 ? mode.dmDisplayFrequency : 0;
            var scale = 100;
            var monitor = Native.MonitorFromPoint(new Native.POINT { X = b.X + b.Width / 2, Y = b.Y + b.Height / 2 }, Native.MONITOR_DEFAULTTONEAREST);
            if (monitor != 0 && Native.GetDpiForMonitor(monitor, Native.MDT_EFFECTIVE_DPI, out var dpiX, out _) >= 0 && dpiX > 0)
                scale = (int)Math.Round(dpiX * 100.0 / 96);
            var adapter = gpus.FirstOrDefault(g => g.Outputs.Any(o => string.Equals(o, screen.DeviceName, StringComparison.OrdinalIgnoreCase)))?.Name;
            displays.Add(new TelemetryDisplay(screen.DeviceName, b.Width, b.Height, refresh, scale, screen.Primary, adapter));
        }
        return displays;
    }

    /// <summary>
    /// 指纹：程序版本、显卡驱动（注册表里装着的显卡和驱动版本）、显示器（名字、哪块是主的）、是不是远程桌面会话，算 SHA-256。
    /// 不含分辨率：全屏游戏改分辨率不该触发重新探测（分辨率、刷新率、缩放每次上报前都会重新读）。
    /// 全是便宜的读取，Agent 进程每次连上服务器时算一遍，和上次探测时的比。
    /// </summary>
    public static string Fingerprint()
    {
        var text = new StringBuilder();
        text.Append("agent ").Append(AgentVersion).Append('\n');
        foreach (var driver in DisplayDrivers().Order(StringComparer.Ordinal)) text.Append("gpu ").Append(driver).Append('\n');
        foreach (var screen in Screen.AllScreens.OrderBy(s => s.DeviceName, StringComparer.OrdinalIgnoreCase))
            text.Append("display ").Append(screen.DeviceName).Append(screen.Primary ? " primary" : "").Append('\n');
        text.Append("rdp ").Append(RemoteDesktopSession());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))).ToLowerInvariant();
    }

    /// <summary>注册表"显示适配器"类下装着的驱动："名字|版本"（普通用户读得到；Properties 这种子键读不了，跳过）。</summary>
    private static List<string> DisplayDrivers()
    {
        var drivers = new List<string>();
        using var root = Registry.LocalMachine.OpenSubKey(DisplayClassKey);
        if (root is null) return drivers;
        foreach (var name in root.GetSubKeyNames())
        {
            if (name.Length != 4 || !name.All(char.IsAsciiDigit)) continue;
            try
            {
                using var key = root.OpenSubKey(name);
                if (key?.GetValue("DriverDesc") is string desc) drivers.Add($"{desc}|{key.GetValue("DriverVersion") as string}");
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        }
        return drivers;
    }

    private static class Native
    {
        public const int RelationProcessorCore = 0;
        public const int SM_REMOTESESSION = 0x1000;
        public const int ENUM_CURRENT_SETTINGS = -1;
        public const int MONITOR_DEFAULTTONEAREST = 2;
        public const int MDT_EFFECTIVE_DPI = 0;
        public const int WTSSessionInfoEx = 25;
        public const int WTS_SESSIONSTATE_LOCK = 0;

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MEMORYSTATUSEX
        {
            public int dwLength; public int dwMemoryLoad;
            public ulong ullTotalPhys; public ulong ullAvailPhys; public ulong ullTotalPageFile; public ulong ullAvailPageFile;
            public ulong ullTotalVirtual; public ulong ullAvailVirtual; public ulong ullAvailExtendedVirtual;
        }

        /// <summary>DEVMODEW（220 字节）：只用到 dmSize 和 dmDisplayFrequency。</summary>
        [StructLayout(LayoutKind.Explicit, Size = 220)]
        public struct DEVMODEW
        {
            [FieldOffset(68)] public short dmSize;
            [FieldOffset(172)] public int dmPelsWidth;
            [FieldOffset(176)] public int dmPelsHeight;
            [FieldOffset(184)] public int dmDisplayFrequency;
        }

        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref int length);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);
        [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool EnumDisplaySettingsW(string deviceName, int modeNum, ref DEVMODEW mode);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT pt, int flags);
        [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out int dpiX, out int dpiY);
        [DllImport("kernel32.dll")] public static extern bool ProcessIdToSessionId(int processId, out int sessionId);
        [DllImport("wtsapi32.dll", SetLastError = true)] public static extern bool WTSQuerySessionInformationW(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytes);
        [DllImport("wtsapi32.dll")] public static extern void WTSFreeMemory(IntPtr memory);
    }
}

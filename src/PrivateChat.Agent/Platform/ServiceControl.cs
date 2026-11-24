using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace PrivateChat.Agent.Platform;

/// <summary>装 / 起 / 停 / 删 Windows 服务（OpenSCManager、CreateService……）。除了查状态都要管理员权限（提权后的进程）。</summary>
internal static class ServiceControl
{
    public const int Stopped = 1;
    public const int Running = 4;

    /// <summary>装一个以 LocalSystem 跑的服务。commandLine：带引号的完整路径和参数（路径里有空格不加引号会被劫持）。</summary>
    public static void Create(string name, string displayName, string commandLine, bool autoStart, string? description = null)
    {
        using var manager = OpenManager(Native.SC_MANAGER_CREATE_SERVICE);
        var service = Native.CreateServiceW(manager.Handle, name, displayName, Native.SERVICE_ALL_ACCESS, Native.SERVICE_WIN32_OWN_PROCESS,
            autoStart ? Native.SERVICE_AUTO_START : Native.SERVICE_DEMAND_START, Native.SERVICE_ERROR_NORMAL, commandLine, null, IntPtr.Zero, null, null, null);
        if (service == IntPtr.Zero) throw Failed($"CreateService({name})");
        try
        {
            if (description is not null)
            {
                var info = new Native.SERVICE_DESCRIPTIONW { lpDescription = description };
                Native.ChangeServiceConfig2W(service, Native.SERVICE_CONFIG_DESCRIPTION, ref info);
            }
        }
        finally
        {
            Native.CloseServiceHandle(service);
        }
    }

    /// <summary>服务进程的额外环境变量（服务注册表项下的 Environment，REG_MULTI_SZ），下次启动生效。</summary>
    public static void SetEnvironment(string name, IReadOnlyDictionary<string, string> variables)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}", writable: true)
            ?? throw new InvalidOperationException($"服务 {name} 的注册表项不存在");
        key.SetValue("Environment", variables.Select(v => $"{v.Key}={v.Value}").ToArray(), RegistryValueKind.MultiString);
    }

    public static bool Exists(string name) => State(name) is not null;

    /// <summary>服务现在的状态（1 停止、2 正在启动、3 正在停止、4 运行中……）；没有这个服务时 null。普通用户也能查。</summary>
    public static int? State(string name)
    {
        using var manager = OpenManager(Native.SC_MANAGER_CONNECT);
        var service = Native.OpenServiceW(manager.Handle, name, Native.SERVICE_QUERY_STATUS);
        if (service == IntPtr.Zero) return null;
        try
        {
            return Native.QueryServiceStatus(service, out var status) ? status.dwCurrentState : null;
        }
        finally
        {
            Native.CloseServiceHandle(service);
        }
    }

    /// <summary>
    /// 改成开机自动启动、换成这个命令行；崩了（或者非正常退出）5 秒、30 秒、2 分钟后自动重启，一天没再出事就从头计。
    /// </summary>
    public static void Configure(string name, string commandLine)
    {
        using var service = OpenService(name, Native.SERVICE_ALL_ACCESS);
        if (!Native.ChangeServiceConfigW(service.Handle, Native.SERVICE_NO_CHANGE, Native.SERVICE_AUTO_START, Native.SERVICE_NO_CHANGE, commandLine,
                null, IntPtr.Zero, null, null, null, null))
            throw Failed($"ChangeServiceConfig({name})");
        int[] delays = [5_000, 30_000, 120_000];
        var actions = Marshal.AllocHGlobal(8 * delays.Length);
        try
        {
            for (var i = 0; i < delays.Length; i++)
            {
                Marshal.WriteInt32(actions, i * 8, Native.SC_ACTION_RESTART);
                Marshal.WriteInt32(actions, i * 8 + 4, delays[i]);
            }
            var failure = new Native.SERVICE_FAILURE_ACTIONSW { dwResetPeriod = 24 * 3600, cActions = delays.Length, lpsaActions = actions };
            if (!Native.ChangeServiceConfig2W(service.Handle, Native.SERVICE_CONFIG_FAILURE_ACTIONS, ref failure)) throw Failed($"设置 {name} 的失败重启");
            var flag = new Native.SERVICE_FAILURE_ACTIONS_FLAG { fFailureActionsOnNonCrashFailures = 1 };
            Native.ChangeServiceConfig2W(service.Handle, Native.SERVICE_CONFIG_FAILURE_ACTIONS_FLAG, ref flag);
        }
        finally
        {
            Marshal.FreeHGlobal(actions);
        }
    }

    /// <summary>服务进程的进程号；没在跑（或者没有这个服务）时 null。</summary>
    public static int? ProcessId(string name)
    {
        using var manager = OpenManager(Native.SC_MANAGER_CONNECT);
        var service = Native.OpenServiceW(manager.Handle, name, Native.SERVICE_QUERY_STATUS);
        if (service == IntPtr.Zero) return null;
        try
        {
            var buffer = new byte[36];
            if (!Native.QueryServiceStatusEx(service, Native.SC_STATUS_PROCESS_INFO, buffer, buffer.Length, out _)) return null;
            // SERVICE_STATUS_PROCESS：7 个 DWORD 的 SERVICE_STATUS，第 8 个是 dwProcessId
            var pid = BitConverter.ToInt32(buffer, 28);
            return pid > 0 ? pid : null;
        }
        finally
        {
            Native.CloseServiceHandle(service);
        }
    }

    public static void Start(string name)
    {
        using var service = OpenService(name, Native.SERVICE_START);
        if (!Native.StartServiceW(service.Handle, 0, IntPtr.Zero)) throw Failed($"StartService({name})");
    }

    /// <summary>叫它停、最多等 wait；停了（或者本来就没在跑）返回 true。</summary>
    public static bool Stop(string name, TimeSpan wait)
    {
        using var service = OpenService(name, Native.SERVICE_STOP | Native.SERVICE_QUERY_STATUS);
        if (!Native.QueryServiceStatus(service.Handle, out var status)) throw Failed($"QueryServiceStatus({name})");
        if (status.dwCurrentState == Stopped) return true;
        Native.ControlService(service.Handle, Native.SERVICE_CONTROL_STOP, out _);
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < wait)
        {
            if (Native.QueryServiceStatus(service.Handle, out status) && status.dwCurrentState == Stopped) return true;
            Thread.Sleep(200);
        }
        return false;
    }

    /// <summary>删掉（还在跑的话要等它停了才真的没了）。</summary>
    public static void Delete(string name)
    {
        using var service = OpenService(name, Native.DELETE);
        if (!Native.DeleteService(service.Handle) && Marshal.GetLastWin32Error() != Native.ERROR_SERVICE_MARKED_FOR_DELETE) throw Failed($"DeleteService({name})");
    }

    /// <summary>这个进程是提权后的管理员（UAC 下没提权的管理员算 false）。</summary>
    public static bool IsElevatedAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static ScHandle OpenManager(uint access)
    {
        var manager = Native.OpenSCManagerW(null, null, access);
        return manager == IntPtr.Zero ? throw Failed("OpenSCManager") : new ScHandle(manager);
    }

    private static ScHandle OpenService(string name, uint access)
    {
        using var manager = OpenManager(Native.SC_MANAGER_CONNECT);
        var service = Native.OpenServiceW(manager.Handle, name, access);
        return service == IntPtr.Zero ? throw Failed($"OpenService({name})") : new ScHandle(service);
    }

    private static Win32Exception Failed(string what)
    {
        var error = Marshal.GetLastWin32Error();
        return new Win32Exception(error, $"{what} 失败：{new Win32Exception(error).Message}（{error}）");
    }

    private sealed class ScHandle(IntPtr handle) : IDisposable
    {
        public IntPtr Handle { get; } = handle;

        public void Dispose() => Native.CloseServiceHandle(Handle);
    }

    private static class Native
    {
        public const uint SC_MANAGER_CONNECT = 0x0001;
        public const uint SC_MANAGER_CREATE_SERVICE = 0x0002;
        public const uint SERVICE_QUERY_STATUS = 0x0004;
        public const uint SERVICE_START = 0x0010;
        public const uint SERVICE_STOP = 0x0020;
        public const uint DELETE = 0x00010000;
        public const uint SERVICE_ALL_ACCESS = 0x000F01FF;
        public const uint SERVICE_WIN32_OWN_PROCESS = 0x10;
        public const uint SERVICE_AUTO_START = 2;
        public const uint SERVICE_DEMAND_START = 3;
        public const uint SERVICE_ERROR_NORMAL = 1;
        public const int SERVICE_CONTROL_STOP = 1;
        public const int SERVICE_CONFIG_DESCRIPTION = 1;
        public const int ERROR_SERVICE_MARKED_FOR_DELETE = 1072;

        [StructLayout(LayoutKind.Sequential)]
        public struct SERVICE_STATUS
        {
            public int dwServiceType, dwCurrentState, dwControlsAccepted, dwWin32ExitCode, dwServiceSpecificExitCode, dwCheckPoint, dwWaitHint;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct SERVICE_DESCRIPTIONW { public string? lpDescription; }

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] public static extern IntPtr OpenSCManagerW(string? machine, string? database, uint access);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] public static extern IntPtr OpenServiceW(IntPtr manager, string name, uint access);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateServiceW(IntPtr manager, string name, string displayName, uint access, uint serviceType, uint startType, uint errorControl,
            string binaryPath, string? loadOrderGroup, IntPtr tagId, string? dependencies, string? account, string? password);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] public static extern bool ChangeServiceConfig2W(IntPtr service, int infoLevel, ref SERVICE_DESCRIPTIONW info);
        [DllImport("advapi32.dll", SetLastError = true)] public static extern bool StartServiceW(IntPtr service, int argc, IntPtr argv);
        [DllImport("advapi32.dll", SetLastError = true)] public static extern bool ControlService(IntPtr service, int control, out SERVICE_STATUS status);
        [DllImport("advapi32.dll", SetLastError = true)] public static extern bool QueryServiceStatus(IntPtr service, out SERVICE_STATUS status);
        [DllImport("advapi32.dll", SetLastError = true)] public static extern bool DeleteService(IntPtr service);
        public const int SC_STATUS_PROCESS_INFO = 0;
        public const uint SERVICE_NO_CHANGE = 0xFFFFFFFF;
        public const int SERVICE_CONFIG_FAILURE_ACTIONS = 2;
        public const int SERVICE_CONFIG_FAILURE_ACTIONS_FLAG = 4;
        public const int SC_ACTION_RESTART = 1;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct SERVICE_FAILURE_ACTIONSW
        {
            public int dwResetPeriod;
            public string? lpRebootMsg;
            public string? lpCommand;
            public int cActions;
            public IntPtr lpsaActions;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SERVICE_FAILURE_ACTIONS_FLAG { public int fFailureActionsOnNonCrashFailures; }

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool ChangeServiceConfigW(IntPtr service, uint serviceType, uint startType, uint errorControl, string? binaryPath, string? loadOrderGroup,
            IntPtr tagId, string? dependencies, string? account, string? password, string? displayName);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] public static extern bool ChangeServiceConfig2W(IntPtr service, int infoLevel, ref SERVICE_FAILURE_ACTIONSW info);
        [DllImport("advapi32.dll", SetLastError = true)] public static extern bool ChangeServiceConfig2W(IntPtr service, int infoLevel, ref SERVICE_FAILURE_ACTIONS_FLAG info);
        [DllImport("advapi32.dll", SetLastError = true)] public static extern bool QueryServiceStatusEx(IntPtr service, int infoLevel, byte[] buffer, int size, out int needed);
        [DllImport("advapi32.dll", SetLastError = true)] public static extern bool CloseServiceHandle(IntPtr handle);
    }
}

/// <summary>
/// 远程发 Ctrl+Alt+Del：SendSAS（sas.dll）。要本机策略允许软件产生 SAS（SoftwareSASGeneration，HKLM 策略）：
/// 0 谁都不行、1 服务、2 轻松使用（屏幕键盘之类）、3 服务和轻松使用；没设时 Windows 按"只有轻松使用"处理。
/// 我们要打开时：没设就写 3（不丢掉轻松使用的默认），设了就把"服务"那一位或上去；原来的值记下来，卸载时还原。
/// </summary>
internal static class SasPolicy
{
    private const string KeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
    private const string ValueName = "SoftwareSASGeneration";

    /// <summary>现在的值；没设时 null。</summary>
    public static int? Read()
    {
        using var key = Registry.LocalMachine.OpenSubKey(KeyPath);
        return key?.GetValue(ValueName) is int value ? value : null;
    }

    /// <summary>写回某个值；null = 删掉（回到没设）。要管理员 / SYSTEM。</summary>
    public static void Write(int? value)
    {
        using var key = Registry.LocalMachine.CreateSubKey(KeyPath, writable: true);
        if (value is { } v) key.SetValue(ValueName, v, RegistryValueKind.DWord);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>要让服务能发 SAS，这个值该是多少（见类说明）。</summary>
    public static int Allowing(int? current) => current is { } v ? v | 1 : 3;

    public static bool AllowsServices(int? current) => current is { } v && (v & 1) != 0;

    /// <summary>asUser：调用方是不是以当前用户的身份跑（服务里是 false）。没有返回值：成没成要看输入桌面有没有换成 Winlogon。</summary>
    [DllImport("sas.dll")] public static extern void SendSAS([MarshalAs(UnmanagedType.Bool)] bool asUser);
}

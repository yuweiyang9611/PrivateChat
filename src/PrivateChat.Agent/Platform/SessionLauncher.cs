using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PrivateChat.Agent.Platform;

/// <summary>
/// 服务（会话 0，LocalSystem）在控制台会话里起进程：
/// - <see cref="StartAsSystem"/>：复制服务自己的 SYSTEM 令牌、把令牌的会话号改成目标会话（要 SeTcbPrivilege，LocalSystem 有），在 winsta0\default 上起——
///   远程时截屏、注入的工作进程，它能切到 UAC / 锁屏的安全桌面；restricted = 去掉所有特权（DISABLE_MAX_PRIVILEGE，只留 SeChangeNotify）。
/// - <see cref="StartAsUser"/>：用这个会话里登录的人的令牌（WTSQueryUserToken；管理员在 UAC 下拿到的是没提权的那份）——托盘、同意窗口、红条。
/// 不继承句柄（不同会话之间本来也继承不了），进程间只走命名管道；也不放进作业对象（会话 0 的作业装不了别的会话的进程）。
/// 子进程靠管道断了、或者看到服务进程没了自己退出。
/// </summary>
internal static class SessionLauncher
{
    /// <summary>
    /// 开发 / 自动化测试（--service-dev）：不以 SYSTEM、也不以别的用户起，都以当前用户普通地起，继承标准输出（子进程的日志和服务的混在一起）。
    /// </summary>
    public static bool CurrentUserOnly { get; set; }

    /// <summary>工作进程、探测进程跑在哪个账号下（管道只放给它）：平时是 SYSTEM，开发模式是当前用户。</summary>
    public static System.Security.Principal.SecurityIdentifier SystemAccount
        => CurrentUserOnly ? System.Security.Principal.WindowsIdentity.GetCurrent().User! : PipeChannel.LocalSystem;

    private static LaunchedProcess StartPlain(string exePath, IReadOnlyList<string> args, IReadOnlyDictionary<string, string?>? environment)
    {
        var start = new System.Diagnostics.ProcessStartInfo(exePath) { UseShellExecute = false };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        foreach (var (name, value) in environment ?? new Dictionary<string, string?>())
        {
            if (value is null) start.Environment.Remove(name);
            else start.Environment[name] = value;
        }
        var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("子进程起不来");
        return new LaunchedProcess(process.Id, process.SafeHandle);
    }

    /// <summary>以 SYSTEM 在 sessionId 会话里起 exePath args。environment：在 SYSTEM 默认环境变量上改的（值 null = 删掉）。</summary>
    public static LaunchedProcess StartAsSystem(uint sessionId, string exePath, IReadOnlyList<string> args, bool restricted = false, IReadOnlyDictionary<string, string?>? environment = null)
    {
        if (CurrentUserOnly) return StartPlain(exePath, args, environment);
        var self = IntPtr.Zero;
        var primary = IntPtr.Zero;
        var limited = IntPtr.Zero;
        try
        {
            if (!Native.OpenProcessToken(Native.GetCurrentProcess(), Native.TOKEN_DUPLICATE | Native.TOKEN_QUERY | Native.TOKEN_ASSIGN_PRIMARY | Native.TOKEN_ADJUST_DEFAULT | Native.TOKEN_ADJUST_SESSIONID, out self))
                throw Failed("OpenProcessToken");
            if (!Native.DuplicateTokenEx(self, Native.MAXIMUM_ALLOWED, IntPtr.Zero, Native.SecurityIdentification, Native.TokenPrimary, out primary))
                throw Failed("DuplicateTokenEx");
            var token = primary;
            if (restricted)
            {
                if (!Native.CreateRestrictedToken(primary, Native.DISABLE_MAX_PRIVILEGE, 0, IntPtr.Zero, 0, IntPtr.Zero, 0, IntPtr.Zero, out limited))
                    throw Failed("CreateRestrictedToken");
                token = limited;
            }
            var session = sessionId;
            if (!Native.SetTokenInformation(token, Native.TokenSessionId, ref session, sizeof(uint))) throw Failed($"SetTokenInformation(TokenSessionId={sessionId})");
            return Start(token, exePath, args, environment);
        }
        finally
        {
            Close(limited);
            Close(primary);
            Close(self);
        }
    }

    /// <summary>以 sessionId 会话里登录的人起 exePath args（没人登录时抛 Win32Exception ERROR_NO_TOKEN 1008）。</summary>
    public static LaunchedProcess StartAsUser(uint sessionId, string exePath, IReadOnlyList<string> args, IReadOnlyDictionary<string, string?>? environment = null)
    {
        if (CurrentUserOnly) return StartPlain(exePath, args, environment);
        var user = IntPtr.Zero;
        var primary = IntPtr.Zero;
        try
        {
            if (!Native.WTSQueryUserToken(sessionId, out user)) throw Failed($"WTSQueryUserToken({sessionId})");
            if (!Native.DuplicateTokenEx(user, Native.MAXIMUM_ALLOWED, IntPtr.Zero, Native.SecurityIdentification, Native.TokenPrimary, out primary))
                throw Failed("DuplicateTokenEx");
            return Start(primary, exePath, args, environment);
        }
        finally
        {
            Close(primary);
            Close(user);
        }
    }

    /// <summary>sessionId 会话里登录的人（没人登录时抛 Win32Exception ERROR_NO_TOKEN 1008）。调用方 Dispose。</summary>
    public static System.Security.Principal.WindowsIdentity UserIdentity(uint sessionId)
    {
        if (!Native.WTSQueryUserToken(sessionId, out var token)) throw Failed($"WTSQueryUserToken({sessionId})");
        try
        {
            return new System.Security.Principal.WindowsIdentity(token);
        }
        finally
        {
            Close(token);
        }
    }

    private static LaunchedProcess Start(IntPtr token, string exePath, IReadOnlyList<string> args, IReadOnlyDictionary<string, string?>? environment)
    {
        if (!Path.IsPathFullyQualified(exePath)) throw new ArgumentException("要完整路径（不让系统去搜程序）", nameof(exePath));
        var block = EnvironmentBlock(token, environment);
        try
        {
            var commandLine = new StringBuilder(CommandLine(exePath, args));
            var startup = new Native.STARTUPINFOW { cb = Marshal.SizeOf<Native.STARTUPINFOW>(), lpDesktop = @"winsta0\default" };
            if (!Native.CreateProcessAsUserW(token, exePath, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                    Native.CREATE_UNICODE_ENVIRONMENT | Native.CREATE_NO_WINDOW, block, Environment.SystemDirectory, ref startup, out var info))
                throw Failed("CreateProcessAsUser");
            Close(info.hThread);
            return new LaunchedProcess(info.dwProcessId, new SafeProcessHandle(info.hProcess, ownsHandle: true));
        }
        finally
        {
            Marshal.FreeHGlobal(block);
        }
    }

    /// <summary>令牌对应的默认环境变量（CreateEnvironmentBlock），再按 overrides 改，排好序拼成 Unicode 环境块（调用方 FreeHGlobal）。</summary>
    private static IntPtr EnvironmentBlock(IntPtr token, IReadOnlyDictionary<string, string?>? overrides)
    {
        var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Native.CreateEnvironmentBlock(out var native, token, false)) throw Failed("CreateEnvironmentBlock");
        try
        {
            var p = native;
            while (true)
            {
                var entry = Marshal.PtrToStringUni(p);
                if (string.IsNullOrEmpty(entry)) break;
                p += (entry.Length + 1) * 2;
                // 开头的 '=' 是 "=C:=C:\" 这类每个盘的当前目录，从第二个字符起找分隔
                var split = entry.IndexOf('=', 1);
                if (split > 0) variables[entry[..split]] = entry[(split + 1)..];
            }
        }
        finally
        {
            Native.DestroyEnvironmentBlock(native);
        }
        foreach (var (name, value) in overrides ?? new Dictionary<string, string?>())
        {
            if (value is null) variables.Remove(name);
            else variables[name] = value;
        }
        var text = new StringBuilder();
        foreach (var (name, value) in variables) text.Append(name).Append('=').Append(value).Append('\0');
        text.Append('\0');
        return Marshal.StringToHGlobalUni(text.ToString());
    }

    internal static string CommandLine(string exePath, IReadOnlyList<string> args) => CommandLines.Build(exePath, args);

    internal static string Quote(string arg) => CommandLines.Quote(arg);

    private static Win32Exception Failed(string what)
    {
        var error = Marshal.GetLastWin32Error();
        return new Win32Exception(error, $"{what} 失败：{new Win32Exception(error).Message}（{error}）");
    }

    private static void Close(IntPtr handle)
    {
        if (handle != IntPtr.Zero) Native.CloseHandle(handle);
    }

    private static class Native
    {
        public const uint TOKEN_ASSIGN_PRIMARY = 0x0001;
        public const uint TOKEN_DUPLICATE = 0x0002;
        public const uint TOKEN_QUERY = 0x0008;
        public const uint TOKEN_ADJUST_DEFAULT = 0x0080;
        public const uint TOKEN_ADJUST_SESSIONID = 0x0100;
        public const uint MAXIMUM_ALLOWED = 0x02000000;
        public const int SecurityIdentification = 1;
        public const int TokenPrimary = 1;
        public const int TokenSessionId = 12;
        public const uint DISABLE_MAX_PRIVILEGE = 0x1;
        public const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
        public const uint CREATE_NO_WINDOW = 0x08000000;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct STARTUPINFOW
        {
            public int cb;
            public string? lpReserved;
            public string? lpDesktop;
            public string? lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PROCESS_INFORMATION
        {
            public IntPtr hProcess, hThread;
            public int dwProcessId, dwThreadId;
        }

        [DllImport("kernel32.dll")] public static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool CloseHandle(IntPtr handle);
        [DllImport("advapi32.dll", SetLastError = true)] public static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool DuplicateTokenEx(IntPtr existing, uint access, IntPtr attributes, int impersonationLevel, int tokenType, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)] public static extern bool SetTokenInformation(IntPtr token, int infoClass, ref uint info, int length);
        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool CreateRestrictedToken(IntPtr existing, uint flags, uint disableSidCount, IntPtr sidsToDisable, uint deletePrivilegeCount,
            IntPtr privilegesToDelete, uint restrictedSidCount, IntPtr sidsToRestrict, out IntPtr token);
        [DllImport("wtsapi32.dll", SetLastError = true)] public static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);
        [DllImport("userenv.dll", SetLastError = true)] public static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);
        [DllImport("userenv.dll", SetLastError = true)] public static extern bool DestroyEnvironmentBlock(IntPtr environment);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool CreateProcessAsUserW(IntPtr token, string applicationName, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
            bool inheritHandles, uint creationFlags, IntPtr environment, string currentDirectory, ref STARTUPINFOW startupInfo, out PROCESS_INFORMATION processInformation);
    }
}

/// <summary><see cref="SessionLauncher"/> 起的进程（拿着进程句柄：等它退出、杀掉、看退出码）。</summary>
internal sealed class LaunchedProcess(int id, SafeProcessHandle handle) : IDisposable
{
    public int Id { get; } = id;

    public SafeProcessHandle Handle { get; } = handle;

    public bool WaitForExit(int milliseconds) => Native.WaitForSingleObject(Handle, milliseconds) == 0;

    public bool HasExited => WaitForExit(0);

    /// <summary>还在跑时 null。</summary>
    public int? ExitCode => HasExited && Native.GetExitCodeProcess(Handle, out var code) ? code : null;

    public Task WaitForExitAsync(CancellationToken ct) => Task.Run(() =>
    {
        while (!WaitForExit(200)) ct.ThrowIfCancellationRequested();
    }, ct);

    public void Kill()
    {
        if (!HasExited) Native.TerminateProcess(Handle, 1);
    }

    public void Dispose() => Handle.Dispose();

    private static class Native
    {
        [DllImport("kernel32.dll")] public static extern int WaitForSingleObject(SafeProcessHandle handle, int milliseconds);
        [DllImport("kernel32.dll")] public static extern bool GetExitCodeProcess(SafeProcessHandle handle, out int code);
        [DllImport("kernel32.dll")] public static extern bool TerminateProcess(SafeProcessHandle handle, int code);
    }
}

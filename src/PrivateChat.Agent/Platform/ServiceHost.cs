using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace PrivateChat.Agent.Platform;

/// <summary>
/// 作为 Windows 服务跑（服务控制管理器 SCM 起的进程）：手写的 StartServiceCtrlDispatcher / RegisterServiceCtrlHandlerEx / SetServiceStatus
/// （ServiceBase 不在共享框架里，要另装 NuGet 包）。body 在自己的线程上跑，返回就算服务停了；SCM 要停（停止、关机）时取消它的 ct。
/// 会话变化（有人登录、注销、锁屏、解锁、切换用户）放进 body 收到的通道里——控制回调要马上返回，不能在里面干活。
/// 不是 SCM 起的（双击、命令行）时 StartServiceCtrlDispatcher 失败（ERROR_FAILED_SERVICE_CONTROLLER_CONNECT 1063），返回 1063。
/// </summary>
internal static class ServiceHost
{
    /// <summary>WM_WTSSESSION_CHANGE 的事件号：1 控制台连上、2 控制台断开、5 登录、6 注销、7 锁屏、8 解锁……</summary>
    public readonly record struct SessionChange(int EventType, uint SessionId);

    public const int ErrorNotService = 1063;

    /// <summary>
    /// 要停（停止、关机、关机前）时在控制回调里同步调一次、要快（关机时 SCM 只等几秒）：最要紧的收尾放这里（比如还原改过的系统设置），
    /// 别的交给 body 收到取消后慢慢做。
    /// </summary>
    public static Action? OnStopping { get; set; }

    /// <summary>最后一次收到的停止类控制码（1 停止、5 关机、0xF 关机前）；还没收到时 0。</summary>
    public static int LastStopControl { get; private set; }

    public const int ControlStop = 1;
    public const int ControlShutdown = 5;
    public const int ControlPreshutdown = 0xF;

    // 交给系统的委托要一直有人引用着，不然被回收后系统回调就崩
    private static Native.ServiceMainProc? _main;
    private static Native.HandlerEx? _handler;
    private static IntPtr _statusHandle;
    private static Native.SERVICE_STATUS _status;
    private static readonly Lock StatusGate = new();
    private static readonly CancellationTokenSource Stop = new();
    private static readonly Channel<SessionChange> Changes = Channel.CreateUnbounded<SessionChange>();
    private static Func<ChannelReader<SessionChange>, CancellationToken, Task<int>>? _body;
    private static string _name = "";

    public static int Run(string name, Func<ChannelReader<SessionChange>, CancellationToken, Task<int>> body)
    {
        _name = name;
        _body = body;
        _main = ServiceMain;
        _handler = Handler;
        var table = new[]
        {
            new Native.SERVICE_TABLE_ENTRYW { lpServiceName = name, lpServiceProc = _main },
            new Native.SERVICE_TABLE_ENTRYW(),
        };
        if (!Native.StartServiceCtrlDispatcherW(table)) return Marshal.GetLastWin32Error();
        return _status.dwWin32ExitCode;
    }

    private static void ServiceMain(int argc, IntPtr argv)
    {
        _statusHandle = Native.RegisterServiceCtrlHandlerExW(_name, _handler!, IntPtr.Zero);
        if (_statusHandle == IntPtr.Zero)
        {
            AgentLog.Error($"RegisterServiceCtrlHandlerEx 失败：{new Win32Exception(Marshal.GetLastWin32Error()).Message}");
            return;
        }
        SetState(Native.SERVICE_START_PENDING, waitHintMs: 10_000);
        SetState(Native.SERVICE_RUNNING);
        var exit = 0;
        try
        {
            exit = _body!(Changes.Reader, Stop.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (Stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            AgentLog.Error("服务异常退出", ex);
            exit = 1;
        }
        SetState(Native.SERVICE_STOPPED, exitCode: exit);
    }

    private static int Handler(int control, int eventType, IntPtr eventData, IntPtr context)
    {
        switch (control)
        {
            case Native.SERVICE_CONTROL_STOP:
            case Native.SERVICE_CONTROL_SHUTDOWN:
            case Native.SERVICE_CONTROL_PRESHUTDOWN:
                LastStopControl = control;
                SetState(Native.SERVICE_STOP_PENDING, waitHintMs: 15_000);
                try { OnStopping?.Invoke(); }
                catch (Exception ex) { AgentLog.Error("停止前的收尾出错", ex); }
                Stop.Cancel();
                return 0;
            case Native.SERVICE_CONTROL_INTERROGATE:
                return 0;
            case Native.SERVICE_CONTROL_SESSIONCHANGE:
                // WTSSESSION_NOTIFICATION：cbSize、dwSessionId
                var session = eventData != IntPtr.Zero ? (uint)Marshal.ReadInt32(eventData, 4) : ConsoleSession.None;
                Changes.Writer.TryWrite(new SessionChange(eventType, session));
                return 0;
            default:
                return Native.ERROR_CALL_NOT_IMPLEMENTED;
        }
    }

    private static void SetState(int state, int exitCode = 0, int waitHintMs = 0)
    {
        lock (StatusGate)
        {
            _status.dwServiceType = Native.SERVICE_WIN32_OWN_PROCESS;
            _status.dwCurrentState = state;
            _status.dwControlsAccepted = state is Native.SERVICE_RUNNING
                ? Native.SERVICE_ACCEPT_STOP | Native.SERVICE_ACCEPT_PRESHUTDOWN | Native.SERVICE_ACCEPT_SESSIONCHANGE
                : 0;
            _status.dwWin32ExitCode = exitCode;
            _status.dwWaitHint = waitHintMs;
            _status.dwCheckPoint = state is Native.SERVICE_START_PENDING or Native.SERVICE_STOP_PENDING ? _status.dwCheckPoint + 1 : 0;
            if (_statusHandle != IntPtr.Zero) Native.SetServiceStatus(_statusHandle, ref _status);
        }
    }

    private static class Native
    {
        public const int SERVICE_WIN32_OWN_PROCESS = 0x10;
        public const int SERVICE_STOPPED = 1;
        public const int SERVICE_START_PENDING = 2;
        public const int SERVICE_STOP_PENDING = 3;
        public const int SERVICE_RUNNING = 4;
        public const int SERVICE_ACCEPT_STOP = 0x1;
        public const int SERVICE_ACCEPT_SHUTDOWN = 0x4;
        public const int SERVICE_ACCEPT_SESSIONCHANGE = 0x80;
        public const int SERVICE_ACCEPT_PRESHUTDOWN = 0x100;
        public const int SERVICE_CONTROL_PRESHUTDOWN = 0xF;
        public const int SERVICE_CONTROL_STOP = 1;
        public const int SERVICE_CONTROL_INTERROGATE = 4;
        public const int SERVICE_CONTROL_SHUTDOWN = 5;
        public const int SERVICE_CONTROL_SESSIONCHANGE = 0xE;
        public const int ERROR_CALL_NOT_IMPLEMENTED = 120;

        [UnmanagedFunctionPointer(CallingConvention.Winapi)] public delegate void ServiceMainProc(int argc, IntPtr argv);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] public delegate int HandlerEx(int control, int eventType, IntPtr eventData, IntPtr context);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct SERVICE_TABLE_ENTRYW
        {
            public string? lpServiceName;
            public ServiceMainProc? lpServiceProc;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SERVICE_STATUS
        {
            public int dwServiceType, dwCurrentState, dwControlsAccepted, dwWin32ExitCode, dwServiceSpecificExitCode, dwCheckPoint, dwWaitHint;
        }

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] public static extern bool StartServiceCtrlDispatcherW(SERVICE_TABLE_ENTRYW[] table);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] public static extern IntPtr RegisterServiceCtrlHandlerExW(string name, HandlerEx handler, IntPtr context);
        [DllImport("advapi32.dll", SetLastError = true)] public static extern bool SetServiceStatus(IntPtr handle, ref SERVICE_STATUS status);
    }
}

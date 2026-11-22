using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PrivateChat.Agent.Telemetry;

/// <summary>
/// 探测子进程放进一个"句柄关了就全杀"的作业对象（JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE）：Agent 不管怎么没的（结束任务、崩溃、
/// 升级换版本后退出），系统收掉它的句柄时子进程跟着结束，不会变成孤儿接着跑完 75 秒（还往数据目录里写结果文件、和重启后的 Agent 抢显卡）。
/// 作业对象整个进程只建一个，句柄一直开着、不继承给子进程（不然子进程自己拿着它，作业永远关不掉），进程退出时由系统关。
/// </summary>
internal static class KillOnCloseJob
{
    private static readonly Lazy<IntPtr> Job = new(Create);

    /// <summary>把子进程放进去；放不进去抛 Win32Exception（调用方记一笔接着用，子进程还有自己的 75 秒上限）。</summary>
    public static void Add(Process process)
    {
        if (!Native.AssignProcessToJobObject(Job.Value, process.Handle)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private static IntPtr Create()
    {
        var job = Native.CreateJobObjectW(IntPtr.Zero, null);
        if (job == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        var info = new Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = Native.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if (!Native.SetInformationJobObject(job, Native.JobObjectExtendedLimitInformation, ref info, Marshal.SizeOf<Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
        {
            var error = Marshal.GetLastWin32Error();
            Native.CloseHandle(job);
            throw new Win32Exception(error);
        }
        return job;
    }

    private static class Native
    {
        public const int JobObjectExtendedLimitInformation = 9;
        public const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public nuint MinimumWorkingSetSize;
            public nuint MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public nuint Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct IO_COUNTERS
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        /// <summary>64 位下 144 字节。</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] public static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int length);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool CloseHandle(IntPtr handle);
    }
}

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace PrivateChat.Agent.Platform;

/// <summary>
/// 输入桌面：键鼠现在送到哪个桌面、屏幕上显示的是哪个。winsta0 里有 Default（平常的桌面）、Winlogon（UAC 提示、Ctrl+Alt+Del、锁屏）、
/// Screen-saver（带密码的屏保）。普通用户进程打不开 Winlogon；以 SYSTEM 在控制台会话里跑、把线程切过去（<see cref="DesktopFollower"/>）才截得到、点得到。
/// </summary>
internal static class InputDesktop
{
    public const string Default = "Default";
    public const string Winlogon = "Winlogon";

    /// <summary>现在的输入桌面叫什么；打不开（普通用户遇到安全桌面）时 null。</summary>
    public static string? Name()
    {
        var desktop = Native.OpenInputDesktop(0, false, Native.DESKTOP_READOBJECTS);
        if (desktop == IntPtr.Zero) return null;
        try
        {
            return NameOf(desktop);
        }
        finally
        {
            Native.CloseDesktop(desktop);
        }
    }

    /// <summary>调用这个线程现在挂在哪个桌面上。</summary>
    public static string? ThreadDesktopName() => NameOf(Native.GetThreadDesktop(Native.GetCurrentThreadId()));

    internal static string? NameOf(IntPtr desktop)
    {
        if (desktop == IntPtr.Zero) return null;
        var buffer = new byte[256];
        if (!Native.GetUserObjectInformationW(desktop, Native.UOI_NAME, buffer, buffer.Length, out var needed)) return null;
        return Encoding.Unicode.GetString(buffer, 0, Math.Max(0, Math.Min(needed, buffer.Length) - 2));
    }

    internal static class Native
    {
        public const int UOI_NAME = 2;
        public const uint DESKTOP_READOBJECTS = 0x0001;
        public const uint GENERIC_ALL = 0x10000000;
        public const int ERROR_BUSY = 170;

        [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool SetThreadDesktop(IntPtr desktop);
        [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr GetThreadDesktop(uint threadId);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool CloseDesktop(IntPtr desktop);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool GetUserObjectInformationW(IntPtr obj, int index, byte[] info, int length, out int needed);
        [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    }
}

/// <summary>
/// 让一个线程跟着输入桌面走：每次截屏 / 注入前调 <see cref="EnsureAttached"/>，输入桌面换了就 SetThreadDesktop 过去。
/// 只能在建它的那个线程上用（SetThreadDesktop 管的是调用的线程）；这个线程不能有窗口或钩子，不然 SetThreadDesktop 失败（ERROR_BUSY）。
/// allow：只切到允许的桌面（不允许的就留在原来的桌面上——留在 Default 上时 Windows 自己就不让截、不让点安全桌面，策略写错了也漏不出去）。
/// 切换时先切过去再关旧的句柄（线程还挂着的桌面句柄关不掉）。
/// </summary>
internal sealed class DesktopFollower : IDisposable
{
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private readonly IntPtr _original = InputDesktop.Native.GetThreadDesktop(InputDesktop.Native.GetCurrentThreadId());
    private IntPtr _attached;

    public DesktopFollower()
    {
        Current = InputDesktop.NameOf(_original);
    }

    /// <summary>这个线程现在挂着的桌面的名字。</summary>
    public string? Current { get; private set; }

    /// <summary>上一次没跟上的原因（"OpenInputDesktop：拒绝访问（5）"）；跟上了就清掉。</summary>
    public string? Problem { get; private set; }

    /// <summary>
    /// 跟上现在的输入桌面。返回 true = 线程已经在输入桌面上（<see cref="Current"/> 是它的名字）；
    /// false = 没跟上（打不开、不允许、SetThreadDesktop 失败），线程还在原来的桌面上，原因在 <see cref="Problem"/>。
    /// </summary>
    public bool EnsureAttached(Func<string, bool>? allow = null)
    {
        if (Environment.CurrentManagedThreadId != _threadId) throw new InvalidOperationException("DesktopFollower 只能在建它的线程上用");
        var desktop = InputDesktop.Native.OpenInputDesktop(0, false, InputDesktop.Native.GENERIC_ALL);
        if (desktop == IntPtr.Zero)
        {
            Problem = $"OpenInputDesktop：{Describe(Marshal.GetLastWin32Error())}";
            return false;
        }
        var name = InputDesktop.NameOf(desktop);
        if (name is null)
        {
            Problem = $"GetUserObjectInformation：{Describe(Marshal.GetLastWin32Error())}";
            InputDesktop.Native.CloseDesktop(desktop);
            return false;
        }
        if (string.Equals(name, Current, StringComparison.OrdinalIgnoreCase))
        {
            InputDesktop.Native.CloseDesktop(desktop);
            Problem = null;
            return true;
        }
        if (allow is not null && !allow(name))
        {
            InputDesktop.Native.CloseDesktop(desktop);
            Problem = $"不允许切到桌面 {name}";
            return false;
        }
        if (!InputDesktop.Native.SetThreadDesktop(desktop))
        {
            var error = Marshal.GetLastWin32Error();
            InputDesktop.Native.CloseDesktop(desktop);
            Problem = $"SetThreadDesktop({name})：{Describe(error)}";
            return false;
        }
        if (_attached != IntPtr.Zero) InputDesktop.Native.CloseDesktop(_attached);
        _attached = desktop;
        Current = name;
        Problem = null;
        return true;
    }

    /// <summary>回到线程原来的桌面、关掉打开的句柄。要在同一个线程上调。</summary>
    public void Dispose()
    {
        if (_attached == IntPtr.Zero) return;
        if (Environment.CurrentManagedThreadId == _threadId && InputDesktop.Native.SetThreadDesktop(_original))
            InputDesktop.Native.CloseDesktop(_attached);
        // 换不回去（别的线程上调的、或者 SetThreadDesktop 失败）就不关：线程还挂着的桌面句柄关不掉，进程退出时系统收
        _attached = IntPtr.Zero;
    }

    internal static string Describe(int error) => $"{new Win32Exception(error).Message}（{error}）";
}

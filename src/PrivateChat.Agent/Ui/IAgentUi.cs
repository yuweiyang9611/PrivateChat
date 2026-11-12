using System.Runtime.InteropServices;
using System.Text;
using PrivateChat.Contracts.Remote;

namespace PrivateChat.Agent.Ui;

public enum AgentState { NeedsSetup, Registering, Connecting, Online, InSession }

/// <summary>Agent 的界面：托盘 + 窗口（<see cref="TrayUi"/>），或无界面模式（<see cref="HeadlessUi"/>，自动化测试用）。</summary>
public interface IAgentUi
{
    /// <summary>首次运行（或用户点"重新设置"）：要配置串 / 服务器地址。null = 用户关掉了窗口。</summary>
    Task<string?> AskSetupAsync(string? error, CancellationToken ct);

    /// <summary>显示设备码（null 收起）。</summary>
    void ShowDeviceCode(string? code, string? serverUrl);

    void SetState(AgentState state, string detail);

    /// <summary>"XX 请求远程控制"：true 同意、false 拒绝、null 没回答（超时或请求被取消，ct 触发）。</summary>
    Task<bool?> AskConsentAsync(AgentSessionRequest request, CancellationToken ct);

    /// <summary>屏幕顶部的红条"正在被 XX 远程控制 [断开]"（null 收起）。</summary>
    void ShowBanner(AgentSessionRequest? request);

    Task<string?> ReadClipboardAsync();
    Task WriteClipboardAsync(string text);

    /// <summary>用户在本机要求断开（红条上的按钮、Ctrl+Alt+Shift+Q、托盘菜单）。</summary>
    event Action? DisconnectRequested;

    /// <summary>托盘菜单"重新设置"。</summary>
    event Action? SetupRequested;
}

/// <summary>
/// 无界面模式（--headless）：设备码打印到标准输出并写到数据目录的 device-code.txt，
/// 同意与否由 --consent 决定（accept / decline / ignore），剪贴板只在内存里。
/// </summary>
public sealed class HeadlessUi(string dataDir, string? serverUrl, string consent) : IAgentUi
{
    private string _clipboard = "";

    public event Action? DisconnectRequested { add { } remove { } }
    public event Action? SetupRequested { add { } remove { } }

    public Task<string?> AskSetupAsync(string? error, CancellationToken ct)
    {
        if (error is not null) AgentLog.Warn($"配置无效：{error}");
        return Task.FromResult(error is null ? serverUrl : null);
    }

    public void ShowDeviceCode(string? code, string? server)
    {
        if (code is null) return;
        Console.WriteLine($"DEVICE-CODE: {code}");
        File.WriteAllText(Path.Combine(dataDir, "device-code.txt"), code);
    }

    public void SetState(AgentState state, string detail)
    {
        AgentLog.Info($"状态：{state} {detail}");
        File.WriteAllText(Path.Combine(dataDir, "state.txt"), state.ToString());
    }

    public Task<bool?> AskConsentAsync(AgentSessionRequest request, CancellationToken ct)
    {
        AgentLog.Info($"{request.ControllerName} 请求{(request.Mode == RemoteSessionMode.View ? "查看" : "控制")}，自动回复：{consent}");
        return Task.FromResult<bool?>(consent switch { "accept" => true, "decline" => false, _ => null });
    }

    public void ShowBanner(AgentSessionRequest? request)
        => AgentLog.Info(request is null ? "红条收起" : $"红条：正在被 {request.ControllerName} 远程{(request.Mode == RemoteSessionMode.View ? "查看" : "控制")}");

    public Task<string?> ReadClipboardAsync() => Task.FromResult<string?>(_clipboard);

    public Task WriteClipboardAsync(string text)
    {
        _clipboard = text;
        AgentLog.Info($"剪贴板写入 {text.Length} 字");
        return Task.CompletedTask;
    }
}

/// <summary>桌面状态：剪贴板序号、当前输入桌面是不是普通桌面（锁屏、UAC 提示在安全桌面上，截不到也注入不了）。</summary>
public static class Desktop
{
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetUserObjectInformationW(IntPtr obj, int index, byte[] info, int length, out int needed);

    private const int UOI_NAME = 2;
    private const uint DESKTOP_READOBJECTS = 0x0001;

    public static uint ClipboardSequence => GetClipboardSequenceNumber();

    public static bool IsInputDesktopNormal()
    {
        var desktop = OpenInputDesktop(0, false, DESKTOP_READOBJECTS);
        if (desktop == IntPtr.Zero) return false; // 锁屏 / 安全桌面时普通进程打不开
        try
        {
            var buffer = new byte[256];
            if (!GetUserObjectInformationW(desktop, UOI_NAME, buffer, buffer.Length, out var needed)) return true;
            var name = Encoding.Unicode.GetString(buffer, 0, Math.Max(0, Math.Min(needed, buffer.Length) - 2));
            return name.Equals("Default", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            CloseDesktop(desktop);
        }
    }
}

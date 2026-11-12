using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json;
using PrivateChat.Contracts.Remote;

namespace PrivateChat.Agent.Input;

/// <summary>真正把键鼠送进系统的那一层（SendInput，或测试时写日志）。坐标是虚拟桌面上的物理像素。</summary>
public interface IInputSink
{
    void MouseMove(int x, int y);
    void MouseButton(RemoteMouseButton button, bool down);
    void Wheel(int dx, int dy);
    void Key(ushort scanCode, bool down);
    void Text(string text);
}

/// <summary>
/// 控制端发来的输入 → 系统输入：归一化坐标按当前画面所在的显示器换算到虚拟桌面；
/// 记住按下没松开的键和鼠标键，控制端失焦、断开、会话结束时全部松开（不留"卡住的 Ctrl"）。
/// </summary>
public sealed class InputController(IInputSink sink)
{
    private readonly HashSet<ushort> _keys = [];
    private readonly HashSet<RemoteMouseButton> _buttons = [];
    private readonly Lock _gate = new();

    // 常用扫描码（集合 1；0xE0 前缀的是扩展键）
    private const ushort Ctrl = 0x1D, Shift = 0x2A, Alt = 0x38, Tab = 0x0F, Esc = 0x01, LeftWin = 0xE05B, PrintScreen = 0xE037;

    public void MouseMove(Rectangle screen, float x, float y)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y)) return;
        var px = screen.X + (int)Math.Round(Math.Clamp(x, 0f, 1f) * (screen.Width - 1));
        var py = screen.Y + (int)Math.Round(Math.Clamp(y, 0f, 1f) * (screen.Height - 1));
        sink.MouseMove(px, py);
    }

    public void MouseButton(RemoteMouseButton button, bool down)
    {
        lock (_gate)
        {
            if (down) _buttons.Add(button);
            else if (!_buttons.Remove(button)) return; // 没按下过的松开：忽略（避免凭空多一次点击）
        }
        sink.MouseButton(button, down);
    }

    public void Wheel(short dx, short dy)
    {
        if (dx != 0 || dy != 0) sink.Wheel(dx, dy);
    }

    public void Key(ushort scanCode, bool down)
    {
        if (scanCode == 0) return;
        lock (_gate)
        {
            if (down) _keys.Add(scanCode);
            else _keys.Remove(scanCode); // 自动重复的按下、多余的松开都原样转发，系统能处理
        }
        sink.Key(scanCode, down);
    }

    public void Text(string text)
    {
        if (text.Length > 0) sink.Text(text);
    }

    /// <summary>浏览器拦不住的组合键，由控制端工具栏按钮触发。返回 false 表示做不到（Ctrl+Alt+Del 只有系统服务能发）。</summary>
    public bool Special(RemoteSpecialKey key)
    {
        switch (key)
        {
            case RemoteSpecialKey.Win:
                Tap(LeftWin);
                return true;
            case RemoteSpecialKey.PrintScreen:
                Tap(PrintScreen);
                return true;
            case RemoteSpecialKey.TaskManager:
                Chord(Ctrl, Shift, Esc);
                return true;
            case RemoteSpecialKey.AltTab:
                Chord(Alt, Tab);
                return true;
            default:
                return false;
        }
    }

    private void Tap(ushort scan)
    {
        sink.Key(scan, true);
        sink.Key(scan, false);
    }

    private void Chord(params ushort[] scans)
    {
        foreach (var s in scans) sink.Key(s, true);
        for (var i = scans.Length - 1; i >= 0; i--) sink.Key(scans[i], false);
    }

    public void ReleaseAll()
    {
        List<ushort> keys;
        List<RemoteMouseButton> buttons;
        lock (_gate)
        {
            keys = [.. _keys];
            buttons = [.. _buttons];
            _keys.Clear();
            _buttons.Clear();
        }
        foreach (var k in keys) sink.Key(k, false);
        foreach (var b in buttons) sink.MouseButton(b, false);
    }
}

/// <summary>Win32 SendInput。以当前用户运行：管理员权限的窗口（UAC 提权的程序）收不到注入，是 v1 的已知限制。</summary>
public sealed class SendInputSink : IInputSink
{
    private const int INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    private const uint MOUSEEVENTF_MOVE = 0x0001, MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004,
        MOUSEEVENTF_RIGHTDOWN = 0x0008, MOUSEEVENTF_RIGHTUP = 0x0010, MOUSEEVENTF_MIDDLEDOWN = 0x0020, MOUSEEVENTF_MIDDLEUP = 0x0040,
        MOUSEEVENTF_XDOWN = 0x0080, MOUSEEVENTF_XUP = 0x0100, MOUSEEVENTF_WHEEL = 0x0800, MOUSEEVENTF_HWHEEL = 0x1000,
        MOUSEEVENTF_VIRTUALDESK = 0x4000, MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001, KEYEVENTF_KEYUP = 0x0002, KEYEVENTF_UNICODE = 0x0004, KEYEVENTF_SCANCODE = 0x0008;
    private const ushort VK_RETURN = 0x0D, VK_TAB = 0x09;

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUT
    {
        [FieldOffset(0)] public int type;
        [FieldOffset(8)] public MOUSEINPUT mi;
        [FieldOffset(8)] public KEYBDINPUT ki;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    private static void Send(params INPUT[] inputs) => SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());

    private static INPUT Mouse(uint flags, int dx = 0, int dy = 0, uint data = 0)
        => new() { type = INPUT_MOUSE, mi = new MOUSEINPUT { dx = dx, dy = dy, mouseData = data, dwFlags = flags } };

    private static INPUT Keyboard(ushort vk, ushort scan, uint flags)
        => new() { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags } };

    public void MouseMove(int x, int y)
    {
        // 绝对坐标按虚拟桌面 0..65535 归一化（多显示器、负坐标都覆盖）
        var v = SystemInformation.VirtualScreen;
        var nx = (int)Math.Round((x - v.X) * 65535.0 / Math.Max(1, v.Width - 1));
        var ny = (int)Math.Round((y - v.Y) * 65535.0 / Math.Max(1, v.Height - 1));
        Send(Mouse(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK, nx, ny));
    }

    public void MouseButton(RemoteMouseButton button, bool down)
    {
        var input = button switch
        {
            RemoteMouseButton.Left => Mouse(down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP),
            RemoteMouseButton.Right => Mouse(down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP),
            RemoteMouseButton.Middle => Mouse(down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP),
            RemoteMouseButton.Back => Mouse(down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP, data: 1),
            _ => Mouse(down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP, data: 2),
        };
        Send(input);
    }

    public void Wheel(int dx, int dy)
    {
        if (dy != 0) Send(Mouse(MOUSEEVENTF_WHEEL, data: unchecked((uint)dy)));
        if (dx != 0) Send(Mouse(MOUSEEVENTF_HWHEEL, data: unchecked((uint)dx)));
    }

    public void Key(ushort scanCode, bool down)
    {
        var flags = KEYEVENTF_SCANCODE | (down ? 0 : KEYEVENTF_KEYUP);
        if ((scanCode & 0xFF00) == 0xE000) flags |= KEYEVENTF_EXTENDEDKEY;
        Send(Keyboard(0, (ushort)(scanCode & 0xFF), flags));
    }

    /// <summary>文字（输入法打好的中文、手机键盘）：按 UTF-16 码元逐个发 Unicode 键；换行、制表用真实按键。</summary>
    public void Text(string text)
    {
        var inputs = new List<INPUT>(text.Length * 2);
        foreach (var c in text)
        {
            if (c == '\r') continue;
            if (c is '\n' or '\t')
            {
                var vk = c == '\n' ? VK_RETURN : VK_TAB;
                inputs.Add(Keyboard(vk, 0, 0));
                inputs.Add(Keyboard(vk, 0, KEYEVENTF_KEYUP));
                continue;
            }
            inputs.Add(Keyboard(0, c, KEYEVENTF_UNICODE));
            inputs.Add(Keyboard(0, c, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP));
        }
        if (inputs.Count > 0) Send([.. inputs]);
    }
}

/// <summary>测试用：不碰真实键鼠，每个输入写一行 JSON（e2e 读这个文件断言）。</summary>
public sealed class DryRunInputSink(string path) : IInputSink
{
    private readonly Lock _gate = new();

    private void Log(object entry)
    {
        lock (_gate) File.AppendAllText(path, JsonSerializer.Serialize(entry) + Environment.NewLine);
    }

    public void MouseMove(int x, int y) => Log(new { t = "move", x, y });
    public void MouseButton(RemoteMouseButton button, bool down) => Log(new { t = "button", button = button.ToString(), down });
    public void Wheel(int dx, int dy) => Log(new { t = "wheel", dx, dy });
    public void Key(ushort scanCode, bool down) => Log(new { t = "key", scan = scanCode, down });
    public void Text(string text) => Log(new { t = "text", text });
}

using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Media;
using PrivateChat.Contracts.Remote;

namespace PrivateChat.Agent.Ui;

/// <summary>
/// 托盘界面（WinForms，跑在主线程）。AgentService 在后台线程调用这里的方法，统一切回界面线程执行。
/// 面向的是爸妈：字大、话直白、按钮少；同意请求的窗口默认焦点在"拒绝"上，回车不会误同意。
/// </summary>
public sealed class TrayUi : IAgentUi, IDisposable
{
    private static readonly Font BodyFont = new("Microsoft YaHei UI", 11f);
    private static readonly Font TitleFont = new("Microsoft YaHei UI", 14f, FontStyle.Bold);
    private static readonly Color Accent = Color.FromArgb(0xE8, 0x87, 0x3A);
    private static readonly Color Danger = Color.FromArgb(0xD9, 0x30, 0x25);

    private readonly string _dataDir;
    private readonly Control _invoker;
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _status = new("正在启动") { Enabled = false };
    private readonly ToolStripMenuItem _showCode = new("显示设备码") { Visible = false };
    private readonly ToolStripMenuItem _disconnect = new("断开远程") { Visible = false };
    private readonly ToolStripMenuItem _autostart = new("开机自动启动");
    private readonly Dictionary<AgentState, Icon> _icons = [];
    private readonly HotkeyWindow _hotkey;
    private DeviceCodeForm? _codeForm;
    private BannerForm? _banner;
    private AgentState _state = AgentState.Connecting;

    public ApplicationContext Context { get; } = new();
    public event Action? DisconnectRequested;
    public event Action? SetupRequested;
    public event Action? ExitRequested;

    public TrayUi(string dataDir)
    {
        _dataDir = dataDir;
        _invoker = new Control();
        _invoker.CreateControl();
        _ = _invoker.Handle;

        foreach (var state in Enum.GetValues<AgentState>())
            _icons[state] = MakeIcon(state switch
            {
                AgentState.Online => Accent,
                AgentState.InSession => Danger,
                _ => Color.Gray,
            });

        var menu = new ContextMenuStrip { Font = BodyFont };
        _showCode.Click += (_, _) => _codeForm?.ShowAndActivate();
        _disconnect.Click += (_, _) => DisconnectRequested?.Invoke();
        _autostart.Checked = SafeAutostart();
        _autostart.Click += (_, _) =>
        {
            try
            {
                Autostart.Enabled = !_autostart.Checked;
                _autostart.Checked = Autostart.Enabled;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"设置失败：{ex.Message}", "PrivateChat 远程协助");
            }
        };
        var setup = new ToolStripMenuItem("重新设置…");
        setup.Click += (_, _) =>
        {
            if (MessageBox.Show("重新设置后需要在网页上再输入一次设备码。继续吗？", "PrivateChat 远程协助", MessageBoxButtons.OKCancel) == DialogResult.OK)
                SetupRequested?.Invoke();
        };
        var logs = new ToolStripMenuItem("打开日志目录");
        logs.Click += (_, _) => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_dataDir}\"") { UseShellExecute = true });
        var exit = new ToolStripMenuItem("退出");
        exit.Click += (_, _) => ExitRequested?.Invoke();
        menu.Items.AddRange([_status, new ToolStripSeparator(), _showCode, _disconnect, _autostart, setup, logs, new ToolStripSeparator(), exit]);

        _tray = new NotifyIcon { Icon = _icons[AgentState.Connecting], Text = "PrivateChat 远程协助", ContextMenuStrip = menu, Visible = true };
        _tray.DoubleClick += (_, _) =>
        {
            if (_state == AgentState.Registering) _codeForm?.ShowAndActivate();
        };

        _hotkey = new HotkeyWindow(() => DisconnectRequested?.Invoke());
    }

    private static bool SafeAutostart()
    {
        try { return Autostart.Enabled; }
        catch (Exception) { return false; }
    }

    private void Ui(Action action)
    {
        if (_invoker.IsDisposed) return;
        if (_invoker.InvokeRequired) _invoker.BeginInvoke(action);
        else action();
    }

    private Task<T> UiAsync<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Ui(() =>
        {
            try { tcs.TrySetResult(func()); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });
        return tcs.Task;
    }

    public void Exit() => Ui(() =>
    {
        _tray.Visible = false;
        Context.ExitThread();
    });

    // ---- IAgentUi ----

    public Task<string?> AskSetupAsync(string? error, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Ui(() =>
        {
            if (ct.IsCancellationRequested)
            {
                tcs.TrySetResult(null);
                return;
            }
            var form = new SetupForm(string.IsNullOrEmpty(error) ? null : error);
            form.Show();
            form.Activate();
            var registration = ct.Register(() => Ui(() =>
            {
                if (!form.IsDisposed) form.Close();
            }));
            form.FormClosed += (_, _) =>
            {
                registration.Dispose();
                tcs.TrySetResult(form.DialogResult == DialogResult.OK ? form.Value : null);
            };
        });
        return tcs.Task;
    }

    public void ShowDeviceCode(string? code, string? serverUrl) => Ui(() =>
    {
        if (code is null)
        {
            _codeForm?.Dispose();
            _codeForm = null;
            _showCode.Visible = false;
            return;
        }
        if (_codeForm is null || _codeForm.IsDisposed)
        {
            _codeForm = new DeviceCodeForm();
            _codeForm.ResetRequested += () => SetupRequested?.Invoke();
        }
        _codeForm.SetCode(code, serverUrl ?? "");
        _codeForm.ShowAndActivate();
        _showCode.Visible = true;
    });

    public void SetState(AgentState state, string detail) => Ui(() =>
    {
        _state = state;
        _tray.Icon = _icons[state];
        var text = state switch
        {
            AgentState.NeedsSetup => "未设置",
            AgentState.Registering => "等待添加到家庭",
            AgentState.Connecting => detail,
            AgentState.Online => "在线，可以接受远程协助",
            _ => detail,
        };
        _status.Text = text;
        var tip = $"PrivateChat 远程协助：{text}";
        _tray.Text = tip.Length > 63 ? tip[..63] : tip;
        _disconnect.Visible = state == AgentState.InSession;
        _codeForm?.SetStatus(state == AgentState.Registering ? detail : "");
    });

    public Task<bool?> AskConsentAsync(AgentSessionRequest request, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Ui(() =>
        {
            // 窗口还没建出来请求就已经结束了（对方取消、超时）：不弹
            if (ct.IsCancellationRequested)
            {
                tcs.TrySetResult(null);
                return;
            }
            var form = new ConsentForm(request);
            form.Show();
            form.Activate();
            SystemSounds.Exclamation.Play();
            var registration = ct.Register(() => Ui(() =>
            {
                if (!form.IsDisposed) form.Close();
            }));
            form.FormClosed += (_, _) =>
            {
                registration.Dispose();
                tcs.TrySetResult(form.Answer);
            };
        });
        return tcs.Task;
    }

    public void ShowBanner(AgentSessionRequest? request) => Ui(() =>
    {
        _banner?.Close();
        _banner = null;
        if (request is null) return;
        _banner = new BannerForm(request, () => DisconnectRequested?.Invoke());
        _banner.Show();
        _tray.ShowBalloonTip(5000, "远程协助已开始",
            $"{request.ControllerName} 正在{(request.Mode == RemoteSessionMode.View ? "查看" : "控制")}这台电脑。随时可以点屏幕顶部的「断开」或按 Ctrl+Alt+Shift+Q。", ToolTipIcon.Info);
    });

    public Task<string?> ReadClipboardAsync() => UiAsync(() =>
    {
        try { return Clipboard.ContainsText() ? Clipboard.GetText() : null; }
        catch (System.Runtime.InteropServices.ExternalException) { return null; } // 别的程序正占着剪贴板
    });

    public Task WriteClipboardAsync(string text) => UiAsync(() =>
    {
        Clipboard.SetText(text.Length == 0 ? " " : text);
        return true;
    });

    public void Dispose()
    {
        _hotkey.Dispose();
        _tray.Dispose();
        foreach (var icon in _icons.Values) icon.Dispose();
        _invoker.Dispose();
    }

    /// <summary>托盘图标：圆角方块里画一台显示器。灰 = 未连接，橙 = 在线，红 = 正在被远程。</summary>
    private static Icon MakeIcon(Color color)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = RoundedRect(new Rectangle(1, 1, 30, 30), 7);
            using var fill = new SolidBrush(color);
            g.FillPath(fill, path);
            using var white = new Pen(Color.White, 2.5f);
            g.DrawRectangle(white, 7, 8, 18, 12);
            g.DrawLine(white, 16, 20, 16, 24);
            g.DrawLine(white, 11, 24.5f, 21, 24.5f);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    internal static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    // ---- 窗口 ----

    private static Label Text(string text, Font? font = null, Color? color = null) => new()
    {
        Text = text,
        Font = font ?? BodyFont,
        ForeColor = color ?? SystemColors.ControlText,
        AutoSize = true,
        MaximumSize = new Size(460, 0),
        Margin = new Padding(0, 0, 0, 10),
    };

    private static Button Button(string text, bool primary = false) => new()
    {
        Text = text,
        Font = BodyFont,
        AutoSize = true,
        MinimumSize = new Size(96, 38),
        Padding = new Padding(10, 2, 10, 2),
        Margin = new Padding(8, 0, 0, 0),
        BackColor = primary ? Accent : SystemColors.Control,
        ForeColor = primary ? Color.White : SystemColors.ControlText,
        FlatStyle = primary ? FlatStyle.Flat : FlatStyle.Standard,
    };

    private static FlowLayoutPanel Column() => new() { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };

    private static FlowLayoutPanel Buttons(params Button[] buttons)
    {
        var row = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right, Margin = new Padding(0, 10, 0, 0) };
        row.Controls.AddRange(buttons);
        return row;
    }

    private sealed class SetupForm : Form
    {
        private readonly TextBox _input = new() { Font = BodyFont, Width = 460 };
        public string Value => _input.Text.Trim();

        public SetupForm(string? error)
        {
            Text = "PrivateChat 远程协助 · 设置";
            Font = BodyFont;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(24);
            BackColor = Color.White;
            TopMost = true;

            var col = Column();
            col.Controls.Add(TrayUi.Text("连接到家里的 PrivateChat", TitleFont));
            col.Controls.Add(TrayUi.Text("在 PrivateChat 网页上打开「设备」→「添加电脑」，复制里面的配置串，粘贴到下面："));
            col.Controls.Add(_input);
            if (error is not null) col.Controls.Add(TrayUi.Text(error, color: Danger));
            var ok = Button("下一步", primary: true);
            var cancel = Button("取消");
            ok.Click += (_, _) =>
            {
                DialogResult = DialogResult.OK;
                Close();
            };
            cancel.Click += (_, _) => Close();
            AcceptButton = ok;
            CancelButton = cancel;
            col.Controls.Add(Buttons(ok, cancel));
            Controls.Add(col);
        }
    }

    private sealed class DeviceCodeForm : Form
    {
        private readonly Label _code = new() { Font = new Font("Consolas", 34f, FontStyle.Bold), ForeColor = Accent, AutoSize = true, Margin = new Padding(0, 4, 0, 4) };
        private readonly Label _server = TrayUi.Text("", color: Color.Gray);
        private readonly Label _status = TrayUi.Text("", color: Color.Gray);
        private string _raw = "";
        public event Action? ResetRequested;

        public DeviceCodeForm()
        {
            Text = "PrivateChat 远程协助 · 添加到家庭";
            Font = BodyFont;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(24);
            BackColor = Color.White;

            var col = Column();
            col.Controls.Add(TrayUi.Text("把这台电脑添加到家里", TitleFont));
            col.Controls.Add(TrayUi.Text("在 PrivateChat 网页上打开「设备」→「添加电脑」，输入下面的设备码："));
            col.Controls.Add(_code);
            col.Controls.Add(_server);
            col.Controls.Add(_status);
            var copy = Button("复制设备码", primary: true);
            copy.Click += (_, _) => Clipboard.SetText(_raw);
            var reset = Button("重新设置");
            reset.Click += (_, _) => ResetRequested?.Invoke();
            col.Controls.Add(Buttons(copy, reset));
            Controls.Add(col);
        }

        public void SetCode(string code, string server)
        {
            _raw = code;
            _code.Text = code.Length == 8 ? $"{code[..4]} {code[4..]}" : code;
            _server.Text = server.Length > 0 ? $"服务器：{server}" : "";
        }

        public void SetStatus(string text) => _status.Text = text;

        public void ShowAndActivate()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        // 关掉只是藏起来，托盘菜单"显示设备码"还能打开
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
            }
            base.OnFormClosing(e);
        }
    }

    private sealed class ConsentForm : Form
    {
        private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
        private readonly Label _countdown = TrayUi.Text("", color: Color.Gray);
        private int _left;
        public bool? Answer { get; private set; }

        public ConsentForm(AgentSessionRequest request)
        {
            Text = "PrivateChat 远程协助";
            Font = BodyFont;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(24);
            BackColor = Color.White;
            TopMost = true;
            ShowInTaskbar = true;

            var view = request.Mode == RemoteSessionMode.View;
            var col = Column();
            col.Controls.Add(TrayUi.Text(view ? $"{request.ControllerName} 想看看你的屏幕" : $"{request.ControllerName} 想远程控制这台电脑", TitleFont));
            col.Controls.Add(TrayUi.Text(view
                ? "同意后对方能看到你的屏幕，但不能操作。"
                : "同意后对方能看到你的屏幕，并用鼠标键盘帮你操作。"));
            col.Controls.Add(TrayUi.Text("随时可以点屏幕顶部红条上的「断开」，或按 Ctrl+Alt+Shift+Q 结束。", color: Color.DimGray));
            col.Controls.Add(_countdown);
            var accept = Button("同意", primary: true);
            var decline = Button("拒绝");
            accept.Click += (_, _) =>
            {
                Answer = true;
                Close();
            };
            decline.Click += (_, _) =>
            {
                Answer = false;
                Close();
            };
            CancelButton = decline;
            col.Controls.Add(Buttons(accept, decline));
            Controls.Add(col);
            Shown += (_, _) => decline.Focus();

            _left = Math.Max(1, request.ExpiresInSeconds);
            UpdateCountdown();
            _timer.Tick += (_, _) =>
            {
                _left--;
                UpdateCountdown();
                if (_left <= 0) Close();
            };
            _timer.Start();
        }

        private void UpdateCountdown() => _countdown.Text = $"{_left} 秒后自动拒绝";

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _timer.Dispose();
            base.OnFormClosed(e);
        }
    }

    /// <summary>屏幕顶部正中的红条：不抢焦点、不进任务栏、始终在最上面。</summary>
    private sealed class BannerForm : Form
    {
        public BannerForm(AgentSessionRequest request, Action disconnect)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Danger;
            StartPosition = FormStartPosition.Manual;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(14, 6, 6, 6);

            var row = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false, BackColor = Danger };
            row.Controls.Add(new Label
            {
                Text = $"● 正在被 {request.ControllerName} 远程{(request.Mode == RemoteSessionMode.View ? "查看" : "控制")}",
                Font = BodyFont,
                ForeColor = Color.White,
                AutoSize = true,
                Margin = new Padding(0, 7, 12, 0),
            });
            var button = new Button
            {
                Text = "断开",
                Font = BodyFont,
                AutoSize = true,
                BackColor = Color.White,
                ForeColor = Danger,
                FlatStyle = FlatStyle.Flat,
                Padding = new Padding(8, 0, 8, 0),
            };
            button.FlatAppearance.BorderSize = 0;
            button.Click += (_, _) => disconnect();
            row.Controls.Add(button);
            Controls.Add(row);
            Load += (_, _) =>
            {
                var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
                Location = new Point(area.X + (area.Width - Width) / 2, area.Y);
            };
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                const int WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                return cp;
            }
        }
    }

    /// <summary>全局快捷键 Ctrl+Alt+Shift+Q：立即断开。</summary>
    private sealed class HotkeyWindow : NativeWindow, IDisposable
    {
        private const int WM_HOTKEY = 0x0312, Id = 0x5043;
        private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_NOREPEAT = 0x4000;
        private readonly Action _pressed;

        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

        public HotkeyWindow(Action pressed)
        {
            _pressed = pressed;
            CreateHandle(new CreateParams());
            if (!RegisterHotKey(Handle, Id, MOD_CONTROL | MOD_ALT | MOD_SHIFT | MOD_NOREPEAT, (uint)Keys.Q))
                AgentLog.Warn("快捷键 Ctrl+Alt+Shift+Q 注册失败（被别的程序占用了），仍可以点红条上的「断开」");
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY && m.WParam == Id) _pressed();
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            UnregisterHotKey(Handle, Id);
            DestroyHandle();
        }
    }
}

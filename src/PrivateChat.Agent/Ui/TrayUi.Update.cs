namespace PrivateChat.Agent.Ui;

public sealed partial class TrayUi
{
    private UpdateForm? _updateForm;

    public void BindUpdateCheck(Func<Func<string, long, CancellationToken, Task<bool>>, CancellationToken, Task<string>> check, CancellationToken stopping)
    {
        _checkUpdate.Enabled = true;
        _checkUpdate.Click += async (_, _) =>
        {
            if (stopping.IsCancellationRequested) return;
            if (_updateForm is not null)
            {
                if (!_updateForm.IsDisposed) _updateForm.Activate();
                return;
            }
            using var form = new UpdateForm(_invoker);
            _updateForm = form;
            try { await form.RunAsync(check, stopping); }
            finally { _updateForm = null; }
        };
    }

    private sealed class UpdateForm : Form
    {
        private readonly Control _dispatcher;
        private readonly Label _status = TrayUi.Text("正在检查更新…", TitleFont);
        private readonly Label _details = TrayUi.Text("", color: Color.DimGray);
        private readonly Button _install = Button("立即升级", primary: true);
        private readonly Button _close = Button("关闭");
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource<bool>? _choice;
        private CancellationTokenSource? _operation;

        public UpdateForm(Control dispatcher)
        {
            _dispatcher = dispatcher;
            Name = "UpdateForm";
            Text = "PrivateChat 远程协助 · 检查更新";
            Font = BodyFont;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(24);
            BackColor = Color.White;

            var version = TrayUi.Text($"当前版本 {AgentService.Version}", color: Color.DimGray);
            version.Name = "CurrentVersion";
            _status.Name = "UpdateStatus";
            _details.Name = "UpdateDetails";
            _details.Visible = false;
            _install.Name = "InstallUpdate";
            _install.Visible = false;
            _install.Click += (_, _) =>
            {
                if (_choice is not { } choice) return;
                _choice = null;
                _status.Text = "正在下载并核对更新…";
                _details.Text = "下载期间可以关闭窗口取消。开始安装后，程序会完成更新并自动重启。";
                _install.Visible = false;
                _close.Text = "关闭";
                choice.TrySetResult(true);
            };
            _close.Name = "CloseUpdate";
            _close.Click += (_, _) => Close();
            AcceptButton = CancelButton = _close;
            var col = Column();
            col.Controls.Add(_status);
            col.Controls.Add(version);
            col.Controls.Add(_details);
            col.Controls.Add(Buttons(_close, _install));
            Controls.Add(col);
            Shown += (_, _) => _close.Focus();
        }

        public async Task RunAsync(Func<Func<string, long, CancellationToken, Task<bool>>, CancellationToken, Task<string>> check, CancellationToken stopping)
        {
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            _operation = operation;
            Show();
            Activate();
            try
            {
                var result = await check(ConfirmInstallAsync, operation.Token);
                if (!operation.IsCancellationRequested && !IsDisposed) ShowResult(result);
            }
            catch (OperationCanceledException) when (operation.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (!operation.IsCancellationRequested && !IsDisposed)
                {
                    AgentLog.Warn($"手动更新未完成：{ex.Message}");
                    ShowResult("更新未完成", ex.Message);
                }
            }
            if (operation.IsCancellationRequested && !IsDisposed) Close();
            try { await _closed.Task.WaitAsync(stopping); }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
            finally { _operation = null; }
        }

        private Task<bool> ConfirmInstallAsync(string version, long bytes, CancellationToken ct) =>
            _dispatcher.InvokeAsync<bool>(async token =>
            {
                token.ThrowIfCancellationRequested();
                if (IsDisposed) return false;
                _choice = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _status.Text = $"发现新版本 {version}";
                var size = bytes < 1024 ? $"{bytes} B" : bytes < 1024 * 1024 ? $"{bytes / 1024d:0.#} KB" : $"{bytes / (1024d * 1024):0.#} MB";
                _details.Text = $"下载大小 {size}。升级后程序会自动重启。";
                _details.Visible = true;
                _install.Visible = true;
                _close.Text = "稍后";
                _close.Focus();
                return await _choice.Task.WaitAsync(token);
            }, ct);

        private void ShowResult(string message, string? detail = null)
        {
            _choice = null;
            _status.Text = message;
            _details.Text = detail ?? "";
            _details.Visible = !string.IsNullOrWhiteSpace(detail);
            _install.Visible = false;
            _close.Text = "关闭";
            _close.Focus();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            // 等待确认时关窗等同“稍后”，让服务记住本次选择。检查或下载途中关窗则取消进行中的工作。
            if (_choice is { } choice) choice.TrySetResult(false);
            else _operation?.Cancel();
            _closed.TrySetResult();
            base.OnFormClosed(e);
        }
    }
}

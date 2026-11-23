namespace PrivateChat.Agent.Ui;

public sealed partial class TrayUi
{
    private UpdateForm? _updateForm;

    public void BindUpdateCheck(Func<Func<string, long, CancellationToken, Task<bool>>, CancellationToken, Task<string>> check, CancellationToken stopping)
        => BindUpdateCheckWithProgress((confirm, ct, _) => check(confirm, ct), stopping);

    public void BindUpdateCheckWithProgress(Func<Func<string, long, CancellationToken, Task<bool>>, CancellationToken, IProgress<UpdateProgress>, Task<string>> check,
        CancellationToken stopping)
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
        private readonly ProgressBar _bar = new() { Name = "DownloadProgress", Width = 320, Height = 12, Visible = false, AccessibleName = "下载进度" };
        private readonly Label _transfer = TrayUi.Text("");
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
            _transfer.Name = "DownloadTransfer";
            _transfer.Visible = false;
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
            col.Controls.Add(_bar);
            col.Controls.Add(_transfer);
            col.Controls.Add(_details);
            col.Controls.Add(Buttons(_close, _install));
            Controls.Add(col);
            Shown += (_, _) => _close.Focus();
        }

        public async Task RunAsync(Func<Func<string, long, CancellationToken, Task<bool>>, CancellationToken, IProgress<UpdateProgress>, Task<string>> check,
            CancellationToken stopping)
        {
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            _operation = operation;
            Show();
            Activate();
            var finished = false;
            var progress = new Progress<UpdateProgress>(p =>
            {
                if (!finished && !IsDisposed && !operation.IsCancellationRequested) ShowProgress(p);
            });
            try
            {
                var result = await check(ConfirmInstallAsync, operation.Token, progress);
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
            finally { finished = true; }
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
                _details.Text = $"下载大小 {FormatBytes(bytes)}。升级后程序会自动重启。";
                _details.Visible = true;
                _install.Visible = true;
                _close.Text = "稍后";
                _close.Focus();
                return await _choice.Task.WaitAsync(token);
            }, ct);

        private static string FormatBytes(long bytes) => bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024d:0.#} KB",
            _ => $"{bytes / (1024d * 1024):0.#} MB",
        };

        private void ShowProgress(UpdateProgress p)
        {
            var downloading = p.Stage is UpdateStage.Connecting or UpdateStage.Downloading;
            _bar.Visible = downloading;
            _transfer.Visible = downloading;
            if (downloading)
            {
                var percent = p.TotalBytes > 0 ? (int)Math.Clamp(p.BytesReceived * 100d / p.TotalBytes, 0d, 100d) : 0;
                _bar.Value = percent;
                var speed = p.BytesPerSecond is { } rate ? $" · {FormatBytes(rate)}/秒" : "";
                _transfer.Text = $"已接收 {p.BytesReceived:N0} / {p.TotalBytes:N0} 字节（{percent}%）{speed}";
            }
            _status.Text = p.Stage switch
            {
                UpdateStage.CheckingCache => "正在核对本机已有的安装包…",
                UpdateStage.Connecting => "正在等待下载服务器发送数据…",
                UpdateStage.Downloading => "正在下载安装包…",
                UpdateStage.Verifying => "正在核对安装包的大小和 SHA-256…",
                UpdateStage.CacheHit => "本机已有安装包，核对通过",
                UpdateStage.Verified => "下载完成，核对通过",
                UpdateStage.Installing => "正在安装更新…",
                _ => _status.Text,
            };
            _details.Text = p.Stage switch
            {
                UpdateStage.Connecting => $"已等待 {(p.ElapsedMs ?? 0) / 1000} 秒。关闭窗口可取消下载。",
                UpdateStage.Downloading => "关闭窗口可取消下载。",
                UpdateStage.CacheHit => "无需重新下载。",
                UpdateStage.Installing when p.BytesReceived == 0 => "正在使用本机缓存安装。完成后程序会自动重启。",
                UpdateStage.Installing => "完成后程序会自动重启。",
                _ => "",
            };
            _details.Visible = _details.Text.Length > 0;
        }

        private void ShowResult(string message, string? detail = null)
        {
            _choice = null;
            _status.Text = message;
            _details.Text = detail ?? "";
            _details.Visible = !string.IsNullOrWhiteSpace(detail);
            _transfer.Visible = _bar.Visible = false;
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

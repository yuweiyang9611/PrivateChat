using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using PrivateChat.Agent.Platform;

namespace PrivateChat.Agent.Service;

/// <summary>
/// 装成系统服务 / 卸载 / 重新设置（都要管理员；没提权的话以管理员身份重新起自己，电脑前的人点一次 UAC）：
///   --install [配置串]   拷到 Program Files、核对发布签名、建好只有 SYSTEM 和管理员能动的数据目录、打开 Ctrl+Alt+Del 的策略、
///                        装服务并启动、经管理管道把配置串交给服务（之后托盘会弹设备码）、登记到"设置 → 应用"里
///   --uninstall          停掉删掉服务、还原策略、删掉程序和数据
///   --configure          重新输入配置串（托盘"重新设置"起的）
/// 安全上要注意的（见 docs/DESIGN.md"远程协助：UAC、锁屏"）：
/// - 先拷进 Program Files 再核对那一份的签名：从"下载"文件夹运行时，点 UAC 的工夫里同一个用户的程序可以把那个 exe 换掉；
/// - ProgramData 下的目录可能是普通用户先建好的（他是所有者，能改权限）或者是个目录联接：这种的挪开、重建，所有者设成管理员组；
/// - 原来的 SoftwareSASGeneration 记在 install.json 里，卸载时还原（原来没设就删掉）。
/// </summary>
internal static class Installer
{
    private const string Title = "PrivateChat 远程协助";
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PrivateChatAgent";
    private static string InstallInfoPath => Path.Combine(ServicePaths.DataDir, "install.json");

    public static int Run(string[] args)
    {
        var command = args[0];
        if (!ServiceControl.IsElevatedAdministrator())
        {
            try
            {
                var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
                foreach (var arg in args) start.ArgumentList.Add(arg);
                Process.Start(start);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                MessageBox.Show("要管理员同意才能继续。", Title);
            }
            return 0;
        }
        AgentLog.Init(Path.Combine(Path.GetTempPath(), "PrivateChat"), console: false, fileName: "install.log");
        ApplicationConfiguration.Initialize();
        switch (command)
        {
            case "--install":
                Application.Run(new InstallForm(args.Length > 1 ? args[1] : null));
                return 0;
            case "--uninstall":
                if (MessageBox.Show("卸载后这台电脑就不能再被远程协助了（要再用得重新安装、重新添加）。继续吗？", Title, MessageBoxButtons.OKCancel) != DialogResult.OK) return 0;
                try
                {
                    var leftovers = Uninstall();
                    MessageBox.Show(leftovers ? "已卸载。还有几个文件正被占用，重启后会自动删掉。" : "已卸载。", Title);
                }
                catch (Exception ex)
                {
                    AgentLog.Error("卸载出错", ex);
                    MessageBox.Show($"卸载没有完成：{ex.Message}", Title);
                }
                return 0;
            case "--configure":
                Application.Run(new ConfigureForm());
                return 0;
            default:
                return 2;
        }
    }

    /// <summary>安装的全过程（在后台线程上跑），每一步 progress 一句。setup：配置串（null = 服务已经设置过的话不改）。</summary>
    public static async Task InstallAsync(string? setup, Action<string> progress, CancellationToken ct)
    {
        if (setup is not null && !AgentConfig.TryParseSetup(setup, out _, out _, out _)) throw new InvalidOperationException("配置串或服务器地址不对，请从网页「添加电脑」里复制");

        if (ServiceControl.Exists(ServicePaths.ServiceName))
        {
            progress("停下已经装着的服务……");
            ServiceControl.Stop(ServicePaths.ServiceName, TimeSpan.FromSeconds(30));
        }

        progress("拷贝程序到 Program Files……");
        Directory.CreateDirectory(ServicePaths.ProgramDir);
        var self = Environment.ProcessPath!;
        if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(ServicePaths.ProgramExe), StringComparison.OrdinalIgnoreCase))
            CopyReplacing(self, ServicePaths.ProgramExe);
        // 拷过去的那一份（不是"下载"里的原件）带着我们的发布签名才装
        if (Environment.GetEnvironmentVariable("PRIVATECHAT_AGENT_TEST") == "1" && Environment.GetEnvironmentVariable("PRIVATECHAT_AGENT_UNSIGNED_INSTALL") == "1")
        {
            progress("（测试：不核对发布签名）");
        }
        else
        {
            var signed = ReleaseSignature.Verify(ServicePaths.ProgramExe);
            progress($"发布签名核对无误（版本 {signed.Version}）");
        }

        progress("准备数据目录……");
        SafeDirectory.Ensure(Path.GetDirectoryName(ServicePaths.DataDir)!);
        SafeDirectory.Ensure(ServicePaths.DataDir);
        Directory.CreateDirectory(ServicePaths.LogDir);
        Directory.CreateDirectory(ServicePaths.RuntimeDir);

        progress("打开远程 Ctrl+Alt+Del 的系统策略……");
        var info = File.Exists(InstallInfoPath) ? JsonNode.Parse(File.ReadAllText(InstallInfoPath)) as JsonObject ?? [] : [];
        if (!info.ContainsKey("sasOriginal")) info["sasOriginal"] = SasPolicy.Read();
        info["version"] = AgentService.Version;
        info["installedAt"] = DateTimeOffset.Now.ToString("O");
        File.WriteAllText(InstallInfoPath, info.ToJsonString());
        SasPolicy.Write(SasPolicy.Allowing(info["sasOriginal"]?.GetValue<int?>()));

        progress("安装服务……");
        var commandLine = $"{SessionLauncher.Quote(ServicePaths.ProgramExe)} --service";
        if (!ServiceControl.Exists(ServicePaths.ServiceName))
            ServiceControl.Create(ServicePaths.ServiceName, "PrivateChat 远程协助", commandLine, autoStart: true,
                "家人远程协助这台电脑（PrivateChat）。锁屏、管理员确认（UAC）时也能远程。");
        ServiceControl.Configure(ServicePaths.ServiceName, commandLine);
        ServiceControl.SetEnvironment(ServicePaths.ServiceName, new Dictionary<string, string> { ["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = ServicePaths.RuntimeDir });
        ServiceControl.Start(ServicePaths.ServiceName);

        if (setup is not null)
        {
            progress("把配置串交给服务……");
            var peer = await AdminPipe.ConnectAsync(TimeSpan.FromSeconds(30), ct);
            await peer.CallAsync("setup", new JsonObject { ["text"] = setup }, TimeSpan.FromSeconds(10), ct);
            await peer.Channel.DisposeAsync();
        }

        progress("登记到「设置 → 应用」……");
        using (var key = Registry.LocalMachine.CreateSubKey(UninstallKey))
        {
            key.SetValue("DisplayName", "PrivateChat 远程协助");
            key.SetValue("DisplayVersion", AgentService.Version);
            key.SetValue("Publisher", "PrivateChat");
            key.SetValue("DisplayIcon", ServicePaths.ProgramExe);
            key.SetValue("InstallLocation", ServicePaths.ProgramDir);
            key.SetValue("UninstallString", $"{SessionLauncher.Quote(ServicePaths.ProgramExe)} --uninstall");
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            key.SetValue("EstimatedSize", (int)(new FileInfo(ServicePaths.ProgramExe).Length / 1024), RegistryValueKind.DWord);
        }

        // 这个用户原来按老方式装的（开机自启 + 托盘）：去掉自启；正在跑的那个看到服务后自己退出
        try { Autostart.Enabled = false; }
        catch (Exception) { }
        progress("装好了。");
    }

    /// <summary>卸载：停掉删掉服务、还原策略、删程序和数据。返回 true = 有文件正被占用，重启后删。</summary>
    public static bool Uninstall()
    {
        if (ServiceControl.Exists(ServicePaths.ServiceName))
        {
            ServiceControl.Stop(ServicePaths.ServiceName, TimeSpan.FromSeconds(30));
            ServiceControl.Delete(ServicePaths.ServiceName);
            for (var i = 0; i < 50 && ServiceControl.Exists(ServicePaths.ServiceName); i++) Thread.Sleep(200);
        }
        try
        {
            if (File.Exists(InstallInfoPath) && JsonNode.Parse(File.ReadAllText(InstallInfoPath)) is JsonObject info && info.ContainsKey("sasOriginal"))
                SasPolicy.Write(info["sasOriginal"]?.GetValue<int?>());
        }
        catch (Exception ex)
        {
            AgentLog.Error("还原 SoftwareSASGeneration 失败", ex);
        }
        Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
        try
        {
            if (Directory.Exists(ServicePaths.DataDir)) Directory.Delete(ServicePaths.DataDir, recursive: true);
            var parent = Path.GetDirectoryName(ServicePaths.DataDir)!;
            if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any()) Directory.Delete(parent);
        }
        catch (Exception ex)
        {
            AgentLog.Error("删数据目录失败", ex);
        }
        var leftovers = false;
        if (Directory.Exists(ServicePaths.ProgramDir))
        {
            foreach (var file in Directory.EnumerateFiles(ServicePaths.ProgramDir))
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception)
                {
                    // 正在运行的就是它（从"设置 → 应用"卸载时）：重启后删
                    Native.MoveFileExW(file, null, Native.MOVEFILE_DELAY_UNTIL_REBOOT);
                    leftovers = true;
                }
            }
            try { Directory.Delete(ServicePaths.ProgramDir); }
            catch (Exception)
            {
                Native.MoveFileExW(ServicePaths.ProgramDir, null, Native.MOVEFILE_DELAY_UNTIL_REBOOT);
                leftovers = true;
            }
        }
        return leftovers;
    }

    private static void CopyReplacing(string source, string target)
    {
        try
        {
            File.Copy(source, target, overwrite: true);
        }
        catch (IOException)
        {
            // 旧的还被占着（服务刚停、界面进程还没退）：改个名让开
            var old = $"{target}.{DateTime.Now.Ticks}.old";
            File.Move(target, old);
            Native.MoveFileExW(old, null, Native.MOVEFILE_DELAY_UNTIL_REBOOT);
            File.Copy(source, target, overwrite: true);
        }
    }

    private sealed class InstallForm : Form
    {
        private readonly TextBox _setup = new() { Width = 520 };
        private readonly TextBox _log = new() { Multiline = true, ReadOnly = true, Height = 150, Width = 520, ScrollBars = ScrollBars.Vertical };
        private readonly Button _install = new() { Text = "安装", AutoSize = true };

        public InstallForm(string? setup)
        {
            Text = $"{Title} · 安装";
            Font = new Font("Microsoft YaHei UI", 10.5f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(20);
            var column = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
            column.Controls.Add(new Label
            {
                AutoSize = true,
                MaximumSize = new Size(520, 0),
                Text = "把远程协助装成系统服务：以后锁屏、弹出管理员确认（UAC）时家人也能远程帮忙，开机还没登录也在线。\r\n\r\n" +
                       "在 PrivateChat 网页上打开「设备」→「添加电脑」，复制配置串粘贴到下面（已经添加过的电脑也要粘一次，装好后要在网页上再输一次设备码）：",
            });
            _setup.Text = setup ?? "";
            column.Controls.Add(_setup);
            column.Controls.Add(_install);
            column.Controls.Add(_log);
            Controls.Add(column);
            AcceptButton = _install;
            _install.Click += async (_, _) =>
            {
                _install.Enabled = false;
                _setup.Enabled = false;
                var text = _setup.Text.Trim();
                try
                {
                    await Task.Run(() => InstallAsync(text.Length == 0 ? null : text, line => BeginInvoke(() => _log.AppendText(line + "\r\n")), CancellationToken.None));
                    _log.AppendText("\r\n托盘里会弹出设备码，到网页上输入就好了。这个窗口可以关了。\r\n");
                }
                catch (Exception ex)
                {
                    AgentLog.Error("安装出错", ex);
                    _log.AppendText($"\r\n安装没有完成：{ex.Message}\r\n");
                    _install.Enabled = _setup.Enabled = true;
                }
            };
        }
    }

    private sealed class ConfigureForm : Form
    {
        public ConfigureForm()
        {
            Text = $"{Title} · 重新设置";
            Font = new Font("Microsoft YaHei UI", 10.5f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(20);
            var column = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
            var input = new TextBox { Width = 520 };
            var status = new Label { AutoSize = true, MaximumSize = new Size(520, 0) };
            var ok = new Button { Text = "确定", AutoSize = true };
            column.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(520, 0), Text = "在 PrivateChat 网页上打开「设备」→「添加电脑」，复制配置串粘贴到下面。之后托盘会弹出设备码。" });
            column.Controls.Add(input);
            column.Controls.Add(ok);
            column.Controls.Add(status);
            Controls.Add(column);
            AcceptButton = ok;
            ok.Click += async (_, _) =>
            {
                ok.Enabled = false;
                try
                {
                    var text = input.Text.Trim();
                    if (!AgentConfig.TryParseSetup(text, out _, out _, out _)) throw new InvalidOperationException("配置串或服务器地址不对，请从网页「添加电脑」里复制");
                    var peer = await AdminPipe.ConnectAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
                    await peer.CallAsync("setup", new JsonObject { ["text"] = text }, TimeSpan.FromSeconds(10), CancellationToken.None);
                    await peer.Channel.DisposeAsync();
                    status.Text = "好了，托盘里会弹出设备码。这个窗口可以关了。";
                }
                catch (Exception ex)
                {
                    status.Text = $"没设置成：{ex.Message}";
                    ok.Enabled = true;
                }
            };
        }
    }

    private static class Native
    {
        public const int MOVEFILE_DELAY_UNTIL_REBOOT = 0x4;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] public static extern bool MoveFileExW(string existing, string? newName, int flags);
    }
}

/// <summary>
/// ProgramData 下只给 SYSTEM 和管理员的目录：普通用户能在 ProgramData 里建目录（建的人是所有者、能改权限），
/// 所以已经存在的先看一眼——是目录联接 / 符号链接、或者所有者不是管理员组 / SYSTEM / TrustedInstaller 的，挪开（改名）重建。
/// 新建的：所有者管理员组，不继承上层权限，SYSTEM 和管理员完全控制。
/// </summary>
internal static class SafeDirectory
{
    public static void Ensure(string path)
    {
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        if (Directory.Exists(path) || File.Exists(path))
        {
            var info = new DirectoryInfo(path);
            var trusted = (info.Attributes & FileAttributes.ReparsePoint) == 0 && File.GetAttributes(path).HasFlag(FileAttributes.Directory)
                && info.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier owner
                && (owner == admins || owner == system || owner.Value == "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");
            if (!trusted)
            {
                var aside = $"{path}.{DateTime.Now.Ticks}.untrusted";
                AgentLog.Warn($"{path} 不是管理员建的（或者是个链接），挪到 {aside}");
                Directory.Move(path, aside);
            }
        }
        var security = new DirectorySecurity();
        security.SetOwner(admins);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { system, admins })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
        var directory = new DirectoryInfo(path);
        if (directory.Exists) directory.SetAccessControl(security);
        else directory.Create(security);
    }
}

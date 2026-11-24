using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using PrivateChat.Agent.Platform;

namespace PrivateChat.Agent.Diagnostics;

/// <summary>
/// 锁屏 / UAC 实验（docs/DESIGN.md"远程协助：UAC、锁屏"第一步）：在一台真电脑上看清楚以 SYSTEM 跑的进程在 UAC 提示、Ctrl+Alt+Del、锁屏时
/// 截得到什么、点不点得到，正式改成服务之前先把没把握的几件事弄清楚。
///   PrivateChat.Agent.exe --secure-desktop-test     双击或命令行跑：要管理员同意，弹一个窗口，点"开始"后全自动（约 3 分钟），
///                                                  最后锁屏要电脑前的人输密码解锁（可以不做这一步）；结果打包成桌面上的 zip
/// 过程：把自己拷到 %ProgramFiles%\PrivateChatDesktopTest（普通用户在那里建不了东西），临时装一个服务（<see cref="ServiceName"/>，LocalSystem），
/// 服务在控制台会话里起几个子进程——SYSTEM 工作进程、去掉特权的 SYSTEM 工作进程、专门测"线程切桌面"的 SYSTEM 进程、以登录用户运行的界面进程——
/// 再一步步：平常桌面 → 弹 UAC 再自动按 Esc 点掉 → 三种方式发 Ctrl+Alt+Del、每次按 Esc 返回 → 锁屏、按空格出密码框、打几个字再删掉 → 等人解锁。
/// 每一步记下输入桌面、锁屏标志、焦点在哪个窗口、GDI / 桌面复制截到的画面（存 PNG）、键鼠送没送到。
///
/// 收尾（不管怎么结束都要做到）：
/// - Ctrl+Alt+Del 要临时打开 SoftwareSASGeneration：服务只在第 3 步前后打开、做完立刻还原；原值记在注册表 HKLM\SOFTWARE\PrivateChat\DesktopTest
///   （不在临时目录里，删目录不会把它弄丢），没还原成功就不删这条记录；
/// - 服务停的时候（含关机）在控制回调里先还原、再删掉自己（<see cref="ServiceHost.OnStopping"/>），然后才收子进程、写报告；
/// - 服务是"自动启动"的：中途断电、重启了，开机时它发现不是这次开机开始的实验，就只做收尾（还原、删目录、删自己）；
/// - 窗口这边收尾后再核对一遍（服务没了、策略是原来的值），不对就把手动恢复的命令显示出来；
/// - 下次再运行先清掉上次没清干净的。
/// </summary>
internal static partial class SecureDesktopTest
{
    public const string ServiceName = "PrivateChatDesktopTest";
    private const string Title = "PrivateChat 锁屏 / UAC 实验";
    private const string StateKey = @"SOFTWARE\PrivateChat\DesktopTest";

    private static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PrivateChatDesktopTest");
    private static string BinDir => Path.Combine(Root, "bin");
    private static string ReportDir => Path.Combine(Root, "report");
    private static string RuntimeDir => Path.Combine(Root, "runtime");

    /// <summary>这次开机的时刻（毫秒，Unix 时间）：和记下的比，看实验是不是这次开机开始的。</summary>
    private static long BootTime => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - Environment.TickCount64;

    /// <summary>入口：没提权就以管理员身份重新起自己（把用户的桌面路径带过去，结果放那里），提权了就开窗口。</summary>
    public static int Launch(string[] args)
    {
        var i = Array.IndexOf(args, "--out");
        var outDir = i >= 0 && i + 1 < args.Length ? args[i + 1] : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!ServiceControl.IsElevatedAdministrator())
        {
            try
            {
                var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
                start.ArgumentList.Add("--secure-desktop-test");
                start.ArgumentList.Add("--out");
                start.ArgumentList.Add(outDir);
                Process.Start(start);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                MessageBox.Show("要管理员同意才能做这个实验（要临时装一个系统服务）。", Title);
            }
            return 0;
        }
        AgentLog.Init(Path.Combine(Path.GetTempPath(), "PrivateChatDesktopTest"), console: false, fileName: "launcher.log");
        using var single = new Mutex(true, @"Global\PrivateChatDesktopTest.Launcher", out var first);
        if (!first)
        {
            MessageBox.Show("实验的窗口已经开着了。", Title);
            return 0;
        }
        ApplicationConfiguration.Initialize();
        Application.Run(new LauncherForm(outDir));
        return 0;
    }

    private sealed class LauncherForm : Form
    {
        private readonly string _outDir;
        private readonly TextBox _log;
        private readonly Button _start;
        private readonly CheckBox _lockStep;
        private readonly System.Windows.Forms.Timer _timer = new() { Interval = 300 };
        private long _progressOffset;
        private bool _running;
        private bool _serviceSeenRunning;
        private DateTime _startedAt;
        /// <summary>正在做的准备 / 收尾：关窗口要等它做完。</summary>
        private Task? _busy;

        public LauncherForm(string outDir)
        {
            _outDir = outDir;
            Text = Title;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96f, 96f);
            Width = 760;
            Height = 680;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9.5f);
            // 只读的多行文本框：高分屏下字变大了也能滚动着看全（Label 固定高度会把后面几行裁掉）
            var intro = new TextBox
            {
                Dock = DockStyle.Top,
                Height = 260,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BorderStyle = BorderStyle.None,
                BackColor = SystemColors.Control,
                TabStop = false,
                Text = "看看以后远程协助能不能操作管理员确认（UAC）、锁屏和 Ctrl+Alt+Del。点\"开始\"后自动做下面这些，全程约 3 分钟：\r\n" +
                       "1. 临时装一个系统服务（做完自动删掉）；\r\n" +
                       "2. 弹一次管理员确认（UAC），然后自动点掉——要是任务栏上只出现一个闪烁的盾牌按钮，点一下它；\r\n" +
                       "3. 发三次 Ctrl+Alt+Del，每次自动返回；\r\n" +
                       "4. 锁屏，在密码框里自动打几个字再删掉，然后你输入密码（或 PIN）解锁。\r\n" +
                       "期间别动鼠标和键盘。UAC 提示或 Ctrl+Alt+Del 界面过了半分钟还没自己消失：点\"否\"或按 Esc，实验会接着做。\r\n" +
                       "结果（各个界面的截图、平常桌面只留小缩略图）打包成桌面上的一个 zip，发给我就行。卡住了直接关掉这个窗口，会自动清理。",
            };
            _lockStep = new CheckBox { Dock = DockStyle.Top, Height = 30, Padding = new Padding(12, 0, 0, 0), Checked = true, Text = "包括第 4 步锁屏（最后要输入这台电脑的登录密码或 PIN）" };
            if (AutoLogon())
            {
                _lockStep.Checked = false;
                _lockStep.Text += "——这台电脑设了自动登录，不知道密码就别勾";
            }
            _start = new Button { Text = "开始", Dock = DockStyle.Top, Height = 40 };
            _start.Click += (_, _) => Begin();
            _log = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 9.5f) };
            Controls.Add(_log);
            Controls.Add(_start);
            Controls.Add(_lockStep);
            Controls.Add(intro);
            _timer.Tick += (_, _) => Poll();
            Shown += async (_, _) =>
            {
                if (!Leftovers()) return;
                _start.Enabled = false;
                Write("上次的实验没收尾干净（多半是中途关机了），先收拾：上次的结果打包到桌面上，再清理……");
                _busy = Task.Run(() => FinishAndCollect(_outDir, Write));
                await _busy;
                _busy = null;
                _start.Enabled = true;
            };
        }

        private void Write(string line)
        {
            if (InvokeRequired)
            {
                BeginInvoke(() => Write(line));
                return;
            }
            _log.AppendText($"{DateTime.Now:HH:mm:ss} {line}\r\n");
            AgentLog.Info(line);
        }

        private async void Begin()
        {
            // 要在这台电脑前直接做：远程桌面里、或者现在屏幕上是别的家人的账号，UAC、锁屏都会落到别人身上（或者根本看不到）
            var here = ConsoleSession.CurrentProcessSession();
            if (SystemInformation.TerminalServerSession || here != ConsoleSession.ActiveId())
            {
                Write("请在这台电脑前直接运行（不要用远程桌面，也不要在切换用户后的后台账号里运行）。");
                return;
            }
            _start.Enabled = false;
            _lockStep.Enabled = false;
            Write("准备……");
            var includeLock = _lockStep.Checked;
            _busy = Task.Run(() => Prepare(_outDir, includeLock, Write));
            try
            {
                await _busy;
                _running = true;
                _startedAt = DateTime.Now;
                _timer.Start();
                Write("临时服务已启动，开始实验");
            }
            catch (Exception ex)
            {
                Write($"准备失败：{ex.Message}");
                _busy = Task.Run(() => CleanUp(Write));
                await _busy;
            }
            finally
            {
                _busy = null;
            }
        }

        private void Poll()
        {
            // 让界面进程能把自己的小窗口放到前台：后台进程申请管理员权限时，UAC 不直接弹出来，只在任务栏上闪
            Native.AllowSetForegroundWindow(Native.ASFW_ANY);
            try
            {
                var progress = Path.Combine(ReportDir, "progress.jsonl");
                if (File.Exists(progress))
                {
                    using var stream = new FileStream(progress, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    stream.Seek(_progressOffset, SeekOrigin.Begin);
                    using var reader = new StreamReader(stream);
                    var text = reader.ReadToEnd();
                    var complete = text.LastIndexOf('\n');
                    if (complete >= 0)
                    {
                        _progressOffset += System.Text.Encoding.UTF8.GetByteCount(text[..(complete + 1)]);
                        foreach (var line in text[..complete].Split('\n', StringSplitOptions.RemoveEmptyEntries))
                            Write(JsonNode.Parse(line)?["msg"]?.GetValue<string>() ?? line);
                    }
                }
            }
            catch (Exception) { }
            int? state;
            try
            {
                state = ServiceControl.State(ServiceName);
            }
            catch (Exception)
            {
                state = null;
            }
            if (state == ServiceControl.Running) _serviceSeenRunning = true;
            var done = File.Exists(Path.Combine(ReportDir, "done.json"));
            // 服务停了（起来过、或者 30 秒了还没起来——起都没起来就不用等满 15 分钟）
            var gone = state is null or ServiceControl.Stopped && (_serviceSeenRunning || DateTime.Now - _startedAt > TimeSpan.FromSeconds(30));
            var timedOut = DateTime.Now - _startedAt > TimeSpan.FromMinutes(15);
            if (done || gone || timedOut)
            {
                if (timedOut) Write("超时了，结束实验");
                else if (gone && !done) Write("临时服务意外停了");
                Finish();
            }
        }

        private async void Finish()
        {
            _timer.Stop();
            _running = false;
            Write("收尾：删掉临时服务、还原设置、打包结果……");
            var finish = Task.Run(() => FinishAndCollect(_outDir, Write));
            _busy = finish;
            await finish;
            _busy = null;
        }

        protected override async void OnFormClosing(FormClosingEventArgs e)
        {
            if (_busy is { IsCompleted: false } busy)
            {
                // 正在准备 / 收尾：等它做完再关（中途被关，后台线程会被杀掉、收尾做一半）
                e.Cancel = true;
                Enabled = false;
                Write("正在清理，请稍等……");
                await busy;
                _busy = null;
                Close();
                return;
            }
            if (_running)
            {
                e.Cancel = true;
                Enabled = false;
                _timer.Stop();
                _running = false;
                Write("收尾……");
                await Task.Run(() => FinishAndCollect(_outDir, Write));
                Close();
                return;
            }
            base.OnFormClosing(e);
        }
    }

    /// <summary>上次的实验还有东西没收拾（服务、记录、目录）。</summary>
    private static bool Leftovers()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(StateKey);
            return ServiceControl.Exists(ServiceName) || key is not null || Directory.Exists(Root);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool AutoLogon()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon");
        return key?.GetValue("AutoAdminLogon")?.ToString() == "1";
    }

    /// <summary>清掉上次的，记下这次实验（注册表）、建好目录、拷程序、装服务（自动启动：断电重启后它自己收尾）并启动。</summary>
    private static void Prepare(string outDir, bool includeLock, Action<string> write)
    {
        CleanUp(write);
        using (var key = Registry.LocalMachine.CreateSubKey(StateKey))
        {
            key.SetValue("BootTime", BootTime, RegistryValueKind.QWord);
            key.SetValue("Session", (int)ConsoleSession.CurrentProcessSession(), RegistryValueKind.DWord);
            key.SetValue("OutDir", outDir);
            key.SetValue("IncludeLock", includeLock ? 1 : 0, RegistryValueKind.DWord);
        }
        // Program Files 下普通用户建不了目录：不会被人抢先建好、放进别的东西
        if (Directory.Exists(Root)) throw new IOException($"{Root} 删不掉");
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(BinDir);
        Directory.CreateDirectory(RuntimeDir);
        CreateReportDirectory(ReportDir);
        var exe = Path.Combine(BinDir, "PrivateChat.Agent.exe");
        File.Copy(Environment.ProcessPath!, exe, overwrite: true);
        ServiceControl.Create(ServiceName, "PrivateChat 锁屏 / UAC 实验（临时）",
            $"{SessionLauncher.Quote(exe)} --secure-desktop-test-service {SessionLauncher.Quote(ReportDir)}", autoStart: true,
            "PrivateChat 远程协助的一次性实验，做完自己删掉；中途断电重启的话开机时自己收尾。");
        ServiceControl.SetEnvironment(ServiceName, new Dictionary<string, string> { ["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = RuntimeDir });
        ServiceControl.Start(ServiceName);
    }

    /// <summary>停掉删掉服务、还原 SAS 策略、核对、把结果打包到 outDir、删掉临时目录。返回 zip 路径（没有结果时 null）。</summary>
    private static string? FinishAndCollect(string outDir, Action<string> write)
    {
        StopService(write);
        var problems = RestoreAndCheck(write);
        string? zip = null;
        if (Directory.Exists(ReportDir) && Directory.EnumerateFileSystemEntries(ReportDir).Any())
        {
            // 这个窗口是以管理员身份跑的：桌面路径上有目录联接 / 符号链接的话，可能被引到别处去写。不往那里写，结果留在原地
            if (LinkOnPath(outDir) is { } link)
            {
                write($"桌面路径上的 {link} 是个链接，不往那里写。结果留在 {ReportDir}（只有管理员能打开），请手动拷出来");
                DeleteDirectoryOrLater(BinDir);
                DeleteDirectoryOrLater(RuntimeDir);
                return null;
            }
            for (var attempt = 0; attempt < 5 && zip is null; attempt++)
            {
                try
                {
                    Directory.CreateDirectory(outDir);
                    var path = Path.Combine(outDir, $"PrivateChat锁屏实验-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
                    Zip(ReportDir, path);
                    zip = path;
                }
                catch (IOException ex)
                {
                    AgentLog.Warn($"打包结果失败（第 {attempt + 1} 次）：{ex.Message}");
                    Thread.Sleep(1000);
                }
            }
            if (zip is null)
            {
                // 打不了包就把整个结果目录拷出来，别把唯一的一份结果删了
                var copy = Path.Combine(outDir, $"PrivateChat锁屏实验-{DateTime.Now:yyyyMMdd-HHmmss}");
                try
                {
                    CopyDirectory(ReportDir, copy);
                    write($"打包失败，结果拷到了文件夹 {copy}（把整个文件夹发给我）");
                }
                catch (Exception ex)
                {
                    write($"结果拷不出来（{ex.Message}），先不删，还在 {ReportDir}");
                    return null;
                }
            }
        }
        DeleteRoot(write);
        if (problems.Count == 0) write(zip is not null ? $"完成。结果在：{zip}\r\n请把这个文件发给我。" : "完成。");
        else write("有没收拾干净的地方（见上面红字），按上面写的手动恢复一下，或者把这个窗口的内容拍照发给我。");
        return zip;
    }

    /// <summary>path 和它的上层目录里第一个是链接（目录联接、符号链接）的；都不是返回 null。</summary>
    private static string? LinkOnPath(string path)
    {
        for (var dir = new DirectoryInfo(Path.GetFullPath(path)); dir is not null; dir = dir.Parent)
        {
            if (!dir.Exists) continue;
            if (dir.LinkTarget is not null) return dir.FullName;
        }
        return null;
    }

    /// <summary>删掉一个目录；有文件正被占用（比如正在跑的 exe）的话，安排重启后删。</summary>
    private static void DeleteDirectoryOrLater(string path)
    {
        if (!Directory.Exists(path)) return;
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception)
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                try { File.Delete(file); }
                catch (Exception) { Native.MoveFileExW(file, null, Native.MOVEFILE_DELAY_UNTIL_REBOOT); }
            foreach (var dir in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
                Native.MoveFileExW(dir, null, Native.MOVEFILE_DELAY_UNTIL_REBOOT);
            Native.MoveFileExW(path, null, Native.MOVEFILE_DELAY_UNTIL_REBOOT);
        }
    }

    private static void CleanUp(Action<string> write)
    {
        StopService(write);
        RestoreAndCheck(write);
        DeleteRoot(write);
    }

    private static void StopService(Action<string> write)
    {
        try
        {
            if (!ServiceControl.Exists(ServiceName)) return;
            if (!ServiceControl.Stop(ServiceName, TimeSpan.FromSeconds(30)) && ServiceControl.ProcessId(ServiceName) is { } pid)
            {
                write($"临时服务 30 秒没停下，结束它（进程 {pid}）");
                try { Process.GetProcessById(pid).Kill(); }
                catch (Exception ex) { AgentLog.Warn($"结束服务进程失败：{ex.Message}"); }
            }
            ServiceControl.Delete(ServiceName);
            for (var i = 0; i < 50 && ServiceControl.Exists(ServiceName); i++) Thread.Sleep(200);
        }
        catch (Exception ex)
        {
            write($"删临时服务出错：{ex.Message}");
        }
    }

    /// <summary>还原 SAS 策略（按注册表里记的原值），核对服务没了、策略对了；都对了才删掉记录。返回没对上的。</summary>
    private static List<string> RestoreAndCheck(Action<string> write)
    {
        var problems = new List<string>();
        try
        {
            RestoreSas();
        }
        catch (Exception ex)
        {
            problems.Add($"还原 Ctrl+Alt+Del 策略失败：{ex.Message}");
        }
        if (SasOriginal() is { } original && !SasMatches(original))
            problems.Add($"Ctrl+Alt+Del 策略（SoftwareSASGeneration）没还原成原来的{(original.Kind is null ? "（没设）" : $" {original.Value}")}。" +
                         (original.Kind is null
                             ? @"手动恢复：管理员命令行里运行 reg delete HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System /v SoftwareSASGeneration /f"
                             : $@"手动恢复：管理员命令行里运行 reg add HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System /v SoftwareSASGeneration /t REG_DWORD /d {original.Value} /f"));
        try
        {
            if (ServiceControl.Exists(ServiceName)) problems.Add($"临时服务还在。手动删除：管理员命令行里运行 sc delete {ServiceName}");
        }
        catch (Exception) { }
        foreach (var problem in problems) write($"！！ {problem}");
        if (problems.Count == 0)
        {
            try { Registry.LocalMachine.DeleteSubKeyTree(StateKey, throwOnMissingSubKey: false); }
            catch (Exception ex) { AgentLog.Warn($"删实验记录失败：{ex.Message}"); }
        }
        return problems;
    }

    private static void DeleteRoot(Action<string>? write)
    {
        for (var i = 0; i < 10 && Directory.Exists(Root); i++)
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (Exception) when (i < 9)
            {
                Thread.Sleep(500);
            }
            catch (Exception ex)
            {
                write?.Invoke($"临时目录 {Root} 有文件删不掉（{ex.Message}），重启后自动删");
                foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                    Native.MoveFileExW(file, null, Native.MOVEFILE_DELAY_UNTIL_REBOOT);
                foreach (var dir in Directory.EnumerateDirectories(Root, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
                    Native.MoveFileExW(dir, null, Native.MOVEFILE_DELAY_UNTIL_REBOOT);
                Native.MoveFileExW(Root, null, Native.MOVEFILE_DELAY_UNTIL_REBOOT);
                return;
            }
        }
    }

    /// <summary>报告目录：只有 SYSTEM 和管理员能动（不继承 Program Files 给普通用户的读权限：里面有截图）。</summary>
    private static void CreateReportDirectory(string path)
    {
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var security = new DirectorySecurity();
        security.SetOwner(admins);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { system, admins })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).Create(security);
    }

    /// <summary>打包：只收普通文件，不跟着目录联接 / 链接走。</summary>
    private static void Zip(string dir, string zipPath)
    {
        using var file = new FileStream(zipPath, FileMode.CreateNew);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var path in Directory.EnumerateFiles(dir, "*", options))
            zip.CreateEntryFromFile(path, Path.GetRelativePath(dir, path).Replace('\\', '/'));
    }

    private static void CopyDirectory(string from, string to)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var path in Directory.EnumerateFiles(from, "*", options))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, path));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(path, target, overwrite: true);
        }
    }

    // ---- SoftwareSASGeneration：原值记在注册表里（值的类型也记：不是 DWORD 的不碰）----

    private sealed record SasValue(RegistryValueKind? Kind, int? Value);

    private const string SasKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";

    private static SasValue ReadSas()
    {
        using var key = Registry.LocalMachine.OpenSubKey(SasKeyPath);
        if (key?.GetValue("SoftwareSASGeneration") is not { } value) return new SasValue(null, null);
        var kind = key.GetValueKind("SoftwareSASGeneration");
        return new SasValue(kind, value is int i ? i : null);
    }

    private static SasValue? SasOriginal()
    {
        using var key = Registry.LocalMachine.OpenSubKey(StateKey);
        if (key?.GetValue("SasOriginalKind") is not string kind) return null;
        return kind == "absent" ? new SasValue(null, null) : new SasValue(RegistryValueKind.DWord, key.GetValue("SasOriginal") is int v ? v : null);
    }

    private static bool SasMatches(SasValue original)
    {
        var now = ReadSas();
        return original.Kind is null ? now.Kind is null : now.Kind == RegistryValueKind.DWord && now.Value == original.Value;
    }

    /// <summary>
    /// 服务在第 3 步前调：记下原值（第一次）、打开"服务能发 SAS"。原值不是 DWORD（有人手改过注册表）就不碰，返回 false（这一步的结果就是"策略没开"）。
    /// </summary>
    private static bool AllowSas()
    {
        var current = ReadSas();
        if (current.Kind is not null and not RegistryValueKind.DWord) return false;
        using (var key = Registry.LocalMachine.CreateSubKey(StateKey))
        {
            if (key.GetValue("SasOriginalKind") is null)
            {
                key.SetValue("SasOriginalKind", current.Kind is null ? "absent" : "dword");
                if (current.Value is { } v) key.SetValue("SasOriginal", v, RegistryValueKind.DWord);
            }
        }
        SasPolicy.Write(SasPolicy.Allowing(current.Value));
        return true;
    }

    /// <summary>按注册表里记的原值还原（没记过就什么都不做）。可以重复调。</summary>
    private static void RestoreSas()
    {
        if (SasOriginal() is not { } original) return;
        if (!SasMatches(original)) SasPolicy.Write(original.Kind is null ? null : original.Value);
    }

    private static partial class Native
    {
        public const uint ASFW_ANY = 0xFFFFFFFF;
        public const int MOVEFILE_DELAY_UNTIL_REBOOT = 0x4;

        [DllImport("user32.dll")] public static extern bool AllowSetForegroundWindow(uint processId);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] public static extern bool MoveFileExW(string existing, string? newName, int flags);
    }
}

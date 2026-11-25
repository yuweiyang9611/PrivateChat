using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Win32;
using PrivateChat.Agent.Platform;
using PrivateChat.Agent.Telemetry;

namespace PrivateChat.Agent.Diagnostics;

internal static partial class SecureDesktopTest
{
    /// <summary>
    /// 服务（SCM 起的，SYSTEM，会话 0）：起子进程、按步骤做实验、写报告；做完还原 SAS 策略、删掉自己。最多跑 12 分钟。
    /// 开机自己起来、发现实验不是这次开机开始的（中途断电 / 重启了）：只收尾。
    /// </summary>
    public static int Service(string reportDir)
    {
        AgentLog.Init(Path.Combine(reportDir, "log-service"), console: false);
        ServiceHost.OnStopping = () =>
        {
            // 控制回调里同步做最要紧的：还原 SAS 策略（关机时 SCM 只等几秒）。删服务留给 body（关机时不删：开机后它自己起来收尾）
            try { RestoreSas(); }
            catch (Exception ex) { AgentLog.Error("还原 SAS 策略失败", ex); }
        };
        return ServiceHost.Run(ServiceName, async (changes, ct) =>
        {
            if (StaleRun())
            {
                CleanUpAfterReboot();
                return 0;
            }
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromMinutes(12));
            var run = new ServiceRun(reportDir, changes);
            try
            {
                await run.RunAsync(limit.Token);
            }
            catch (Exception ex)
            {
                run.Progress(limit.IsCancellationRequested ? "实验超时或被停止" : $"实验出错：{ex.Message}");
                AgentLog.Error("实验出错", ex);
                run.Report["error"] = ex.ToString();
            }
            await run.FinishAsync(deleteSelf: !ShuttingDown());
            return 0;
        });
    }

    /// <summary>
    /// 开发时在本机空跑（--secure-desktop-test-dry DIR）：不装服务、不弹 UAC、不锁屏、不注入；子进程以当前用户普通地起，
    /// 只走第 1 步（平常桌面截图、线程切换、声音），看管道、截图、报告这些环节通不通。
    /// </summary>
    public static int Dry(string reportDir)
    {
        Directory.CreateDirectory(reportDir);
        AgentLog.Init(Path.Combine(reportDir, "log-service"), console: true);
        var run = new ServiceRun(reportDir, Channel.CreateUnbounded<ServiceHost.SessionChange>().Reader, dry: true);
        using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            run.RunAsync(limit.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            AgentLog.Error("空跑出错", ex);
            run.Report["error"] = ex.ToString();
        }
        finally
        {
            run.FinishAsync(deleteSelf: false).GetAwaiter().GetResult();
        }
        return run.Report["error"] is null ? 0 : 1;
    }

    /// <summary>实验不是这次开机开始的（记下的开机时刻和现在的差了一分钟以上）。</summary>
    private static bool StaleRun()
    {
        using var key = Registry.LocalMachine.OpenSubKey(StateKey);
        return key?.GetValue("BootTime") is not long boot || Math.Abs(boot - BootTime) > 60_000;
    }

    /// <summary>系统正在关机（这时候别删自己：开机后要靠它收尾）。看 SCM 发来的是不是关机 / 关机前的控制码（会话 0 里的 SM_SHUTTINGDOWN 不可靠）。</summary>
    private static bool ShuttingDown()
        => ServiceHost.LastStopControl is ServiceHost.ControlShutdown or ServiceHost.ControlPreshutdown || Native.GetSystemMetrics(Native.SM_SHUTTINGDOWN) != 0;

    /// <summary>
    /// 断电 / 重启后开机时：还原策略、删掉实验记录、删掉程序（自己的 exe 正在跑，下次重启后删）、删掉自己。
    /// 结果（report 目录，只有 SYSTEM 和管理员能看）留着：下次双击实验程序时，它会先把上次的结果打包到桌面上，再清理干净。
    /// </summary>
    private static void CleanUpAfterReboot()
    {
        AgentLog.Info("上次的实验没做完（中途关机了），只收尾");
        try { RestoreSas(); }
        catch (Exception ex) { AgentLog.Error("还原 SAS 策略失败", ex); }
        if (SasOriginal() is not { } original || SasMatches(original))
            try { Registry.LocalMachine.DeleteSubKeyTree(StateKey, throwOnMissingSubKey: false); }
            catch (Exception ex) { AgentLog.Error("删实验记录失败", ex); }
        DeleteDirectoryOrLater(BinDir);
        DeleteDirectoryOrLater(RuntimeDir);
        try { ServiceControl.Delete(ServiceName); }
        catch (Exception ex) { AgentLog.Error("服务删自己失败", ex); }
    }

    private sealed class ServiceRun(string reportDir, ChannelReader<ServiceHost.SessionChange> changes, bool dry = false)
    {
        private readonly JsonArray _steps = [];
        private readonly List<JsonObject> _sessionChanges = [];
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly List<Child> _children = [];
        private readonly List<LaunchedProcess> _launched = [];
        private uint _session = ConsoleSession.None;
        private long _unlockEventAt = -1;
        private CancellationTokenSource? _changesReader;
        private Task _changesTask = Task.CompletedTask;

        public JsonObject Report { get; } = new() { ["startedAt"] = DateTime.Now.ToString("O") };

        private long Now => _clock.ElapsedMilliseconds;

        public void Progress(string message)
        {
            AgentLog.Info(message);
            try
            {
                File.AppendAllText(Path.Combine(reportDir, "progress.jsonl"), JsonSerializer.Serialize(new JsonObject { ["t"] = Now, ["msg"] = message }) + "\n");
            }
            catch (IOException) { }
        }

        public async Task RunAsync(CancellationToken ct)
        {
            Report["steps"] = _steps;
            _changesReader = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var reader = _changesReader.Token;
            _changesTask = Task.Run(async () =>
            {
                try
                {
                    await foreach (var change in changes.ReadAllAsync(reader))
                    {
                        lock (_sessionChanges) _sessionChanges.Add(new JsonObject { ["t"] = Now, ["event"] = change.EventType, ["session"] = change.SessionId });
                        if (change.EventType == 8 && change.SessionId == _session) Interlocked.Exchange(ref _unlockEventAt, Now); // WTS_SESSION_UNLOCK
                    }
                }
                catch (OperationCanceledException) { }
            }, CancellationToken.None);

            Progress(dry ? "开始空跑" : "服务已启动（SYSTEM，会话 0）");
            Report["service"] = ServiceEnvironment();
            _session = dry ? ConsoleSession.CurrentProcessSession() : await WaitForConsoleUser(ct);
            Progress($"控制台会话 {_session}，登录的是 {Mask(ConsoleSession.UserName(_session) ?? "?")}");
            if (!dry && StartedInSession() is { } started && started != _session)
                throw new InvalidOperationException($"现在屏幕上的是会话 {_session}，实验是在会话 {started} 里开始的（切换了用户？）：不做");
            var includeLock = !dry && IncludeLock();

            var helper = await StartChild("helper", user: true, restricted: false, ct);
            var worker = await StartChild("system", user: false, restricted: false, ct);
            Child? limited = null;
            Child? threads = null;
            try
            {
                limited = await StartChild("restricted", user: false, restricted: true, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Report["restrictedStart"] = ex.Message;
                Progress($"去掉特权的工作进程起不来：{ex.Message}");
            }
            try
            {
                threads = await StartChild("threads", user: false, restricted: false, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Report["threadsStart"] = ex.Message;
            }
            Report["helper"] = await Safe(() => helper.Call("info", ct: ct));
            Report["worker"] = await Safe(() => worker.Call("info", ct: ct));
            if (limited is not null) Report["restricted"] = await Safe(() => limited.Call("info", ct: ct));

            async Task<JsonObject> Probe(Child child, string label, bool thumb = false, CancellationToken? token = null)
            {
                var result = await Safe(() => child.Call("probe", new JsonObject { ["label"] = label, ["thumb"] = thumb }, timeoutMs: 40_000, ct: token ?? ct));
                result["t"] = Now;
                return result;
            }
            Task<JsonObject> Key(Child child, int? scan = null, int? vk = null, int repeat = 1, CancellationToken? token = null)
            {
                var keys = new JsonArray();
                for (var i = 0; i < repeat; i++)
                {
                    keys.Add(new JsonObject { ["scan"] = scan, ["vk"] = vk, ["up"] = false });
                    keys.Add(new JsonObject { ["scan"] = scan, ["vk"] = vk, ["up"] = true });
                }
                return Safe(() => child.Call("keys", new JsonObject { ["keys"] = keys }, ct: token ?? ct));
            }
            Task<JsonObject> Focus(Child child) => Safe(() => child.Call("focus", ct: ct));
            Task<JsonObject> Mouse(Child child, double x, double y) => Safe(() => child.Call("mouse", new JsonObject { ["x"] = x, ["y"] = y }, ct: ct));
            async Task<string?> DesktopName(CancellationToken? token = null) => (await Safe(() => worker.Call("desktop", ct: token ?? ct)))["name"]?.GetValue<string>();
            async Task<JsonObject> WaitDesktop(bool wantDefault, int timeoutMs)
            {
                var result = await Safe(() => worker.Call("waitDesktop", new JsonObject { ["wantDefault"] = wantDefault, ["timeoutMs"] = timeoutMs }, timeoutMs: timeoutMs + 5000, ct: ct));
                result["t"] = Now;
                return result;
            }
            static bool Matched(JsonObject wait) => wait["matched"]?.GetValue<bool>() == true;
            // 平常桌面、而且连着 2 秒都是（没有 UAC 提示开着）：每一步开始前要这样，才能把下一步的变化算在它头上
            async Task<bool> StableDefault(int timeoutMs = 10_000)
            {
                var watch = Stopwatch.StartNew();
                long since = -1;
                while (watch.ElapsedMilliseconds < timeoutMs)
                {
                    var ok = await DesktopName() == InputDesktop.Default && ConsentProcesses(_session).Count == 0;
                    if (!ok) since = -1;
                    else if (since < 0) since = watch.ElapsedMilliseconds;
                    else if (watch.ElapsedMilliseconds - since >= 2000) return true;
                    await Task.Delay(200, ct);
                }
                return false;
            }
            async Task<JsonObject> ThreadSwitch(string label)
                => threads is null ? new JsonObject { ["skipped"] = "没有测线程的进程" } : await Safe(() => threads.Call("switch", new JsonObject { ["label"] = label }, timeoutMs: 60_000, ct: ct));

            await Step("1-平常桌面", async step =>
            {
                if (threads is not null) step["threadsSetup"] = await Safe(() => threads.Call("setup", timeoutMs: 60_000, ct: ct));
                step["system"] = await Probe(worker, "1-default", thumb: true);
                if (limited is not null) step["restricted"] = await Probe(limited, "1-default-restricted", thumb: true);
                step["focus"] = await Focus(worker);
                if (!dry)
                {
                    step["shift"] = await Key(worker, scan: 0x2A);
                    step["mouse"] = await Mouse(worker, 0.5, 0.5);
                }
                step["audio"] = await Safe(() => worker.Call("audio", ct: ct));
                step["helperDesktop"] = await Safe(() => helper.Call("desktop", ct: ct));
                step["threads"] = await ThreadSwitch("1-default");
            });
            if (dry)
            {
                Progress("空跑做完了（只做第 1 步）");
                return;
            }

            var stuck = false;
            await Step("2-UAC", async step =>
            {
                if (!await StableDefault())
                {
                    step["skipped"] = "开始前不在平常桌面上";
                    stuck = true;
                    return;
                }
                Progress("弹出管理员确认（UAC），会自动点掉");
                step["elevate"] = await Safe(() => helper.Call("elevate", ct: ct));
                // 等提示出来。默认（PromptOnSecureDesktop 不是 0）提示在安全桌面上：切过去才算弹出来了；
                // consent.exe 已经在了、10 秒还没切过去，多半是只在任务栏上闪（申请的窗口没在前台）。PromptOnSecureDesktop=0 时提示就在平常桌面上。
                var onSecure = PromptOnSecureDesktop();
                step["promptOnSecureDesktop"] = onSecure;
                async Task<JsonObject> WaitUac(int timeoutMs, bool stopWhenDeferred)
                {
                    var watch = Stopwatch.StartNew();
                    long consentSince = -1;
                    while (true)
                    {
                        var name = await DesktopName();
                        var consent = ConsentProcesses(_session).Count > 0;
                        if (consent && consentSince < 0) consentSince = watch.ElapsedMilliseconds;
                        var secure = name is not null && name != InputDesktop.Default;
                        var shown = secure || (!onSecure && consent);
                        var deferred = onSecure && consent && !secure && watch.ElapsedMilliseconds - consentSince >= 10_000;
                        if (shown || (stopWhenDeferred && deferred) || watch.ElapsedMilliseconds >= timeoutMs)
                            return new JsonObject
                            {
                                ["matched"] = secure, ["shown"] = shown, ["consent"] = consent, ["deferred"] = deferred,
                                ["name"] = name, ["ms"] = watch.ElapsedMilliseconds, ["t"] = Now,
                            };
                        await Task.Delay(100, ct);
                    }
                }
                var switched = await WaitUac(20_000, stopWhenDeferred: true);
                if (switched["shown"]?.GetValue<bool>() != true)
                {
                    step["firstWait"] = switched;
                    Progress("任务栏上要是有个闪烁的盾牌按钮（用户帐户控制），请点一下它");
                    switched = await WaitUac(60_000, stopWhenDeferred: false);
                }
                step["switch"] = switched;
                step["lock"] = ConsoleSession.Lock(_session).ToString();
                step["consent"] = new JsonArray([.. ConsentProcesses(_session).Select(p => (JsonNode)p)]);
                step["helperDesktop"] = await Safe(() => helper.Call("desktop", ct: ct));
                if (switched["shown"]?.GetValue<bool>() == true)
                {
                    // 刚切过去时对话框还在淡入：先截一张"马上的"（只在确实切到了安全桌面时），等 2 秒再截、再操作
                    if (Matched(switched)) step["immediate"] = await Probe(worker, "2-uac-0s");
                    await Task.Delay(2000, ct);
                    step["system"] = await Probe(worker, "2-uac");
                    if (limited is not null) step["restricted"] = await Probe(limited, "2-uac-restricted");
                    step["focus"] = await Focus(worker);
                    if (limited is not null) step["mouseRestricted"] = await Mouse(limited, 0.5, 0.15);
                    step["mouse"] = await Mouse(worker, 0.5, 0.1);
                    step["threads"] = await ThreadSwitch("2-uac");
                }
                // 关掉提示：先用去掉特权的那个按 Esc，看它点不点得到；不行再用完整 SYSTEM 的（最多三次）
                var attempts = new JsonArray();
                step["dismiss"] = attempts;
                var who = new List<(string Name, Child Child)>();
                if (limited is not null) who.Add(("restricted", limited));
                who.AddRange([("system", worker), ("system", worker), ("system", worker)]);
                foreach (var (name, child) in who)
                {
                    if (await UacGone(helper, ct)) break;
                    var attempt = new JsonObject { ["by"] = name, ["t"] = Now, ["sent"] = await Key(child, scan: 0x01) };
                    attempts.Add(attempt);
                    if (Matched(switched)) attempt["back"] = await WaitDesktop(wantDefault: true, 5000);
                    var settle = Stopwatch.StartNew();
                    while (settle.ElapsedMilliseconds < 5000 && !await UacGone(helper, ct)) await Task.Delay(200, ct);
                    attempt["goneAfterMs"] = await UacGone(helper, ct) ? settle.ElapsedMilliseconds : null;
                }
                if (!await UacGone(helper, ct))
                {
                    Progress("自动点不掉 UAC 提示：请手动点\"否\"（最多等 2 分钟）");
                    var watch = Stopwatch.StartNew();
                    while (watch.Elapsed < TimeSpan.FromMinutes(2) && !await UacGone(helper, ct)) await Task.Delay(500, ct);
                }
                var gone = await UacGone(helper, ct);
                step["gone"] = gone;
                step["elevateResult"] = await Safe(() => helper.Call("elevateResult", new JsonObject { ["timeoutMs"] = 3000 }, timeoutMs: 10_000, ct: ct));
                if (!gone)
                {
                    stuck = true;
                    Progress("UAC 提示还开着：后面几步不做了");
                }
            });

            await Step("3-Ctrl+Alt+Del", async step =>
            {
                if (stuck)
                {
                    step["skipped"] = "UAC 提示还开着";
                    return;
                }
                step["policyBefore"] = SasDescribe();
                step["policyAllowed"] = AllowSas();
                step["policyNow"] = SasDescribe();
                var first = true;
                var variants = new List<string> { "service", "service-as-worker", "worker" };
                if (limited is not null) variants.Add("worker-restricted");
                try
                {
                    foreach (var variant in variants)
                    {
                        var attempt = new JsonObject();
                        step[variant] = attempt;
                        if (!await StableDefault())
                        {
                            attempt["skipped"] = "开始前不在平常桌面上（上一种的界面还开着？）";
                            stuck = true;
                            break;
                        }
                        Progress($"发 Ctrl+Alt+Del（{variant}）");
                        attempt["sentAt"] = Now;
                        try
                        {
                            switch (variant)
                            {
                                case "service":
                                    SasPolicy.SendSAS(false);
                                    break;
                                case "service-as-worker":
                                    worker.Channel.RunAsClient(() => SasPolicy.SendSAS(false));
                                    break;
                                case "worker":
                                    attempt["call"] = await Safe(() => worker.Call("sas", ct: ct));
                                    break;
                                default:
                                    attempt["call"] = await Safe(() => limited!.Call("sas", ct: ct));
                                    break;
                            }
                        }
                        catch (Exception ex)
                        {
                            attempt["error"] = ex.Message;
                        }
                        var switched = await WaitDesktop(wantDefault: false, 3000);
                        attempt["switch"] = switched;
                        if (!Matched(switched))
                        {
                            // 晚到的也记下（分开记，免得算到下一种头上）
                            var late = await WaitDesktop(wantDefault: false, 5000);
                            attempt["late"] = late;
                            if (!Matched(late)) continue;
                        }
                        await Task.Delay(1500, ct);
                        attempt["probe"] = await Probe(worker, $"3-sas-{variant}");
                        attempt["focus"] = await Focus(worker);
                        if (first)
                        {
                            first = false;
                            if (limited is not null)
                            {
                                attempt["restrictedProbe"] = await Probe(limited, "3-sas-restricted");
                                attempt["mouseRestricted"] = await Mouse(limited, 0.5, 0.15);
                            }
                            attempt["mouse"] = await Mouse(worker, 0.5, 0.1);
                            attempt["threads"] = await ThreadSwitch("3-sas");
                        }
                        var back = new JsonArray();
                        attempt["back"] = back;
                        foreach (var (name, child) in new[] { ("restricted", limited), ("system", worker), ("system", worker) })
                        {
                            if (child is null) continue;
                            if (await DesktopName() == InputDesktop.Default) break;
                            back.Add(new JsonObject { ["by"] = name, ["sent"] = await Key(child, scan: 0x01), ["wait"] = await WaitDesktop(wantDefault: true, 2500) });
                        }
                        if (await DesktopName() != InputDesktop.Default)
                        {
                            Progress("自动返回不了：请按 Esc 或点\"取消\"（最多等 2 分钟）");
                            attempt["backManual"] = await WaitDesktop(wantDefault: true, 120_000);
                        }
                    }
                }
                finally
                {
                    RestoreSas();
                    step["policyRestored"] = SasDescribe();
                }
            });

            if (includeLock && !stuck)
            {
                await Step("4-锁屏", async step =>
                {
                    if (!await StableDefault())
                    {
                        step["skipped"] = "开始前不在平常桌面上";
                        return;
                    }
                    Progress("锁屏（最后要你输入密码解锁）");
                    step["lockCall"] = await Safe(() => helper.Call("lock", ct: ct));
                    var timeline = new JsonArray();
                    step["timeline"] = timeline;
                    string? lastDesktop = null, lastLock = null;
                    var watch = Stopwatch.StartNew();
                    while (watch.ElapsedMilliseconds < 10_000)
                    {
                        var desktop = await DesktopName();
                        var locked = ConsoleSession.Lock(_session).ToString();
                        if (desktop != lastDesktop || locked != lastLock)
                        {
                            timeline.Add(new JsonObject { ["ms"] = watch.ElapsedMilliseconds, ["desktop"] = desktop, ["lock"] = locked });
                            (lastDesktop, lastLock) = (desktop, locked);
                        }
                        if (locked == nameof(ConsoleSession.LockState.Locked) && watch.ElapsedMilliseconds > 3000) break;
                        await Task.Delay(200, ct);
                    }
                    if (ConsoleSession.Lock(_session) != ConsoleSession.LockState.Locked)
                    {
                        step["skipped"] = "没锁上";
                        return;
                    }
                    // 只在确实锁着、输入桌面不是平常桌面时才往里打字
                    async Task<bool> OnLockScreen(CancellationToken? token = null)
                        => ConsoleSession.Lock(_session) == ConsoleSession.LockState.Locked && await DesktopName(token) != InputDesktop.Default;
                    await Task.Delay(1500, ct);
                    step["helperDesktop"] = await Safe(() => helper.Call("desktop", ct: ct));
                    step["curtain"] = await Probe(worker, "4-lock-curtain");
                    if (limited is not null) step["curtainRestricted"] = await Probe(limited, "4-lock-curtain-restricted");
                    step["focusCurtain"] = await Focus(worker);
                    step["threads"] = await ThreadSwitch("4-lock");
                    // Windows 11：锁屏先是一层"幕布"——LockApp 在平常桌面上、是前台（2026-11-26 在虚拟机里看到的），按个键才换到 Winlogon 上的密码框。
                    // 只在确实锁着、前台确实是 LockApp 时按空格；先用去掉特权的进程按（要求最低的那种），没反应再用 SYSTEM 按
                    async Task<bool> OnCurtain()
                        => ConsoleSession.Lock(_session) == ConsoleSession.LockState.Locked && await DesktopName() == InputDesktop.Default
                           && (await Focus(worker))["focus"]?["foreground"]?["process"]?.GetValue<string>() == "LockApp";
                    if (await OnCurtain())
                    {
                        var by = limited ?? worker;
                        step["curtainSpaceBy"] = limited is not null ? "restricted" : "system";
                        step["curtainSpace"] = await Key(by, scan: 0x39);
                        step["curtainGone"] = await WaitDesktop(wantDefault: false, timeoutMs: 3000);
                        if (limited is not null && await OnCurtain())
                        {
                            step["curtainSpaceSystem"] = await Key(worker, scan: 0x39);
                            step["curtainGoneSystem"] = await WaitDesktop(wantDefault: false, timeoutMs: 3000);
                        }
                    }
                    try
                    {
                        if (limited is not null && await OnLockScreen())
                        {
                            step["spaceRestricted"] = await Key(limited, scan: 0x39);
                            await Task.Delay(1500, ct);
                            step["afterSpaceRestricted"] = await Probe(worker, "4-lock-after-space-restricted");
                        }
                        if (await OnLockScreen())
                        {
                            step["space"] = await Key(worker, scan: 0x39);
                            await Task.Delay(1500, ct);
                            step["prompt"] = await Probe(worker, "4-lock-prompt");
                            step["focusPrompt"] = await Focus(worker);
                            step["mouse"] = await Mouse(worker, 0.5, 0.1);
                        }
                        // 一个一个打、每打一个截一张：分得清是 Unicode（远程粘贴的方式）、扫描码（远程按键的方式）还是去掉特权的进程打进去的。
                        // PIN 最短 4 位、密码要按回车才提交：只打 3 个数字，不会被当成一次登录
                        if (await OnLockScreen())
                        {
                            step["typeUnicode"] = await Safe(() => worker.Call("text", new JsonObject { ["text"] = "1" }, ct: ct));
                            await Task.Delay(600, ct);
                            step["typedUnicode"] = await Probe(worker, "4-lock-typed-unicode");
                        }
                        if (await OnLockScreen())
                        {
                            step["typeScan"] = await Key(worker, scan: 0x03);
                            await Task.Delay(600, ct);
                            step["typedScan"] = await Probe(worker, "4-lock-typed-scancode");
                        }
                        if (limited is not null && await OnLockScreen())
                        {
                            step["typeRestricted"] = await Safe(() => limited.Call("text", new JsonObject { ["text"] = "3" }, ct: ct));
                            await Task.Delay(600, ct);
                            step["typedRestricted"] = await Probe(worker, "4-lock-typed-restricted");
                        }
                    }
                    finally
                    {
                        // 删干净：扫描码和虚拟键两种退格各按十次（打进去的不超过 4 个字）。实验超时、被停下也照样删（不用 ct）
                        try
                        {
                            if (await OnLockScreen(CancellationToken.None))
                            {
                                step["erase"] = await Key(worker, scan: 0x0E, repeat: 10, token: CancellationToken.None);
                                step["eraseVk"] = await Key(worker, vk: 0x08, repeat: 10, token: CancellationToken.None);
                                await Task.Delay(600, CancellationToken.None);
                                step["erased"] = await Probe(worker, "4-lock-erased", token: CancellationToken.None);
                            }
                        }
                        catch (Exception ex)
                        {
                            step["eraseError"] = ex.Message;
                        }
                    }
                    Progress("请输入密码解锁（最多等 5 分钟）");
                    var waited = Stopwatch.StartNew();
                    var unlockedBy = "";
                    while (waited.Elapsed < TimeSpan.FromMinutes(5))
                    {
                        if (Interlocked.Read(ref _unlockEventAt) >= 0) unlockedBy = "解锁通知";
                        else if (ConsoleSession.Lock(_session) == ConsoleSession.LockState.Unlocked && await DesktopName() == InputDesktop.Default) unlockedBy = "锁屏标志";
                        // 锁屏标志读不到时才看这个：Windows 11 的幕布就在平常桌面上，锁着时用户进程也打得开输入桌面
                        else if (ConsoleSession.Lock(_session) != ConsoleSession.LockState.Locked
                                 && (await Safe(() => helper.Call("desktop", ct: ct)))["name"]?.GetValue<string>() == InputDesktop.Default) unlockedBy = "用户进程打开了输入桌面";
                        if (unlockedBy.Length > 0) break;
                        await Task.Delay(500, ct);
                    }
                    step["unlockedAfterMs"] = waited.ElapsedMilliseconds;
                    step["unlockedBy"] = unlockedBy;
                    step["lockAfter"] = ConsoleSession.Lock(_session).ToString();
                    Progress(unlockedBy.Length > 0 ? "已解锁" : "等了 5 分钟还没解锁，接着收尾");
                });
            }

            await Step("5-之后", async step =>
            {
                await Task.Delay(1500, ct);
                step["system"] = await Probe(worker, "5-after", thumb: true);
                step["helperDesktop"] = await Safe(() => helper.Call("desktop", ct: ct));
                step["threads"] = await ThreadSwitch("5-after");
            });
            Progress("实验做完了");
        }

        /// <summary>UAC 提示关了：界面进程那边的"以管理员身份运行"已经有了结果（或者根本没弹过），控制台会话里也没有 consent.exe 了。</summary>
        private async Task<bool> UacGone(Child helper, CancellationToken ct)
        {
            if (ConsentProcesses(_session).Count > 0) return false;
            var result = await Safe(() => helper.Call("elevateDone", ct: ct));
            return result["done"]?.GetValue<bool>() != false;
        }

        public async Task FinishAsync(bool deleteSelf)
        {
            // 最要紧的先做：还原策略、删掉自己（还在跑的服务删了也照样跑完，停了才真的没了）
            if (!dry)
            {
                try { RestoreSas(); }
                catch (Exception ex) { AgentLog.Error("还原 SAS 策略失败", ex); }
                if (deleteSelf)
                    try { ServiceControl.Delete(ServiceName); }
                    catch (Exception ex) { AgentLog.Error("服务删自己失败", ex); }
            }
            foreach (var child in _children)
                try { await child.DisposeAsync(); }
                catch (Exception) { }
            foreach (var process in _launched)
                try { process.Kill(); }
                catch (Exception) { }
            if (_changesReader is { } reader)
            {
                await reader.CancelAsync();
                try { await _changesTask; }
                catch (Exception) { }
            }
            Report["finishedAt"] = DateTime.Now.ToString("O");
            WriteReport();
            Progress(dry ? "空跑收尾" : "已还原 Ctrl+Alt+Del 的策略，临时服务删掉了自己");
            try { File.WriteAllText(Path.Combine(reportDir, "done.json"), "{}"); }
            catch (Exception ex) { AgentLog.Error("写 done.json 失败", ex); }
        }

        private void WriteReport()
        {
            try
            {
                lock (_sessionChanges) Report["sessionChanges"] = new JsonArray([.. _sessionChanges.Select(c => c.DeepClone())]);
                File.WriteAllText(Path.Combine(reportDir, "report.json"), Report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                AgentLog.Error("写报告失败", ex);
            }
        }

        private async Task Step(string name, Func<JsonObject, Task> body)
        {
            var step = new JsonObject { ["name"] = name, ["t"] = Now };
            _steps.Add(step);
            try
            {
                await body(step);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                step["error"] = ex.ToString();
                Progress($"{name} 出错：{ex.Message}");
            }
            step["ms"] = Now - step["t"]!.GetValue<long>();
        }

        private static async Task<JsonObject> Safe(Func<Task<JsonObject>> call)
        {
            try
            {
                return await call();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new JsonObject { ["ok"] = false, ["error"] = $"{ex.GetType().Name}：{ex.Message}" };
            }
        }

        private static uint? StartedInSession()
        {
            using var key = Registry.LocalMachine.OpenSubKey(StateKey);
            return key?.GetValue("Session") is int session ? (uint)session : null;
        }

        /// <summary>UAC 提示是不是在安全桌面上弹（PromptOnSecureDesktop，没设按 1）。</summary>
        private static bool PromptOnSecureDesktop()
        {
            using var key = Registry.LocalMachine.OpenSubKey(SasKeyPath);
            return key?.GetValue("PromptOnSecureDesktop") is not 0;
        }

        private static bool IncludeLock()
        {
            using var key = Registry.LocalMachine.OpenSubKey(StateKey);
            return key?.GetValue("IncludeLock") is not 0;
        }

        private static async Task<uint> WaitForConsoleUser(CancellationToken ct)
        {
            while (true)
            {
                var session = ConsoleSession.ActiveId();
                if (session != ConsoleSession.None && ConsoleSession.UserName(session) is not null) return session;
                await Task.Delay(500, ct);
            }
        }

        private async Task<Child> StartChild(string label, bool user, bool restricted, CancellationToken ct)
        {
            var pipe = $"PrivateChatDesktopTest.{label}.{Convert.ToHexString(RandomNumberGenerator.GetBytes(16))}";
            var exe = Environment.ProcessPath!;
            var environment = new Dictionary<string, string?>();
            if (!user) environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = Environment.GetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR");
            SecurityIdentifier peer;
            LaunchedProcess process;
            string[] args = user
                ? ["--secure-desktop-test-helper", pipe, Environment.ProcessId.ToString(), reportDir]
                : ["--secure-desktop-test-worker", pipe, Environment.ProcessId.ToString(), label, reportDir];
            if (dry)
            {
                // 本机空跑：当前用户、普通子进程
                using var me = WindowsIdentity.GetCurrent();
                peer = me.User!;
                var start = new ProcessStartInfo(exe) { UseShellExecute = false };
                foreach (var arg in args) start.ArgumentList.Add(arg);
                var started = Process.Start(start)!;
                process = new LaunchedProcess(started.Id, started.SafeHandle);
            }
            else if (user)
            {
                using var identity = SessionLauncher.UserIdentity(_session);
                peer = identity.User!;
                process = SessionLauncher.StartAsUser(_session, exe, args);
            }
            else
            {
                peer = PipeChannel.LocalSystem;
                process = SessionLauncher.StartAsSystem(_session, exe, args, restricted, environment);
            }
            _launched.Add(process);
            Progress($"起了{label}进程 {process.Id}");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(30_000);
            try
            {
                var channel = await PipeChannel.ListenAsync(pipe, PipeChannel.Security(PipeChannel.LocalSystem, peer), process.Id, timeout.Token);
                var child = new Child(label, process, new PipeRpc(channel));
                _children.Add(child);
                _ = child.Rpc.RunAsync(CancellationToken.None);
                return child;
            }
            catch (Exception ex)
            {
                var exit = process.ExitCode;
                process.Kill();
                if (ex is OperationCanceledException && !ct.IsCancellationRequested)
                    throw new TimeoutException($"{label}进程 30 秒没连上管道（退出码 {exit?.ToString() ?? "还在跑"}）");
                throw;
            }
        }

        /// <summary>系统和服务这边的情况：Windows 版本、UAC 和锁屏的设置、SAS 策略、控制台会话、代理、单文件解压到哪了、本机有没有在跑的远程协助程序。</summary>
        private static JsonObject ServiceEnvironment()
        {
            var env = new JsonObject();
            try
            {
                using var nt = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                env["os"] = new JsonObject
                {
                    ["product"] = nt?.GetValue("ProductName")?.ToString(),
                    ["displayVersion"] = nt?.GetValue("DisplayVersion")?.ToString(),
                    ["build"] = nt?.GetValue("CurrentBuild")?.ToString(),
                    ["ubr"] = nt?.GetValue("UBR")?.ToString(),
                    ["edition"] = nt?.GetValue("EditionID")?.ToString(),
                };
                using var policies = Registry.LocalMachine.OpenSubKey(SasKeyPath);
                using var personalization = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows\Personalization");
                using var winlogon = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon");
                env["policies"] = new JsonObject
                {
                    ["EnableLUA"] = policies?.GetValue("EnableLUA")?.ToString(),
                    ["ConsentPromptBehaviorAdmin"] = policies?.GetValue("ConsentPromptBehaviorAdmin")?.ToString(),
                    ["ConsentPromptBehaviorUser"] = policies?.GetValue("ConsentPromptBehaviorUser")?.ToString(),
                    ["PromptOnSecureDesktop"] = policies?.GetValue("PromptOnSecureDesktop")?.ToString(),
                    ["DisableCAD"] = policies?.GetValue("DisableCAD")?.ToString(),
                    ["DisableLockWorkstation"] = policies?.GetValue("DisableLockWorkstation")?.ToString(),
                    ["NoLockScreen"] = personalization?.GetValue("NoLockScreen")?.ToString(),
                    ["AutoAdminLogon"] = winlogon?.GetValue("AutoAdminLogon")?.ToString(),
                    ["SoftwareSASGeneration"] = SasDescribe(),
                };
            }
            catch (Exception ex)
            {
                env["registryError"] = ex.Message;
            }
            var console = ConsoleSession.ActiveId();
            env["console"] = new JsonObject
            {
                ["session"] = console,
                ["lock"] = console != ConsoleSession.None ? ConsoleSession.Lock(console).ToString() : null,
                ["connectState"] = console != ConsoleSession.None ? ConsoleSession.ConnectState(console) : -1,
            };
            env["process"] = ProcessInfo();
            try
            {
                var proxy = HttpClient.DefaultProxy.GetProxy(new Uri("https://www.cloudflare.com/"));
                env["defaultProxy"] = ProxySummary(proxy?.ToString());
                env["winhttpProxy"] = WinHttpDefaultProxy();
            }
            catch (Exception ex)
            {
                env["proxyError"] = ex.Message;
            }
            env["agents"] = new JsonArray([.. Process.GetProcessesByName("PrivateChat.Agent").Where(p => p.Id != Environment.ProcessId)
                .Select(p => (JsonNode)new JsonObject { ["session"] = p.SessionId })]);
            env["os2"] = Try(() => JsonSerializer.SerializeToNode(HardwareInfo.Os()));
            return env;

            static JsonNode? Try(Func<JsonNode?> f)
            {
                try { return f(); }
                catch (Exception ex) { return ex.Message; }
            }
        }

        private static string WinHttpDefaultProxy()
        {
            var info = new Native.WINHTTP_PROXY_INFO();
            if (!Native.WinHttpGetDefaultProxyConfiguration(ref info)) return $"出错 {Marshal.GetLastWin32Error()}";
            try
            {
                return $"type {info.dwAccessType} {ProxySummary(Marshal.PtrToStringUni(info.lpszProxy))}";
            }
            finally
            {
                if (info.lpszProxy != IntPtr.Zero) Native.GlobalFree(info.lpszProxy);
                if (info.lpszProxyBypass != IntPtr.Zero) Native.GlobalFree(info.lpszProxyBypass);
            }
        }
    }

    /// <summary>控制台会话里的 consent.exe（UAC 提示）。</summary>
    private static List<string> ConsentProcesses(uint session)
        => [.. Process.GetProcessesByName("consent").Where(p => (uint)p.SessionId == session).Select(p => $"consent.exe {p.Id}")];

    private static JsonObject SasDescribe()
    {
        var now = ReadSas();
        return new JsonObject { ["kind"] = now.Kind?.ToString() ?? "absent", ["value"] = now.Value };
    }

    /// <summary>代理只记有没有、是不是本机（Clash / v2rayN 的"系统代理"是本机端口），不记地址。</summary>
    private static string ProxySummary(string? proxy)
    {
        if (string.IsNullOrWhiteSpace(proxy)) return "无";
        var loopback = proxy.Contains("127.0.0.1", StringComparison.Ordinal) || proxy.Contains("localhost", StringComparison.OrdinalIgnoreCase) || proxy.Contains("[::1]", StringComparison.Ordinal);
        return loopback ? "有（本机端口）" : "有（别的机器）";
    }

    /// <summary>服务起的一个子进程和连着它的管道。</summary>
    private sealed class Child(string label, LaunchedProcess process, PipeRpc rpc) : IAsyncDisposable
    {
        public string Label => label;

        public PipeRpc Rpc => rpc;

        public PipeChannel Channel => rpc.Channel;

        public async Task<JsonObject> Call(string cmd, JsonObject? args = null, int timeoutMs = 20_000, CancellationToken ct = default)
        {
            var request = args ?? new JsonObject();
            request["cmd"] = cmd;
            var reply = await rpc.CallAsync(request, timeoutMs, ct);
            reply.Remove("id");
            return reply;
        }

        public async ValueTask DisposeAsync()
        {
            try { await Call("quit", timeoutMs: 1000); }
            catch (Exception) { }
            if (!process.WaitForExit(1500)) process.Kill();
            process.Dispose();
            await rpc.Channel.DisposeAsync();
        }
    }

    /// <summary>"DOMAIN\张三" → "DOMAIN\张*"：报告里不放完整的用户名。</summary>
    private static string Mask(string user)
    {
        var slash = user.LastIndexOf('\\');
        var name = user[(slash + 1)..];
        return user[..(slash + 1)] + (name.Length <= 1 ? "*" : name[0] + new string('*', name.Length - 1));
    }

    /// <summary>这个进程：账号、会话、特权、单文件解压目录（不记用户的临时目录，里面有用户名）。</summary>
    private static JsonObject ProcessInfo()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new JsonObject
        {
            ["pid"] = Environment.ProcessId,
            ["user"] = Mask(identity.Name),
            ["isSystem"] = identity.IsSystem,
            ["session"] = ConsoleSession.CurrentProcessSession(),
            ["privileges"] = new JsonArray([.. Privileges(identity.Token).Select(p => (JsonNode)p)]),
            ["elevationType"] = ElevationType(identity.Token),
            ["bundleExtract"] = Environment.GetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR"),
            ["baseDirectory"] = identity.IsSystem ? AppContext.BaseDirectory : null,
        };
    }

    /// <summary>令牌里的特权（名字，启用着的后面带 *）。</summary>
    private static List<string> Privileges(IntPtr token)
    {
        var list = new List<string>();
        Native.GetTokenInformation(token, Native.TokenPrivileges, IntPtr.Zero, 0, out var length);
        if (length <= 0) return list;
        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (!Native.GetTokenInformation(token, Native.TokenPrivileges, buffer, length, out _)) return list;
            var count = Marshal.ReadInt32(buffer);
            for (var i = 0; i < count; i++)
            {
                // TOKEN_PRIVILEGES：PrivilegeCount，然后每个 LUID_AND_ATTRIBUTES 12 字节（LUID 8 + Attributes 4）
                var entry = buffer + 4 + i * 12;
                var luid = Marshal.ReadInt64(entry);
                var attributes = Marshal.ReadInt32(entry, 8);
                var name = new System.Text.StringBuilder(64);
                var size = name.Capacity;
                list.Add((Native.LookupPrivilegeNameW(null, ref luid, name, ref size) ? name.ToString() : $"LUID {luid}") + ((attributes & 2) != 0 ? "*" : ""));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return list;
    }

    /// <summary>TokenElevationType：1 没有 UAC 拆分（标准用户 / SYSTEM / 关了 UAC）、2 提权后的、3 管理员没提权的那份。</summary>
    private static int ElevationType(IntPtr token)
    {
        var buffer = Marshal.AllocHGlobal(4);
        try
        {
            return Native.GetTokenInformation(token, Native.TokenElevationType, buffer, 4, out _) ? Marshal.ReadInt32(buffer) : -1;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static partial class Native
    {
        public const int TokenPrivileges = 3;
        public const int TokenElevationType = 18;
        public const int SM_SHUTTINGDOWN = 0x2000;

        [StructLayout(LayoutKind.Sequential)]
        public struct WINHTTP_PROXY_INFO
        {
            public int dwAccessType;
            public IntPtr lpszProxy;
            public IntPtr lpszProxyBypass;
        }

        [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
        [DllImport("kernel32.dll")] public static extern IntPtr GlobalFree(IntPtr memory);
        [DllImport("advapi32.dll", SetLastError = true)] public static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length, out int returned);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool LookupPrivilegeNameW(string? system, ref long luid, System.Text.StringBuilder name, ref int length);
        [DllImport("winhttp.dll", SetLastError = true)] public static extern bool WinHttpGetDefaultProxyConfiguration(ref WINHTTP_PROXY_INFO info);
    }
}

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;
using PrivateChat.Agent.Audio;
using PrivateChat.Agent.Platform;
using PrivateChat.Agent.Video;

namespace PrivateChat.Agent.Diagnostics;

internal static partial class SecureDesktopTest
{
    /// <summary>
    /// 工作进程（服务以 SYSTEM 在控制台会话里起的；restricted 那个去掉了特权；threads 那个专门测"手里有编码器 / 桌面复制的线程还能不能切桌面"）：
    /// 截屏在一个跟着输入桌面走的线程上，注入在另一个同样跟着走的线程上（和正式版打算的一样），按服务的命令做事。
    /// 服务没了（管道断了、服务进程没了）就退出；最多活 15 分钟。
    /// </summary>
    public static int Worker(string pipe, int servicePid, string label, string reportDir)
    {
        AgentLog.Init(Path.Combine(reportDir, $"log-worker-{label}"), console: false);
        AgentLog.Info($"工作进程 {label} 启动，进程 {Environment.ProcessId}");
        ExitWithService(servicePid);
        using var capture = new FollowingThread("capture");
        using var input = new FollowingThread("input");
        var tests = new ThreadTests();
        try
        {
            using var channel = PipeChannel.ConnectAsync(pipe, servicePid, TokenImpersonationLevel.Impersonation, 30_000, CancellationToken.None).GetAwaiter().GetResult();
            PipeRpc.Serve(channel, request => HandleWorker(request, reportDir, capture, input, tests), CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            AgentLog.Error("工作进程出错", ex);
            return 1;
        }
        return 0;
    }

    /// <summary>服务进程没了（或者过了 15 分钟）就退出：不留下没人管的 SYSTEM 进程。</summary>
    private static void ExitWithService(int servicePid)
    {
        var handle = Native.OpenProcess(Native.SYNCHRONIZE, false, (uint)servicePid);
        new Thread(() =>
        {
            if (handle != IntPtr.Zero) Native.WaitForSingleObject(handle, (int)TimeSpan.FromMinutes(15).TotalMilliseconds);
            else Thread.Sleep(TimeSpan.FromMinutes(15));
            AgentLog.Warn("服务没了（或者到了 15 分钟），退出");
            Environment.Exit(3);
        }) { IsBackground = true }.Start();
    }

    private static async Task<JsonObject> HandleWorker(JsonObject request, string reportDir, FollowingThread capture, FollowingThread input, ThreadTests tests)
    {
        var cmd = request["cmd"]?.GetValue<string>();
        switch (cmd)
        {
            case "info":
                return await capture.Run(follower =>
                {
                    var info = ProcessInfo();
                    info["threadDesktop"] = follower.Current;
                    info["inputDesktop"] = InputDesktop.Name();
                    info["screens"] = new JsonArray([.. GdiScreenSource.List().Select(s => (JsonNode)$"{s.DeviceName} {s.Bounds} {(s.IsPrimary ? "主" : "")}")]);
                    try
                    {
                        info["gpus"] = new JsonArray([.. Telemetry.HardwareInfo.Gpus().Select(g => (JsonNode)$"{g.Name}（{g.DriverVersion}）")]);
                    }
                    catch (Exception ex)
                    {
                        info["gpus"] = ex.Message;
                    }
                    return info;
                });
            case "desktop":
                return new JsonObject { ["name"] = InputDesktop.Name(), ["lock"] = ConsoleSession.Lock(ConsoleSession.CurrentProcessSession()).ToString() };
            case "waitDesktop":
            {
                var wantDefault = request["wantDefault"]?.GetValue<bool>() ?? true;
                var timeout = request["timeoutMs"]?.GetValue<int>() ?? 3000;
                var clock = Stopwatch.StartNew();
                string? name;
                bool matched;
                while (true)
                {
                    name = InputDesktop.Name();
                    matched = wantDefault ? name == InputDesktop.Default : name is not null && name != InputDesktop.Default;
                    if (matched || clock.ElapsedMilliseconds >= timeout) break;
                    await Task.Delay(50);
                }
                return new JsonObject { ["matched"] = matched, ["name"] = name, ["ms"] = clock.ElapsedMilliseconds };
            }
            case "probe":
            {
                var label = request["label"]?.GetValue<string>() ?? "probe";
                var thumb = request["thumb"]?.GetValue<bool>() ?? false;
                // 每次截图都在一个新线程上做、做完就扔：线程里不留 D3D / GDI 的东西，下一次切桌面不受它影响
                // （"留着桌面复制的线程能不能跟着切"由 threads 那个进程专门测）
                return await OnFreshThread(follower => Probe(follower, label, thumb, reportDir));
            }
            case "keys":
            {
                var keys = request["keys"]!.AsArray()
                    .Select(k => (Scan: k!["scan"]?.GetValue<int?>(), Vk: k["vk"]?.GetValue<int?>(), Up: k["up"]?.GetValue<bool>() ?? false)).ToList();
                return await input.Run(follower =>
                {
                    var result = Attach(follower);
                    result["sent"] = new JsonArray([.. keys.Select(k => (JsonNode)Inject.Key(k.Scan, k.Vk, k.Up))]);
                    return result;
                });
            }
            case "text":
            {
                var text = request["text"]?.GetValue<string>() ?? "";
                return await input.Run(follower =>
                {
                    var result = Attach(follower);
                    result["sent"] = new JsonArray([.. text.Select(c => (JsonNode)Inject.Unicode(c))]);
                    return result;
                });
            }
            case "mouse":
            {
                var x = request["x"]?.GetValue<double>() ?? 0.5;
                var y = request["y"]?.GetValue<double>() ?? 0.5;
                return await input.Run(follower =>
                {
                    var result = Attach(follower);
                    result["move"] = Inject.MoveTo(x, y);
                    return result;
                });
            }
            case "focus":
                return await input.Run(follower =>
                {
                    var result = Attach(follower);
                    result["focus"] = Inject.FocusInfo();
                    return result;
                });
            case "audio":
                return await Task.Run(TestAudio);
            case "setup":
                return await Task.Run(tests.Setup);
            case "switch":
            {
                var label = request["label"]?.GetValue<string>() ?? "";
                return await Task.Run(() => tests.Switch(label));
            }
            case "sas":
                // 不是服务进程、只是 SYSTEM：看看行不行（文档说要服务或者 uiAccess）
                SasPolicy.SendSAS(false);
                return new JsonObject { ["ok"] = true };
            case "quit":
                _ = Task.Delay(200).ContinueWith(_ => Environment.Exit(0));
                return new JsonObject { ["ok"] = true };
            default:
                return new JsonObject { ["ok"] = false, ["error"] = $"不认识的命令 {cmd}" };
        }
    }

    private static JsonObject Attach(DesktopFollower follower)
    {
        var before = follower.Current;
        var ok = follower.EnsureAttached();
        return new JsonObject { ["attached"] = ok, ["from"] = before, ["desktop"] = follower.Current, ["problem"] = follower.Problem };
    }

    /// <summary>新开一个 MTA 线程，带一个新的 <see cref="DesktopFollower"/>，做完 work 就收掉。最多等 40 秒。</summary>
    private static Task<JsonObject> OnFreshThread(Func<DesktopFollower, JsonObject> work)
    {
        var done = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var follower = new DesktopFollower();
                done.TrySetResult(work(follower));
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
            }
        }) { IsBackground = true, Name = "probe" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        return done.Task.WaitAsync(TimeSpan.FromSeconds(40));
    }

    /// <summary>
    /// 在一个新线程上：跟上输入桌面；GDI 截主显示器，别的显示器各截一张小的；桌面复制新建一个截（最多等 2 秒拿新画面）。
    /// 截到的是平常桌面（没锁屏、输入桌面是 Default）时一律只存小缩略图，不管调用方要的是什么：别把聊天、文档存进报告里。
    /// </summary>
    private static JsonObject Probe(DesktopFollower follower, string label, bool thumb, string reportDir)
    {
        var result = Attach(follower);
        result["label"] = label;
        var inputDesktop = InputDesktop.Name();
        var lockState = ConsoleSession.Lock(ConsoleSession.CurrentProcessSession());
        result["inputDesktop"] = inputDesktop;
        result["lock"] = lockState.ToString();
        if (lockState != ConsoleSession.LockState.Locked
            && (string.Equals(follower.Current, InputDesktop.Default, StringComparison.OrdinalIgnoreCase) || string.Equals(inputDesktop, InputDesktop.Default, StringComparison.OrdinalIgnoreCase)))
            thumb = true;
        result["thumb"] = thumb;
        var screens = GdiScreenSource.List();
        if (screens.Count == 0)
        {
            result["error"] = "没有显示器";
            return result;
        }
        var screen = screens.FirstOrDefault(s => s.IsPrimary) ?? screens[0];
        var (width, height) = FrameGeometry.Fit(screen.Bounds.Width, screen.Bounds.Height, thumb ? 400 : 960, thumb ? 250 : 600);
        result["screen"] = $"{screen.DeviceName} {screen.Bounds.Width}x{screen.Bounds.Height} → {width}x{height}";

        using (var gdi = new GdiScreenSource()) result["gdi"] = Gdi(gdi, screen, width, height, Path.Combine(reportDir, $"{label}-gdi.png"));
        var others = new JsonArray();
        foreach (var other in screens.Where(s => s != screen))
        {
            var (w, h) = FrameGeometry.Fit(other.Bounds.Width, other.Bounds.Height, 320, 200);
            using var gdi = new GdiScreenSource();
            others.Add(Gdi(gdi, other, w, h, Path.Combine(reportDir, $"{label}-gdi-{other.Index}.png")));
        }
        result["gdiOthers"] = others;
        using (var dda = new DxgiScreenSource()) result["dda"] = Dda(dda, screen, width, height, Path.Combine(reportDir, $"{label}-dda.png"), 2000);
        result["cursor"] = Inject.CursorVisible();
        result["inputDesktopAfter"] = InputDesktop.Name();
        return result;
    }

    private static JsonObject Gdi(GdiScreenSource source, ScreenInfo screen, int width, int height, string png)
    {
        try
        {
            var clock = Stopwatch.StartNew();
            var frame = source.Capture(screen, width, height);
            var ms = clock.Elapsed.TotalMilliseconds;
            var mean = Images.MeanLumaBgra(frame.Data, frame.Stride, frame.Width, frame.Height);
            Images.SaveBgra(png, frame.Data, frame.Stride, frame.Width, frame.Height);
            return new JsonObject { ["ok"] = true, ["ms"] = Math.Round(ms, 1), ["mean"] = Math.Round(mean, 1), ["png"] = Path.GetFileName(png) };
        }
        catch (Exception ex)
        {
            return new JsonObject { ["ok"] = false, ["error"] = $"{ex.GetType().Name}：{ex.Message}" };
        }
    }

    /// <summary>最多试 waitMs 拿一幅新画面（刚建好复制时第一下可能只有指针；切桌面后要重建复制），记下每次"没拿到"的原因第一次出现在什么时候。</summary>
    private static JsonObject Dda(DxgiScreenSource source, ScreenInfo screen, int width, int height, string png, int waitMs)
    {
        var before = source.Images;
        var result = new JsonObject();
        var attempts = new JsonArray();
        string? lastProblem = null;
        CapturedFrame? frame = null;
        var clock = Stopwatch.StartNew();
        try
        {
            while (clock.ElapsedMilliseconds < waitMs)
            {
                frame = source.Capture(screen, width, height);
                if (source.Images > before) break;
                if (source.Problem != lastProblem)
                {
                    lastProblem = source.Problem;
                    attempts.Add(new JsonObject { ["ms"] = clock.ElapsedMilliseconds, ["problem"] = lastProblem });
                }
                Thread.Sleep(30);
            }
        }
        catch (Exception ex)
        {
            result["error"] = $"{ex.GetType().Name}：{ex.Message}";
        }
        result["ms"] = clock.ElapsedMilliseconds;
        result["newImages"] = source.Images - before;
        result["problems"] = attempts;
        result["secureDesktop"] = source.SecureDesktop;
        result["name"] = source.Name;
        if (frame is { } f && source.Images > before && f.Format == FrameFormat.Nv12)
        {
            try
            {
                var bgra = Images.Nv12ToBgra(f.Data, f.Width, f.Height);
                result["mean"] = Math.Round(Images.MeanLumaBgra(bgra, f.Width * 4, f.Width, f.Height), 1);
                Images.SaveBgra(png, bgra, f.Width * 4, f.Width, f.Height);
                result["png"] = Path.GetFileName(png);
            }
            catch (Exception ex)
            {
                result["pngError"] = ex.Message;
            }
        }
        return result;
    }

    private static JsonObject TestAudio()
    {
        try
        {
            using var audio = new WasapiLoopback();
            var buffer = new short[AudioFormat.SampleRate * AudioFormat.Channels];
            var samples = 0;
            var peak = 0;
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < 1000)
            {
                var n = audio.Read(buffer);
                for (var i = 0; i < n * AudioFormat.Channels; i++) peak = Math.Max(peak, Math.Abs((int)buffer[i]));
                samples += n;
                Thread.Sleep(20);
            }
            return new JsonObject { ["ok"] = true, ["device"] = audio.Description, ["samples"] = samples, ["peak"] = peak };
        }
        catch (Exception ex)
        {
            return new JsonObject { ["ok"] = false, ["error"] = $"{ex.GetType().Name}：{ex.Message}" };
        }
    }

    /// <summary>
    /// 一直开着的几个线程，各自手里拿着一样东西（什么都不拿、软件编码器、硬件编码器、桌面复制、GDI）：每一步（平常桌面、UAC、Ctrl+Alt+Del、锁屏、之后）
    /// 都让它们跟一次输入桌面（SetThreadDesktop，有窗口或钩子就切不了，ERROR_BUSY），切完再用一次手里的东西。
    /// 正式版的采集线程就是这样：远程中途切到 UAC、切回来，编码器一直在手里。放在单独的进程里，硬件编码器出事也不影响另外两个工作进程。
    /// </summary>
    private sealed class ThreadTests
    {
        private readonly List<(string Name, FollowingThread Thread, Func<object?> Setup, Func<object?, JsonNode?> Use)> _threads = [];
        private readonly Dictionary<string, object?> _state = [];

        public JsonObject Setup()
        {
            var screen = () => GdiScreenSource.List().First(s => s.IsPrimary);
            Add("plain", () => null, _ => null);
            Add("mfSoftware", () => Encoder("software"), Encode);
            Add("mfHardware", () => Encoder("hardware"), Encode);
            Add("dda", () =>
            {
                var source = new DxgiScreenSource();
                source.Capture(screen(), 640, 360);
                return source;
            }, state =>
            {
                if (state is not DxgiScreenSource source) return null;
                var before = source.Images;
                var clock = Stopwatch.StartNew();
                CapturedFrame? frame = null;
                while (clock.ElapsedMilliseconds < 2500 && source.Images == before)
                {
                    frame = source.Capture(screen(), 640, 360);
                    Thread.Sleep(30);
                }
                double? mean = frame is { Format: FrameFormat.Nv12 } f && source.Images > before
                    ? Math.Round(Images.MeanLumaBgra(Images.Nv12ToBgra(f.Data, f.Width, f.Height), f.Width * 4, f.Width, f.Height), 1) : null;
                return new JsonObject { ["newImages"] = source.Images - before, ["problem"] = source.Problem, ["mean"] = mean };
            });
            Add("gdi", () =>
            {
                var gdi = new GdiScreenSource();
                var frame = gdi.Capture(screen(), 640, 360);
                using var stream = new MemoryStream();
                Images.SaveBgra(stream, frame.Data, frame.Stride, frame.Width, frame.Height);
                return gdi;
            }, state =>
            {
                if (state is not GdiScreenSource gdi) return null;
                var frame = gdi.Capture(screen(), 640, 360);
                return new JsonObject { ["mean"] = Math.Round(Images.MeanLumaBgra(frame.Data, frame.Stride, frame.Width, frame.Height), 1) };
            });
            var result = new JsonObject();
            foreach (var (name, thread, setup, _) in _threads)
            {
                var task = thread.Run(_ =>
                {
                    try
                    {
                        _state[name] = setup();
                        return new JsonObject { ["ok"] = true };
                    }
                    catch (Exception ex)
                    {
                        return new JsonObject { ["ok"] = false, ["error"] = $"{ex.GetType().Name}：{ex.Message}" };
                    }
                });
                result[name] = task.Wait(TimeSpan.FromSeconds(20)) ? task.Result : new JsonObject { ["timeout"] = true };
            }
            Environment.SetEnvironmentVariable(VideoEncoders.SwitchVariable, null);
            return result;

            static IDisposable Encoder(string mode)
            {
                Environment.SetEnvironmentVariable(VideoEncoders.SwitchVariable, mode);
                var encoder = VideoEncoders.Create(640, 360, 30, 2000, [], 0, out _);
                encoder.Encode(new byte[640 * 360 * 3 / 2], TimeSpan.Zero);
                return encoder;
            }

            static JsonNode? Encode(object? state)
            {
                if (state is not IVideoEncoder encoder) return null;
                var frame = encoder.Encode(new byte[640 * 360 * 3 / 2], TimeSpan.FromMilliseconds(33));
                return new JsonObject { ["encoder"] = encoder.Name, ["output"] = frame?.Data.Length ?? 0 };
            }
        }

        private void Add(string name, Func<object?> setup, Func<object?, JsonNode?> use) => _threads.Add((name, new FollowingThread($"test-{name}"), setup, use));

        public JsonObject Switch(string label)
        {
            var result = new JsonObject { ["label"] = label, ["inputDesktop"] = InputDesktop.Name() };
            foreach (var (name, thread, _, use) in _threads)
            {
                var task = thread.Run(follower =>
                {
                    var one = Attach(follower);
                    try
                    {
                        one["use"] = use(_state.GetValueOrDefault(name));
                    }
                    catch (Exception ex)
                    {
                        one["use"] = $"{ex.GetType().Name}：{ex.Message}";
                    }
                    return one;
                });
                result[name] = task.Wait(TimeSpan.FromSeconds(10)) ? task.Result : new JsonObject { ["timeout"] = true };
            }
            return result;
        }
    }

    /// <summary>
    /// 界面进程（服务以登录用户的身份在控制台会话里起的）：弹 UAC（以管理员身份运行 cmd /c exit）、锁屏、看用户这边看到的输入桌面。
    /// 弹 UAC 时先开一个置顶的小窗口放到前台再申请（后台进程申请的话，UAC 不直接弹出来，只在任务栏上闪）。日志不写进报告目录（那里普通用户写不了）。
    /// </summary>
    public static int Helper(string pipe, int servicePid, string reportDir)
    {
        AgentLog.Init(Path.Combine(Path.GetTempPath(), "PrivateChatDesktopTest"), console: false, fileName: "helper.log");
        ExitWithService(servicePid);
        TaskCompletionSource<JsonObject>? elevation = null;
        try
        {
            using var channel = PipeChannel.ConnectAsync(pipe, servicePid, TokenImpersonationLevel.Identification, 30_000, CancellationToken.None).GetAwaiter().GetResult();
            PipeRpc.Serve(channel, async request =>
            {
                switch (request["cmd"]?.GetValue<string>())
                {
                    case "info":
                    {
                        var info = ProcessInfo();
                        info["proxy"] = IeProxy();
                        info["inputDesktop"] = InputDesktop.Name();
                        return info;
                    }
                    case "desktop":
                        return new JsonObject
                        {
                            ["name"] = InputDesktop.Name(),
                            ["lock"] = ConsoleSession.Lock(ConsoleSession.CurrentProcessSession()).ToString(),
                        };
                    case "elevate":
                        elevation = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
                        Elevate(elevation);
                        return new JsonObject { ["started"] = true };
                    case "elevateDone":
                        return new JsonObject { ["done"] = elevation is null || elevation.Task.IsCompleted };
                    case "elevateResult":
                    {
                        if (elevation is null) return new JsonObject { ["ok"] = false, ["error"] = "没弹过" };
                        var timeout = request["timeoutMs"]?.GetValue<int>() ?? 5000;
                        try
                        {
                            return (JsonObject)(await elevation.Task.WaitAsync(TimeSpan.FromMilliseconds(timeout))).DeepClone();
                        }
                        catch (TimeoutException)
                        {
                            return new JsonObject { ["ok"] = false, ["error"] = "还没有结果（提示还开着）" };
                        }
                    }
                    case "lock":
                    {
                        var ok = Native.LockWorkStation();
                        return new JsonObject { ["ok"] = ok, ["error"] = ok ? 0 : Marshal.GetLastWin32Error() };
                    }
                    case "quit":
                        _ = Task.Delay(200).ContinueWith(_ => Environment.Exit(0));
                        return new JsonObject { ["ok"] = true };
                    default:
                        return new JsonObject { ["ok"] = false, ["error"] = "不认识的命令" };
                }
            }, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            AgentLog.Error("界面进程出错", ex);
            return 1;
        }
        return 0;
    }

    /// <summary>
    /// 在一个 STA 线程上开个置顶的小窗口、放到前台（实验窗口那边每 0.3 秒 AllowSetForegroundWindow(ASFW_ANY)，允许我们这么做），
    /// 再以它为父窗口申请"以管理员身份运行"：这样 UAC 直接切到安全桌面。结果（同意 / 拒绝 / 出错）放进 done。
    /// </summary>
    private static void Elevate(TaskCompletionSource<JsonObject> done)
    {
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new Form
                {
                    Text = "PrivateChat 实验",
                    TopMost = true,
                    Width = 380,
                    Height = 130,
                    StartPosition = FormStartPosition.CenterScreen,
                    FormBorderStyle = FormBorderStyle.FixedToolWindow,
                };
                form.Controls.Add(new Label { Text = "正在测试管理员确认（UAC），会自动点掉……", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter });
                form.Shown += (_, _) =>
                {
                    form.Activate();
                    var foreground = Native.SetForegroundWindow(form.Handle);
                    var isForeground = Native.GetForegroundWindow() == form.Handle;
                    var owner = form.Handle;
                    // ShellExecuteEx 自己调：父窗口交给它（.NET 的 Process.Start 只在 ErrorDialog=true 时才传父窗口），不弹出错对话框。
                    // 它要等 UAC 有了结果才返回，放在另一个 STA 线程上，窗口这边照常处理消息
                    var shell = new Thread(() =>
                    {
                        var clock = Stopwatch.StartNew();
                        var info = new Native.SHELLEXECUTEINFOW
                        {
                            cbSize = Marshal.SizeOf<Native.SHELLEXECUTEINFOW>(),
                            fMask = Native.SEE_MASK_NOCLOSEPROCESS | Native.SEE_MASK_NOASYNC | Native.SEE_MASK_FLAG_NO_UI,
                            hwnd = owner,
                            lpVerb = "runas",
                            lpFile = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                            lpParameters = "/c exit",
                            nShow = 0,
                        };
                        JsonObject result;
                        if (Native.ShellExecuteExW(ref info))
                        {
                            if (info.hProcess != IntPtr.Zero) Native.CloseHandle(info.hProcess);
                            result = new JsonObject { ["ok"] = true, ["result"] = "同意了（程序以管理员身份运行了）" };
                        }
                        else
                        {
                            var error = Marshal.GetLastWin32Error();
                            result = new JsonObject { ["ok"] = error == 1223, ["result"] = error == 1223 ? "拒绝了（ERROR_CANCELLED）" : $"出错 {error}", ["error"] = error };
                        }
                        result["ms"] = clock.ElapsedMilliseconds;
                        result["setForeground"] = foreground;
                        result["wasForeground"] = isForeground;
                        done.TrySetResult(result);
                        try { form.BeginInvoke(form.Close); }
                        catch (Exception) { }
                    }) { IsBackground = true, Name = "runas" };
                    shell.SetApartmentState(ApartmentState.STA);
                    shell.Start();
                };
                Application.Run(form);
            }
            catch (Exception ex)
            {
                done.TrySetResult(new JsonObject { ["ok"] = false, ["error"] = ex.Message });
            }
        }) { IsBackground = true, Name = "elevate" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    /// <summary>登录用户在"Internet 选项"里设的代理（Clash / v2rayN 的"系统代理"就是这个）：以 SYSTEM 跑的服务看不到它。只记有没有、是不是本机。</summary>
    private static JsonObject IeProxy()
    {
        var config = new Native.WINHTTP_CURRENT_USER_IE_PROXY_CONFIG();
        if (!Native.WinHttpGetIEProxyConfigForCurrentUser(ref config)) return new JsonObject { ["error"] = Marshal.GetLastWin32Error() };
        try
        {
            return new JsonObject
            {
                ["autoDetect"] = config.fAutoDetect,
                ["autoConfig"] = config.lpszAutoConfigUrl != IntPtr.Zero,
                ["proxy"] = ProxySummary(Marshal.PtrToStringUni(config.lpszProxy)),
            };
        }
        finally
        {
            foreach (var p in new[] { config.lpszAutoConfigUrl, config.lpszProxy, config.lpszProxyBypass })
                if (p != IntPtr.Zero) Native.GlobalFree(p);
        }
    }

    /// <summary>一个专门的 MTA 线程，带一个 <see cref="DesktopFollower"/>：交给它的活按顺序在这个线程上做。</summary>
    private sealed class FollowingThread : IDisposable
    {
        private readonly BlockingCollection<Action<DesktopFollower>> _work = [];
        private readonly Thread _thread;

        public FollowingThread(string name)
        {
            _thread = new Thread(() =>
            {
                using var follower = new DesktopFollower();
                foreach (var work in _work.GetConsumingEnumerable()) work(follower);
            }) { Name = name, IsBackground = true };
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
        }

        public Task<JsonObject> Run(Func<DesktopFollower, JsonObject> work)
        {
            var done = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            _work.Add(follower =>
            {
                try { done.SetResult(work(follower)); }
                catch (Exception ex) { done.SetException(ex); }
            });
            return done.Task;
        }

        public void Dispose() => _work.CompleteAdding();
    }

    /// <summary>注入（SendInput）：记下返回值和出错码——注入不进去时 SendInput 返回 0，被 UIPI 挡住时却照样返回 1，所以还要看截图和焦点。</summary>
    private static class Inject
    {
        public static string Key(int? scan, int? vk, bool up)
        {
            var input = new Native.INPUT { type = 1 };
            if (scan is { } s) input.ki = new Native.KEYBDINPUT { wScan = (ushort)s, dwFlags = 0x0008u | (up ? 0x0002u : 0u) };
            else input.ki = new Native.KEYBDINPUT { wVk = (ushort)(vk ?? 0), dwFlags = up ? 0x0002u : 0u };
            return Send(input);
        }

        public static string Unicode(char c)
        {
            var down = Send(new Native.INPUT { type = 1, ki = new Native.KEYBDINPUT { wScan = c, dwFlags = 0x0004 } });
            var up = Send(new Native.INPUT { type = 1, ki = new Native.KEYBDINPUT { wScan = c, dwFlags = 0x0004 | 0x0002 } });
            return $"{down}/{up}";
        }

        /// <summary>把鼠标移到主显示器上 (x, y)（0–1 的比例）：和远程时一样用虚拟桌面的绝对坐标；移完读回指针位置。</summary>
        public static JsonObject MoveTo(double x, double y)
        {
            var screen = GdiScreenSource.List().First(s => s.IsPrimary).Bounds;
            var virtualScreen = SystemInformation.VirtualScreen;
            var targetX = screen.X + (int)(x * (screen.Width - 1));
            var targetY = screen.Y + (int)(y * (screen.Height - 1));
            var dx = (int)Math.Round((targetX - virtualScreen.X) * 65535.0 / Math.Max(1, virtualScreen.Width - 1));
            var dy = (int)Math.Round((targetY - virtualScreen.Y) * 65535.0 / Math.Max(1, virtualScreen.Height - 1));
            var sent = Send(new Native.INPUT { type = 0, mi = new Native.MOUSEINPUT { dx = dx, dy = dy, dwFlags = 0x0001 | 0x8000 | 0x4000 } });
            Thread.Sleep(100);
            var ok = Native.GetCursorPos(out var pos);
            return new JsonObject
            {
                ["sent"] = sent,
                ["target"] = $"{targetX},{targetY}",
                ["actual"] = ok ? $"{pos.x},{pos.y}" : $"GetCursorPos 失败（{Marshal.GetLastWin32Error()}）",
            };
        }

        public static JsonObject CursorVisible()
        {
            var info = new Native.CURSORINFO { cbSize = Marshal.SizeOf<Native.CURSORINFO>() };
            var ok = Native.GetCursorInfo(ref info);
            return new JsonObject { ["ok"] = ok, ["error"] = ok ? 0 : Marshal.GetLastWin32Error(), ["showing"] = (info.flags & 1) != 0, ["x"] = info.x, ["y"] = info.y };
        }

        /// <summary>这个桌面上焦点 / 活动窗口是谁（类名、哪个程序、哪个会话）：按键没效果时分得清是"被挡住了"还是"焦点不在那儿"。</summary>
        public static JsonObject FocusInfo()
        {
            var info = new Native.GUITHREADINFO { cbSize = Marshal.SizeOf<Native.GUITHREADINFO>() };
            var result = new JsonObject();
            if (!Native.GetGUIThreadInfo(0, ref info))
            {
                result["error"] = Marshal.GetLastWin32Error();
                return result;
            }
            result["focus"] = Window(info.hwndFocus);
            result["active"] = Window(info.hwndActive);
            result["foreground"] = Window(Native.GetForegroundWindow());
            return result;
        }

        private static JsonNode? Window(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return null;
            var name = new StringBuilder(256);
            Native.GetClassNameW(hwnd, name, name.Capacity);
            Native.GetWindowThreadProcessId(hwnd, out var pid);
            string process;
            int session;
            try
            {
                using var p = Process.GetProcessById((int)pid);
                process = p.ProcessName;
                session = p.SessionId;
            }
            catch (Exception)
            {
                process = $"进程 {pid}";
                session = -1;
            }
            return new JsonObject { ["class"] = name.ToString(), ["process"] = process, ["session"] = session };
        }

        private static string Send(Native.INPUT input)
        {
            var sent = Native.SendInput(1, [input], Marshal.SizeOf<Native.INPUT>());
            return sent == 1 ? "1" : $"0（{Marshal.GetLastWin32Error()}）";
        }
    }

    /// <summary>存 PNG（缩小过的截图）、算平均亮度（全黑 = 没截到）、NV12 转回 BGRA（BT.709 有限范围）。</summary>
    private static class Images
    {
        public static double MeanLumaBgra(byte[] bgra, int stride, int width, int height)
        {
            double sum = 0;
            var count = 0;
            for (var y = 0; y < height; y += 8)
            for (var x = 0; x < width; x += 8)
            {
                var i = y * stride + x * 4;
                sum += 0.0722 * bgra[i] + 0.7152 * bgra[i + 1] + 0.2126 * bgra[i + 2];
                count++;
            }
            return count == 0 ? 0 : sum / count;
        }

        public static byte[] Nv12ToBgra(byte[] nv12, int width, int height)
        {
            var bgra = new byte[width * height * 4];
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var luma = 1.164 * (nv12[y * width + x] - 16);
                var uv = width * height + y / 2 * width + (x & ~1);
                double u = nv12[uv] - 128, v = nv12[uv + 1] - 128;
                var o = (y * width + x) * 4;
                bgra[o] = Clamp(luma + 2.112 * u);
                bgra[o + 1] = Clamp(luma - 0.213 * u - 0.533 * v);
                bgra[o + 2] = Clamp(luma + 1.793 * v);
                bgra[o + 3] = 255;
            }
            return bgra;

            static byte Clamp(double value) => (byte)Math.Clamp((int)Math.Round(value), 0, 255);
        }

        public static void SaveBgra(string path, byte[] bgra, int stride, int width, int height)
        {
            using var file = File.Create(path);
            SaveBgra(file, bgra, stride, width, height);
        }

        public static void SaveBgra(Stream stream, byte[] bgra, int stride, int width, int height)
        {
            using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppRgb);
            var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
            try
            {
                for (var y = 0; y < height; y++) Marshal.Copy(bgra, y * stride, data.Scan0 + y * data.Stride, width * 4);
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
            bitmap.Save(stream, ImageFormat.Png);
        }
    }

    private static partial class Native
    {
        public const uint SYNCHRONIZE = 0x00100000;

        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Explicit)]
        public struct INPUT
        {
            [FieldOffset(0)] public int type;
            [FieldOffset(8)] public MOUSEINPUT mi;
            [FieldOffset(8)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct CURSORINFO { public int cbSize; public int flags; public IntPtr hCursor; public int x; public int y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int x; public int y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int left, top, right, bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct GUITHREADINFO
        {
            public int cbSize;
            public int flags;
            public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
            public RECT rcCaret;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct WINHTTP_CURRENT_USER_IE_PROXY_CONFIG
        {
            [MarshalAs(UnmanagedType.Bool)] public bool fAutoDetect;
            public IntPtr lpszAutoConfigUrl;
            public IntPtr lpszProxy;
            public IntPtr lpszProxyBypass;
        }

        public const uint SEE_MASK_NOCLOSEPROCESS = 0x40;
        public const uint SEE_MASK_NOASYNC = 0x100;
        public const uint SEE_MASK_FLAG_NO_UI = 0x400;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct SHELLEXECUTEINFOW
        {
            public int cbSize;
            public uint fMask;
            public IntPtr hwnd;
            public string? lpVerb;
            public string? lpFile;
            public string? lpParameters;
            public string? lpDirectory;
            public int nShow;
            public IntPtr hInstApp;
            public IntPtr lpIDList;
            public string? lpClass;
            public IntPtr hkeyClass;
            public uint dwHotKey;
            public IntPtr hIconOrMonitor;
            public IntPtr hProcess;
        }

        [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)] public static extern bool ShellExecuteExW(ref SHELLEXECUTEINFOW info);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr handle);
        [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint count, INPUT[] inputs, int size);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool GetCursorInfo(ref CURSORINFO info);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool GetCursorPos(out POINT point);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool LockWorkStation();
        [DllImport("user32.dll", SetLastError = true)] public static extern bool GetGUIThreadInfo(uint threadId, ref GUITHREADINFO info);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(IntPtr hwnd, StringBuilder name, int max);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
        [DllImport("kernel32.dll")] public static extern int WaitForSingleObject(IntPtr handle, int milliseconds);
        [DllImport("winhttp.dll", SetLastError = true)] public static extern bool WinHttpGetIEProxyConfigForCurrentUser(ref WINHTTP_CURRENT_USER_IE_PROXY_CONFIG config);
    }
}

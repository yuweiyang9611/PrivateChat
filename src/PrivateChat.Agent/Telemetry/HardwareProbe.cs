using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using PrivateChat.Contracts.Remote;

namespace PrivateChat.Agent.Telemetry;

/// <summary>
/// 探测子进程：PrivateChat.Agent.exe --probe-hardware OUT.json（Agent 在没有远程的时候自己起，见 <see cref="HardwareTelemetry"/>；也可以手动跑来看）。
/// 依次看：系统和处理器、内存 → 显卡（DXGI）→ 显示器 → GDI 截屏 → 每块显示器的桌面复制 → 每个 H.264 编码器（先微软的软件编码器、再各家硬件的），
/// 结果写成 <see cref="AgentTelemetrySnapshot"/> 的 JSON。
/// 每做完一步就把到目前为止的结果写一遍（先写 .tmp 再换名），最后一条 Problems 是"探测没做完，停在：……"；全部做完再写一遍不带这条的。
/// 父进程等不到（超时杀掉）或子进程被显卡驱动带崩时，读到的就是已经做完的部分和停在了哪一步。
/// 不弹任何窗口（连崩溃对话框也关掉），不碰单实例锁、不写日志；75 秒还没做完就自己结束（父进程 60 秒就会杀；父进程被强行结束时
/// 作业对象也会带走它，见 <see cref="KillOnCloseJob"/>——这是最后一道，防放不进作业对象时变成孤儿）。
/// </summary>
internal static class HardwareProbe
{
    public const string UnfinishedPrefix = "探测没做完，停在：";
    public const string LockedProblem = "探测时这台电脑锁着屏：桌面复制、截屏的结果不准（解锁后会重新探测）";
    /// <summary>过了这个时间不再开始新的试编（父进程 60 秒就杀）。</summary>
    private static readonly TimeSpan StartTestsBefore = TimeSpan.FromSeconds(40);
    private static readonly TimeSpan SelfLimit = TimeSpan.FromSeconds(75);
    private const int MaxProblems = 12;

    /// <summary>探测结果文件（子进程的输出、Agent 的缓存）：中文不转义，人打开也看得懂。</summary>
    public static readonly JsonSerializerOptions FileJson = new(ServerApi.Json) { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static int Run(string path)
    {
        // 显卡驱动在子进程里崩了：别弹"已停止工作"的对话框，直接退出（父进程看退出码）
        SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX);
        var code = 1;
        // MTA 线程（Main 是 STA）：和远程时编码、截屏的线程一样
        var thread = new Thread(() => code = Probe(Path.GetFullPath(path))) { IsBackground = true, Name = "probe" };
        thread.Start();
        if (!thread.Join(SelfLimit)) Process.GetCurrentProcess().Kill(); // 卡在驱动里：等不到了
        return code;
    }

    private static int Probe(string path)
    {
        var clock = Stopwatch.StartNew();
        var result = new Builder(path);

        result.Step("系统和处理器", () =>
        {
            result.Os = HardwareInfo.Os();
            result.Cpu = HardwareInfo.Cpu();
            result.Memory = HardwareInfo.MemoryBytes();
            result.RemoteDesktop = HardwareInfo.RemoteDesktopSession();
            result.Fingerprint = HardwareInfo.Fingerprint();
            if (HardwareInfo.ScreenLocked()) result.AddProblem(LockedProblem);
        });
        result.Step("显卡", () => result.Gpus.AddRange(HardwareInfo.Gpus()));
        result.Step("显示器", () => result.Displays.AddRange(HardwareInfo.Displays(result.Gpus)));
        result.Step("GDI 截屏", () => (result.GdiNative, result.GdiScaled) = CaptureProbe.Gdi(), result.AddCaptureError);
        result.Step("桌面复制", () => CaptureProbe.Duplication(result.Duplication, result.TryWrite), result.AddCaptureError);

        var candidates = new List<EncoderCandidate>();
        result.Step("列编码器", () => candidates.AddRange(EncoderProbe.List()));
        foreach (var candidate in candidates)
        {
            result.Step($"编码器 {candidate.Name}", () => result.Encoders.Add(EncoderProbe.Probe(candidate, test: clock.Elapsed < StartTestsBefore)));
            candidate.Dispose();
        }

        try
        {
            result.Write(null);
            return 0;
        }
        catch (Exception)
        {
            return 2; // 写不出结果文件
        }
    }

    /// <summary>探测到目前为止的结果。</summary>
    private sealed class Builder(string path)
    {
        private readonly DateTimeOffset _collectedAt = DateTimeOffset.UtcNow;
        private readonly List<string> _problems = [];

        public TelemetryOs Os = new("", "", Environment.OSVersion.Version.Build, 0, "", Environment.Is64BitOperatingSystem);
        public TelemetryCpu Cpu = new("", Environment.ProcessorCount, Environment.ProcessorCount, 0);
        public long Memory;
        public bool RemoteDesktop;
        public string Fingerprint = "";
        public readonly List<TelemetryGpu> Gpus = [];
        public readonly List<TelemetryDisplay> Displays = [];
        public readonly List<TelemetryEncoder> Encoders = [];
        public readonly List<TelemetryDuplication> Duplication = [];
        public double? GdiNative;
        public double? GdiScaled;
        public string? CaptureError;

        /// <summary>做一步：先写一遍"停在这一步"，做完（或出错记进 Problems）再往下。</summary>
        /// <param name="failed">出错时把说明交给它（而不是记进 Problems）。</param>
        public void Step(string stage, Action action, Action<string>? failed = null)
        {
            TryWrite(stage);
            try
            {
                action();
            }
            catch (Exception ex)
            {
                var text = HResults.Clip($"{stage}出错：{HResults.Explain(ex)}");
                if (failed is not null) failed(text);
                else AddProblem(text);
            }
        }

        public void AddProblem(string text)
        {
            if (_problems.Count < MaxProblems) _problems.Add(text);
        }

        public AgentTelemetrySnapshot Snapshot(string? stage)
        {
            List<string> problems = [.. _problems];
            if (stage is not null) problems.Add(HResults.Clip(UnfinishedPrefix + stage));
            return new AgentTelemetrySnapshot(HardwareInfo.AgentVersion, _collectedAt, Fingerprint, Os, Cpu, Memory,
                [.. Gpus], [.. Displays], [.. Encoders], new TelemetryCapture([.. Duplication], GdiNative, GdiScaled, CaptureError),
                RemoteDesktop, problems);
        }

        public void AddCaptureError(string text) => CaptureError = CaptureError is null ? text : HResults.Clip($"{CaptureError}；{text}", 300);

        /// <param name="stage">正在做哪一步；null = 全部做完了。</param>
        public void Write(string? stage)
        {
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(Snapshot(stage), FileJson));
            File.Move(path + ".tmp", path, overwrite: true);
        }

        /// <summary>中途那几次写不进去不要紧（接着探测）；最后那次再写不进去就退出码 2。</summary>
        public void TryWrite(string? stage)
        {
            try
            {
                Write(stage);
            }
            catch (Exception) { }
        }
    }

    private const int SEM_FAILCRITICALERRORS = 0x0001;
    private const int SEM_NOGPFAULTERRORBOX = 0x0002;
    [DllImport("kernel32.dll")] private static extern int SetErrorMode(int mode);
}

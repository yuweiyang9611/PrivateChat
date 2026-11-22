using PrivateChat.Agent.Video;
using PrivateChat.Contracts.Remote;

namespace PrivateChat.Agent.Session;

/// <summary>自动档现在为什么降了分辨率。</summary>
public enum AutoResolutionReason { None, Network, Cpu }

/// <summary>
/// 自动档的分辨率：在几级里选——画质设置的框、1280×800、960×600，都按屏幕等比缩、不放大；缩出来不比上一级小的不要
/// （1280×720 的屏幕只有 1280×720、960×540 两级）。从最大的一级开始：
/// - 网络：目标码率持续很低就降一级，持续够高再升回去。门槛按像素数折算，以 1280×800 为准：低于 400 kbps 连续 5 秒降，
///   高于 900 kbps 连续 10 秒升；1920×1200 的像素是它的 2.25 倍，门槛也乘 2.25。像素少一半，同样的码率帧率高一倍。
///   降只在画面在动时算：画面不动时码率没理由往上涨（码率控制要看到大帧顺利送到才加），刚连上时还停在起步的 600 kbps，
///   比 1920 宽的屏上第 0 级的门槛（810–900 kbps）还低，不动的桌面不能因此算成网络慢。升不管画面动不动。
/// - 电脑：画面持续在动时，采集加编码一帧的时间（指数平均）连续 3 秒都占了一帧时间的八成五以上，就降一级，之后 60 秒不升回这一级之上
///   （这一级之下因为网络慢降的，网络好了照样退回来）。中间画面停过就从头数。忙不忙只看在这一级上真量到的帧（至少 3 帧）：
///   换了一级（不管为什么换）就从头量，不拿按像素数折算过来的估计——截屏那一块不随分辨率变，从小的一级折算上来会估得偏高，
///   不忙的电脑也会被算成忙。
///   这台电脑软件编码，弱一点的机器整屏在动时赶不上 30 帧，分辨率低一级每帧就省一半左右。
///   降过一次之后，再往上升（不管为什么升）都要先估一下：按像素数折算，上一级每帧要用的时间得在一帧时间的六成以内
///   （拿最近 10 秒内量到的算；最近 10 秒画面没动过、量不到就先升，真忙不过来会再降）。
///   降了之后在新的一级上量够 2 秒（画面在动的时间，至少 10 帧）：每帧没比降之前（忙的那 3 秒里量到的平均）快 15 % 以上就退回去，
///   之后不再因为电脑忙降（直到换了屏幕或画质设置）。还没下结论就因为网络换了一级：这次降不算，不留"60 秒不升""升之前先估"。
///   上面"像素少一半就省一半"不一定对：真实屏幕上截屏（GDI 按原始尺寸截，缩小时还要 HALFTONE 缩放）
///   可能占了大头，它不随输出分辨率变小，缩放甚至比原样截更费；这种电脑降了分辨率只会更糊、照样卡。
/// 纯计算：时间都是调用方传进来的毫秒数，方便测试。只在采集线程上用。
/// </summary>
public sealed class AutoResolution
{
    /// <summary>目标码率持续低于门槛这么久降一级。</summary>
    public const int DownAfterMs = 5000;
    /// <summary>目标码率持续高于门槛这么久升一级。</summary>
    public const int UpAfterMs = 10_000;
    /// <summary>采集加编码一帧占了一帧时间的这么多算忙不过来（每秒的统计里 CpuBound 也按这个算）。</summary>
    public const double BusyShare = 0.85;
    /// <summary>持续忙不过来这么久降一级。</summary>
    public const int BusyForMs = 3000;
    /// <summary>
    /// 换到一级之后，在这一级上量到这么多帧才开始看忙不忙：换了分辨率编码器要重建，头一帧是关键帧、偏重，
    /// 不让它算进"降之前每帧用多久"。
    /// </summary>
    public const int BusyAfterFrames = 3;
    /// <summary>因为电脑忙降了之后，这么久里不往回升。</summary>
    public const int CpuHoldMs = 60_000;
    /// <summary>往上升时，估出来上一级每帧要用的时间不能超过一帧时间的这么多。</summary>
    public const double PredictShare = 0.6;
    /// <summary>每帧用时的平均值超过这么久没更新（画面一直没动）就不拿它估了。</summary>
    public const int FreshMs = 10_000;
    /// <summary>
    /// 两帧之间隔了这么久，"持续忙不过来"从头数：画面一直在动时，量的那些帧前后最多隔 300 ms（上一次变化在 300 ms 以内才算一直在动），
    /// 隔得更久说明中间画面停过。不然拖一下窗口、停 20 秒、再拖一下，会被当成一直忙了 20 秒。
    /// </summary>
    public const int GapMs = 500;
    /// <summary>因为电脑忙降了一级之后，在新的一级上量够这么久（画面一直在动的时间；中间停过的那段不算，接着等）再看降得有没有用。</summary>
    public const int TrialMs = 2000;
    /// <summary>……而且至少量到这么多帧。</summary>
    public const int TrialFrames = 10;
    /// <summary>降了一级之后每帧的用时得降到降之前的这么多以内（至少快 15 %）才算有用；不然退回去，之后不再因为电脑忙降。</summary>
    public const double HelpedShare = 0.85;
    private const int RefPixels = 1280 * 800;
    private const int DownKbps = 400;
    private const int UpKbps = 900;
    private const double Alpha = 0.2;

    private (int Width, int Height)[] _rungs = [(0, 0)];
    /// <summary>每一级是因为什么降到的（第 0 级没有）。</summary>
    private AutoResolutionReason[] _why = [AutoResolutionReason.None];
    private int _rung;
    private long? _slowSince;
    private long? _fastSince;
    /// <summary>正在持续忙不过来：从什么时候起，这期间量到的帧一共用了多少毫秒、几帧（不忙就是 null，三样一起从头数）。</summary>
    private (long Since, double SumMs, int Frames)? _busy;
    /// <summary>
    /// 当前这一级每帧采集加编码的用时（指数平均，毫秒；负数表示还没量到）。换级时按像素数折算过去：
    /// 只拿来估"往上升忙不忙得过来"（看忙不忙用 _rungMs）。
    /// </summary>
    private double _costMs = -1;
    private long _costAt;
    /// <summary>只拿在这一级上真量到的帧算的指数平均（负数表示这一级还没量到）：看忙不忙用它。换了一级从头量，不按像素折算。</summary>
    private double _rungMs = -1;
    /// <summary>在这一级上量到了几帧。</summary>
    private int _rungFrames;
    /// <summary>因为电脑忙降过：之后往上升都要先估一下。</summary>
    private bool _cpuLowered;
    /// <summary>最近一次因为电脑忙降到了第几级：到 _holdUntil 之前不往这一级之上升（之下因为网络慢降的可以退回来）。</summary>
    private int _cpuRung;
    private long _holdUntil = long.MinValue;
    /// <summary>刚因为电脑忙降了一级、还在看降得有没有用（没有就是 null）。</summary>
    private CpuTrial? _trial;
    /// <summary>降过一次发现没用：之后不再因为电脑忙降（Reset 之前）。</summary>
    private bool _cpuStepsOff;

    /// <summary>
    /// 因为电脑忙降了一级之后的观察：降之前在哪一级、那一级每帧用多久（忙的那 3 秒里在那一级上量到的帧的平均，和是几帧的平均），
    /// 降之前"电脑忙"的那几样状态（没用、或者没下结论就换了一级时原样放回去），和在新的一级上量到的帧。
    /// </summary>
    private sealed class CpuTrial(int from, double beforeMs, int beforeFrames, bool cpuLowered, int cpuRung, long holdUntil)
    {
        public int From { get; } = from;
        public double BeforeMs { get; } = beforeMs;
        public int BeforeFrames { get; } = beforeFrames;
        public bool CpuLowered { get; } = cpuLowered;
        public int CpuRung { get; } = cpuRung;
        public long HoldUntil { get; } = holdUntil;
        public int Samples { get; set; }
        public double SumMs { get; set; }
        /// <summary>量到的这些帧一共跨了多久（只算前后隔得不久的两帧之间）。</summary>
        public long SpanMs { get; set; }
    }

    /// <summary>现在在第几级（0 是最大的）。</summary>
    public int Rung => _rung;
    /// <summary>一共几级。</summary>
    public int Rungs => _rungs.Length;
    public (int Width, int Height) Size => _rungs[_rung];
    /// <summary>最近一次降到现在这一级的原因（在第 0 级时是 None）。</summary>
    public AutoResolutionReason Reason => _why[_rung];
    /// <summary>最近量到的每帧采集加编码用时（毫秒，还没量到是 0；换级时按像素数折算过）。</summary>
    public double CostMs => Math.Max(0, _costMs);

    /// <summary>每秒统计里的标志：现在这一级是不是因为网络 / 电脑降下来的（两样都有可能）。</summary>
    public RemoteStatsFlags Flags
    {
        get
        {
            var flags = RemoteStatsFlags.None;
            for (var i = 1; i <= _rung; i++)
                flags |= _why[i] == AutoResolutionReason.Cpu ? RemoteStatsFlags.LoweredForCpu : RemoteStatsFlags.LoweredForNetwork;
            return flags;
        }
    }

    /// <summary>换了屏幕或者画质设置（编码器要按新的重建）：重新排几级，从最大的一级开始，之前量到的都不算了。</summary>
    public void Reset(int screenWidth, int screenHeight, VideoSettings settings)
    {
        (int Width, int Height)[] candidates =
        [
            settings.Box(screenWidth, screenHeight),
            FrameGeometry.Fit(screenWidth, screenHeight, 1280, 800),
            FrameGeometry.Fit(screenWidth, screenHeight, 960, 600),
        ];
        var rungs = new List<(int Width, int Height)>(candidates.Length);
        foreach (var size in candidates)
            if (rungs.Count == 0 || Pixels(size) < Pixels(rungs[^1])) rungs.Add(size);
        _rungs = [.. rungs];
        _why = new AutoResolutionReason[_rungs.Length];
        _rung = 0;
        _slowSince = _fastSince = null;
        _busy = null;
        _costMs = _rungMs = -1;
        _rungFrames = 0;
        _cpuLowered = false;
        _cpuRung = 0;
        _holdUntil = long.MinValue;
        _trial = null;
        _cpuStepsOff = false;
    }

    /// <summary>
    /// 采集循环每一轮调一次，带上现在的目标码率。moving：画面在不在动（不动时不算"网络慢"，见类的说明）。
    /// 降 / 升了一级时返回给日志的一句话。
    /// </summary>
    public string? OnNetwork(int targetKbps, bool moving, double frameIntervalMs, long nowMs)
    {
        var slow = moving && _rung + 1 < _rungs.Length && targetKbps < DownKbps * Scale(_rung);
        var fast = _rung > 0 && targetKbps > UpKbps * Scale(_rung - 1);
        _slowSince = slow ? _slowSince ?? nowMs : null;
        _fastSince = fast ? _fastSince ?? nowMs : null;
        if (_slowSince is { } since && nowMs - since >= DownAfterMs)
        {
            Step(_rung + 1, AutoResolutionReason.Network, nowMs);
            return $"自动画质：网络慢（目标码率 {targetKbps} kbps），分辨率降到 {Size.Width}x{Size.Height}";
        }
        if (_fastSince is { } fastFrom && nowMs - fastFrom >= UpAfterMs && CpuAllows(_rung - 1, frameIntervalMs, nowMs))
        {
            Step(_rung - 1, AutoResolutionReason.None, nowMs);
            return $"自动画质：分辨率回到 {Size.Width}x{Size.Height}";
        }
        return null;
    }

    /// <summary>
    /// 画面持续在动时，每编完一帧调一次：这一帧采集（含比较变化）加编码（含转 NV12）用了多久。
    /// frameIntervalMs：按现在实际用的帧率，一帧有多少毫秒。因为电脑忙降了一级、或者发现降了没用又退回去时，返回给日志的一句话。
    /// </summary>
    public string? OnFrameCost(double costMs, double frameIntervalMs, long nowMs)
    {
        var gap = nowMs - _costAt;
        // 和上一帧隔得太久：中间画面停过，之前那段忙不算连着的（平均值留着：停之前量到的还是这台电脑的样子）
        if (gap > GapMs) _busy = null;
        _costMs = _costMs < 0 ? costMs : _costMs + Alpha * (costMs - _costMs);
        _rungFrames++;
        // 换到这一级后的头几帧不算：编码器重建，头一帧是关键帧，要平常的两到七倍。从它起算的平均值要好几帧才退下来，
        // 打字那种几百毫秒一帧时，光这一帧就能让一台不忙的电脑"忙"满 3 秒
        if (_rungFrames >= BusyAfterFrames) _rungMs = _rungMs < 0 ? costMs : _rungMs + Alpha * (costMs - _rungMs);
        _costAt = nowMs;
        if (_trial is { } trial && Judge(trial, costMs, gap) is { } undo) return undo;
        // 忙不忙只看在这一级上真量到的帧，不看按像素折算过来的 _costMs（见类的说明）
        var busy = _rungFrames >= BusyAfterFrames && _rungMs >= BusyShare * frameIntervalMs;
        if (!busy) _busy = null;
        else
        {
            var (since, sumMs, frames) = _busy ?? (nowMs, 0.0, 0);
            _busy = (since, sumMs + costMs, frames + 1);
        }
        // 刚降的那一级还没看出有没有用（先等结论，不接着降）；或者已经知道降了也没用
        if (_trial is not null || _cpuStepsOff) return null;
        if (_busy is not { } run || nowMs - run.Since < BusyForMs || _rung + 1 >= _rungs.Length) return null;
        // 降之前每帧用多久：忙的这 3 秒里量到的帧的平均。不用某一刻的指数平均：它抖得厉害，降之前恰好卡了几帧就会偏高，
        // 把"快没快 15 %"翻过来
        var before = run.SumMs / run.Frames;
        var trialFor = new CpuTrial(_rung, before, run.Frames, _cpuLowered, _cpuRung, _holdUntil);
        Step(_rung + 1, AutoResolutionReason.Cpu, nowMs);
        _trial = trialFor;
        return $"自动画质：电脑忙不过来（采集加编码每帧 {before:0} ms，一帧只有 {frameIntervalMs:0} ms），分辨率降到 {Size.Width}x{Size.Height}";
    }

    /// <summary>
    /// 因为电脑忙降了一级之后，在新的一级上量到一帧（gap：和上一帧隔了多久）。量够了就下结论：
    /// 有用就照常往下走；没快多少就退回原来那一级，返回给日志的一句话。
    /// </summary>
    private string? Judge(CpuTrial trial, double costMs, long gap)
    {
        // 只拿在这一级上新量的帧（不用按像素折算过来的平均值），而且和"降之前"一样跳过换级后的头几帧（关键帧偏重，会把"降了没用"算出来）。
        // 前后隔得不久才算画面一直在动的时间；隔久了（中间停过）那段不算，接着等
        if (_rungFrames < BusyAfterFrames) return null;
        if (trial.Samples > 0 && gap <= GapMs) trial.SpanMs += gap;
        trial.Samples++;
        trial.SumMs += costMs;
        if (trial.SpanMs < TrialMs || trial.Samples < TrialFrames) return null;
        _trial = null;
        var after = trial.SumMs / trial.Samples;
        if (after <= HelpedShare * trial.BeforeMs) return null;
        // 没快多少：多半慢在截屏上（不随分辨率变小）。退回原来那一级，每帧用时放回在那一级上量到的（不按像素折算回去），
        // "电脑忙"的状态放回降之前的样子（这次降的一分钟不升、升之前先估都不算了），之后不再因为电脑忙降
        _why[_rung] = AutoResolutionReason.None;
        _rung = trial.From;
        _slowSince = _fastSince = null;
        _busy = null;
        _costMs = _rungMs = trial.BeforeMs;
        _rungFrames = trial.BeforeFrames;
        PutBack(trial);
        _cpuStepsOff = true;
        return $"自动画质：降了分辨率也没快多少（采集加编码每帧 {trial.BeforeMs:0} → {after:0} ms，多半慢在截屏上），回到 {Size.Width}x{Size.Height}";
    }

    /// <summary>
    /// 码率改由另一条线路的那一套来定了（中继 ↔ 快速线路）：之前那条线路"一直很慢"的结论不作数，
    /// 因为网络慢降下来的级数退回去（电脑忙降的不动，往回升也照样要电脑这边允许）。回到了更大的一级时返回给日志的一句话。
    /// </summary>
    public string? OnNetworkChanged(double frameIntervalMs, long nowMs)
    {
        _slowSince = _fastSince = null;
        var moved = false;
        while (_rung > 0 && _why[_rung] == AutoResolutionReason.Network && CpuAllows(_rung - 1, frameIntervalMs, nowMs))
        {
            Step(_rung - 1, AutoResolutionReason.None, nowMs);
            moved = true;
        }
        return moved ? $"自动画质：分辨率回到 {Size.Width}x{Size.Height}" : null;
    }

    /// <summary>电脑这边允不允许升到第 to 级。</summary>
    private bool CpuAllows(int to, double frameIntervalMs, long nowMs)
    {
        if (!_cpuLowered) return true;
        if (to < _cpuRung && nowMs < _holdUntil) return false;
        // 最近 10 秒画面没动过：量不到，先升（真忙不过来会再降）
        if (_costMs < 0 || nowMs - _costAt > FreshMs) return true;
        return _costMs * Pixels(_rungs[to]) / Pixels(_rungs[_rung]) < PredictShare * frameIntervalMs;
    }

    /// <summary>"电脑忙"的那几样状态放回那次降之前的样子（那次降不算了）。</summary>
    private void PutBack(CpuTrial trial) => (_cpuLowered, _cpuRung, _holdUntil) = (trial.CpuLowered, trial.CpuRung, trial.HoldUntil);

    private void Step(int to, AutoResolutionReason reason, long nowMs)
    {
        // 每帧用时大体和像素数成正比：折算到新的一级，往上升之前估的时候接着用（看忙不忙不用它，在新的一级上从头量）
        if (_costMs >= 0) _costMs = _costMs * Pixels(_rungs[to]) / Pixels(_rungs[_rung]);
        if (to > _rung) _why[to] = reason;
        _rung = to;
        _rungMs = -1;
        _rungFrames = 0;
        _slowSince = _fastSince = null;
        _busy = null;
        // 换了一级（不管为什么换）：之前那次"电脑忙降了有没有用"不看了。看着的时候不会因为电脑忙再降，所以这是网络那边换的：
        // 那次降没下结论，"电脑忙"的状态也放回降之前的样子——不然截屏慢的电脑会被那次降留下的"一分钟不升""升之前先估"
        // （按像素估，永远估得升不上去）压在小的一级
        // 那一级也不再记成"因为电脑忙降的"：不然换线路时退不回去，工具栏上一直标着"电脑忙不过来，已降低分辨率"
        if (_trial is { } open)
        {
            PutBack(open);
            _why[open.From + 1] = AutoResolutionReason.Network;
        }
        _trial = null;
        if (reason == AutoResolutionReason.Cpu)
        {
            _cpuLowered = true;
            _cpuRung = to;
            _holdUntil = nowMs + CpuHoldMs;
        }
    }

    private double Scale(int rung) => (double)Pixels(_rungs[rung]) / RefPixels;

    private static long Pixels((int Width, int Height) size) => (long)size.Width * size.Height;
}

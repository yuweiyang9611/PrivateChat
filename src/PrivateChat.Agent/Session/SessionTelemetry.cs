using System.Text.RegularExpressions;
using PrivateChat.Contracts.Remote;

namespace PrivateChat.Agent.Session;

/// <summary>统计线程每秒交给 <see cref="SessionTelemetry"/> 的一个样本（只在控制端连着的那些秒）。</summary>
/// <param name="SentFps">这一秒发出去几帧。</param>
/// <param name="SentKbps">这一秒发了多少（画面加声音）。</param>
/// <param name="TargetKbps">码率控制给的目标码率（画面走哪条线路就是哪条的）。</param>
/// <param name="CaptureMs">这一秒里平均每帧采集（含比较变化）用了多久；这一秒一帧没编是 null（不算进统计）。</param>
/// <param name="EncodeMs">同上，编码（含转 NV12）。</param>
/// <param name="CpuPercent">这个程序占了整台电脑 CPU 的百分之几。</param>
/// <param name="RttMs">往返时间（画面走哪条线路就是哪条的）；还没量到是 0（不算进统计）。</param>
/// <param name="CpuBound">这一秒采集加编码占满了一帧的时间（每秒统计里的 CpuBound）。</param>
/// <param name="Realtime">画面只走快速线路了（控制端确认在那边收到画面；确认之前两条线路都发，算中继）。</param>
/// <param name="Settings">现在的画质设置。</param>
/// <param name="Width">现在编的画面多宽；还没建编码器是 0（不算进"编多大"）。</param>
/// <param name="SecureDesktop">
/// 这台电脑在锁屏 / 系统安全提示界面（不采集）：这一秒的发出帧率、码率、采集 / 编码用时不算进统计——一帧不发是因为没东西可发，不是卡了。
/// </param>
public readonly record struct SessionSecond(
    int SentFps, int SentKbps, int TargetKbps, double? CaptureMs, double? EncodeMs, int CpuPercent, int RttMs, bool CpuBound,
    bool Realtime, VideoSettings Settings, int Width, int Height, bool SecureDesktop);

/// <summary>
/// 一次远程里被控端这边的遥测汇总（<see cref="AgentSessionTelemetry"/>，docs/DESIGN.md"远程协助遥测"）。
/// 统计线程每秒喂一个样本（<see cref="Sample"/>），出事的地方各记一笔（关键帧请求、断流、发送队列满、控制端限帧率、
/// 自动档换分辨率、快速线路接通 / 失败、出错）；中途每 60 秒、结束时各 <see cref="Build"/> 一份交给服务器——
/// 服务器按会话覆盖，所以每份都是从会话开始到现在的汇总。
/// - 每秒的数（帧率、码率、采集 / 编码用时、往返时间……）算成 <see cref="TelemetryStat"/>：样本数、平均、最小、P10、P50、P90、最大。
///   百分位取排好序后的第 ceil(p×n) 个（取到的都是真出现过的某一秒）；数都保留一位小数。
/// - 每个数最多留 2 小时的样本（<see cref="MaxKeptSamples"/>）：满了就隔一个扔一个，之后也隔一个记一个（又满了再减半），
///   留下的始终均匀铺满整次远程。样本数、平均、最小、最大按全部样本算，不受影响。
/// - 出错和线路问题只记程序自己的说明和异常的说明（不含屏幕内容、键盘输入、剪贴板；说明里的文件路径换掉，路径里可能有用户名，
///   见 <see cref="ScrubPaths"/>）：相同的只留一条、后面写次数。
/// 纯计算、不碰 Windows（测试项目把这个文件编进去测）。哪个线程都能调：都在一把锁里，锁里只做几次加法、拷几个数组；
/// <see cref="Build"/> 排序在锁外，采集线程最多等一次拷贝。
/// </summary>
public sealed class SessionTelemetry(string agentVersion)
{
    /// <summary>设成 off 就什么遥测都不报：不探测硬件、不上报，每次远程的汇总也不做。</summary>
    public const string SwitchVariable = "PRIVATECHAT_AGENT_TELEMETRY";
    /// <summary>每个数最多留这么多个样本（每秒一个，2 小时）：再多就减半，见类的说明。得是偶数。</summary>
    public const int MaxKeptSamples = 7200;
    /// <summary>出错、线路问题各最多报这么多条（服务器也只收这么多）：不同的说明多了，前面的照记，后来的合成最后一条"另有 N 次"。</summary>
    public const int MaxNotes = 20;
    /// <summary>画质设置、编码大小各最多报这么多种（服务器也只收这么多）：多了只报用得最久的。</summary>
    public const int MaxUses = 32;
    /// <summary>
    /// 每条说明最多这么多字（服务器收 300 字，后面还要加"（N 次）"）。JSON 里每个汉字写成 \uXXXX 占 6 个字节：
    /// 两样各 20 条写满也只有 40 KB 左右，整份报告在服务器的 64 KB 以内。
    /// </summary>
    public const int MaxNoteLength = 160;

    /// <summary>环境变量 <see cref="SwitchVariable"/> 是 off / 0 / false。</summary>
    public static bool Disabled => Environment.GetEnvironmentVariable(SwitchVariable)?.Trim().ToLowerInvariant() is "off" or "0" or "false";

    /// <summary>自动档换了一级分辨率：网络那边换的（降或升）、电脑忙降的、降了没用退回去的。</summary>
    public enum AutoStep { Network, Cpu, CpuUndo }

    private static readonly TelemetryStat Empty = new(0, 0, 0, 0, 0, 0, 0);

    private readonly Lock _gate = new();
    private readonly Series _sentFps = new(), _sentKbps = new(), _targetKbps = new(), _captureMs = new(), _encodeMs = new(), _cpuPercent = new(), _rttMs = new();
    private readonly Series _lossPermille = new(), _receivedKbps = new(), _jitterBufferMs = new(), _viewerFps = new();
    private readonly Uses<(RemoteVideoMode Mode, int MaxWidth, int MaxHeight, int Fps, int MaxKbps)> _settings = new();
    private readonly Uses<(int Width, int Height)> _sizes = new();
    private readonly Notes _lineProblems = new(), _errors = new();
    private int _revision;
    private int _videoSeconds, _relaySeconds, _realtimeSeconds, _cpuBoundSeconds, _secureSeconds;
    private int _autoNetworkSteps, _autoCpuSteps, _autoCpuUndos;
    private int _keyFrames, _stalls, _relayDrops;
    private int _maxFpsCaps, _minMaxFps;
    private int _lineConnects, _lineFailures;
    private string _encoder = "", _capture = "";
    private bool _qualityMode;

    /// <summary>每记一笔加一：和上次报的时候一样就是什么都没变（中途那份可以不报）。</summary>
    public int Revision
    {
        get { lock (_gate) return _revision; }
    }

    /// <summary>统计线程每秒一次（控制端连着时）。</summary>
    public void Sample(SessionSecond s)
    {
        lock (_gate)
        {
            _revision++;
            _videoSeconds++;
            if (s.Realtime) _realtimeSeconds++;
            else _relaySeconds++;
            _settings.Add((s.Settings.Mode, s.Settings.MaxWidth, s.Settings.MaxHeight, s.Settings.Fps, s.Settings.MaxKbps));
            if (s.Width > 0 && s.Height > 0) _sizes.Add((s.Width, s.Height));
            _targetKbps.Add(s.TargetKbps);
            _cpuPercent.Add(s.CpuPercent);
            if (s.RttMs > 0) _rttMs.Add(s.RttMs);
            if (s.SecureDesktop)
            {
                _secureSeconds++;
                return;
            }
            _sentFps.Add(s.SentFps);
            _sentKbps.Add(s.SentKbps);
            if (s.CaptureMs is { } capture) _captureMs.Add(capture);
            if (s.EncodeMs is { } encode) _encodeMs.Add(encode);
            if (s.CpuBound) _cpuBoundSeconds++;
        }
    }

    /// <summary>快速线路上控制端每秒一次的汇报（控制端确认在那边收到画面之后）：丢包千分比、收到的码率、画面缓冲、它解出的帧率。</summary>
    public void OnReceiverReport(int lossPermille, int receivedKbps, int jitterBufferMs, int fps)
    {
        lock (_gate)
        {
            _revision++;
            _lossPermille.Add(lossPermille);
            _receivedKbps.Add(receivedKbps);
            _jitterBufferMs.Add(jitterBufferMs);
            _viewerFps.Add(fps);
        }
    }

    /// <summary>（重新）建了编码器：现在用的编码器、是不是按质量编、怎么截屏。</summary>
    public void SetPipeline(string encoder, bool qualityMode, string capture)
    {
        lock (_gate)
        {
            if (encoder == _encoder && qualityMode == _qualityMode && capture == _capture) return;
            (_encoder, _qualityMode, _capture) = (encoder, qualityMode, capture);
            _revision++;
        }
    }

    public void OnAutoStep(AutoStep step)
    {
        lock (_gate)
        {
            _revision++;
            switch (step)
            {
                case AutoStep.Network: _autoNetworkSteps++; break;
                case AutoStep.Cpu: _autoCpuSteps++; break;
                default: _autoCpuUndos++; break;
            }
        }
    }

    /// <summary>对面要关键帧：控制端经控制通道要的（它丢了帧、解码出错），或者快速线路上 SFU 转来的 PLI / FIR（调用方半秒内只算一次）。</summary>
    public void OnKeyFrameRequested() => Count(ref _keyFrames);

    /// <summary>最早那帧很久没确认（断流了），从关键帧重新开始。</summary>
    public void OnStall() => Count(ref _stalls);

    /// <summary>中继的发送队列满了（网络发不动），丢了一帧。</summary>
    public void OnRelaySendDrop() => Count(ref _relayDrops);

    /// <summary>
    /// 控制端说它每秒最多收几帧（MaxFps）：before 是之前的限制（-1 没说过、0 不限）。
    /// 设了限制或者限得更紧才算一次；放宽、不限、没变都不算。
    /// </summary>
    public void OnMaxFps(int before, int fps)
    {
        if (fps <= 0 || (before > 0 && fps >= before)) return;
        lock (_gate)
        {
            _revision++;
            _maxFpsCaps++;
            _minMaxFps = _minMaxFps == 0 ? fps : Math.Min(_minMaxFps, fps);
        }
    }

    /// <summary>快速线路接通了（连上了 Cloudflare，还不知道控制端那边接没接上）。</summary>
    public void OnLineConnected() => Count(ref _lineConnects);

    /// <summary>快速线路没接通、或者用着用着不能用了（回到中继），带上原因。</summary>
    public void OnLineFailed(string reason)
    {
        lock (_gate)
        {
            _revision++;
            _lineFailures++;
            _lineProblems.Add(Clean(reason));
        }
    }

    /// <summary>出错了（采集 / 编码、数据通道、声音）：程序自己的说明加异常的说明。</summary>
    public void OnError(string error)
    {
        lock (_gate)
        {
            _revision++;
            _errors.Add(Clean(error));
        }
    }

    /// <summary>到现在为止的汇总。final：会话结束时的最后一份。</summary>
    public AgentSessionTelemetry Build(bool final)
    {
        AgentSessionTelemetry report;
        Series.Snapshot[] series;
        lock (_gate)
        {
            series =
            [
                _sentFps.Copy(), _sentKbps.Copy(), _targetKbps.Copy(), _captureMs.Copy(), _encodeMs.Copy(), _cpuPercent.Copy(), _rttMs.Copy(),
                _lossPermille.Copy(), _receivedKbps.Copy(), _jitterBufferMs.Copy(), _viewerFps.Copy(),
            ];
            report = new AgentSessionTelemetry(
                agentVersion, final,
                _videoSeconds, _relaySeconds, _realtimeSeconds,
                _encoder, _qualityMode, _capture,
                [.. _settings.Top().Select(u => new TelemetrySettingsUse(u.Key.Mode, u.Key.MaxWidth, u.Key.MaxHeight, u.Key.Fps, u.Key.MaxKbps, u.Seconds))],
                [.. _sizes.Top().Select(u => new TelemetrySizeUse(u.Key.Width, u.Key.Height, u.Seconds))],
                Empty, Empty, Empty,
                Empty, Empty, Empty, Empty,
                _cpuBoundSeconds, _secureSeconds,
                _autoNetworkSteps, _autoCpuSteps, _autoCpuUndos,
                _keyFrames, _stalls, _relayDrops,
                _maxFpsCaps, _minMaxFps,
                _lineConnects, _lineFailures, _lineProblems.ToList(),
                null, null, null, null,
                _errors.ToList());
        }
        // 排序在锁外：采集线程、统计线程不用等
        var stats = series.Select(s => s.ToStat()).ToArray();
        return report with
        {
            SentFps = stats[0], SentKbps = stats[1], TargetKbps = stats[2],
            CaptureMs = stats[3], EncodeMs = stats[4], CpuPercent = stats[5], RttMs = stats[6],
            // 没走快速线路（控制端一次都没在那边汇报过）时是 null
            LossPermille = Line(7), ReceivedKbps = Line(8), JitterBufferMs = Line(9), ViewerFps = Line(10),
        };

        TelemetryStat? Line(int i) => stats[i].Samples > 0 ? stats[i] : null;
    }

    private void Count(ref int counter)
    {
        lock (_gate)
        {
            _revision++;
            counter++;
        }
    }

    /// <summary>说明压成一行、截短；里面的文件路径换成"&lt;路径&gt;"（路径里可能有用户名、文件名）。</summary>
    internal static string Clean(string text)
    {
        var line = ScrubPaths(string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
        return line.Length > MaxNoteLength ? line[..MaxNoteLength] : line;
    }

    /// <summary>
    /// 文件路径换成"&lt;路径&gt;"（探测结果里的说明也这样洗，见 Telemetry/HResults.Clip）。先压成一行再调。
    /// - 引号里的整个换掉、引号留着：异常的说明一般这样写（Could not find file 'C:\Users\John Smith\…'），路径里有空格也不漏；
    ///   到同样的引号、而且后面不紧跟着字母数字为止（O'Brien 这种名字里的撇号不算完）。
    /// - 没引号的从开头到空白或引号为止。
    /// 开头：C:\ 或 C:/（盘符前面紧挨着字母的不算：https://、wss:// 这种网址不动）、\\服务器（\\.\DISPLAY1 这种设备名不算）、file://。
    /// </summary>
    internal static string ScrubPaths(string text) => BarePath.Replace(QuotedPath.Replace(text, "$1<路径>$1"), "<路径>");

    private const string PathStart = @"(?:(?<![A-Za-z])[A-Za-z]:[\\/]|\\\\(?!\.\\)|(?i:file)://)";
    private static readonly Regex QuotedPath = new(@"(['""])" + PathStart + @".*?\1(?![\p{L}\p{N}])", RegexOptions.CultureInvariant | RegexOptions.Singleline);
    private static readonly Regex BarePath = new(PathStart + @"[^\s""'<>|]*", RegexOptions.CultureInvariant);

    /// <summary>一个每秒一个样本的数：全部样本的个数、和、最小、最大，加上留下来算百分位的那些（满了减半，见类的说明）。</summary>
    private sealed class Series
    {
        private readonly List<float> _kept = [];
        /// <summary>每几个样本留一个（减半一次翻一倍）。</summary>
        private int _stride = 1;
        private int _count;
        private double _sum;
        private double _min = double.MaxValue, _max = double.MinValue;

        public void Add(double value)
        {
            if (!double.IsFinite(value)) return;
            if (_count % _stride == 0)
            {
                _kept.Add((float)value);
                if (_kept.Count >= MaxKeptSamples)
                {
                    // 隔一个扔一个：留下第 0、2、4……个（都是 2 × _stride 的整数倍那些样本），之后也隔一个记一个
                    var half = 0;
                    for (var i = 0; i < _kept.Count; i += 2) _kept[half++] = _kept[i];
                    _kept.RemoveRange(half, _kept.Count - half);
                    _stride *= 2;
                }
            }
            _count++;
            _sum += value;
            _min = Math.Min(_min, value);
            _max = Math.Max(_max, value);
        }

        public Snapshot Copy() => new([.. _kept], _count, _sum, _min, _max);

        public readonly record struct Snapshot(float[] Kept, int Count, double Sum, double Min, double Max)
        {
            public TelemetryStat ToStat()
            {
                if (Count == 0 || Kept.Length == 0) return Empty;
                Array.Sort(Kept);
                return new TelemetryStat(Count, Round(Sum / Count), Round(Min), Pick(0.1), Pick(0.5), Pick(0.9), Round(Max));
            }

            /// <summary>第 ceil(p×n) 个（减一点点：0.1×30 这种乘出来可能比 3 大一丝）。</summary>
            private double Pick(double p) => Round(Kept[Math.Clamp((int)Math.Ceiling(p * Kept.Length - 1e-9) - 1, 0, Kept.Length - 1)]);

            private static double Round(double value) => Math.Round(value, 1);
        }
    }

    /// <summary>某样东西（画质设置、编码大小）各用了几秒，按第一次用到的先后排；种类最多记 MaxUses 的两倍，再来新的不记了。</summary>
    private sealed class Uses<TKey> where TKey : notnull
    {
        private readonly Dictionary<TKey, int> _index = [];
        private readonly List<(TKey Key, int Seconds)> _items = [];

        public void Add(TKey key)
        {
            if (_index.TryGetValue(key, out var i)) _items[i] = (key, _items[i].Seconds + 1);
            else if (_items.Count < 2 * MaxUses)
            {
                _index[key] = _items.Count;
                _items.Add((key, 1));
            }
        }

        /// <summary>用得最久的 MaxUses 种（一样久的先用到的优先），还是按第一次用到的先后排。</summary>
        public List<(TKey Key, int Seconds)> Top() => _items.Count <= MaxUses
            ? [.. _items]
            : [.. _items.Select((use, order) => (use, order)).OrderByDescending(x => x.use.Seconds).ThenBy(x => x.order)
                .Take(MaxUses).OrderBy(x => x.order).Select(x => x.use)];
    }

    /// <summary>出过的问题：同样的说明只留一条、记次数；不同的说明最多 MaxNotes - 1 条，再来新的合成最后一条"另有 N 次"。</summary>
    private sealed class Notes
    {
        private readonly List<(string Text, int Count)> _items = [];
        private int _others;

        public void Add(string text)
        {
            for (var i = 0; i < _items.Count; i++)
            {
                if (_items[i].Text != text) continue;
                _items[i] = (text, _items[i].Count + 1);
                return;
            }
            if (_items.Count < MaxNotes - 1) _items.Add((text, 1));
            else _others++;
        }

        public List<string> ToList()
        {
            var list = _items.Select(n => n.Count > 1 ? $"{n.Text}（{n.Count} 次）" : n.Text).ToList();
            if (_others > 0) list.Add($"另有 {_others} 次别的问题");
            return list;
        }
    }
}

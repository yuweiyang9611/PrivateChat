namespace PrivateChat.Agent.Session;

/// <summary>
/// 快速线路（经 Cloudflare Realtime）的码率控制。这条线路上没有逐帧的确认，SFU 也只管转发、不会替我们限速，
/// 所以靠两样东西：控制端的浏览器每秒汇报一次自己收得怎么样（<see cref="OnReceiverReport"/>），
/// SFU 的 RTCP 接收报告说我们发给它的丢了多少（<see cref="OnUpstreamLoss"/>）。
/// - 降：丢包超过一成，按丢得多少降（最少降一成半，最多降一半）；连续两次"画面收到的明显比发出的少"，降到收到的九成
///   （一次最多降一半；只比画面、而且画面发得够多才比——声音不在控制端汇报的数里，发得少时差一帧就是一大截，
///   在真实的 Cloudflare 上这两样都被误判成过拥塞）；
///   连续两次浏览器里的画面缓冲很深（包到得一阵一阵的），降一成半。每次降完至少隔一秒半才会再降。
/// - 平：丢包在 2%–10% 之间不升不降。
/// - 升：没有上面这些迹象、而且最近确实把码率用起来了（画面在动），每次汇报往上试一步；还没降过的时候步子大一些。
///   画面一直不动时不升——没有证据。
/// 发出去的字节怎么记账、一帧能花多少，和中继那条线路用同一套算法（见 <see cref="NetworkEstimator"/>）。
/// 线程：采集线程调 OnSent / CanSend / Tick，接收循环调 OnReceiverReport，RTCP 线程调 OnUpstreamLoss，声音线程调 OnSentUntracked。
/// </summary>
public sealed class RealtimeRateController : IRateController
{
    private const double HighLoss = 0.10;
    private const double LowLoss = 0.02;
    /// <summary>浏览器里画面缓冲超过这么多毫秒算"到得一阵一阵的"（正常只有十几到几十毫秒）。</summary>
    private const int DeepBufferMs = 250;
    private const int DecreaseGapMs = 1500;
    /// <summary>画面发得太少时"收到的比发出的少"不算数：数字小，两边统计的时间差一点点、凑巧差一帧就是一大截。</summary>
    private const int MinComparableKbps = 300;

    private readonly Lock _gate = new();
    private readonly int _minKbps;
    private int _maxKbps;
    private double _targetKbps;
    private double _virtualFreeMs;
    private bool _slowStart = true;
    private long _lastDecreaseAt = long.MinValue / 2;
    private long _lastReportAt = long.MinValue / 2;
    private long _windowStart;
    private long _windowBytes;
    private double _sentKbps;
    private bool _used;
    private int _deficitStreak;
    private int _deepStreak;
    private double _upstreamLoss;
    private long _upstreamLossAt = long.MinValue / 2;
    private int _receiverRttMs;
    private int _upstreamRttMs;
    private int _minRttMs = int.MaxValue;

    public RealtimeRateController(int minKbps, int maxKbps, int startKbps)
    {
        _minKbps = minKbps;
        _maxKbps = Math.Max(minKbps, maxKbps);
        _targetKbps = Math.Clamp(startKbps, minKbps, _maxKbps);
    }

    public int TargetKbps { get { lock (_gate) return (int)_targetKbps; } }
    public int MaxKbps { get { lock (_gate) return _maxKbps; } }
    /// <summary>被控端到 SFU 加上 SFU 到控制端的往返时间。</summary>
    public int LastRttMs { get { lock (_gate) return _receiverRttMs + _upstreamRttMs; } }
    public int MinRttMs { get { lock (_gate) return _minRttMs == int.MaxValue ? 0 : _minRttMs; } }
    public int InFlight => 0;

    /// <summary>控制端最近一次汇报是什么时候（还没汇报过是一个很早的时间）。</summary>
    public long LastReportAt { get { lock (_gate) return _lastReportAt; } }

    public void SetMax(int maxKbps)
    {
        lock (_gate)
        {
            _maxKbps = Math.Max(_minKbps, maxKbps);
            _targetKbps = Math.Min(_targetKbps, _maxKbps);
        }
    }

    /// <summary>发了一帧画面：记账，并且算进"这段时间画面发了多少"（控制端汇报的也只是它收到的画面）。</summary>
    public void OnSent(uint ts, int bytes, long nowMs)
    {
        lock (_gate)
        {
            Charge(bytes, nowMs);
            _windowBytes += bytes;
        }
    }

    /// <summary>发了声音：只记账。不能算进和控制端比的那个数里——它汇报的是画面，把声音算上会显得"发得多、收得少"。</summary>
    public void OnSentUntracked(int bytes, long nowMs)
    {
        lock (_gate) Charge(bytes, nowMs);
    }

    private void Charge(int bytes, long nowMs) => _virtualFreeMs = Math.Max(_virtualFreeMs, nowMs) + bytes * 8.0 / _targetKbps;

    /// <summary>被控端到 SFU 的往返时间（RTCP 量的）。</summary>
    public void SetUpstreamRtt(int rttMs)
    {
        lock (_gate) _upstreamRttMs = Math.Max(0, rttMs);
    }

    /// <summary>SFU 说我们发给它的包最近丢了多大比例（0..1）。</summary>
    public void OnUpstreamLoss(double fraction, long nowMs)
    {
        lock (_gate)
        {
            _upstreamLoss = Math.Clamp(fraction, 0, 1);
            _upstreamLossAt = nowMs;
        }
    }

    /// <summary>这段时间里画面实际发了多快（最近一次汇报时算的）。</summary>
    public int SentKbps { get { lock (_gate) return (int)_sentKbps; } }

    /// <summary>
    /// 控制端每秒一次的汇报：丢包千分比、收到的码率、浏览器里画面缓冲了多久、它到 SFU 的往返时间。
    /// 这次汇报让码率降了的话返回原因（记日志用），否则 null。
    /// </summary>
    public string? OnReceiverReport(int lossPermille, int receivedKbps, int jitterBufferMs, int rttMs, long nowMs)
    {
        lock (_gate)
        {
            // 这一段时间里画面实际发了多快（和它收到的画面比）
            var elapsed = nowMs - _windowStart;
            if (elapsed >= 300)
            {
                _sentKbps = _windowBytes * 8.0 / elapsed;
                _windowBytes = 0;
                _windowStart = nowMs;
                if (_sentKbps >= _targetKbps * 0.5) _used = true;
            }
            _lastReportAt = nowMs;
            _receiverRttMs = Math.Max(0, rttMs);

            // 两段都量到了才算数（只有一段时会显得特别短）
            if (_receiverRttMs > 0) _minRttMs = Math.Min(_minRttMs, _receiverRttMs + _upstreamRttMs);

            _deficitStreak = _sentKbps >= MinComparableKbps && receivedKbps < _sentKbps * 0.7 ? _deficitStreak + 1 : 0;
            _deepStreak = jitterBufferMs > DeepBufferMs ? _deepStreak + 1 : 0;

            if (nowMs - _lastDecreaseAt < DecreaseGapMs) return null;
            // 上行的丢包报告几秒才来一次：每份只算一次，放了太久的不算
            var upstream = nowMs - _upstreamLossAt < 6000 ? _upstreamLoss : 0;
            var loss = Math.Max(lossPermille / 1000.0, upstream);
            _upstreamLoss = 0;
            var before = (int)_targetKbps;
            string? why = null;
            if (loss > HighLoss)
            {
                Decrease(_targetKbps * Math.Clamp(1 - loss / 2, 0.5, 0.85), nowMs);
                why = $"丢包 {loss:P0}（控制端看到 {lossPermille / 10.0:0.#}%，到 Cloudflare 这一段 {upstream:P0}）";
            }
            else if (_deficitStreak >= 2)
            {
                var sent = (int)_sentKbps;
                // 降到它收到的九成，但一次最多降一半：这个信号没有丢包那么确凿
                Decrease(Math.Max(_targetKbps * 0.5, Math.Min(_targetKbps * 0.85, receivedKbps * 0.9)), nowMs);
                why = $"控制端连着两次收到的比发出的少得多（发 {sent} kbps，收 {receivedKbps} kbps）";
            }
            else if (_deepStreak >= 2)
            {
                Decrease(_targetKbps * 0.85, nowMs);
                why = $"控制端的画面缓冲了 {jitterBufferMs} ms";
            }
            else if (loss < LowLoss && _used && _deficitStreak == 0 && _deepStreak == 0)
            {
                _used = false;
                _targetKbps = Math.Min(_maxKbps, _slowStart ? _targetKbps * 1.3 : _targetKbps * 1.08 + 10);
            }
            return why is null ? null : $"{why}：{before} → {(int)_targetKbps} kbps";
        }
    }

    private void Decrease(double next, long nowMs)
    {
        _targetKbps = Math.Max(_minKbps, next);
        _lastDecreaseAt = nowMs;
        _slowStart = false;
        _deficitStreak = 0;
        _deepStreak = 0;
        _used = false;
    }

    public void Tick(long nowMs)
    {
    }

    public bool CanSend(long nowMs, int frameMs)
    {
        lock (_gate) return Backlog(nowMs) <= NetworkEstimator.SendMarkMs;
    }

    public bool Stalled(long nowMs) => false;

    public int ChangeBudget(long nowMs, bool sustained, int frameMs)
    {
        lock (_gate) return (int)((sustained ? frameMs : Math.Max(frameMs, NetworkEstimator.BurstMs - Backlog(nowMs))) * _targetKbps / 8);
    }

    public int Credit(long nowMs)
    {
        lock (_gate) return (int)(Math.Max(0, NetworkEstimator.RefineMs - Backlog(nowMs)) * _targetKbps / 8);
    }

    private double Backlog(long nowMs) => Math.Max(0, _virtualFreeMs - nowMs);

    public bool Idle(long nowMs)
    {
        lock (_gate) return _virtualFreeMs <= nowMs;
    }

    public void ForgetInFlight()
    {
    }
}

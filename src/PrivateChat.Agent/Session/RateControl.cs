namespace PrivateChat.Agent.Session;

/// <summary>
/// 按网络状况定码率，只靠控制端的回应（每帧画面的 FrameAck、探测包的 Pong）。弱网下不卡的关键在这里：
/// - 码率预算：发出去的每个字节都按"目标码率"记账（<see cref="OnSent"/>），账上欠得多了就先不采新画面（<see cref="CanSend"/>）。
///   偶尔编出一个大帧，后果是接下来少发几帧，而不是在路上越堆越多、延迟越来越大。
/// - 拥塞判断看的是"比这条线路本来的往返时间多等了多久"，不是数有几帧没确认：线路本身延迟高（跨国、移动网络）但带宽够的时候照样满帧率。
///   多等的时间里扣掉按目标码率本来就该花的传输时间，所以一个大帧不会被误判成拥塞。
///   "本来的往返时间"有两个：线路本身的（小探测包量，它不带画面），和一帧画面从发出到确认的（小帧量，比前者多出控制端解码、
///   画出来的时间）。判断拥塞用后一个：手机解码慢是每一帧都慢，不是网络堵了，不能因此把码率压下去。
/// - 降：连续两次多等超过阈值就降到实测送达速率的九成（最少降两成；刚开始还在试探时降多少都行，之后一次最多降一半）；
///   降过之后要等"降速之后发的帧"的确认回来才会再降。只看一次不算：解码器刚启动时第一帧会慢几百毫秒。
/// - 在途的帧数也有上限（按往返时间算，够它本来该有的再多几帧）：控制端解码跟不上时，发得再小也没用，只能少发。
/// - 升：只有真的把码率用起来过（发过够大的帧）而且没有多等，才每秒往上试一步；画面一直不动时不升——没有证据。
/// 线程：采集线程调 OnSent / CanSend / Tick，接收循环调 OnAck / OnProbe，声音线程调 OnSentUntracked。
/// </summary>
public sealed class NetworkEstimator
{
    /// <summary>画面突然大变时，这一帧最多可以用掉这么多毫秒的码率（大了传得久，小了太糊）。</summary>
    public const int BurstMs = 350;
    /// <summary>补清晰的一步最多用这么多毫秒的码率：它传完之前，后面的操作画面只能排队等着。</summary>
    public const int RefineMs = 200;
    /// <summary>账上欠的不超过这么多毫秒就可以再发一帧。</summary>
    public const int SendMarkMs = 100;
    private const int StartupWindow = 4;
    private const int WatchdogMs = 10_000;
    private const int ProbeMs = 60;
    /// <summary>按目标码率算传输时间不超过这么多的帧算"小帧"：它的往返时间里基本没有传输时间，可以当画面往返的基准。</summary>
    private const int SmallFrameMs = 30;

    private readonly Lock _gate = new();
    private readonly Queue<Sent> _inFlight = new();
    private readonly int _minKbps;
    private int _maxKbps;
    private double _targetKbps;
    private double _virtualFreeMs;
    private int _minRttCur = int.MaxValue;
    private int _minRttPrev = int.MaxValue;
    private long _minRttRotatedAt;
    private int _minFrameCur = int.MaxValue;
    private int _minFramePrev = int.MaxValue;
    private int _lastRttMs;
    private int _overuseStreak;
    private long _lastOveruseAt = long.MinValue / 2;
    private long _lastDecreaseAt = long.MinValue / 2;
    private long _lastIncreaseAt;
    private bool _slowStart = true;
    private bool _probeOk;
    private long _ackedBytes;
    private double _rateSampleKbps;
    private long _rateSampleAt = long.MinValue / 2;

    private readonly record struct Sent(uint Ts, long At, int Bytes, int ModelDelayMs, long AckedBytesAtSend);

    public NetworkEstimator(int minKbps, int maxKbps, int startKbps)
    {
        _minKbps = minKbps;
        _maxKbps = Math.Max(minKbps, maxKbps);
        _targetKbps = Math.Clamp(startKbps, minKbps, _maxKbps);
    }

    public int TargetKbps { get { lock (_gate) return (int)_targetKbps; } }
    public int MaxKbps { get { lock (_gate) return _maxKbps; } }
    /// <summary>最近一帧画面从发出到确认用了多久。</summary>
    public int LastRttMs { get { lock (_gate) return _lastRttMs; } }
    /// <summary>线路本来的往返时间（还没量到是 0）。</summary>
    public int MinRttMs { get { lock (_gate) return MinRtt == int.MaxValue ? 0 : MinRtt; } }
    public int InFlight { get { lock (_gate) return _inFlight.Count; } }

    private int MinRtt => Math.Min(_minRttCur, _minRttPrev);

    /// <summary>一帧小画面从发出到确认本来要多久（线路往返 + 控制端解码）；还没有小帧的样本时先用线路往返。</summary>
    private int FrameBase => Math.Min(_minFrameCur, _minFramePrev) is var frame and < int.MaxValue ? frame : MinRtt;

    /// <summary>多等多久算拥塞：线路往返时间的一半，60–250 ms 之间（移动网络本身就有几十毫秒的抖动）。</summary>
    private static int Threshold(int minRtt) => Math.Clamp(minRtt / 2, 60, 250);

    /// <summary>换了画质档位：上限变了，目标码率跟着收进新的范围。</summary>
    public void SetMax(int maxKbps)
    {
        lock (_gate)
        {
            _maxKbps = Math.Max(_minKbps, maxKbps);
            _targetKbps = Math.Min(_targetKbps, _maxKbps);
        }
    }

    /// <summary>发了一帧画面（要等它的确认）。</summary>
    public void OnSent(uint ts, int bytes, long nowMs)
    {
        lock (_gate) _inFlight.Enqueue(new Sent(ts, nowMs, bytes, Charge(bytes, nowMs), _ackedBytes));
    }

    /// <summary>发了不等确认的数据（声音）：只记账。</summary>
    public void OnSentUntracked(int bytes, long nowMs)
    {
        lock (_gate) Charge(bytes, nowMs);
    }

    /// <summary>按目标码率，这些字节要到什么时候才"发完"；返回从现在算起要多久。</summary>
    private int Charge(int bytes, long nowMs)
    {
        _virtualFreeMs = Math.Max(_virtualFreeMs, nowMs) + bytes * 8.0 / _targetKbps;
        return (int)(_virtualFreeMs - nowMs);
    }

    /// <summary>探测包的往返时间（不带画面，量的是线路本身）。</summary>
    public void OnProbe(int rttMs, long nowMs)
    {
        lock (_gate) Baseline(Math.Max(0, rttMs), nowMs);
    }

    private void Baseline(int rtt, long nowMs)
    {
        if (nowMs - _minRttRotatedAt > 15_000)
        {
            // 只记最近 15–30 秒里最小的：换了线路（Wi-Fi → 移动网络）之后旧的基准会过期
            _minRttPrev = _minRttCur;
            _minRttCur = int.MaxValue;
            _minFramePrev = _minFrameCur;
            _minFrameCur = int.MaxValue;
            _minRttRotatedAt = nowMs;
        }
        _minRttCur = Math.Min(_minRttCur, rtt);
    }

    public void OnAck(uint ts, long nowMs)
    {
        lock (_gate)
        {
            Sent? hit = null;
            while (_inFlight.TryPeek(out var s) && s.Ts <= ts)
            {
                _inFlight.Dequeue();
                _ackedBytes += s.Bytes;
                if (s.Ts == ts) hit = s;
            }
            // 发出去的都已经送到了：路上是空的，账上按目标码率算还"没发完"的部分一笔勾销（线路比估计的快，不用白等）
            if (_inFlight.Count == 0) _virtualFreeMs = Math.Min(_virtualFreeMs, nowMs);
            if (hit is not { } frame) return;

            var rtt = (int)Math.Max(0, nowMs - frame.At);
            _lastRttMs = rtt;
            var known = MinRtt;
            Baseline(rtt, nowMs);
            if (frame.ModelDelayMs <= SmallFrameMs) _minFrameCur = Math.Min(_minFrameCur, rtt);
            var minRtt = MinRtt;

            // 送达速率：这一帧在路上的这段时间里，对面一共收到了多少。只在它确实排了队 / 传了一阵时才算数
            var sojourn = rtt - (known == int.MaxValue ? 0 : known);
            if (sojourn >= 50)
            {
                _rateSampleKbps = (_ackedBytes - frame.AckedBytesAtSend) * 8.0 / sojourn;
                _rateSampleAt = nowMs;
            }

            var excess = rtt - FrameBase - frame.ModelDelayMs;
            var threshold = Threshold(minRtt);
            if (excess > threshold)
            {
                _overuseStreak++;
                _lastOveruseAt = nowMs;
                if (_overuseStreak >= 2 && frame.At >= _lastDecreaseAt) Decrease(nowMs);
            }
            else
            {
                _overuseStreak = 0;
                if (excess < threshold / 2 && frame.ModelDelayMs >= ProbeMs) _probeOk = true;
            }
        }
    }

    private void Decrease(long nowMs)
    {
        var next = _targetKbps * 0.8;
        if (nowMs - _rateSampleAt < 1500) next = Math.Clamp(_rateSampleKbps * 0.9, _slowStart ? _minKbps : _targetKbps * 0.5, _targetKbps * 0.8);
        _targetKbps = Math.Max(_minKbps, next);
        _lastDecreaseAt = nowMs;
        _lastIncreaseAt = nowMs;
        _slowStart = false;
        _overuseStreak = 0;
        _probeOk = false;
    }

    /// <summary>采集循环每一轮调一次：条件满足时每秒把码率往上试一步。</summary>
    public void Tick(long nowMs)
    {
        lock (_gate)
        {
            if (nowMs - _lastIncreaseAt < 1000) return;
            _lastIncreaseAt = nowMs;
            if (!_probeOk || nowMs - _lastOveruseAt < 2000) return;
            _probeOk = false;
            _targetKbps = Math.Min(_maxKbps, _slowStart ? _targetKbps * 1.3 : _targetKbps * 1.08 + 10);
        }
    }

    /// <summary>
    /// 现在能不能再发一帧画面：账上欠得不多；没确认的帧数没超过按往返时间本来该有的（再多几帧）；最早那帧也没有等得离谱。
    /// frameMs：一帧的时间（1000 / 帧率）。
    /// </summary>
    public bool CanSend(long nowMs, int frameMs)
    {
        lock (_gate)
        {
            if (Backlog(nowMs) > SendMarkMs) return false;
            if (_inFlight.Count == 0) return true;
            var minRtt = MinRtt;
            if (minRtt == int.MaxValue) return _inFlight.Count < StartupWindow;
            var frameBase = FrameBase;
            if (_inFlight.Count >= Math.Max(StartupWindow, (frameBase + 150) / Math.Max(1, frameMs) + 2)) return false;
            var oldest = _inFlight.Peek();
            return nowMs - oldest.At <= frameBase + oldest.ModelDelayMs + Math.Max(4 * Threshold(minRtt), 500);
        }
    }

    /// <summary>最早那帧十秒都没确认：控制端多半把它丢了（解码器重置之类），从关键帧重新开始。</summary>
    public bool Stalled(long nowMs)
    {
        lock (_gate) return _inFlight.TryPeek(out var oldest) && nowMs - oldest.At > WatchdogMs + oldest.ModelDelayMs;
    }

    /// <summary>
    /// 这一帧里"变化的部分"可以花多少字节。frameMs：一帧的时间（1000 / 帧率）。
    /// sustained：画面在持续变化，要帧率，每帧只给一帧时间的量；否则是安静之后的第一次变化，可以把突发额度里剩下的都用上。
    /// </summary>
    public int ChangeBudget(long nowMs, bool sustained, int frameMs)
    {
        lock (_gate) return (int)((sustained ? frameMs : Math.Max(frameMs, BurstMs - Backlog(nowMs))) * _targetKbps / 8);
    }

    /// <summary>补清晰的额度里还剩多少字节（连带变化一起也不能超过它）。</summary>
    public int Credit(long nowMs)
    {
        lock (_gate) return (int)(Math.Max(0, RefineMs - Backlog(nowMs)) * _targetKbps / 8);
    }

    private double Backlog(long nowMs) => Math.Max(0, _virtualFreeMs - nowMs);

    /// <summary>账上不欠了（上一批已经按目标码率"发完"）。</summary>
    public bool Idle(long nowMs)
    {
        lock (_gate) return _virtualFreeMs <= nowMs;
    }

    /// <summary>在途的帧不会再被确认了（控制端要了关键帧）：清空，码率估计留着。</summary>
    public void ForgetInFlight()
    {
        lock (_gate)
        {
            _inFlight.Clear();
            _overuseStreak = 0;
        }
    }

    /// <summary>控制端换了一条连接：往返时间的基准重新量。</summary>
    public void ResetPath()
    {
        lock (_gate)
        {
            _inFlight.Clear();
            _overuseStreak = 0;
            _minRttCur = _minRttPrev = int.MaxValue;
            _minFrameCur = _minFramePrev = int.MaxValue;
            _virtualFreeMs = 0;
            _probeOk = false;
        }
    }
}

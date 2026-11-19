namespace PrivateChat.Agent.Session;

/// <summary>画面分成 64×64 的小块，比较前后两帧哪些块变了（预算、清晰度都按块算）。</summary>
public static class FrameTiles
{
    public const int Size = 64;

    public static int Count(int width, int height) => ((width + Size - 1) / Size) * ((height + Size - 1) / Size);

    /// <summary>逐块比较两幅 BGRA 画面，changed 里记下每块变没变，返回变了几块。previous 为空算全变。</summary>
    public static int Diff(ReadOnlySpan<byte> previous, ReadOnlySpan<byte> current, int width, int height, int stride, Span<bool> changed)
    {
        var count = 0;
        var index = 0;
        for (var top = 0; top < height; top += Size)
        {
            var rows = Math.Min(Size, height - top);
            for (var left = 0; left < width; left += Size, index++)
            {
                var bytes = Math.Min(Size, width - left) * 4;
                var differs = previous.IsEmpty;
                for (var y = 0; y < rows && !differs; y++)
                {
                    var offset = (top + y) * stride + left * 4;
                    differs = !current.Slice(offset, bytes).SequenceEqual(previous.Slice(offset, bytes));
                }
                changed[index] = differs;
                if (differs) count++;
            }
        }
        return count;
    }
}

/// <summary>
/// 每一帧用什么质量编（编码器跑在"按质量"的模式，质量可以逐帧改）。
/// Windows 自带编码器的固定码率模式管不住突变：设 300 kbps，静止一会儿再滚动，一秒能冲到 3 Mbps、单帧 80 KB 以上，
/// 弱网下这一帧就要传好几秒。所以改成自己定：
/// - 画面大变的那一帧先糊一点（大小卡在预算里，马上能看到）；
/// - 停下来之后一步一步补清晰（每一步也卡在预算里），最后总能补到这一档的上限——网再慢，静止的文字也会变清楚；
/// - 小变化（打字、鼠标）没几个字节，跟着周围画面的质量走。
/// 实测这个编码器的规律（1280×800 桌面画面，写进了下面的初值）：
/// - 质量每高 1，大小约乘 1.02；整屏新内容在质量 50 时约 86 KB，质量 5 时也有 30 KB（这是下限，再慢的网也得传这么多）；
/// - 质量是对整幅画面生效的：把质量调高，没变的部分也会被重新编一遍（补清晰），所以每块现在是什么质量要记着，
///   调高之前先算上这笔开销；调低则不花钱（没变的块直接跳过）；
/// - 补清晰一步的开销约是"整屏新内容"的 3.5% × 步长（封顶 55%），步长小于 6 基本不起作用；
/// - 滚动的第一帧几乎和整屏新内容一样贵，之后十来帧降到四分之一：安静之后的第一次变化按"整屏新内容"估，持续变化按最近几帧估。
/// </summary>
public sealed class QualityPlanner
{
    public const int Floor = 0;
    private const double Ratio = 1.02;
    private const int MinStep = 6;
    private static readonly double LogRatio = Math.Log(Ratio);

    private readonly byte[] _tiles;
    private double _fullCost;
    private double _motionCost;
    private double _refineScale = 1;

    public QualityPlanner(int width, int height, int ceiling)
    {
        _tiles = new byte[FrameTiles.Count(width, height)];
        _fullCost = 86_000.0 * width * height / 1_024_000;
        _motionCost = _fullCost / 2;
        Ceiling = ceiling;
        Array.Fill(_tiles, (byte)ceiling);
    }

    /// <summary>这一档画质的上限（静止后补到这里为止）。</summary>
    public int Ceiling { get; set; }

    /// <summary>画面里最糊的那一块现在是什么质量。</summary>
    public int Picture
    {
        get
        {
            var min = 100;
            foreach (var q in _tiles) min = Math.Min(min, q);
            return min;
        }
    }

    /// <summary>已经全部补到上限了。</summary>
    public bool Sharp => Picture >= Ceiling;

    private static int QualityFor(double budgetBytes, double bytesAt50)
        => (int)Math.Floor(50 + Math.Log(Math.Max(1, budgetBytes) / Math.Max(1, bytesAt50)) / LogRatio);

    private static double Scale(int quality) => Math.Pow(Ratio, quality - 50);

    private static double Step(int step) => Math.Min(0.55, 0.035 * step);

    /// <summary>把质量调到 to，顺带补清晰要多少字节（skip：这些块本来就要重编，不重复算）。</summary>
    private double RefineBytes(int to, ReadOnlySpan<bool> skip)
    {
        var sum = 0.0;
        for (var i = 0; i < _tiles.Length; i++)
            if (_tiles[i] < to && (skip.IsEmpty || !skip[i])) sum += Step(to - _tiles[i]);
        return sum * _fullCost / _tiles.Length * Scale(to) * _refineScale;
    }

    /// <summary>
    /// 有变化的一帧（或关键帧）用什么质量。
    /// changeBudget：变化的部分最多花多少字节；credit：补清晰的额度。
    /// sustained：画面在持续变化（上一次变化就在刚才）。
    /// 质量比没变的那些块现在的质量高，就等于顺带把它们补清晰了，这笔开销要算进去：
    /// - 安静之后的第一次变化（点了一下、敲了一个字）不顺带——这一帧是给人看操作结果的，越小越快到；画面停下来自然会补；
    /// - 画面一直在变（角落里有个转圈的动画、小窗口在放视频）就等不到"停下来"，这时在额度之内顺带补。
    /// </summary>
    public int Plan(ReadOnlySpan<bool> changed, int changedCount, bool key, bool sustained, int changeBudget, int credit)
    {
        if (key) return Math.Clamp(QualityFor(changeBudget, _fullCost), Floor, Ceiling);
        var at50 = (sustained ? _motionCost : _fullCost) * Math.Max(changedCount, 1) / _tiles.Length;
        var q = Math.Clamp(QualityFor(changeBudget, at50), Floor, Ceiling);
        if (RefineBytes(q, changed) <= 0) return q; // 不比没变的块清楚：没有额外开销
        // 不顺带补的话最高能用多少：没变的块里最糊的那个质量
        var keep = q;
        while (keep > Floor && RefineBytes(keep, changed) > 0) keep--;
        if (!sustained) return keep;
        var to = q;
        while (to > keep && at50 * Scale(to) + RefineBytes(to, changed) > credit) to--;
        return to >= Ceiling || to >= keep + MinStep ? to : keep;
    }

    public void Learn(int quality, ReadOnlySpan<bool> changed, int changedCount, bool key, bool sustained, int bytes)
    {
        // 顺带补了清晰的帧，大小里混着两部分，不拿来校正
        var mixed = !key && RefineBytes(quality, changed) > 0;
        var area = key ? 1.0 : (double)Math.Max(changedCount, 1) / _tiles.Length;
        if (!mixed && (key || area >= 0.05))
        {
            var at50 = bytes / Scale(quality) / area;
            if (key)
            {
                _fullCost = Mix(_fullCost, at50, at50 > _fullCost ? 0.5 : 0.15);
            }
            else if (sustained)
            {
                _motionCost = Mix(_motionCost, at50, at50 > _motionCost ? 0.5 : 0.3);
            }
            else
            {
                // 一轮变化的第一帧：比"整屏新内容"的估计还贵就往上改；这一轮后面的帧先按它的八成估，再跟着实际走
                if (area >= 0.3 && at50 > _fullCost) _fullCost = Mix(_fullCost, at50, 0.5);
                _motionCost = at50 * 0.8;
            }
        }
        for (var i = 0; i < _tiles.Length; i++)
        {
            if (key || changed[i]) _tiles[i] = (byte)quality;
            else if (_tiles[i] < quality) _tiles[i] = (byte)quality;
        }
    }

    /// <summary>
    /// 画面静止时：值得补一帧更清晰的吗，补到多少。
    /// force：线路已经空了好一阵，预算不够也迈一小步（不然很慢的网永远补不清）。
    /// </summary>
    public int? Refine(int credit, bool force)
    {
        var picture = Picture;
        if (picture >= Ceiling) return null;
        var smallest = Math.Min(Ceiling, picture + MinStep);
        for (var to = Ceiling; to >= smallest; to--)
            if (RefineBytes(to, default) <= credit) return to;
        return force ? smallest : null;
    }

    public void LearnRefine(int quality, int bytes)
    {
        var predicted = RefineBytes(quality, default);
        if (predicted > 0)
        {
            var observed = _refineScale * bytes / predicted;
            _refineScale = Math.Clamp(Mix(_refineScale, observed, observed > _refineScale ? 0.5 : 0.3), 0.3, 3);
        }
        for (var i = 0; i < _tiles.Length; i++)
            if (_tiles[i] < quality) _tiles[i] = (byte)quality;
    }

    private static double Mix(double old, double observed, double weight) => old * (1 - weight) + observed * weight;
}

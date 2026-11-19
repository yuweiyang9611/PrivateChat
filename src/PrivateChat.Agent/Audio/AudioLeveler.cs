namespace PrivateChat.Agent.Audio;

/// <summary>
/// 把声音拉到正常响度再编码。环回采集录到的是"这台电脑实际往外放的"：对方把音量调得很低时（爸妈常这样），
/// 传过来只剩一点点，控制的人把自己的音量开到最大也听不清。做法：记最近的峰值（突然变响马上跟上，变轻后慢慢回落，约 7 秒减半），
/// 增益 = 目标峰值 ÷ 最近峰值，只放大不缩小，最多 30 dB；增益在一包之内线性过渡，不会有咔哒声。
/// </summary>
public sealed class AudioLeveler
{
    private const float TargetPeak = 16000;   // 约 -6 dBFS
    private const float MaxGain = 31.6f;      // +30 dB
    private const float Release = 0.998f;     // 每 20 ms

    private float _envelope;
    private float _gain = 1;

    /// <summary>这一包里最响的采样（没处理前）：调用方拿它判断是不是静音。</summary>
    public static int Peak(ReadOnlySpan<short> pcm)
    {
        var peak = 0;
        foreach (var s in pcm) peak = Math.Max(peak, Math.Abs((int)s));
        return peak;
    }

    /// <summary>就地处理一包（20 ms）。</summary>
    public void Process(Span<short> pcm, int peak)
    {
        _envelope = Math.Max(peak, _envelope * Release);
        var wanted = Math.Clamp(TargetPeak / Math.Max(_envelope, 1), 1, MaxGain);
        // 要降的时候立刻降（不然这一包就削波了），要升的时候慢慢升
        var next = wanted < _gain ? wanted : _gain + (wanted - _gain) * 0.05f;
        if (_gain == 1 && next == 1) return;
        // 往下降：整包直接用新的增益（突然变响的那一下可能就在包的开头）；往上升：在这一包里线性过渡
        var gain = Math.Min(_gain, next);
        var step = (next - gain) / pcm.Length;
        for (var i = 0; i < pcm.Length; i++)
        {
            gain += step;
            pcm[i] = (short)Math.Clamp(pcm[i] * gain, short.MinValue, short.MaxValue);
        }
        _gain = next;
    }
}

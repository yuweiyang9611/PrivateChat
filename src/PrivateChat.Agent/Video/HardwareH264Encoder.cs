using System.Diagnostics;
using System.Runtime.InteropServices;
using PrivateChat.Agent.Telemetry;
using PrivateChat.Contracts.Remote;

namespace PrivateChat.Agent.Video;

/// <summary>
/// 显卡的硬件 H.264 编码器（NVIDIA NVENC、AMD VCE / VCN、Intel Quick Sync 的 Media Foundation 异步 MFT，底层见 <see cref="AsyncMftEncoder"/>），
/// 对采集循环表现得和软件编码器（<see cref="H264Encoder"/>）一样：Encode 送进一帧、等到这一帧的输出才返回。
/// - 设置同软件编码器：低延迟、无 B 帧、GOP 很长、Main profile（快速线路的 SDP 写死了 4d001f）；输入是系统内存里紧凑排列的 NV12。
/// - 逐帧定质量：按给的顺序（<see cref="HardwareRateControl"/>）挑编码器接受的第一种——按质量（AVEncCommonQuality）、
///   逐帧 QP（AVEncVideoEncodeQP，质量按 <see cref="EncoderChoice.QpForQuality"/> 换算），都不行就固定码率（QualityMode = false）。
///   "接受"不等于"真的逐帧生效"：远程自动用的只是探测（Telemetry/EncoderProbe）里验证过的那种方式。
/// - 构造时先热身：编一帧灰画面，送进去之后 500 ms 内要出来，而且是 Main（或 Baseline）profile、带 SPS / PPS，否则抛异常（VideoEncoders 接着试下一个）。
///   热身那帧不输出；之后第一帧强制关键帧。内部的样本时间整体往后挪一帧（热身那帧占了 0）。
/// - 等输出：送进去之后最多 max(100 ms, 3 帧的时间)（<see cref="EncoderChoice.FrameTimeout"/>；等编码器来要输入也最多这么久），
///   按样本时间认是不是这一帧的（<see cref="PendingFrames"/>）。
///   这一帧没等到就返回 null，下一帧强制关键帧（它晚到的输出会被丢掉，后面的帧不能再参考它）；更早的帧晚到的输出直接丢掉。
///   连续 3 帧没等到、Media Foundation 报错、要了 3 次关键帧都没给，抛 <see cref="EncoderFailedException"/>（采集循环换软件编码器，这次远程不断），
///   之后再调 Encode 也一样抛。
/// - 只带 SPS / PPS / SEI 的输出先攒着，等到这一帧的画面一起返回；关键帧缺 SPS / PPS 的补上（同软件编码器）。
/// - Dispose：ShutdownObject（放掉显卡上的编码会话），不抛异常。
/// 只能在一个（MTA）线程上用。
/// </summary>
public sealed class HardwareH264Encoder : IVideoEncoder
{
    private static readonly TimeSpan WarmUpLimit = TimeSpan.FromMilliseconds(500);
    private const int MaxTimeouts = 3;
    private const int MaxKeyMisses = 3;

    private readonly IMFActivate _activate;
    private readonly bool _ownsActivate;
    private readonly AsyncMftEncoder _inner;
    private readonly TimeSpan _frameTimeout;
    private readonly int _inputSize;
    /// <summary>调用方的时间戳 + 这个 = 送给编码器的样本时间（热身那帧占了 0；取整到毫秒，编码器把时间取整到毫秒也对得上）。</summary>
    private readonly long _timeOffset;
    private readonly PendingFrames _pending = new();
    private byte[]? _parameterSets;
    private long _lastTime;
    private bool _keyWanted;
    private int _keyMisses;
    private int _timeouts;
    private string? _failure;
    private bool _disposed;

    public int Width { get; }
    public int Height { get; }
    public int Fps { get; }

    /// <summary>MFT 的友好名（"Intel® Quick Sync Video H.264 Encoder MFT"）：探测结果、MarkFailed 都按这个认。</summary>
    public string FriendlyName { get; }

    /// <summary>"&lt;友好名&gt;（硬件）"。</summary>
    public string Name { get; }

    public bool IsHardware => true;

    /// <summary>显卡厂商（0x10DE NVIDIA、0x1002 AMD、0x8086 Intel；不知道是 0）。</summary>
    public int VendorId { get; }

    /// <summary>实际用的码率控制方式。</summary>
    public HardwareRateControl RateControl => _inner.RateControl;

    public bool QualityMode => RateControl != HardwareRateControl.Cbr;

    /// <summary>热身那帧从送进去到出来用了多久（毫秒）。</summary>
    public double WarmUpMs { get; }

    /// <param name="activate">MFTEnumEx 列出来的那个编码器。ownsActivate：这个编码器 Dispose 时连它一起放（构造失败时不放，仍归调用方）。</param>
    /// <param name="modes">按顺序试的码率控制方式。</param>
    /// <param name="quality">第一帧的质量（0–100）。</param>
    internal HardwareH264Encoder(IMFActivate activate, string friendlyName, int vendorId, int width, int height, int fps, int bitrateKbps,
        IReadOnlyList<HardwareRateControl> modes, int quality, bool ownsActivate)
    {
        _activate = activate;
        _ownsActivate = ownsActivate;
        FriendlyName = friendlyName;
        Name = EncoderChoice.DisplayName(friendlyName);
        VendorId = vendorId;
        Width = width;
        Height = height;
        Fps = fps;
        _inputSize = width * height * 3 / 2;
        _frameTimeout = EncoderChoice.FrameTimeout(fps);
        var frameTicks = TimeSpan.TicksPerSecond / Math.Max(1, fps);
        _timeOffset = (frameTicks + TimeSpan.TicksPerMillisecond - 1) / TimeSpan.TicksPerMillisecond * TimeSpan.TicksPerMillisecond;
        _inner = new AsyncMftEncoder(activate, width, height, fps, bitrateKbps, modes, quality);
        try
        {
            var clock = Stopwatch.StartNew();
            WarmUp();
            WarmUpMs = Math.Round(clock.Elapsed.TotalMilliseconds, 2);
        }
        catch
        {
            _inner.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 编一帧灰画面（Y、U、V 都是 128）：500 ms 内要来要输入，送进去之后 500 ms 内要出画面；profile 要能被 Main 解码器解、要有 SPS / PPS。
    /// 从送进去之后才算出画面的时间：Intel 的驱动偶尔（这台笔记本上约 1/200，多在刚关掉上一个编码器之后）第一次 ProcessInput 本身就要卡 0.5 秒，
    /// 之后 2 ms 就出画面——那是好的编码器。
    /// </summary>
    private void WarmUp()
    {
        var gray = new byte[_inputSize];
        Array.Fill(gray, (byte)128);
        if (!_inner.TrySubmit(gray, 0, WarmUpLimit))
            throw new TimeoutException($"硬件编码器 {WarmUpLimit.TotalMilliseconds:F0} 毫秒内没要第一帧（没发 METransformNeedInput）");
        var clock = Stopwatch.StartNew();
        TimeSpan Left() => WarmUpLimit - clock.Elapsed;
        List<byte[]> parts = [];
        while (true)
        {
            var left = Left();
            if (left <= TimeSpan.Zero || _inner.TryReceive(left) is not { } sample)
                throw new TimeoutException($"硬件编码器 {WarmUpLimit.TotalMilliseconds:F0} 毫秒内没编出第一帧");
            parts.Add(sample.Data);
            if (EncoderChoice.HasPicture(sample.Data)) break;
        }
        var bytes = Concat(parts);
        var sets = H264.ParameterSets(bytes) ?? _inner.ParameterSets
            ?? throw new NotSupportedException("硬件编码器的第一帧没有带 SPS / PPS，媒体类型里也没有");
        if (EncoderChoice.Profile(sets) is not { } profile)
            throw new NotSupportedException("硬件编码器的 SPS 读不出 profile");
        if (!EncoderChoice.MainCompatible(profile))
            throw new NotSupportedException($"硬件编码器输出的是 profile {profile}（要的是 Main 77：快速线路的 SDP 写死了 Main）");
        _parameterSets = sets;
        _inner.DiscardReady();
        _lastTime = 0;
        _keyWanted = true; // 热身那帧不输出：采集循环的第一帧要是关键帧
    }

    public void RequestKeyFrame() => _keyWanted = true;

    public void SetQuality(int quality)
    {
        if (_failure is null && !_disposed) _inner.SetQuality(quality);
    }

    public void SetBitrate(int kbps)
    {
        if (_failure is null && !_disposed) _inner.SetBitrate(kbps);
    }

    public EncodedFrame? Encode(ReadOnlySpan<byte> nv12, TimeSpan timestamp)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (nv12.Length < _inputSize) throw new ArgumentException("NV12 数据不完整");
        if (_failure is not null) throw new EncoderFailedException($"{Name}：{_failure}");
        try
        {
            return EncodeCore(nv12, timestamp);
        }
        catch (EncoderFailedException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not ArgumentException and not ObjectDisposedException)
        {
            throw Fail(HResults.Explain(ex), ex);
        }
    }

    private EncodedFrame? EncodeCore(ReadOnlySpan<byte> nv12, TimeSpan timestamp)
    {
        var time = Math.Max(timestamp.Ticks + _timeOffset, _lastTime + 1);
        _lastTime = time;
        if (_keyWanted) _inner.RequestKeyFrame();
        if (!_inner.TrySubmit(nv12, time, _frameTimeout)) return TimedOut(submitted: false);
        _pending.Add(time);
        var clock = Stopwatch.StartNew(); // 从送进去之后算（ProcessInput 本身偶尔会卡一下，见 WarmUp）

        List<byte[]>? parts = null;
        while (true)
        {
            var left = _frameTimeout - clock.Elapsed;
            if (_inner.TryReceive(left > TimeSpan.Zero ? left : TimeSpan.Zero) is not { } sample) return TimedOut(submitted: true);
            var picture = EncoderChoice.HasPicture(sample.Data);
            var owner = _pending.Owner(sample.Time);
            if (owner != time)
            {
                // 更早那帧晚到的输出（那一帧已经当没出来处理了，这一帧是强制关键帧）、或者认不出是哪一帧的：丢掉
                if (picture)
                {
                    if (owner is { } earlier) _pending.RemoveThrough(earlier);
                    else _keyWanted = true; // 认不出来的画面：说不定是已经发出去那帧的一部分，下一帧从关键帧重新开始
                }
                continue;
            }
            (parts ??= []).Add(sample.Data);
            if (!picture) continue; // 只有 SPS / PPS / SEI：画面在后面
            _pending.RemoveThrough(time);
            break;
        }
        _timeouts = 0;

        var bytes = Concat(parts);
        var isKey = H264.IsKeyFrame(bytes);
        if (isKey)
        {
            if (H264.ParameterSets(bytes) is { } sets) _parameterSets = sets;
            else if (_parameterSets is { } cached) bytes = [.. cached, .. bytes];
        }
        if (_keyWanted)
        {
            if (isKey)
            {
                _keyWanted = false;
                _keyMisses = 0;
            }
            else if (++_keyMisses >= MaxKeyMisses)
            {
                throw Fail($"连续 {MaxKeyMisses} 帧要关键帧都没给（AVEncVideoForceKeyFrame 没有作用）");
            }
        }
        return new EncodedFrame(bytes, isKey);
    }

    /// <summary>这一帧没等到：返回 null；送进去了的话下一帧强制关键帧（它晚到的输出会被丢掉）。连续 3 帧这样就算坏了。</summary>
    private EncodedFrame? TimedOut(bool submitted)
    {
        if (submitted) _keyWanted = true;
        if (++_timeouts >= MaxTimeouts)
            throw Fail($"连续 {MaxTimeouts} 帧没在 {_frameTimeout.TotalMilliseconds:F0} 毫秒内{(submitted ? "编出来" : "要输入")}");
        return null;
    }

    private EncoderFailedException Fail(string reason, Exception? inner = null)
    {
        _failure = reason;
        return new EncoderFailedException($"{Name}：{reason}", inner);
    }

    private static byte[] Concat(List<byte[]> parts) => parts.Count == 1 ? parts[0] : [.. parts.SelectMany(p => p)];

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _inner.Dispose();
        }
        catch (Exception) { }
        if (_ownsActivate)
        {
            try
            {
                Marshal.ReleaseComObject(_activate);
            }
            catch (Exception) { }
        }
    }
}

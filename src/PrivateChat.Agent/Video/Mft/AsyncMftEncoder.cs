using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PrivateChat.Agent.Video;

/// <summary>异步编码器吐出来的一段输出：原样的 Annex B 字节、带着送进去时的样本时间（它可能攒几帧才吐，靠时间认是哪一帧；没带时间是 null）。</summary>
internal readonly record struct EncodedSample(byte[] Data, bool CleanPoint, long? Time);

/// <summary>
/// 显卡的硬件 H.264 编码器（Media Foundation 的异步 MFT：NVIDIA NVENC、AMD VCE / VCN、Intel Quick Sync）的底层驱动；
/// 远程、探测、自检都经 <see cref="HardwareH264Encoder"/> 用它（那边负责"等这一帧的输出"、关键帧、出错换软件）。
/// 异步 MFT 不能想调就调：它发 METransformNeedInput 才能送一帧（ProcessInput），发 METransformHaveOutput 才能取一帧（ProcessOutput）。
/// 事件经 IMFMediaEventGenerator.BeginGetEvent 回调到 MF 的工作线程上，那边只把事件排进队列、叫醒等着的人；
/// ProcessInput / ProcessOutput 都在调用方的线程上做——和 <see cref="H264Encoder"/> 一样，只能在一个（MTA）线程上用。
/// 设置和 H264Encoder 一致：低延迟、无 B 帧、GOP 很长、Main profile；码率控制按 <see cref="HardwareRateControl"/> 给的顺序挑第一个编码器接受的。
/// 输入是系统内存里紧凑排列的 NV12（宽 × 高 × 1.5 字节，和软件编码器一样）；以后接桌面复制的显存纹理时再加 MFT_MESSAGE_SET_D3D_MANAGER。
/// </summary>
internal sealed class AsyncMftEncoder : IDisposable
{
    private const int RateControlCbr = 0;
    private const int RateControlQuality = 3;
    /// <summary>
    /// 关掉编码器时等那个还挂着的事件请求回来最多这么久，再放掉 MFT。平常用不着等：<see cref="MftEventQueue.Stop"/> 自己往 MFT 的事件队列里放一个事件，
    /// 那次请求当场就回来了；这只是 MFT 不接受 QueueEvent 时的兜底——别指望 ShutdownObject 会让它回来（Intel Quick Sync 上实测不回来，每次都白等满）。
    /// </summary>
    private static readonly TimeSpan ShutdownWait = TimeSpan.FromMilliseconds(250);

    private readonly IMFActivate _activate;
    private readonly IMFTransformFull? _mft;
    private readonly ICodecApiFull? _codec;
    private readonly MftEventQueue? _events;
    private readonly int _inputId;
    private readonly int _outputId;
    private readonly int _inputSize;
    private readonly long _frameDuration;
    private readonly Queue<EncodedSample> _ready = new();
    private MftOutputStreamInfo _outputInfo;
    /// <summary>编码器要了、还没送进去的帧数（每个 METransformNeedInput 一帧）。</summary>
    private int _wanted;
    private bool _keyRequested;
    private int _quality = -1;
    private int _qp = -1;
    private int _bitrateKbps;
    private bool _disposed;

    public int Width { get; }
    public int Height { get; }
    public int Fps { get; }

    /// <summary>实际用的码率控制方式（构造时按给的顺序挑的第一个编码器接受的）。</summary>
    public HardwareRateControl RateControl { get; }

    /// <summary>输出媒体类型里的 SPS / PPS（MF_MT_MPEG_SEQUENCE_HEADER，带起始码）；编码器不给是 null（那就只能从关键帧里取）。</summary>
    public byte[]? ParameterSets { get; private set; }

    /// <param name="activate">MFTEnumEx 列出来的那个编码器：这里 ActivateObject 建出来，Dispose 时 ShutdownObject 关掉（activate 本身归调用方放）。</param>
    /// <param name="bitrateKbps">固定码率模式的码率；按质量时只是写进媒体类型里的名义值。</param>
    /// <param name="modes">按顺序试的码率控制方式；一个都不接受就抛 NotSupportedException。</param>
    /// <param name="quality">第一帧的质量（0–100，同 H264Encoder；逐帧 QP 时按 <see cref="EncoderChoice.QpForQuality"/> 换算）。</param>
    public AsyncMftEncoder(IMFActivate activate, int width, int height, int fps, int bitrateKbps, IReadOnlyList<HardwareRateControl> modes, int quality)
    {
        if (width <= 0 || height <= 0 || width % 2 != 0 || height % 2 != 0)
            throw new ArgumentException($"画面尺寸必须是正偶数：{width}x{height}");
        Width = width;
        Height = height;
        Fps = fps;
        _inputSize = width * height * 3 / 2;
        _frameDuration = TimeSpan.TicksPerSecond / fps;
        _activate = activate;

        MF.EnsureStarted();
        MF.Check(activate.ActivateObject(typeof(IMFTransformFull).GUID, out var unknown), "创建编码器（ActivateObject）");
        try
        {
            try
            {
                _mft = (IMFTransformFull)Marshal.GetObjectForIUnknown(unknown);
            }
            finally
            {
                Marshal.Release(unknown);
            }
            // 异步 MFT 要先解锁，否则别的方法都返回 MF_E_TRANSFORM_ASYNC_LOCKED
            MF.Check(_mft.GetAttributes(out var attributes), "读编码器属性");
            try
            {
                if (attributes.GetUINT32(Mft.TransformAsync, out var async) < 0 || async == 0)
                    throw new NotSupportedException("这个编码器不是异步 MFT");
                MF.Check(attributes.SetUINT32(Mft.TransformAsyncUnlock, 1), "解锁异步编码器");
                attributes.SetUINT32(MF.LowLatency, 1);
            }
            finally
            {
                Mft.ReleaseAttributes(attributes, _mft);
            }
            _events = new MftEventQueue(_mft as IMFMediaEventGenerator ?? throw new NotSupportedException("编码器没有事件接口（IMFMediaEventGenerator）"));
            (_inputId, _outputId) = StreamIds(_mft);

            // 码率控制等要在设置媒体类型之前定下来（同 H264Encoder）
            _codec = _mft as ICodecApiFull;
            TrySet(MF.LowLatency, Variant64.Bool(true));
            _bitrateKbps = bitrateKbps;
            quality = Math.Clamp(quality, 0, 100);
            HardwareRateControl? chosen = null;
            foreach (var mode in modes)
            {
                if (TryRateControl(mode, quality, bitrateKbps))
                {
                    chosen = mode;
                    break;
                }
            }
            RateControl = chosen ?? throw new NotSupportedException($"编码器不接受这些码率控制方式：{string.Join(" / ", modes)}");
            TrySet(MF.AVEncMPVDefaultBPictureCount, Variant64.UInt(0));
            TrySet(MF.AVEncMPVGOPSize, Variant64.UInt(fps * 600));

            // 先输出类型、再输入类型
            var output = Mft.H264Type(width, height, fps, bitrateKbps);
            try
            {
                MF.Check(_mft.SetOutputType(_outputId, output, 0), "设置 H.264 输出类型");
            }
            finally
            {
                Marshal.ReleaseComObject(output);
            }
            var input = Mft.Nv12Type(width, height, fps);
            try
            {
                MF.Check(_mft.SetInputType(_inputId, input, 0), "设置 NV12 输入类型");
            }
            finally
            {
                Marshal.ReleaseComObject(input);
            }
            MF.Check(_mft.GetOutputStreamInfo(_outputId, out _outputInfo), "GetOutputStreamInfo");
            ParameterSets = ReadSequenceHeader();

            _events.Start();
            MF.Check(_mft.ProcessMessage(MF.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0), "开始编码（BEGIN_STREAMING）");
            // 异步 MFT 收到 START_OF_STREAM 之后才开始要输入
            MF.Check(_mft.ProcessMessage(MF.MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0), "开始编码（START_OF_STREAM）");
        }
        catch
        {
            Shutdown();
            throw;
        }
    }

    /// <summary>
    /// 设一种码率控制方式（媒体类型之前）：
    /// - 按质量：模式 Quality + AVEncCommonQuality；
    /// - 逐帧 QP：模式 Quality（固定 QP 编码）+ AVEncVideoEncodeQP；
    /// - 固定码率：模式 CBR + 平均码率。连 ICodecAPI 都没有的编码器也当它接受（用它默认的码率控制、媒体类型里的码率）。
    /// </summary>
    private bool TryRateControl(HardwareRateControl mode, int quality, int bitrateKbps)
    {
        switch (mode)
        {
            case HardwareRateControl.Quality:
                if (!TrySet(MF.AVEncCommonRateControlMode, Variant64.UInt(RateControlQuality)) || !TrySet(MF.AVEncCommonQuality, Variant64.UInt(quality)))
                    return false;
                _quality = quality;
                return true;
            case HardwareRateControl.Qp:
                var qp = EncoderChoice.QpForQuality(quality);
                if (!TrySet(MF.AVEncCommonRateControlMode, Variant64.UInt(RateControlQuality)) || !TrySet(Mft.AVEncVideoEncodeQP, Variant64.UInt64(EncoderChoice.PackQp(qp))))
                    return false;
                _quality = quality;
                _qp = qp;
                return true;
            default:
                if (_codec is not null)
                {
                    TrySet(MF.AVEncCommonRateControlMode, Variant64.UInt(RateControlCbr));
                    TrySet(MF.AVEncCommonMeanBitRate, Variant64.UInt(bitrateKbps * 1000));
                }
                return true;
        }
    }

    /// <summary>下一帧编成关键帧（送那一帧之前设 AVEncVideoForceKeyFrame）。</summary>
    public void RequestKeyFrame() => _keyRequested = true;

    /// <summary>之后的帧用这个质量（0–100；固定码率时没有作用）。</summary>
    public void SetQuality(int quality)
    {
        quality = Math.Clamp(quality, 0, 100);
        if (quality == _quality) return;
        switch (RateControl)
        {
            case HardwareRateControl.Quality:
                if (TrySet(MF.AVEncCommonQuality, Variant64.UInt(quality))) _quality = quality;
                break;
            case HardwareRateControl.Qp:
                var qp = EncoderChoice.QpForQuality(quality);
                if (qp == _qp || TrySet(Mft.AVEncVideoEncodeQP, Variant64.UInt64(EncoderChoice.PackQp(qp))))
                {
                    _qp = qp;
                    _quality = quality;
                }
                break;
        }
    }

    /// <summary>固定码率模式下中途改码率。</summary>
    public void SetBitrate(int kbps)
    {
        if (RateControl != HardwareRateControl.Cbr || kbps == _bitrateKbps) return;
        if (TrySet(MF.AVEncCommonMeanBitRate, Variant64.UInt(kbps * 1000))) _bitrateKbps = kbps;
    }

    /// <summary>等编码器要输入（最多等 timeout），送进去一帧 NV12；false：这么久它都没要。</summary>
    public bool TrySubmit(ReadOnlySpan<byte> nv12, long time, TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (nv12.Length < _inputSize) throw new ArgumentException("NV12 数据不完整");
        var deadline = Deadline(timeout);
        if (!WaitUntil(() => _wanted > 0, deadline)) return false;

        MF.Check(MF.MFCreateMemoryBuffer(_inputSize, out var buffer), "MFCreateMemoryBuffer");
        MF.Check(MF.MFCreateSample(out var sample), "MFCreateSample");
        try
        {
            MF.Check(buffer.Lock(out var ptr, out _, out _), "锁定输入缓冲");
            unsafe { nv12[.._inputSize].CopyTo(new Span<byte>((void*)ptr, _inputSize)); }
            buffer.Unlock();
            buffer.SetCurrentLength(_inputSize);
            sample.AddBuffer(buffer);
            sample.SetSampleTime(time);
            sample.SetSampleDuration(_frameDuration);
            if (_keyRequested)
            {
                _keyRequested = false;
                TrySet(MF.AVEncVideoForceKeyFrame, Variant64.UInt(1));
            }
            while (true)
            {
                _wanted--;
                var hr = _mft!.ProcessInput(_inputId, sample, 0);
                if (hr != Mft.MF_E_NOTACCEPTING)
                {
                    MF.Check(hr, "编码输入");
                    return true;
                }
                // 我们记的"它要几帧"和它自己的不一致：当它现在不要，等下一个 METransformNeedInput 再送
                _wanted = 0;
                if (!WaitUntil(() => _wanted > 0, deadline)) return false;
            }
        }
        finally
        {
            Marshal.ReleaseComObject(sample);
            Marshal.ReleaseComObject(buffer);
        }
    }

    /// <summary>等编码器吐出一段输出（最多等 timeout）；没等到返回 null。</summary>
    public EncodedSample? TryReceive(TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        WaitUntil(() => _ready.Count > 0, Deadline(timeout));
        return _ready.TryDequeue(out var sample) ? sample : null;
    }

    /// <summary>已经出来的输出都丢掉（不等）。</summary>
    public void DiscardReady()
    {
        Pump();
        _ready.Clear();
    }

    /// <summary>处理已经到了的事件：要输入的记一笔，有输出的当场取出来。事件报错（状态是失败、MEError）抛 MediaFoundationException。</summary>
    private void Pump()
    {
        while (_events!.TryTake(out var e))
        {
            if (e.Status < 0) throw new MediaFoundationException($"硬件编码器（事件 {e.Type}）", e.Status);
            switch (e.Type)
            {
                case Mft.METransformNeedInput:
                    _wanted++;
                    break;
                case Mft.METransformHaveOutput:
                    TakeOutput();
                    break;
                case Mft.MEError:
                    throw new MediaFoundationException("硬件编码器报错", unchecked((int)0x80004005));
            }
        }
    }

    private static long Deadline(TimeSpan timeout) => Stopwatch.GetTimestamp() + (long)(Math.Max(0, timeout.TotalSeconds) * Stopwatch.Frequency);

    private bool WaitUntil(Func<bool> done, long deadline)
    {
        while (true)
        {
            Pump();
            if (done()) return true;
            var left = deadline - Stopwatch.GetTimestamp();
            if (left <= 0) return false;
            _events!.Wait(TimeSpan.FromSeconds((double)left / Stopwatch.Frequency));
        }
    }

    /// <summary>
    /// 收到 METransformHaveOutput：取一次输出（每个事件只能取一次）。
    /// 编码器说输出格式变了（MF_E_TRANSFORM_STREAM_CHANGE，Intel 的第一帧就这样）：换上它给的格式，等它再发一次 HaveOutput 再取——
    /// 当场再取一次会把下一个事件的那帧提前拿走，到时候 ProcessOutput 就返回 E_UNEXPECTED（FFmpeg 的 mfenc 也是这样等的）。
    /// </summary>
    private void TakeOutput()
    {
        var providesSamples = (_outputInfo.Flags & (MF.MFT_OUTPUT_STREAM_PROVIDES_SAMPLES | MF.MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES)) != 0;
        IMFSample? mine = null;
        IMFMediaBuffer? mineBuffer = null;
        var data = new MftOutputDataBuffer { StreamId = _outputId };
        if (!providesSamples)
        {
            MF.Check(MF.MFCreateMemoryBuffer(Math.Max(_outputInfo.Size, _inputSize), out mineBuffer), "MFCreateMemoryBuffer");
            MF.Check(MF.MFCreateSample(out mine), "MFCreateSample");
            mine.AddBuffer(mineBuffer);
            data.Sample = Marshal.GetComInterfaceForObject<IMFSample, IMFSample>(mine);
        }
        try
        {
            var hr = _mft!.ProcessOutput(0, 1, ref data, out _);
            if (data.Events != 0) Marshal.Release(data.Events);
            if (hr == MF.MF_E_TRANSFORM_STREAM_CHANGE)
            {
                RenegotiateOutput();
                return;
            }
            if (hr == MF.MF_E_TRANSFORM_NEED_MORE_INPUT) return;
            MF.Check(hr, "编码输出");
            if (data.Sample == 0) return;

            var produced = mine ?? (IMFSample)Marshal.GetObjectForIUnknown(data.Sample);
            try
            {
                var cleanPoint = produced.GetUINT32(MF.SampleExtension_CleanPoint, out var clean) >= 0 && clean != 0;
                long? time = produced.GetSampleTime(out var t) >= 0 ? t : null;
                MF.Check(produced.ConvertToContiguousBuffer(out var contiguous), "读取编码输出");
                byte[] bytes;
                try
                {
                    MF.Check(contiguous.Lock(out var ptr, out _, out var length), "锁定输出缓冲");
                    bytes = new byte[length];
                    Marshal.Copy(ptr, bytes, 0, length);
                    contiguous.Unlock();
                }
                finally
                {
                    Marshal.ReleaseComObject(contiguous);
                }
                if (bytes.Length > 0) _ready.Enqueue(new EncodedSample(bytes, cleanPoint, time));
            }
            finally
            {
                if (mine is null) Marshal.ReleaseComObject(produced);
            }
        }
        finally
        {
            if (data.Sample != 0) Marshal.Release(data.Sample);
            if (mine is not null) Marshal.ReleaseComObject(mine);
            if (mineBuffer is not null) Marshal.ReleaseComObject(mineBuffer);
        }
    }

    private void RenegotiateOutput()
    {
        MF.Check(_mft!.GetOutputAvailableType(_outputId, 0, out var type), "编码器换输出格式");
        try
        {
            MF.Check(_mft.SetOutputType(_outputId, type, 0), "编码器换输出格式");
        }
        finally
        {
            Marshal.ReleaseComObject(type);
        }
        MF.Check(_mft.GetOutputStreamInfo(_outputId, out _outputInfo), "GetOutputStreamInfo");
        ParameterSets = ReadSequenceHeader() ?? ParameterSets;
    }

    /// <summary>流号：大多数 MFT 不实现 GetStreamIDs（E_NOTIMPL），那就是从 0 开始。</summary>
    private static (int Input, int Output) StreamIds(IMFTransformFull mft)
    {
        var input = new int[1];
        var output = new int[1];
        return mft.GetStreamIDs(1, input, 1, output) >= 0 ? (input[0], output[0]) : (0, 0);
    }

    private byte[]? ReadSequenceHeader()
    {
        if (_mft!.GetOutputCurrentType(_outputId, out var current) < 0) return null;
        try
        {
            if (current.GetBlobSize(MF.MT_MPEG_SEQUENCE_HEADER, out var size) < 0 || size <= 0) return null;
            var blob = new byte[size];
            int written;
            unsafe
            {
                fixed (byte* p = blob)
                    if (current.GetBlob(MF.MT_MPEG_SEQUENCE_HEADER, (IntPtr)p, size, out written) < 0) return null;
            }
            return PrivateChat.Contracts.Remote.H264.ParameterSets(blob.AsSpan(0, written));
        }
        finally
        {
            Marshal.ReleaseComObject(current);
        }
    }

    private bool TrySet(Guid api, Variant64 value) => _codec is not null && _codec.SetValue(api, value) >= 0;

    /// <summary>
    /// 关掉：不再要事件、把还挂着的那次事件请求叫回来（<see cref="MftEventQueue.Stop"/>）→ END_OF_STREAM / END_STREAMING
    /// → ShutdownObject（硬件编码器要这样关，才会放掉显卡上的编码会话，NVIDIA 的消费级显卡同时只给几路）
    /// → 等那次请求的回调跑完（它在 MF 的工作线程上还会用到这个 MFT；最多 <see cref="ShutdownWait"/>）→ 放掉 MFT。不抛异常。
    /// </summary>
    private void Shutdown()
    {
        try
        {
            _events?.Stop();
            if (_mft is not null)
            {
                _mft.ProcessMessage(Mft.MFT_MESSAGE_NOTIFY_END_OF_STREAM, 0);
                _mft.ProcessMessage(MF.MFT_MESSAGE_NOTIFY_END_STREAMING, 0);
            }
            _activate.ShutdownObject();
            _events?.WaitIdle(ShutdownWait);
        }
        catch (Exception) { } // 驱动已经坏了也要接着放
        try
        {
            if (_mft is not null) Marshal.ReleaseComObject(_mft);
        }
        catch (Exception) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Shutdown();
    }
}

/// <summary>
/// 异步 MFT 的事件：BeginGetEvent 的回调（在 MF 的工作线程上）把事件类型和状态排进队列、叫醒等着的线程，再接着要下一个事件。
/// 不在回调里调 MFT 的别的方法：送输入、取输出都留给编码线程。
/// </summary>
[ComVisible(true)]
internal sealed class MftEventQueue(IMFMediaEventGenerator generator) : IMFAsyncCallback
{
    private readonly ConcurrentQueue<(int Type, int Status)> _queue = new();
    private readonly AutoResetEvent _signal = new(false);
    /// <summary>没有挂着的事件请求、回调也没在跑（关编码器时等这个）。</summary>
    private readonly ManualResetEventSlim _idle = new(true);
    private volatile bool _stopped;

    public void Start() => Arm();

    /// <summary>
    /// 不再要新的事件，并把已经发出去的那一次请求叫回来：自己往 MFT 的事件队列里放一个 MEUnknown（QueueEvent），
    /// 回调拿到它（或者正好先到的别的事件）、看到已经停了，就不再要下一个、记成空闲——关编码器的那边 WaitIdle 当场返回。
    /// 不能指望 MFT 关掉（ShutdownObject）时让那次请求以 MF_E_SHUTDOWN 回来：Intel Quick Sync 上实测不回来，每次 Dispose 都白等满 ShutdownWait。
    /// MFT 不接受 QueueEvent（报错、抛异常）就还是靠 WaitIdle 等到超时。只在用编码器的那个线程上调。
    /// </summary>
    public void Stop()
    {
        _stopped = true;
        // 没有挂着的请求（还没 Start、或者 BeginGetEvent 失败了）：不用叫；_idle 只在 Arm 里清，Arm 只在这个线程（Start）和回调里调，
        // 这里看到空闲就不会再有新的请求
        if (_idle.IsSet) return;
        try
        {
            generator.QueueEvent(Mft.MEUnknown, Guid.Empty, 0 /* S_OK */, IntPtr.Zero);
        }
        catch (Exception) { } // 驱动已经坏了：还有 WaitIdle 兜底
    }

    /// <summary>等挂着的那次请求回来（最多 timeout）。</summary>
    public void WaitIdle(TimeSpan timeout) => _idle.Wait(timeout);

    public bool TryTake(out (int Type, int Status) e) => _queue.TryDequeue(out e);

    /// <summary>等到有新事件或超时。</summary>
    public void Wait(TimeSpan timeout) => _signal.WaitOne(timeout);

    private void Arm()
    {
        _idle.Reset();
        var hr = generator.BeginGetEvent(this, null);
        if (hr < 0)
        {
            _idle.Set();
            Post(Mft.MEError, hr);
        }
    }

    private void Post(int type, int status)
    {
        _queue.Enqueue((type, status));
        _signal.Set();
    }

    int IMFAsyncCallback.GetParameters(out int flags, out int queue)
    {
        flags = 0;
        queue = 0;
        return Mft.E_NOTIMPL; // 用默认的工作队列
    }

    int IMFAsyncCallback.Invoke(IntPtr result)
    {
        var rearmed = false;
        // 这里抛出去的异常会被 COM 吞成错误码：自己接住，当成一个出错事件
        try
        {
            // 关的时候也照样 EndGetEvent（每次 BeginGetEvent 都要配一次）：关编码器的那边等这个回调跑完（WaitIdle）才放掉 MFT；
            // 等超时了才放的话，这里调已经放掉的 MFT 会抛 InvalidComObjectException，下面接住
            var hr = generator.EndGetEvent(result, out var ev);
            if (hr < 0)
            {
                if (!_stopped) Post(Mft.MEError, hr);
                return 0;
            }
            int type, status;
            try
            {
                if (ev.GetEventType(out type) < 0) type = 0;
                if (ev.GetStatus(out status) < 0) status = 0;
            }
            finally
            {
                Marshal.ReleaseComObject(ev);
            }
            if (_stopped) return 0;
            Post(type, status);
            rearmed = true;
            Arm();
        }
        catch (Exception ex)
        {
            if (!_stopped) Post(Mft.MEError, ex.HResult < 0 ? ex.HResult : unchecked((int)0x80004005));
        }
        finally
        {
            if (!rearmed) _idle.Set();
        }
        return 0;
    }
}

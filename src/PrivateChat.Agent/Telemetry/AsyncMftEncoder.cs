using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using PrivateChat.Agent.Video;
using PrivateChat.Contracts.Remote;

namespace PrivateChat.Agent.Telemetry;

/// <summary>异步编码器吐出来的一帧：带着送进去时的时间戳（它可能攒几帧才吐，靠时间戳对上是哪一帧）。</summary>
internal readonly record struct EncodedSample(byte[] Data, bool IsKey, TimeSpan Timestamp);

/// <summary>
/// 显卡的硬件 H.264 编码器（Media Foundation 的异步 MFT：NVIDIA NVENC、AMD VCE / VCN、Intel Quick Sync）。
/// 异步 MFT 不能想调就调：它发 METransformNeedInput 才能送一帧（ProcessInput），发 METransformHaveOutput 才能取一帧（ProcessOutput）。
/// 事件经 IMFMediaEventGenerator.BeginGetEvent 回调到 MF 的工作线程上，那边只把事件排进队列、叫醒等着的人；
/// ProcessInput / ProcessOutput 都在调用方的线程上做——和 <see cref="H264Encoder"/> 一样，只能在一个线程上用。
/// 设置和 H264Encoder 一致：低延迟、无 B 帧、GOP 很长、Main profile；给了 quality 且编码器接受就按质量编，否则固定码率。
/// 输入是系统内存里的 NV12（宽 × 高 × 1.5 字节，和软件编码器一样）；以后接桌面复制时再加显存纹理（MFT_MESSAGE_SET_D3D_MANAGER）。
/// 现在只有探测（--probe-hardware）用它试编；下一步远程画面用硬件编码时就用这个类。
/// </summary>
internal sealed class AsyncMftEncoder : IDisposable
{
    private const int RateControlCbr = 0;
    private const int RateControlQuality = 3;

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
    private byte[]? _parameterSets;
    /// <summary>编码器要了、还没送进去的帧数（每个 METransformNeedInput 一帧）。</summary>
    private int _wanted;
    private bool _drained;
    private bool _keyRequested;
    private int _quality = -1;
    private int _bitrateKbps;
    private bool _disposed;

    public int Width { get; }
    public int Height { get; }
    public int Fps { get; }

    /// <summary>在按质量的模式下工作（false：编码器不接受，用的固定码率）。</summary>
    public bool QualityMode { get; }

    /// <param name="activate">MFTEnumEx 列出来的那个编码器：这里 ActivateObject 建出来，Dispose 时 ShutdownObject 关掉（activate 本身归调用方放）。</param>
    /// <param name="bitrateKbps">固定码率模式的码率；按质量时只是写进媒体类型里的名义值。</param>
    /// <param name="quality">给了就试按质量的模式（0–100，同 H264Encoder）。</param>
    public AsyncMftEncoder(IMFActivate activate, int width, int height, int fps, int bitrateKbps, int? quality = null)
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
            if (quality is { } q && TrySet(MF.AVEncCommonRateControlMode, Variant64.UInt(RateControlQuality))
                && TrySet(MF.AVEncCommonQuality, Variant64.UInt(Math.Clamp(q, 0, 100))))
            {
                QualityMode = true;
                _quality = Math.Clamp(q, 0, 100);
            }
            else
            {
                TrySet(MF.AVEncCommonRateControlMode, Variant64.UInt(RateControlCbr));
                TrySet(MF.AVEncCommonMeanBitRate, Variant64.UInt(bitrateKbps * 1000));
            }
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
            _parameterSets = ReadSequenceHeader();

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

    /// <summary>下一帧编成关键帧。</summary>
    public void RequestKeyFrame() => _keyRequested = true;

    /// <summary>之后的帧用这个质量（0–100；只在按质量的模式下有用）。</summary>
    public void SetQuality(int quality)
    {
        quality = Math.Clamp(quality, 0, 100);
        if (!QualityMode || quality == _quality) return;
        if (TrySet(MF.AVEncCommonQuality, Variant64.UInt(quality))) _quality = quality;
    }

    /// <summary>固定码率模式下中途改码率。</summary>
    public void SetBitrate(int kbps)
    {
        if (QualityMode || kbps == _bitrateKbps) return;
        if (TrySet(MF.AVEncCommonMeanBitRate, Variant64.UInt(kbps * 1000))) _bitrateKbps = kbps;
    }

    /// <summary>等编码器要输入（最多等 timeout），送进去一帧 NV12；false：这么久它都没要。</summary>
    public bool TrySubmit(ReadOnlySpan<byte> nv12, TimeSpan timestamp, TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (nv12.Length < _inputSize) throw new ArgumentException("NV12 数据不完整");
        if (!WaitUntil(() => _wanted > 0, timeout)) return false;
        _wanted--;

        if (_keyRequested)
        {
            _keyRequested = false;
            TrySet(MF.AVEncVideoForceKeyFrame, Variant64.UInt(1));
        }
        MF.Check(MF.MFCreateMemoryBuffer(_inputSize, out var buffer), "MFCreateMemoryBuffer");
        MF.Check(MF.MFCreateSample(out var sample), "MFCreateSample");
        try
        {
            MF.Check(buffer.Lock(out var ptr, out _, out _), "锁定输入缓冲");
            unsafe { nv12[.._inputSize].CopyTo(new Span<byte>((void*)ptr, _inputSize)); }
            buffer.Unlock();
            buffer.SetCurrentLength(_inputSize);
            sample.AddBuffer(buffer);
            sample.SetSampleTime(timestamp.Ticks);
            sample.SetSampleDuration(_frameDuration);
            MF.Check(_mft!.ProcessInput(_inputId, sample, 0), "编码输入");
        }
        finally
        {
            Marshal.ReleaseComObject(sample);
            Marshal.ReleaseComObject(buffer);
        }
        return true;
    }

    /// <summary>等编好的一帧（最多等 timeout）；没等到返回 null。</summary>
    public EncodedSample? TryReceive(TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        WaitUntil(() => _ready.Count > 0, timeout);
        return _ready.TryDequeue(out var sample) ? sample : null;
    }

    /// <summary>不再送新帧了：让编码器把手里的帧都吐出来（最多等 timeout），返回这些帧。之后这个编码器只能 Dispose。</summary>
    public List<EncodedSample> Drain(TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        MF.Check(_mft!.ProcessMessage(Mft.MFT_MESSAGE_COMMAND_DRAIN, 0), "排空编码器（COMMAND_DRAIN）");
        WaitUntil(() => _drained, timeout);
        var all = new List<EncodedSample>(_ready);
        _ready.Clear();
        return all;
    }

    /// <summary>处理已经到了的事件：要输入的记一笔，有输出的当场取出来。</summary>
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
                case Mft.METransformDrainComplete:
                    _drained = true;
                    break;
                case Mft.MEError:
                    throw new MediaFoundationException("硬件编码器报错", unchecked((int)0x80004005));
            }
        }
    }

    private bool WaitUntil(Func<bool> done, TimeSpan timeout)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(Math.Max(0, timeout.TotalSeconds) * Stopwatch.Frequency);
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

            var produced = mine ?? (IMFSample)Marshal.GetObjectForIUnknown(data.Sample);
            try
            {
                var cleanPoint = produced.GetUINT32(MF.SampleExtension_CleanPoint, out var clean) >= 0 && clean != 0;
                var time = produced.GetSampleTime(out var t) >= 0 ? t : -1;
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
                var isKey = cleanPoint || H264.IsKeyFrame(bytes);
                if (isKey)
                {
                    // 关键帧前总带 SPS / PPS（同 H264Encoder）
                    if (H264.ParameterSets(bytes) is { } sets) _parameterSets = sets;
                    else if (_parameterSets is { } cached) bytes = [.. cached, .. bytes];
                }
                if (bytes.Length > 0) _ready.Enqueue(new EncodedSample(bytes, isKey, TimeSpan.FromTicks(time)));
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
        _parameterSets = ReadSequenceHeader() ?? _parameterSets;
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
            return H264.ParameterSets(blob.AsSpan(0, written));
        }
        finally
        {
            Marshal.ReleaseComObject(current);
        }
    }

    private bool TrySet(Guid api, Variant64 value) => _codec is not null && _codec.SetValue(api, value) >= 0;

    private void Shutdown()
    {
        _events?.Stop();
        if (_mft is not null)
        {
            _mft.ProcessMessage(Mft.MFT_MESSAGE_NOTIFY_END_OF_STREAM, 0);
            _mft.ProcessMessage(MF.MFT_MESSAGE_NOTIFY_END_STREAMING, 0);
        }
        // 硬件编码器要 ShutdownObject 才会放掉显卡上的编码会话（NVIDIA 的消费级显卡同时只给几路）
        _activate.ShutdownObject();
        if (_mft is not null) Marshal.ReleaseComObject(_mft);
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
    private volatile bool _stopped;

    public void Start() => Arm();

    /// <summary>不再要新的事件（已经发出去的那一次请求，MFT 关掉时会以 MF_E_SHUTDOWN 回来，丢掉就是）。</summary>
    public void Stop() => _stopped = true;

    public bool TryTake(out (int Type, int Status) e) => _queue.TryDequeue(out e);

    /// <summary>等到有新事件或超时。</summary>
    public void Wait(TimeSpan timeout) => _signal.WaitOne(timeout);

    private void Arm()
    {
        var hr = generator.BeginGetEvent(this, null);
        if (hr < 0) Post(Mft.MEError, hr);
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
        // 这里抛出去的异常会被 COM 吞成错误码：自己接住，当成一个出错事件
        try
        {
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
            Arm();
        }
        catch (Exception ex)
        {
            if (!_stopped) Post(Mft.MEError, ex.HResult < 0 ? ex.HResult : unchecked((int)0x80004005));
        }
        return 0;
    }
}

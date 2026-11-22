using System.Runtime.InteropServices;
using PrivateChat.Contracts.Remote;

namespace PrivateChat.Agent.Video;

/// <summary>
/// Windows 自带的软件 H.264 编码器（Media Foundation，CLSID_CMSH264EncoderMFT），同步 MFT：一帧进、一帧出。
/// 低延迟模式、无 B 帧、GOP 很长（关键帧只在开头和按需时出现，见 <see cref="RequestKeyFrame"/>）。
/// 两种码率控制：
/// - 按质量（给了 quality）：每帧的质量由调用方定（<see cref="SetQuality"/>，可以逐帧改），帧有多大看画面内容。
///   远程画面用这个——编码器自己的固定码率在画面突变时管不住，见 <see cref="PrivateChat.Agent.Session.QualityPlanner"/>。
/// - 固定码率（没给 quality，或者这台电脑的编码器不接受按质量）：<see cref="SetBitrate"/> 可以中途改。
/// 输出 Annex B；关键帧前总是带 SPS / PPS，方便控制端中途加入或解码器重置后直接开始。
/// 远程默认用它；探测验证过的显卡硬件编码器（<see cref="HardwareH264Encoder"/>）能用时用那个，起不来、中途出错又换回它（见 <see cref="VideoEncoders"/>）。
/// 只能在一个线程上用（采集循环）。
/// </summary>
public sealed class H264Encoder : IVideoEncoder
{
    /// <summary>日志、遥测里的名字（会话遥测、诊断里也是这个）。</summary>
    public const string SoftwareName = "Microsoft H.264（软件）";

    public string Name => SoftwareName;
    public bool IsHardware => false;

    private const int ProfileMain = 77;
    private const int RateControlCbr = 0;
    private const int RateControlQuality = 3;

    private readonly IMFTransform _mft;
    private readonly ICodecAPI? _codec;
    private readonly MftOutputStreamInfo _outputInfo;
    private readonly int _inputSize;
    private readonly long _frameDuration;
    private byte[]? _parameterSets;
    private bool _keyRequested;
    private bool _disposed;

    public int Width { get; }
    public int Height { get; }
    public int Fps { get; }

    /// <summary>在按质量的模式下工作（false：这台电脑的编码器不接受，退回了固定码率）。</summary>
    public bool QualityMode { get; }

    private int _quality = -1;
    private int _bitrateKbps;

    /// <param name="bitrateKbps">固定码率模式的码率；按质量时只是写进媒体类型里的名义值。</param>
    /// <param name="quality">给了就用按质量的模式，这是第一帧的质量（0–100）。</param>
    public H264Encoder(int width, int height, int fps, int bitrateKbps, int? quality = null)
    {
        if (width <= 0 || height <= 0 || width % 2 != 0 || height % 2 != 0)
            throw new ArgumentException($"画面尺寸必须是正偶数：{width}x{height}");
        Width = width;
        Height = height;
        Fps = fps;
        _inputSize = width * height * 3 / 2;
        _frameDuration = TimeSpan.TicksPerSecond / fps;

        MF.EnsureStarted();
        var type = Type.GetTypeFromCLSID(MF.CLSID_MSH264EncoderMFT, throwOnError: true)!;
        _mft = (IMFTransform)Activator.CreateInstance(type)!;
        try
        {
            if (_mft.GetAttributes(out var attributes) >= 0)
            {
                attributes.SetUINT32(MF.LowLatency, 1);
                Marshal.ReleaseComObject(attributes);
            }

            _codec = _mft as ICodecAPI;
            // 码率控制等要在设置媒体类型之前定下来
            TrySet(MF.LowLatency, ComVariant32.Bool(true));
            _bitrateKbps = bitrateKbps;
            if (quality is { } q && TrySet(MF.AVEncCommonRateControlMode, ComVariant32.UInt(RateControlQuality))
                && TrySet(MF.AVEncCommonQuality, ComVariant32.UInt(Math.Clamp(q, 0, 100))))
            {
                QualityMode = true;
                _quality = Math.Clamp(q, 0, 100);
            }
            else
            {
                TrySet(MF.AVEncCommonRateControlMode, ComVariant32.UInt(RateControlCbr));
                TrySet(MF.AVEncCommonMeanBitRate, ComVariant32.UInt(bitrateKbps * 1000));
            }
            TrySet(MF.AVEncMPVDefaultBPictureCount, ComVariant32.UInt(0));
            TrySet(MF.AVEncMPVGOPSize, ComVariant32.UInt(fps * 600));
            TrySet(MF.AVEncCommonQualityVsSpeed, ComVariant32.UInt(33));

            // 编码器要求先设输出类型、再设输入类型
            MF.Check(MF.MFCreateMediaType(out var output), "MFCreateMediaType");
            MF.Check(output.SetGUID(MF.MT_MAJOR_TYPE, MF.MediaType_Video), "输出类型");
            MF.Check(output.SetGUID(MF.MT_SUBTYPE, MF.VideoFormat_H264), "输出类型");
            MF.Check(output.SetUINT32(MF.MT_AVG_BITRATE, bitrateKbps * 1000), "输出类型");
            MF.Check(output.SetUINT64(MF.MT_FRAME_SIZE, MF.Pack(width, height)), "输出类型");
            MF.Check(output.SetUINT64(MF.MT_FRAME_RATE, MF.Pack(fps, 1)), "输出类型");
            MF.Check(output.SetUINT64(MF.MT_PIXEL_ASPECT_RATIO, MF.Pack(1, 1)), "输出类型");
            MF.Check(output.SetUINT32(MF.MT_INTERLACE_MODE, 2 /* 逐行 */), "输出类型");
            MF.Check(output.SetUINT32(MF.MT_MPEG2_PROFILE, ProfileMain), "输出类型");
            MF.Check(_mft.SetOutputType(0, output, 0), "设置 H.264 输出类型");
            Marshal.ReleaseComObject(output);

            MF.Check(MF.MFCreateMediaType(out var input), "MFCreateMediaType");
            MF.Check(input.SetGUID(MF.MT_MAJOR_TYPE, MF.MediaType_Video), "输入类型");
            MF.Check(input.SetGUID(MF.MT_SUBTYPE, MF.VideoFormat_NV12), "输入类型");
            MF.Check(input.SetUINT64(MF.MT_FRAME_SIZE, MF.Pack(width, height)), "输入类型");
            MF.Check(input.SetUINT64(MF.MT_FRAME_RATE, MF.Pack(fps, 1)), "输入类型");
            MF.Check(input.SetUINT64(MF.MT_PIXEL_ASPECT_RATIO, MF.Pack(1, 1)), "输入类型");
            MF.Check(input.SetUINT32(MF.MT_INTERLACE_MODE, 2), "输入类型");
            input.SetUINT32(MF.MT_YUV_MATRIX, 1 /* BT.709 */);
            input.SetUINT32(MF.MT_VIDEO_NOMINAL_RANGE, 2 /* 16..235 */);
            MF.Check(_mft.SetInputType(0, input, 0), "设置 NV12 输入类型");
            Marshal.ReleaseComObject(input);

            MF.Check(_mft.GetOutputStreamInfo(0, out _outputInfo), "GetOutputStreamInfo");
            _parameterSets = ReadSequenceHeader();

            _mft.ProcessMessage(MF.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0);
            _mft.ProcessMessage(MF.MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0);
        }
        catch
        {
            Marshal.ReleaseComObject(_mft);
            throw;
        }
    }

    /// <summary>下一帧编成关键帧（控制端刚连上、解码出错、切换了显示器之后）。</summary>
    public void RequestKeyFrame() => _keyRequested = true;

    /// <summary>
    /// 之后的帧用这个质量（0–100，实测每 3.5 对应量化参数差 1：0 ≈ QP 44，70 ≈ QP 24，100 ≈ QP 16）。
    /// 质量是对整幅画面生效的：调高之后，没变的部分也会被重新编一遍。
    /// </summary>
    public void SetQuality(int quality)
    {
        quality = Math.Clamp(quality, 0, 100);
        if (!QualityMode || quality == _quality) return;
        if (TrySet(MF.AVEncCommonQuality, ComVariant32.UInt(quality))) _quality = quality;
    }

    /// <summary>固定码率模式下中途改码率（按质量的模式下没有作用）。</summary>
    public void SetBitrate(int kbps)
    {
        if (QualityMode || kbps == _bitrateKbps) return;
        if (TrySet(MF.AVEncCommonMeanBitRate, ComVariant32.UInt(kbps * 1000))) _bitrateKbps = kbps;
    }

    /// <summary>编一帧 NV12（宽 × 高 × 1.5 字节）。同步编码器一般当场出一帧；偶尔没有输出时返回 null。</summary>
    public EncodedFrame? Encode(ReadOnlySpan<byte> nv12, TimeSpan timestamp)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (nv12.Length < _inputSize) throw new ArgumentException("NV12 数据不完整");

        if (_keyRequested)
        {
            _keyRequested = false;
            TrySet(MF.AVEncVideoForceKeyFrame, ComVariant32.UInt(1));
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
            MF.Check(_mft.ProcessInput(0, sample, 0), "编码输入");
        }
        finally
        {
            Marshal.ReleaseComObject(sample);
            Marshal.ReleaseComObject(buffer);
        }

        var bytes = DrainOutput(out var cleanPoint);
        if (bytes is null) return null;

        var isKey = cleanPoint || H264.IsKeyFrame(bytes);
        if (isKey)
        {
            if (H264.ParameterSets(bytes) is { } sets) _parameterSets = sets;
            else if (_parameterSets is { } cached) bytes = [.. cached, .. bytes];
        }
        return new EncodedFrame(bytes, isKey);
    }

    private byte[]? DrainOutput(out bool cleanPoint)
    {
        cleanPoint = false;
        List<byte[]>? chunks = null;
        var providesSamples = (_outputInfo.Flags & (MF.MFT_OUTPUT_STREAM_PROVIDES_SAMPLES | MF.MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES)) != 0;
        while (true)
        {
            IMFSample? mine = null;
            IMFMediaBuffer? mineBuffer = null;
            var data = new MftOutputDataBuffer();
            if (!providesSamples)
            {
                MF.Check(MF.MFCreateMemoryBuffer(Math.Max(_outputInfo.Size, _inputSize), out mineBuffer), "MFCreateMemoryBuffer");
                MF.Check(MF.MFCreateSample(out mine), "MFCreateSample");
                mine.AddBuffer(mineBuffer);
                data.Sample = Marshal.GetComInterfaceForObject<IMFSample, IMFSample>(mine);
            }
            try
            {
                var hr = _mft.ProcessOutput(0, 1, ref data, out _);
                if (data.Events != 0) Marshal.Release(data.Events);
                if (hr == MF.MF_E_TRANSFORM_NEED_MORE_INPUT) break;
                MF.Check(hr, "编码输出");

                var produced = mine ?? (IMFSample)Marshal.GetObjectForIUnknown(data.Sample);
                try
                {
                    if (produced.GetUINT32(MF.SampleExtension_CleanPoint, out var clean) >= 0 && clean != 0) cleanPoint = true;
                    MF.Check(produced.ConvertToContiguousBuffer(out var contiguous), "读取编码输出");
                    try
                    {
                        MF.Check(contiguous.Lock(out var ptr, out _, out var length), "锁定输出缓冲");
                        var chunk = new byte[length];
                        Marshal.Copy(ptr, chunk, 0, length);
                        contiguous.Unlock();
                        (chunks ??= []).Add(chunk);
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(contiguous);
                    }
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
        return chunks switch
        {
            null => null,
            [var only] => only,
            _ => [.. chunks.SelectMany(c => c)],
        };
    }

    private byte[]? ReadSequenceHeader()
    {
        if (_mft.GetOutputCurrentType(0, out var current) < 0) return null;
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

    private bool TrySet(Guid api, ComVariant32 value) => _codec is not null && _codec.SetValue(api, value) >= 0;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _mft.ProcessMessage(MF.MFT_MESSAGE_NOTIFY_END_STREAMING, 0);
        Marshal.ReleaseComObject(_mft);
    }
}

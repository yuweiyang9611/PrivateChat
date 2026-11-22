using System.Diagnostics;
using System.Runtime.InteropServices;
using PrivateChat.Agent.Video;
using PrivateChat.Contracts.Remote;

namespace PrivateChat.Agent.Telemetry;

/// <summary>MFTEnumEx 列出来的一个 H.264 编码器（还没创建）。</summary>
internal sealed class EncoderCandidate(IMFActivate? activate, string name, bool hardware, int vendorId, bool async, bool microsoft) : IDisposable
{
    /// <summary>null：枚举里没有微软的软件编码器，按 CLSID 直接建。</summary>
    public IMFActivate? Activate { get; } = activate;
    public string Name { get; } = name;
    public bool Hardware { get; } = hardware;
    public int VendorId { get; } = vendorId;
    public bool Async { get; } = async;
    /// <summary>微软的软件编码器（远程现在用的那个）。</summary>
    public bool Microsoft { get; } = microsoft;

    public void Dispose()
    {
        if (Activate is not null) Marshal.ReleaseComObject(Activate);
    }
}

/// <summary>
/// 探测这台电脑上的 H.264 编码器（Media Foundation，只在探测子进程里跑）：
/// 1. <see cref="List"/>：MFTEnumEx 列出 NV12 → H.264 的编码器（硬件、同步、异步都要，按系统的优先顺序），微软的软件编码器排第一个。
/// 2. <see cref="Probe"/> 每一个：先建一个实例问能力——是不是异步、ICodecAPI 接受哪些码率控制方式、能不能设按质量 / 逐帧 QP / 低延迟、
///    1080p 和 4K 的输出输入类型接不接受——问完就关。然后全用远程时的那个类（软件的 <see cref="H264Encoder"/>、
///    硬件的 <see cref="HardwareH264Encoder"/>，每次新建一个实例）实际编 1920×1080 的测试画面（系统内存里的 NV12）：
///    - 逐帧改质量是不是真的有效（每种设得上的方式各试一次：质量 20 的关键帧、同一画面再编一帧、调到 70 再编同一画面，
///      最后那帧要明显更大，见 <see cref="EncoderChoice.RefineWorks"/>）——结果就是报上去的 QualityMode / PerFrameQp
///      （软件编码器远程时不走逐帧 QP，它的 PerFrameQp 还是"设得上"）；两种都没效果的原因写进 Error。
///    - 30 帧（第 16 帧要关键帧；硬件的按验证有效的那种方式编，和远程时一样），量第一帧多久出来、之后每帧平均 / P90 多久、一共多少字节；
///      要了关键帧却没给的写进 Error（不算试编失败：远程时下一帧会再要）。最多试 10 秒。
///    远程自动用哪个硬件编码器就看这里：试编每帧都出来、没出错，而且按质量或逐帧 QP 验证有效（见 HardwareTelemetry）。
/// </summary>
internal static class EncoderProbe
{
    public const int TestWidth = 1920;
    public const int TestHeight = 1080;
    public const int TestFrames = 30;
    public const int TestFps = 30;
    private const int TestKbps = 8000;
    private const int TestQuality = 30;
    /// <summary>试编时在这一帧（从 0 数）要关键帧。</summary>
    private const int KeyFrameAt = 15;
    private const int RefineLow = 20;
    private const int RefineHigh = 70;
    private const int MaxEncoders = 8;
    private static readonly TimeSpan TestLimit = TimeSpan.FromSeconds(10);

    public static List<EncoderCandidate> List()
    {
        var flags = Mft.EnumHardware | Mft.EnumSyncMft | Mft.EnumAsyncMft | Mft.EnumSortAndFilter;
        var list = new List<EncoderCandidate>();
        foreach (var activate in Mft.H264Encoders(flags, MaxEncoders))
        {
            var transformFlags = activate.GetUINT32(Mft.TransformFlags, out var f) >= 0 ? f : 0;
            var microsoft = activate.GetGUID(Mft.TransformClsid, out var clsid) >= 0 && clsid == MF.CLSID_MSH264EncoderMFT;
            var vendor = Mft.VendorId(activate);
            var hardware = (transformFlags & Mft.EnumHardware) != 0 || vendor != 0;
            var name = microsoft ? H264Encoder.SoftwareName : Mft.Name(activate) ?? "（没有名字的编码器）";
            // 硬件 MFT 规定必须是异步的（建不出来的那种也这么报；建出来了再按它自己的 MF_TRANSFORM_ASYNC 改）
            list.Add(new EncoderCandidate(activate, name, hardware, vendor, hardware || (transformFlags & Mft.EnumAsyncMft) != 0, microsoft));
        }
        // 远程默认用的就是它：放第一个，后面的硬件编码器卡住、探测超时也有它的结果
        if (list.FirstOrDefault(c => c.Microsoft) is { } ms)
        {
            list.Remove(ms);
            list.Insert(0, ms);
        }
        else
        {
            list.Insert(0, new EncoderCandidate(null, H264Encoder.SoftwareName, false, 0, false, true));
        }
        return list;
    }

    /// <param name="test">false：时间不够了，只问能力不试编。</param>
    public static TelemetryEncoder Probe(EncoderCandidate candidate, bool test)
    {
        var result = new Inspection { Async = candidate.Async };
        string? error = null;
        try
        {
            Inspect(candidate, result);
        }
        catch (Exception ex)
        {
            error = HResults.Clip(HResults.Explain(ex));
        }

        TelemetryEncodeTest? encodeTest = null;
        bool quality = false, qp = false;
        var notes = new List<string>();
        var refineNotes = new List<string>();
        if (error is null && result.Activated)
        {
            if (!result.Accepts1080p) error = HResults.Clip($"不接受 {TestWidth}×{TestHeight} 的 NV12 → H.264：{result.TypeError}");
            else if (!test) error = "探测时间不够了，没有试编";
            else if (candidate.Microsoft)
            {
                quality = result.QualityMode && RefineWorks(() => new H264Encoder(TestWidth, TestHeight, TestFps, TestKbps, quality: RefineLow), "按质量", refineNotes);
                qp = result.PerFrameQp;
                encodeTest = TestEncode(() => new H264Encoder(TestWidth, TestHeight, TestFps, TestKbps, quality: TestQuality), notes);
            }
            else if (result.Async && candidate.Activate is { } activate)
            {
                HardwareH264Encoder Hardware(IReadOnlyList<HardwareRateControl> modes, int q)
                    => new(activate, candidate.Name, candidate.VendorId, TestWidth, TestHeight, TestFps, TestKbps, modes, q, ownsActivate: false);
                quality = result.QualityMode && RefineWorks(() => Hardware([HardwareRateControl.Quality], RefineLow), "按质量", refineNotes);
                qp = result.PerFrameQp && RefineWorks(() => Hardware([HardwareRateControl.Qp], RefineLow), "逐帧 QP", refineNotes);
                // 和远程时一样：验证有效的那种方式（两种都有效先按质量）；都不行就照没验证过时的顺序（这个编码器远程时不会自动用）
                IReadOnlyList<HardwareRateControl> sessionModes = quality ? [HardwareRateControl.Quality] : qp ? [HardwareRateControl.Qp] : EncoderChoice.UnverifiedModes;
                encodeTest = TestEncode(() => Hardware(sessionModes, TestQuality), notes);
            }
            else error = "同步的第三方编码器，没有试编";
        }
        if (error is null && !quality && !qp && refineNotes.Count > 0) notes.AddRange(refineNotes);
        if (error is null && notes.Count > 0) error = HResults.Clip(string.Join("；", notes));
        return new TelemetryEncoder(candidate.Name, candidate.Hardware, candidate.VendorId, result.Activated, result.Async,
            result.RateControlModes, quality, qp, result.LowLatency, result.Accepts1080p, result.Accepts4k,
            encodeTest, error);
    }

    private sealed class Inspection
    {
        public bool Activated;
        public bool Async;
        public List<string> RateControlModes = [];
        public bool QualityMode;
        public bool PerFrameQp;
        public bool LowLatency;
        public bool Accepts1080p;
        public bool Accepts4k;
        public string? TypeError;
    }

    /// <summary>建一个实例问能力，问完就关（试编另建一个：这个实例的码率控制被问来问去已经乱了）。</summary>
    private static void Inspect(EncoderCandidate candidate, Inspection result)
    {
        IMFTransformFull mft;
        if (candidate.Activate is { } activate)
        {
            MF.Check(activate.ActivateObject(typeof(IMFTransformFull).GUID, out var unknown), "创建编码器（ActivateObject）");
            try
            {
                mft = (IMFTransformFull)Marshal.GetObjectForIUnknown(unknown);
            }
            catch
            {
                activate.ShutdownObject();
                throw;
            }
            finally
            {
                Marshal.Release(unknown);
            }
        }
        else
        {
            var type = Type.GetTypeFromCLSID(MF.CLSID_MSH264EncoderMFT, throwOnError: true)!;
            mft = (IMFTransformFull)Activator.CreateInstance(type)!;
        }
        result.Activated = true;
        try
        {
            if (mft.GetAttributes(out var attributes) >= 0)
            {
                try
                {
                    if (attributes.GetUINT32(Mft.TransformAsync, out var async) >= 0) result.Async = async != 0;
                    if (result.Async) MF.Check(attributes.SetUINT32(Mft.TransformAsyncUnlock, 1), "解锁异步编码器");
                }
                finally
                {
                    Mft.ReleaseAttributes(attributes, mft);
                }
            }

            if (mft is ICodecApiFull codec)
            {
                result.RateControlModes = RateControlModes(codec, out var listed);
                result.LowLatency = codec.IsSupported(MF.LowLatency) == 0 && codec.SetValue(MF.LowLatency, Variant64.Bool(true)) >= 0;
                result.QualityMode = codec.IsSupported(MF.AVEncCommonQuality) == 0
                    && (!listed || result.RateControlModes.Contains(Mft.RateControlName(3)))
                    && codec.SetValue(MF.AVEncCommonRateControlMode, Variant64.UInt(3)) >= 0
                    && codec.SetValue(MF.AVEncCommonQuality, Variant64.UInt(50)) >= 0;
                // 逐帧 QP 设不设得上（设法和 HardwareH264Encoder 一样：四个 16 位都填同一个 QP）；真的有没有效果后面试编时再看
                result.PerFrameQp = codec.IsSupported(Mft.AVEncVideoEncodeQP) == 0
                    && codec.SetValue(Mft.AVEncVideoEncodeQP, Variant64.UInt64(EncoderChoice.PackQp(26))) >= 0;
            }

            var input = new int[1];
            var output = new int[1];
            var (inputId, outputId) = mft.GetStreamIDs(1, input, 1, output) >= 0 ? (input[0], output[0]) : (0, 0);
            result.TypeError = TrySetTypes(mft, inputId, outputId, TestWidth, TestHeight);
            result.Accepts1080p = result.TypeError is null;
            mft.SetInputType(inputId, null, 0);
            mft.SetOutputType(outputId, null, 0);
            result.Accepts4k = TrySetTypes(mft, inputId, outputId, 3840, 2160) is null;
        }
        finally
        {
            if (candidate.Activate is { } a) a.ShutdownObject();
            Marshal.ReleaseComObject(mft);
        }
    }

    /// <summary>码率控制方式：编码器肯列（GetParameterValues）就照它列的；不肯列的挨个试设（设得上不一定真支持，但起码不拒绝）。</summary>
    private static List<string> RateControlModes(ICodecApiFull codec, out bool listed)
    {
        listed = false;
        var modes = new List<string>();
        if (codec.IsSupported(MF.AVEncCommonRateControlMode) != 0) return modes;
        if (codec.GetParameterValues(MF.AVEncCommonRateControlMode, out var values, out var count) >= 0 && values != 0)
        {
            try
            {
                for (var i = 0; i < count; i++)
                {
                    var v = values + i * 24;
                    if (Marshal.ReadInt16(v) is (short)Variant64.VT_UI4 or (short)Variant64.VT_I4 or (short)Variant64.VT_UINT or (short)Variant64.VT_INT)
                        modes.Add(Mft.RateControlName(Marshal.ReadInt32(v, 8)));
                    Mft.VariantClear(v);
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(values);
            }
            if (modes.Count > 0)
            {
                listed = true;
                return modes;
            }
        }
        for (var mode = 0; mode <= 6; mode++)
            if (codec.SetValue(MF.AVEncCommonRateControlMode, Variant64.UInt(mode)) >= 0) modes.Add(Mft.RateControlName(mode));
        return modes;
    }

    /// <summary>设 H.264 输出类型、再设 NV12 输入类型；都接受返回 null，否则返回哪一步、什么错。</summary>
    private static string? TrySetTypes(IMFTransformFull mft, int inputId, int outputId, int width, int height)
    {
        var output = Mft.H264Type(width, height, TestFps, width > 1920 ? 20000 : TestKbps);
        try
        {
            var hr = mft.SetOutputType(outputId, output, 0);
            if (hr < 0) return $"输出类型 {HResults.Describe(hr)}";
        }
        finally
        {
            Marshal.ReleaseComObject(output);
        }
        var input = Mft.Nv12Type(width, height, TestFps);
        try
        {
            var hr = mft.SetInputType(inputId, input, 0);
            return hr < 0 ? $"输入类型 {HResults.Describe(hr)}" : null;
        }
        finally
        {
            Marshal.ReleaseComObject(input);
        }
    }


    /// <summary>
    /// 逐帧改质量真的有效吗（create 建的编码器第一帧质量是 <see cref="RefineLow"/>）：质量 20 的关键帧、同一画面再编一帧、调到 70 再编同一画面。
    /// 没效果、建不起来的原因记进 notes。
    /// </summary>
    private static bool RefineWorks(Func<IVideoEncoder> create, string label, List<string> notes)
    {
        try
        {
            using var encoder = create();
            if (!encoder.QualityMode)
            {
                notes.Add($"{label}：编码器不接受");
                return false;
            }
            var pattern = new TestPattern(TestWidth, TestHeight);
            pattern.Render(0);
            encoder.SetQuality(RefineLow);
            encoder.RequestKeyFrame();
            var key = encoder.Encode(pattern.Data, Timestamp(0))?.Data.Length;
            var same = encoder.Encode(pattern.Data, Timestamp(1))?.Data.Length;
            encoder.SetQuality(RefineHigh);
            var refined = encoder.Encode(pattern.Data, Timestamp(2))?.Data.Length;
            if (EncoderChoice.RefineWorks(same, refined)) return true;
            static string Size(int? bytes) => bytes is { } b ? $"{b} 字节" : "没有输出";
            notes.Add($"{label}逐帧改质量没有效果（质量 {RefineLow} 的关键帧 {Size(key)}，同一画面再编一帧 {Size(same)}，调到 {RefineHigh} 后 {Size(refined)}）");
            return false;
        }
        catch (Exception ex)
        {
            notes.Add($"{label}试不了：{HResults.Explain(ex)}");
            return false;
        }
    }

    /// <summary>
    /// 用远程时的那个类编 30 帧测试画面（一帧一帧来：Encode 返回的就是这一帧的输出），第 <see cref="KeyFrameAt"/> 帧要关键帧。
    /// 第一帧（新建的编码器总从关键帧开始）、要了关键帧的那帧出来的不是关键帧，记进 notes。
    /// </summary>
    private static TelemetryEncodeTest TestEncode(Func<IVideoEncoder> create, List<string> notes)
    {
        var latencies = new List<double>();
        long bytes = 0;
        var frames = 0;
        string? error = null;
        var notKey = new List<int>();
        try
        {
            using var encoder = create();
            var pattern = new TestPattern(TestWidth, TestHeight);
            var clock = Stopwatch.StartNew(); // 只算编码，不算建编码器
            for (var i = 0; i < TestFrames; i++)
            {
                if (clock.Elapsed > TestLimit)
                {
                    error = $"{TestLimit.TotalSeconds:F0} 秒内没编完";
                    break;
                }
                pattern.Render(i);
                if (i == KeyFrameAt) encoder.RequestKeyFrame();
                var start = Stopwatch.GetTimestamp();
                var encoded = encoder.Encode(pattern.Data, Timestamp(i));
                var ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                frames++;
                if (encoded is not { } e) continue;
                latencies.Add(ms);
                bytes += e.Data.Length;
                if ((i == 0 || i == KeyFrameAt) && !e.IsKey) notKey.Add(i + 1);
            }
        }
        catch (Exception ex)
        {
            error = HResults.Explain(ex);
        }
        if (notKey.Count > 0) notes.Add($"要了关键帧的第 {string.Join("、", notKey)} 帧出来的不是关键帧");
        return Summarize(frames, latencies, bytes, error);
    }

    private static TimeSpan Timestamp(int frame) => TimeSpan.FromTicks(frame * TimeSpan.TicksPerSecond / TestFps);

    /// <summary>FirstFrameMs：第一帧（关键帧、编码器刚起步）；AvgMs / P90Ms：之后的帧（只有一帧时就是它）。</summary>
    private static TelemetryEncodeTest Summarize(int frames, List<double> latencies, long bytes, string? error)
    {
        if (latencies.Count == 0)
            return new TelemetryEncodeTest(TestWidth, TestHeight, frames, 0, 0, 0, 0, (int)Math.Min(bytes, int.MaxValue),
                HResults.Clip(error ?? "一帧都没编出来"));
        var rest = latencies.Count > 1 ? latencies.Skip(1).Order().ToList() : latencies;
        var p90 = rest[Math.Max(0, (int)Math.Ceiling(rest.Count * 0.9) - 1)];
        return new TelemetryEncodeTest(TestWidth, TestHeight, frames, latencies.Count, Math.Round(latencies[0], 2), Math.Round(rest.Average(), 2),
            Math.Round(p90, 2), (int)Math.Min(bytes, int.MaxValue), error is null ? null : HResults.Clip(error));
    }
}

/// <summary>
/// 试编用的测试画面（NV12，BT.709 16..235）：浅色底、一大块像文字的细碎图案、一条彩色标题栏，
/// 再加一块每帧挪位置、换颜色的方块（后面的帧才有东西可编）。只在试编前算一次底图，每帧拷一遍再画方块，不算进编码时间。
/// </summary>
internal sealed class TestPattern
{
    private readonly int _width;
    private readonly int _height;
    private readonly byte[] _base;

    public byte[] Data { get; }

    public TestPattern(int width, int height)
    {
        _width = width;
        _height = height;
        _base = new byte[width * height * 3 / 2];
        Data = new byte[_base.Length];
        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            var textLine = y >= height / 10 && y < height * 8 / 10 && y % 24 < 12;
            for (var x = 0; x < width; x++)
            {
                byte luma = (byte)(200 + ((x / 8 + y / 8) & 15));
                if (textLine && x >= width / 16 && x < width * 3 / 4)
                {
                    var hash = (uint)(x * 2654435761u ^ y * 40503u);
                    if (((hash >> 13) & 3) == 0) luma = 40;
                }
                _base[row + x] = luma;
            }
        }
        var chroma = width * height;
        for (var y = 0; y < height / 2; y++)
            for (var x = 0; x < width / 2; x++)
            {
                var titleBar = y < height / 40;
                _base[chroma + y * width + x * 2] = (byte)(titleBar ? 160 : 128);
                _base[chroma + y * width + x * 2 + 1] = (byte)(titleBar ? 100 : 128);
            }
    }

    public void Render(int frame)
    {
        Buffer.BlockCopy(_base, 0, Data, 0, _base.Length);
        const int size = 256;
        var left = (frame * 48) % (_width - size) & ~1;
        var top = (_height - size - 40) & ~1;
        var luma = (byte)(16 + frame * 37 % 200);
        for (var y = top; y < top + size; y++) Data.AsSpan(y * _width + left, size).Fill(luma);
        var chroma = _width * _height;
        for (var y = top / 2; y < (top + size) / 2; y++)
            for (var x = left / 2; x < (left + size) / 2; x++)
            {
                Data[chroma + y * _width + x * 2] = (byte)(64 + frame * 13 % 128);
                Data[chroma + y * _width + x * 2 + 1] = (byte)(192 - frame * 7 % 128);
            }
    }
}

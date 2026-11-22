using System.Runtime.InteropServices;
using PrivateChat.Agent.Video;

namespace PrivateChat.Agent.Telemetry;

// Video/MediaFoundation.cs 之外、列编码器和驱动异步（硬件）编码器要用的 Media Foundation 互操作，写法和那边一样。
// 那边的 IMFTransform、ICodecAPI 只声明了软件编码器用到的方法，这里各有一份声明全了的（同一个接口 GUID，名字后面加 Full）；
// 媒体类型、样本、缓冲还用那边的。

[StructLayout(LayoutKind.Sequential)]
internal struct MftRegisterTypeInfo(Guid majorType, Guid subtype)
{
    public Guid MajorType = majorType;
    public Guid Subtype = subtype;
}

/// <summary>MFTEnumEx 列出来的一个编码器：没创建之前就能读它的名字、厂商、标志（IMFAttributes 的 30 个方法 + 3 个）。</summary>
[ComImport, Guid("7FEE9E9A-4A89-47a6-899C-B6A53A70FB67"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFActivate
{
    void GetItem(); void GetItemType(); void CompareItem(); void Compare();
    [PreserveSig] int GetUINT32(in Guid key, out int value);
    void GetUINT64(); void GetDouble();
    [PreserveSig] int GetGUID(in Guid key, out Guid value);
    void GetStringLength(); void GetString();
    /// <param name="value">用 CoTaskMemFree（Marshal.FreeCoTaskMem）释放。</param>
    [PreserveSig] int GetAllocatedString(in Guid key, out IntPtr value, out int length);
    void GetBlobSize(); void GetBlob(); void GetAllocatedBlob(); void GetUnknown(); void SetItem(); void DeleteItem(); void DeleteAllItems();
    void SetUINT32(); void SetUINT64(); void SetDouble(); void SetGUID(); void SetString(); void SetBlob(); void SetUnknown();
    void LockStore(); void UnlockStore(); void GetCount(); void GetItemByIndex(); void CopyAllItems();
    // IMFActivate
    [PreserveSig] int ActivateObject(in Guid iid, out IntPtr obj);
    /// <summary>关掉 ActivateObject 建出来的那个对象（硬件编码器要这样关，否则显卡上的编码会话不释放）；之后再 ActivateObject 会新建一个。</summary>
    [PreserveSig] int ShutdownObject();
    [PreserveSig] int DetachObject();
}

[ComImport, Guid("DF598932-F10C-4E39-BBA2-C308F101DAA3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaEvent
{
    void GetItem(); void GetItemType(); void CompareItem(); void Compare(); void GetUINT32(); void GetUINT64(); void GetDouble(); void GetGUID();
    void GetStringLength(); void GetString(); void GetAllocatedString(); void GetBlobSize(); void GetBlob(); void GetAllocatedBlob(); void GetUnknown();
    void SetItem(); void DeleteItem(); void DeleteAllItems(); void SetUINT32(); void SetUINT64(); void SetDouble(); void SetGUID(); void SetString();
    void SetBlob(); void SetUnknown(); void LockStore(); void UnlockStore(); void GetCount(); void GetItemByIndex(); void CopyAllItems();
    // IMFMediaEvent（第一个方法在 C 里叫 GetType）
    [PreserveSig] int GetEventType(out int type);
    void GetExtendedType();
    [PreserveSig] int GetStatus(out int status);
    void GetValue();
}

[ComImport, Guid("a27003cf-2354-4f2a-8d6a-ab7cff15437e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFAsyncCallback
{
    [PreserveSig] int GetParameters(out int flags, out int queue);
    /// <param name="result">IMFAsyncResult*（不归我们管，不用 Release）。</param>
    [PreserveSig] int Invoke(IntPtr result);
}

[ComImport, Guid("2CD0BD52-BCD5-4B89-B62C-EADC0C031E7D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaEventGenerator
{
    [PreserveSig] int GetEvent(int flags, out IMFMediaEvent ev);
    [PreserveSig] int BeginGetEvent(IMFAsyncCallback callback, [MarshalAs(UnmanagedType.IUnknown)] object? state);
    [PreserveSig] int EndGetEvent(IntPtr result, out IMFMediaEvent ev);
    void QueueEvent();
}

[ComImport, Guid("bf94c121-5b05-4e6f-8000-ba598961414d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFTransformFull
{
    void GetStreamLimits(); void GetStreamCount();
    [PreserveSig] int GetStreamIDs(int inputSize, [Out] int[] inputIds, int outputSize, [Out] int[] outputIds);
    void GetInputStreamInfo();
    [PreserveSig] int GetOutputStreamInfo(int streamId, out MftOutputStreamInfo info);
    [PreserveSig] int GetAttributes(out IMFAttributes attributes);
    void GetInputStreamAttributes(); void GetOutputStreamAttributes(); void DeleteInputStream(); void AddInputStreams();
    [PreserveSig] int GetInputAvailableType(int streamId, int index, out IMFMediaType type);
    [PreserveSig] int GetOutputAvailableType(int streamId, int index, out IMFMediaType type);
    [PreserveSig] int SetInputType(int streamId, IMFMediaType? type, int flags);
    [PreserveSig] int SetOutputType(int streamId, IMFMediaType? type, int flags);
    void GetInputCurrentType();
    [PreserveSig] int GetOutputCurrentType(int streamId, out IMFMediaType type);
    void GetInputStatus(); void GetOutputStatus(); void SetOutputBounds(); void ProcessEvent();
    [PreserveSig] int ProcessMessage(int message, IntPtr param);
    [PreserveSig] int ProcessInput(int streamId, IMFSample sample, int flags);
    [PreserveSig] int ProcessOutput(int flags, int count, ref MftOutputDataBuffer buffer, out int status);
}

/// <summary>VARIANT（x64 上 24 字节）：VT_UI4 / VT_BOOL 用低 4 字节，VT_UI8（逐帧量化参数）用整 8 字节。</summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct Variant64
{
    public const ushort VT_I4 = 3, VT_BOOL = 11, VT_UI4 = 19, VT_UI8 = 21, VT_INT = 22, VT_UINT = 23;

    [FieldOffset(0)] public ushort Vt;
    [FieldOffset(8)] public long Value;

    public static Variant64 UInt(int v) => new() { Vt = VT_UI4, Value = (uint)v };
    public static Variant64 UInt64(long v) => new() { Vt = VT_UI8, Value = v };
    public static Variant64 Bool(bool v) => new() { Vt = VT_BOOL, Value = v ? -1 : 0 };
}

[ComImport, Guid("901db4c7-31ce-41a2-85dc-8fa0bf41b8da"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICodecApiFull
{
    /// <returns>S_OK（0）支持；S_FALSE（1）不支持。</returns>
    [PreserveSig] int IsSupported(in Guid api);
    [PreserveSig] int IsModifiable(in Guid api);
    void GetParameterRange();
    /// <param name="values">VARIANT 数组：每个 VariantClear，整个 CoTaskMemFree。</param>
    [PreserveSig] int GetParameterValues(in Guid api, out IntPtr values, out int count);
    void GetDefaultValue();
    [PreserveSig] int GetValue(in Guid api, out Variant64 value);
    [PreserveSig] int SetValue(in Guid api, in Variant64 value);
}

internal static class Mft
{
    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFTEnumEx(Guid category, int flags, in MftRegisterTypeInfo input, in MftRegisterTypeInfo output, out IntPtr activates, out int count);

    [DllImport("oleaut32.dll", ExactSpelling = true)] public static extern int VariantClear(IntPtr variant);

    public static readonly Guid CategoryVideoEncoder = new("f79eac7d-e545-4387-bdee-d647d7bde42a");
    public const int EnumSyncMft = 0x1;
    public const int EnumAsyncMft = 0x2;
    public const int EnumHardware = 0x4;
    public const int EnumSortAndFilter = 0x40;

    // IMFActivate / MFT 的属性
    public static readonly Guid FriendlyName = new("314ffbae-5b41-4c95-9c19-4e7d586face3");          // MFT_FRIENDLY_NAME_Attribute
    public static readonly Guid HardwareVendorId = new("3aecb0cc-035b-4bcc-8185-2b8d551ef3af");      // MFT_ENUM_HARDWARE_VENDOR_ID_Attribute："VEN_10DE"
    public static readonly Guid TransformFlags = new("9359bb7e-6275-46c4-a025-1c01e45f1a86");        // MF_TRANSFORM_FLAGS_Attribute
    public static readonly Guid TransformClsid = new("6821c42b-65a4-4e82-99bc-9a88205ecd0c");        // MFT_TRANSFORM_CLSID_Attribute
    public static readonly Guid TransformAsync = new("f81a699a-649a-497d-8c73-29f8fed6ad7a");        // MF_TRANSFORM_ASYNC
    public static readonly Guid TransformAsyncUnlock = new("e5666d6b-3422-4eb6-a421-da7db1f8e207");  // MF_TRANSFORM_ASYNC_UNLOCK

    // ICodecAPI（其余的在 Video/MediaFoundation.cs 的 MF 里）
    public static readonly Guid AVEncVideoEncodeQP = new("2cb5696b-23fb-4ce1-a0f9-ef5b90fd55ca");

    // 事件
    public const int MEError = 1;
    public const int METransformNeedInput = 601;
    public const int METransformHaveOutput = 602;
    public const int METransformDrainComplete = 603;

    public const int MFT_MESSAGE_COMMAND_DRAIN = 1;
    public const int MFT_MESSAGE_NOTIFY_END_OF_STREAM = 0x10000002;
    public const int E_NOTIMPL = unchecked((int)0x80004001);

    /// <summary>eAVEncCommonRateControlMode 的名字（遥测里报这个）。</summary>
    public static string RateControlName(int mode) => mode switch
    {
        0 => "CBR",
        1 => "PeakConstrainedVBR",
        2 => "UnconstrainedVBR",
        3 => "Quality",
        4 => "LowDelayVBR",
        5 => "GlobalVBR",
        6 => "GlobalLowDelayVBR",
        _ => mode.ToString(),
    };

    /// <summary>读 IMFActivate 上的字符串属性；没有是 null。</summary>
    public static string? String(IMFActivate activate, in Guid key)
    {
        if (activate.GetAllocatedString(key, out var value, out _) < 0 || value == 0) return null;
        try
        {
            return Marshal.PtrToStringUni(value);
        }
        finally
        {
            Marshal.FreeCoTaskMem(value);
        }
    }

    /// <summary>MFT 自己的属性和 MFT 是同一个对象时，ReleaseComObject 会把 MFT 也一起放掉：只在不是同一个时才放。</summary>
    public static void ReleaseAttributes(IMFAttributes attributes, object owner)
    {
        if (!ReferenceEquals(attributes, owner)) Marshal.ReleaseComObject(attributes);
    }

    /// <summary>H.264 输出类型：Main profile（快速线路的 SDP 写死了 Main，见 RealtimePublisher）、逐行、方形像素。</summary>
    public static IMFMediaType H264Type(int width, int height, int fps, int bitrateKbps)
    {
        MF.Check(MF.MFCreateMediaType(out var type), "MFCreateMediaType");
        try
        {
            MF.Check(type.SetGUID(MF.MT_MAJOR_TYPE, MF.MediaType_Video), "输出类型");
            MF.Check(type.SetGUID(MF.MT_SUBTYPE, MF.VideoFormat_H264), "输出类型");
            MF.Check(type.SetUINT32(MF.MT_AVG_BITRATE, bitrateKbps * 1000), "输出类型");
            MF.Check(type.SetUINT64(MF.MT_FRAME_SIZE, MF.Pack(width, height)), "输出类型");
            MF.Check(type.SetUINT64(MF.MT_FRAME_RATE, MF.Pack(fps, 1)), "输出类型");
            MF.Check(type.SetUINT64(MF.MT_PIXEL_ASPECT_RATIO, MF.Pack(1, 1)), "输出类型");
            MF.Check(type.SetUINT32(MF.MT_INTERLACE_MODE, 2 /* 逐行 */), "输出类型");
            MF.Check(type.SetUINT32(MF.MT_MPEG2_PROFILE, 77 /* Main */), "输出类型");
            return type;
        }
        catch
        {
            Marshal.ReleaseComObject(type);
            throw;
        }
    }

    /// <summary>NV12 输入类型：BT.709、16..235（和 Nv12.FromBgra 转出来的一样）。</summary>
    public static IMFMediaType Nv12Type(int width, int height, int fps)
    {
        MF.Check(MF.MFCreateMediaType(out var type), "MFCreateMediaType");
        try
        {
            MF.Check(type.SetGUID(MF.MT_MAJOR_TYPE, MF.MediaType_Video), "输入类型");
            MF.Check(type.SetGUID(MF.MT_SUBTYPE, MF.VideoFormat_NV12), "输入类型");
            MF.Check(type.SetUINT64(MF.MT_FRAME_SIZE, MF.Pack(width, height)), "输入类型");
            MF.Check(type.SetUINT64(MF.MT_FRAME_RATE, MF.Pack(fps, 1)), "输入类型");
            MF.Check(type.SetUINT64(MF.MT_PIXEL_ASPECT_RATIO, MF.Pack(1, 1)), "输入类型");
            MF.Check(type.SetUINT32(MF.MT_INTERLACE_MODE, 2), "输入类型");
            type.SetUINT32(MF.MT_YUV_MATRIX, 1 /* BT.709 */);
            type.SetUINT32(MF.MT_VIDEO_NOMINAL_RANGE, 2 /* 16..235 */);
            return type;
        }
        catch
        {
            Marshal.ReleaseComObject(type);
            throw;
        }
    }
}

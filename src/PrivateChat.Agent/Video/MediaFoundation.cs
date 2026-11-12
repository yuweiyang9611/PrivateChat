using System.Runtime.InteropServices;

namespace PrivateChat.Agent.Video;

// Media Foundation 的最小 COM 互操作：只声明用得到的方法。
// [ComImport] 接口不继承基接口的 vtable，所以 IMFMediaType / IMFSample 要把 IMFAttributes 的 30 个方法按顺序重抄一遍；
// 不调用的方法用无参占位（只占 vtable 槽位，永远不会被调用）。

[ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFAttributes
{
    void GetItem(); void GetItemType(); void CompareItem(); void Compare();
    [PreserveSig] int GetUINT32(in Guid key, out int value);
    [PreserveSig] int GetUINT64(in Guid key, out long value);
    void GetDouble(); void GetGUID(); void GetStringLength(); void GetString(); void GetAllocatedString();
    [PreserveSig] int GetBlobSize(in Guid key, out int size);
    [PreserveSig] int GetBlob(in Guid key, IntPtr buffer, int size, out int written);
    void GetAllocatedBlob(); void GetUnknown(); void SetItem(); void DeleteItem(); void DeleteAllItems();
    [PreserveSig] int SetUINT32(in Guid key, int value);
    [PreserveSig] int SetUINT64(in Guid key, long value);
    void SetDouble();
    [PreserveSig] int SetGUID(in Guid key, in Guid value);
}

[ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaType
{
    void GetItem(); void GetItemType(); void CompareItem(); void Compare();
    [PreserveSig] int GetUINT32(in Guid key, out int value);
    [PreserveSig] int GetUINT64(in Guid key, out long value);
    void GetDouble(); void GetGUID(); void GetStringLength(); void GetString(); void GetAllocatedString();
    [PreserveSig] int GetBlobSize(in Guid key, out int size);
    [PreserveSig] int GetBlob(in Guid key, IntPtr buffer, int size, out int written);
    void GetAllocatedBlob(); void GetUnknown(); void SetItem(); void DeleteItem(); void DeleteAllItems();
    [PreserveSig] int SetUINT32(in Guid key, int value);
    [PreserveSig] int SetUINT64(in Guid key, long value);
    void SetDouble();
    [PreserveSig] int SetGUID(in Guid key, in Guid value);
}

[ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSample
{
    void GetItem(); void GetItemType(); void CompareItem(); void Compare();
    [PreserveSig] int GetUINT32(in Guid key, out int value);
    void GetUINT64(); void GetDouble(); void GetGUID(); void GetStringLength(); void GetString(); void GetAllocatedString();
    void GetBlobSize(); void GetBlob(); void GetAllocatedBlob(); void GetUnknown(); void SetItem(); void DeleteItem(); void DeleteAllItems();
    void SetUINT32(); void SetUINT64(); void SetDouble(); void SetGUID(); void SetString(); void SetBlob(); void SetUnknown();
    void LockStore(); void UnlockStore(); void GetCount(); void GetItemByIndex(); void CopyAllItems();
    // IMFSample
    void GetSampleFlags(); void SetSampleFlags();
    [PreserveSig] int GetSampleTime(out long time);
    [PreserveSig] int SetSampleTime(long time);
    void GetSampleDuration();
    [PreserveSig] int SetSampleDuration(long duration);
    void GetBufferCount(); void GetBufferByIndex();
    [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
    [PreserveSig] int AddBuffer(IMFMediaBuffer buffer);
    void RemoveBufferByIndex();
    [PreserveSig] int RemoveAllBuffers();
}

[ComImport, Guid("045FA593-8799-42b8-BC8D-8968C6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaBuffer
{
    [PreserveSig] int Lock(out IntPtr buffer, out int maxLength, out int currentLength);
    [PreserveSig] int Unlock();
    [PreserveSig] int GetCurrentLength(out int length);
    [PreserveSig] int SetCurrentLength(int length);
    [PreserveSig] int GetMaxLength(out int length);
}

[StructLayout(LayoutKind.Sequential)]
internal struct MftOutputStreamInfo
{
    public int Flags;
    public int Size;
    public int Alignment;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MftOutputDataBuffer
{
    public int StreamId;
    public IntPtr Sample;
    public int Status;
    public IntPtr Events;
}

[ComImport, Guid("bf94c121-5b05-4e6f-8000-ba598961414d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFTransform
{
    void GetStreamLimits(); void GetStreamCount(); void GetStreamIDs(); void GetInputStreamInfo();
    [PreserveSig] int GetOutputStreamInfo(int streamId, out MftOutputStreamInfo info);
    [PreserveSig] int GetAttributes(out IMFAttributes attributes);
    void GetInputStreamAttributes(); void GetOutputStreamAttributes(); void DeleteInputStream(); void AddInputStreams();
    void GetInputAvailableType(); void GetOutputAvailableType();
    [PreserveSig] int SetInputType(int streamId, IMFMediaType? type, int flags);
    [PreserveSig] int SetOutputType(int streamId, IMFMediaType? type, int flags);
    void GetInputCurrentType();
    [PreserveSig] int GetOutputCurrentType(int streamId, out IMFMediaType type);
    void GetInputStatus(); void GetOutputStatus(); void SetOutputBounds(); void ProcessEvent();
    [PreserveSig] int ProcessMessage(int message, IntPtr param);
    [PreserveSig] int ProcessInput(int streamId, IMFSample sample, int flags);
    [PreserveSig] int ProcessOutput(int flags, int count, ref MftOutputDataBuffer buffer, out int status);
}

/// <summary>VARIANT（x64 上 24 字节）：ICodecAPI 只用到 VT_UI4 / VT_BOOL。</summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct ComVariant32
{
    [FieldOffset(0)] public ushort Vt;
    [FieldOffset(8)] public int Value;

    public static ComVariant32 UInt(int v) => new() { Vt = 19 /* VT_UI4 */, Value = v };
    public static ComVariant32 Bool(bool v) => new() { Vt = 11 /* VT_BOOL */, Value = v ? -1 : 0 };
}

[ComImport, Guid("901db4c7-31ce-41a2-85dc-8fa0bf41b8da"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICodecAPI
{
    [PreserveSig] int IsSupported(in Guid api);
    [PreserveSig] int IsModifiable(in Guid api);
    void GetParameterRange(); void GetParameterValues(); void GetDefaultValue(); void GetValue();
    [PreserveSig] int SetValue(in Guid api, in ComVariant32 value);
}

internal static class MF
{
    private const int Version = 0x00020070; // MF_SDK_VERSION << 16 | MF_API_VERSION
    private const int StartupLite = 1;      // 不需要套接字 / 网络源

    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFStartup(int version, int flags);
    [DllImport("mfplat.dll", ExactSpelling = true)] public static extern int MFCreateMediaType(out IMFMediaType type);
    [DllImport("mfplat.dll", ExactSpelling = true)] public static extern int MFCreateSample(out IMFSample sample);
    [DllImport("mfplat.dll", ExactSpelling = true)] public static extern int MFCreateMemoryBuffer(int maxLength, out IMFMediaBuffer buffer);

    private static readonly Lazy<int> Started = new(() => MFStartup(Version, StartupLite));

    /// <summary>进程内只启动一次，不关（进程退出时系统回收）。</summary>
    public static void EnsureStarted() => Check(Started.Value, "MFStartup（Windows N 版需要安装媒体功能包）");

    public static void Check(int hr, string what)
    {
        if (hr < 0) throw new MediaFoundationException(what, hr);
    }

    // 媒体类型属性
    public static readonly Guid MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    public static readonly Guid MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    public static readonly Guid MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    public static readonly Guid MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    public static readonly Guid MT_PIXEL_ASPECT_RATIO = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    public static readonly Guid MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    public static readonly Guid MT_AVG_BITRATE = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    public static readonly Guid MT_MPEG2_PROFILE = new("ad76a80b-2d5c-4e0b-b375-64e520137036");
    public static readonly Guid MT_MPEG_SEQUENCE_HEADER = new("3c036de7-3ad0-4c9e-9216-ee6d6ac21cb3");
    public static readonly Guid MT_YUV_MATRIX = new("3e23d450-2c75-4d25-a00e-b91670d12327");
    public static readonly Guid MT_VIDEO_NOMINAL_RANGE = new("c21b8ee5-b956-4071-8daf-325edf5cab11");
    public static readonly Guid MediaType_Video = new("73646976-0000-0010-8000-00AA00389B71");
    public static readonly Guid VideoFormat_H264 = new("34363248-0000-0010-8000-00AA00389B71");
    public static readonly Guid VideoFormat_NV12 = new("3231564E-0000-0010-8000-00AA00389B71");
    public static readonly Guid SampleExtension_CleanPoint = new("9cdf01d8-a0f0-43ba-b077-eaa06cbd728a");
    public static readonly Guid LowLatency = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");

    // ICodecAPI
    public static readonly Guid AVEncCommonRateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    public static readonly Guid AVEncCommonMeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");
    public static readonly Guid AVEncCommonQualityVsSpeed = new("98332df8-03cd-476b-89fa-3f9e442dec9f");
    public static readonly Guid AVEncMPVGOPSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    public static readonly Guid AVEncMPVDefaultBPictureCount = new("8d390aac-dc5c-4200-b57f-814d04babab2");
    public static readonly Guid AVEncVideoForceKeyFrame = new("398c1b98-8353-475a-9ef2-8f265d260345");

    /// <summary>微软软件 H.264 编码器（mfh264enc.dll，Windows 7 起自带）。</summary>
    public static readonly Guid CLSID_MSH264EncoderMFT = new("6CA50344-051A-4DED-9779-A43305165E35");

    public const int MFT_MESSAGE_COMMAND_FLUSH = 0;
    public const int MFT_MESSAGE_NOTIFY_BEGIN_STREAMING = 0x10000000;
    public const int MFT_MESSAGE_NOTIFY_END_STREAMING = 0x10000001;
    public const int MFT_MESSAGE_NOTIFY_START_OF_STREAM = 0x10000003;
    public const int MFT_OUTPUT_STREAM_PROVIDES_SAMPLES = 0x100;
    public const int MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES = 0x200;
    public const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);
    public const int MF_E_TRANSFORM_STREAM_CHANGE = unchecked((int)0xC00D6D61);

    public static long Pack(int high, int low) => ((long)high << 32) | (uint)low;
}

public sealed class MediaFoundationException(string what, int hr)
    : Exception($"{what} 失败：0x{hr:X8}")
{
    public int HResult2 { get; } = hr;
}

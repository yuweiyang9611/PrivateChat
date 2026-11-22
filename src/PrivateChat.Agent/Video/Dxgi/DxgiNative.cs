using System.Drawing;
using System.Runtime.InteropServices;

namespace PrivateChat.Agent.Video;

// 桌面复制截屏（DxgiScreenSource、VideoConverter）用的 DXGI / D3D11 调用：按 vtable 槽位号直接调函数指针，不用 [ComImport]。
// ID3D11DeviceContext 有 115 个槽位、ID3D11VideoContext 有 65 个，写占位方法太容易数错；这里只列用到的方法，槽位号写成常量。
// 槽位号、参数类型和顺序、结构体布局都对照过 Windows SDK 10.0.26100 的头文件（C 接口那段的 XxxVtbl / typedef struct），
// 又用 MinGW 的头文件编了个小程序按 offsetof / sizeof 再数了一遍，两边一致：
//   dxgi.h      IDXGIFactory1（14 个槽位）、IDXGIAdapter1（11）、IDXGIOutput（19）
//   dxgi1_2.h   IDXGIOutput1（23）、IDXGIOutputDuplication（15）、DXGI_OUTDUPL_*
//   dxgi1_5.h   IDXGIOutput5（27）
//   d3d11.h     ID3D11Device（43）、ID3D11DeviceContext（115）、ID3D11Texture2D（11）、ID3D11VideoDevice（20）、
//               ID3D11VideoProcessorEnumerator（13）、ID3D11VideoContext（65）、D3D11_* 结构体
//   d3d11_1.h   ID3D11VideoContext1（79）
// 前几个槽位都是继承来的：IUnknown 0–2；IDXGIObject 3–6；ID3D11DeviceChild 3–6。
// 指针都是原始 COM 指针（拿到时已经 AddRef 过），用完 Com.Release；BOOL 是 int。只在采集线程上用。

[StructLayout(LayoutKind.Sequential)]
internal struct NativeRect
{
    public int Left, Top, Right, Bottom;

    public NativeRect(int left, int top, int right, int bottom) => (Left, Top, Right, Bottom) = (left, top, right, bottom);

    public readonly Rectangle ToRectangle() => Rectangle.FromLTRB(Left, Top, Right, Bottom);
}

/// <summary>DXGI_ADAPTER_DESC1（312 字节）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeAdapterDesc1
{
    public fixed char Description[128];
    public uint VendorId, DeviceId, SubSysId, Revision;
    public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
    public uint LuidLow;
    public int LuidHigh;
    public uint Flags;
}

/// <summary>DXGI_OUTPUT_DESC（96 字节）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeOutputDesc
{
    public fixed char DeviceName[32];
    public NativeRect DesktopCoordinates;
    public int AttachedToDesktop;
    public int Rotation;
    public IntPtr Monitor;
}

/// <summary>DXGI_OUTDUPL_DESC（36 字节）：DXGI_MODE_DESC（宽、高、刷新率分子 / 分母、格式、扫描顺序、缩放）+ 旋转 + 画面是否在内存里。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct OutduplDesc
{
    public uint Width, Height, RefreshNumerator, RefreshDenominator;
    public int Format, ScanlineOrdering, Scaling;
    public int Rotation;
    public int DesktopImageInSystemMemory;
}

/// <summary>DXGI_OUTDUPL_FRAME_INFO（48 字节；PointerPosition 是 POINT + BOOL，展开写）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct OutduplFrameInfo
{
    public long LastPresentTime;
    public long LastMouseUpdateTime;
    public uint AccumulatedFrames;
    public int RectsCoalesced;
    public int ProtectedContentMaskedOut;
    public int PointerX, PointerY, PointerVisible;
    public uint TotalMetadataBufferSize;
    public uint PointerShapeBufferSize;
}

/// <summary>DXGI_OUTDUPL_MOVE_RECT（24 字节）：源区域左上角 + 目标区域（大小一样）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct OutduplMoveRect
{
    public int SourceX, SourceY;
    public NativeRect Destination;
}

/// <summary>DXGI_OUTDUPL_POINTER_SHAPE_INFO（24 字节）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct OutduplPointerShapeInfo
{
    public uint Type, Width, Height, Pitch;
    public int HotX, HotY;
}

/// <summary>D3D11_TEXTURE2D_DESC（44 字节；SampleDesc 是 Count + Quality，展开写）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Texture2DDesc
{
    public uint Width, Height, MipLevels, ArraySize;
    public int Format;
    public uint SampleCount, SampleQuality;
    public int Usage;
    public uint BindFlags, CpuAccessFlags, MiscFlags;
}

/// <summary>D3D11_MAPPED_SUBRESOURCE（16 字节）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MappedSubresource
{
    public IntPtr Data;
    public uint RowPitch, DepthPitch;
}

/// <summary>D3D11_VIDEO_PROCESSOR_CONTENT_DESC（40 字节；两个 DXGI_RATIONAL 展开写）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct VideoProcessorContentDesc
{
    public int InputFrameFormat;
    public uint InputRateNumerator, InputRateDenominator;
    public uint InputWidth, InputHeight;
    public uint OutputRateNumerator, OutputRateDenominator;
    public uint OutputWidth, OutputHeight;
    public int Usage;
}

/// <summary>D3D11_VIDEO_PROCESSOR_CAPS（36 字节）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct VideoProcessorCaps
{
    public uint DeviceCaps, FeatureCaps, FilterCaps, InputFormatCaps, AutoStreamCaps, StereoCaps, RateConversionCapsCount, MaxInputStreams, MaxStreamStates;
}

/// <summary>D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC（16 字节）：FourCC、维度（1 = 2D 纹理）、D3D11_TEX2D_VPIV（MipSlice、ArraySlice）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct VideoProcessorInputViewDesc
{
    public uint FourCC;
    public int ViewDimension;
    public uint MipSlice, ArraySlice;
}

/// <summary>D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC（16 字节）：维度（1 = 2D 纹理），后面是 12 字节的联合（2D 时只用 MipSlice）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct VideoProcessorOutputViewDesc
{
    public int ViewDimension;
    public uint MipSlice, FirstArraySlice, ArraySize;
}

/// <summary>D3D11_VIDEO_PROCESSOR_STREAM（x64 上 72 字节：5 个 UINT / BOOL，补齐 4 字节，6 个指针）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct VideoProcessorStream
{
    public int Enable;
    public uint OutputIndex, InputFrameOrField, PastFrames, FutureFrames;
    public IntPtr PastSurfaces, InputSurface, FutureSurfaces, PastSurfacesRight, InputSurfaceRight, FutureSurfacesRight;
}

internal static unsafe class Com
{
    public static void** Vtbl(IntPtr p) => *(void***)p;

    public static void Release(ref IntPtr p)
    {
        var q = p;
        p = 0;
        if (q != 0) Marshal.Release(q);
    }

    /// <summary>QueryInterface：成功返回 AddRef 过的指针，不支持返回 0。</summary>
    public static IntPtr Query(IntPtr p, Guid iid) => Marshal.QueryInterface(p, in iid, out var result) >= 0 ? result : 0;

    /// <summary>定长 WCHAR 数组（以 0 结尾）读成字符串。</summary>
    public static string Text(char* chars, int max)
    {
        var span = new ReadOnlySpan<char>(chars, max);
        var end = span.IndexOf('\0');
        return new string(end >= 0 ? span[..end] : span);
    }
}

internal static unsafe class DxgiNative
{
    public static readonly Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    public static readonly Guid IID_IDXGIOutput1 = new("00cddea8-939b-4b83-a340-a685226666cc");
    public static readonly Guid IID_IDXGIOutput5 = new("80A07424-AB52-42EB-833C-0C42FD282D98");
    public static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    public static readonly Guid IID_ID3D11VideoDevice = new("10EC4D5B-975A-4689-B9E4-D0AAC30FE333");
    public static readonly Guid IID_ID3D11VideoContext = new("61F21C45-3C0E-4a74-9CEA-67100D9AD5E4");
    public static readonly Guid IID_ID3D11VideoContext1 = new("A7F026DA-A5F8-4487-A564-15E34357651E");

    // HRESULT（winerror.h）
    public const int E_ACCESSDENIED = unchecked((int)0x80070005);
    public const int E_INVALIDARG = unchecked((int)0x80070057);
    public const int E_NOINTERFACE = unchecked((int)0x80004002);
    public const int DXGI_ERROR_INVALID_CALL = unchecked((int)0x887A0001);
    public const int DXGI_ERROR_NOT_FOUND = unchecked((int)0x887A0002);
    public const int DXGI_ERROR_MORE_DATA = unchecked((int)0x887A0003);
    public const int DXGI_ERROR_UNSUPPORTED = unchecked((int)0x887A0004);
    public const int DXGI_ERROR_DEVICE_REMOVED = unchecked((int)0x887A0005);
    public const int DXGI_ERROR_DEVICE_HUNG = unchecked((int)0x887A0006);
    public const int DXGI_ERROR_DEVICE_RESET = unchecked((int)0x887A0007);
    public const int DXGI_ERROR_DRIVER_INTERNAL_ERROR = unchecked((int)0x887A0020);
    public const int DXGI_ERROR_NOT_CURRENTLY_AVAILABLE = unchecked((int)0x887A0022);
    public const int DXGI_ERROR_MODE_CHANGE_IN_PROGRESS = unchecked((int)0x887A0025);
    public const int DXGI_ERROR_ACCESS_LOST = unchecked((int)0x887A0026);
    public const int DXGI_ERROR_WAIT_TIMEOUT = unchecked((int)0x887A0027);
    public const int DXGI_ERROR_SESSION_DISCONNECTED = unchecked((int)0x887A0028);
    public const int DXGI_ERROR_ACCESS_DENIED = unchecked((int)0x887A002B);

    // DXGI_FORMAT（dxgiformat.h）
    public const int FormatB8G8R8A8 = 87;
    public const int FormatB8G8R8A8Srgb = 91;
    public const int FormatNv12 = 103;

    // DXGI_MODE_ROTATION（dxgitype.h）：0 没指定、1 不转、2 = 90°、3 = 180°、4 = 270°
    public const int RotationIdentity = 1;

    [DllImport("dxgi.dll", ExactSpelling = true)] private static extern int CreateDXGIFactory1(in Guid riid, out IntPtr factory);

    public static int CreateFactory(out IntPtr factory) => CreateDXGIFactory1(IID_IDXGIFactory1, out factory);

    /// <summary>IDXGIFactory1::EnumAdapters1（槽位 12）。</summary>
    public static int EnumAdapters1(IntPtr factory, int index, out IntPtr adapter)
    {
        IntPtr result = 0;
        var hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Com.Vtbl(factory)[12])(factory, (uint)index, &result);
        adapter = result;
        return hr;
    }

    /// <summary>IDXGIAdapter1::EnumOutputs（槽位 7）。</summary>
    public static int EnumOutputs(IntPtr adapter, int index, out IntPtr output)
    {
        IntPtr result = 0;
        var hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Com.Vtbl(adapter)[7])(adapter, (uint)index, &result);
        output = result;
        return hr;
    }

    /// <summary>IDXGIAdapter1::GetDesc1（槽位 10）。</summary>
    public static int GetDesc1(IntPtr adapter, out NativeAdapterDesc1 desc)
    {
        NativeAdapterDesc1 result;
        var hr = ((delegate* unmanaged[Stdcall]<IntPtr, NativeAdapterDesc1*, int>)Com.Vtbl(adapter)[10])(adapter, &result);
        desc = result;
        return hr;
    }

    /// <summary>IDXGIOutput::GetDesc（槽位 7）。</summary>
    public static int GetOutputDesc(IntPtr output, out NativeOutputDesc desc)
    {
        NativeOutputDesc result;
        var hr = ((delegate* unmanaged[Stdcall]<IntPtr, NativeOutputDesc*, int>)Com.Vtbl(output)[7])(output, &result);
        desc = result;
        return hr;
    }

    /// <summary>IDXGIOutput1::DuplicateOutput（槽位 22）。device：建在这块显示器所在显卡上的 ID3D11Device。</summary>
    public static int DuplicateOutput(IntPtr output1, IntPtr device, out IntPtr duplication)
    {
        IntPtr result = 0;
        var hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)Com.Vtbl(output1)[22])(output1, device, &result);
        duplication = result;
        return hr;
    }

    /// <summary>IDXGIOutput5::DuplicateOutput1（槽位 26）：只要 formats 里的格式（别的由系统转好再给）。</summary>
    public static int DuplicateOutput1(IntPtr output5, IntPtr device, ReadOnlySpan<int> formats, out IntPtr duplication)
    {
        IntPtr result = 0;
        int hr;
        fixed (int* f = formats)
            hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, int*, IntPtr*, int>)Com.Vtbl(output5)[26])(output5, device, 0, (uint)formats.Length, f, &result);
        duplication = result;
        return hr;
    }

    /// <summary>IDXGIOutputDuplication::GetDesc（槽位 7，无返回值）。</summary>
    public static OutduplDesc GetDuplicationDesc(IntPtr duplication)
    {
        OutduplDesc result;
        ((delegate* unmanaged[Stdcall]<IntPtr, OutduplDesc*, void>)Com.Vtbl(duplication)[7])(duplication, &result);
        return result;
    }

    /// <summary>IDXGIOutputDuplication::AcquireNextFrame（槽位 8）。resource：IDXGIResource*（成功时要 Release，再 ReleaseFrame）。</summary>
    public static int AcquireNextFrame(IntPtr duplication, int timeoutMs, out OutduplFrameInfo info, out IntPtr resource)
    {
        OutduplFrameInfo frame = default;
        IntPtr result = 0;
        var hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, OutduplFrameInfo*, IntPtr*, int>)Com.Vtbl(duplication)[8])(duplication, (uint)timeoutMs, &frame, &result);
        info = frame;
        resource = result;
        return hr;
    }

    /// <summary>IDXGIOutputDuplication::GetFrameDirtyRects（槽位 9）。大小都按字节算。</summary>
    public static int GetFrameDirtyRects(IntPtr duplication, Span<NativeRect> rects, out int count)
    {
        uint required = 0;
        int hr;
        fixed (NativeRect* r = rects)
            hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, NativeRect*, uint*, int>)Com.Vtbl(duplication)[9])(duplication, (uint)(rects.Length * sizeof(NativeRect)), r, &required);
        count = (int)(required / (uint)sizeof(NativeRect));
        return hr;
    }

    /// <summary>IDXGIOutputDuplication::GetFrameMoveRects（槽位 10）。大小都按字节算。</summary>
    public static int GetFrameMoveRects(IntPtr duplication, Span<OutduplMoveRect> rects, out int count)
    {
        uint required = 0;
        int hr;
        fixed (OutduplMoveRect* r = rects)
            hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, OutduplMoveRect*, uint*, int>)Com.Vtbl(duplication)[10])(duplication, (uint)(rects.Length * sizeof(OutduplMoveRect)), r, &required);
        count = (int)(required / (uint)sizeof(OutduplMoveRect));
        return hr;
    }

    /// <summary>IDXGIOutputDuplication::GetFramePointerShape（槽位 11）。</summary>
    public static int GetFramePointerShape(IntPtr duplication, Span<byte> buffer, out int required, out OutduplPointerShapeInfo info)
    {
        uint size = 0;
        OutduplPointerShapeInfo shape = default;
        int hr;
        fixed (byte* b = buffer)
            hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, void*, uint*, OutduplPointerShapeInfo*, int>)Com.Vtbl(duplication)[11])(duplication, (uint)buffer.Length, b, &size, &shape);
        required = (int)size;
        info = shape;
        return hr;
    }

    /// <summary>IDXGIOutputDuplication::ReleaseFrame（槽位 14）。</summary>
    public static int ReleaseFrame(IntPtr duplication)
        => ((delegate* unmanaged[Stdcall]<IntPtr, int>)Com.Vtbl(duplication)[14])(duplication);
}

internal static unsafe class D3D11Native
{
    // D3D11_CREATE_DEVICE_FLAG
    public const int CreateDeviceBgraSupport = 0x20;
    public const int CreateDeviceVideoSupport = 0x800;
    // D3D11_USAGE
    public const int UsageDefault = 0;
    public const int UsageStaging = 3;
    // D3D11_BIND_FLAG / D3D11_CPU_ACCESS_FLAG
    public const uint BindShaderResource = 0x8;
    public const uint BindRenderTarget = 0x20;
    public const uint CpuAccessRead = 0x20000;
    // D3D11_MAP
    public const int MapRead = 1;
    // D3D11_VIDEO_FRAME_FORMAT / D3D11_VIDEO_USAGE
    public const int FrameFormatProgressive = 0;
    public const int VideoUsageOptimalQuality = 2;
    // D3D11_VIDEO_PROCESSOR_FORMAT_SUPPORT
    public const uint FormatSupportInput = 0x1;
    public const uint FormatSupportOutput = 0x2;
    // D3D11_VIDEO_PROCESSOR_FEATURE_CAPS
    public const uint FeatureCapsRotation = 0x40;
    // D3D11_VPIV_DIMENSION / D3D11_VPOV_DIMENSION
    public const int ViewDimensionTexture2D = 1;
    // D3D11_VIDEO_PROCESSOR_COLOR_SPACE 位域：Usage:1、RGB_Range:1（0 = 0–255）、YCbCr_Matrix:1（1 = BT.709）、YCbCr_xvYCC:1、Nominal_Range:2（1 = 16–235）
    public const uint ColorSpaceRgbFull = 0x0;
    public const uint ColorSpaceYCbCr709Studio = 0x14;
    // DXGI_COLOR_SPACE_TYPE（dxgicommon.h）
    public const int ColorSpaceRgbFullG22P709 = 0;
    public const int ColorSpaceYCbCrStudioG22LeftP709 = 8;

    /// <summary>ID3D11Device::CreateTexture2D（槽位 5），不给初始数据。</summary>
    public static int CreateTexture2D(IntPtr device, in Texture2DDesc desc, out IntPtr texture)
    {
        var d = desc;
        IntPtr result = 0;
        var hr = ((delegate* unmanaged[Stdcall]<IntPtr, Texture2DDesc*, void*, IntPtr*, int>)Com.Vtbl(device)[5])(device, &d, null, &result);
        texture = result;
        return hr;
    }

    /// <summary>ID3D11Device::GetDeviceRemovedReason（槽位 39）：设备还好好的时是 S_OK。</summary>
    public static int GetDeviceRemovedReason(IntPtr device)
        => ((delegate* unmanaged[Stdcall]<IntPtr, int>)Com.Vtbl(device)[39])(device);

    /// <summary>ID3D11DeviceContext::Map（槽位 14）。</summary>
    public static int Map(IntPtr context, IntPtr resource, int mapType, out MappedSubresource mapped)
    {
        MappedSubresource result = default;
        var hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int, uint, MappedSubresource*, int>)Com.Vtbl(context)[14])(context, resource, 0, mapType, 0, &result);
        mapped = result;
        return hr;
    }

    /// <summary>ID3D11DeviceContext::Unmap（槽位 15）。</summary>
    public static void Unmap(IntPtr context, IntPtr resource)
        => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)Com.Vtbl(context)[15])(context, resource, 0);

    /// <summary>ID3D11DeviceContext::CopyResource（槽位 47）：先目标、后源。</summary>
    public static void CopyResource(IntPtr context, IntPtr destination, IntPtr source)
        => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)Com.Vtbl(context)[47])(context, destination, source);

    /// <summary>ID3D11Texture2D::GetDesc（槽位 10，无返回值）。</summary>
    public static Texture2DDesc GetTextureDesc(IntPtr texture)
    {
        Texture2DDesc result;
        ((delegate* unmanaged[Stdcall]<IntPtr, Texture2DDesc*, void>)Com.Vtbl(texture)[10])(texture, &result);
        return result;
    }

    /// <summary>ID3D11VideoDevice::CreateVideoProcessorEnumerator（槽位 10）。</summary>
    public static int CreateVideoProcessorEnumerator(IntPtr videoDevice, in VideoProcessorContentDesc desc, out IntPtr enumerator)
    {
        var d = desc;
        IntPtr result = 0;
        var hr = ((delegate* unmanaged[Stdcall]<IntPtr, VideoProcessorContentDesc*, IntPtr*, int>)Com.Vtbl(videoDevice)[10])(videoDevice, &d, &result);
        enumerator = result;
        return hr;
    }

    /// <summary>ID3D11VideoProcessorEnumerator::CheckVideoProcessorFormat（槽位 8）。</summary>
    public static int CheckVideoProcessorFormat(IntPtr enumerator, int format, out uint flags)
    {
        uint result = 0;
        var hr = ((delegate* unmanaged[Stdcall]<IntPtr, int, uint*, int>)Com.Vtbl(enumerator)[8])(enumerator, format, &result);
        flags = result;
        return hr;
    }

    /// <summary>ID3D11VideoProcessorEnumerator::GetVideoProcessorCaps（槽位 9）。</summary>
    public static int GetVideoProcessorCaps(IntPtr enumerator, out VideoProcessorCaps caps)
    {
        VideoProcessorCaps result = default;
        var hr = ((delegate* unmanaged[Stdcall]<IntPtr, VideoProcessorCaps*, int>)Com.Vtbl(enumerator)[9])(enumerator, &result);
        caps = result;
        return hr;
    }

    /// <summary>ID3D11VideoDevice::CreateVideoProcessor（槽位 4）。</summary>
    public static int CreateVideoProcessor(IntPtr videoDevice, IntPtr enumerator, out IntPtr processor)
    {
        IntPtr result = 0;
        var hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, IntPtr*, int>)Com.Vtbl(videoDevice)[4])(videoDevice, enumerator, 0, &result);
        processor = result;
        return hr;
    }

    /// <summary>ID3D11VideoDevice::CreateVideoProcessorInputView（槽位 8）：2D 纹理的第 0 层。</summary>
    public static int CreateVideoProcessorInputView(IntPtr videoDevice, IntPtr texture, IntPtr enumerator, out IntPtr view)
    {
        var desc = new VideoProcessorInputViewDesc { ViewDimension = ViewDimensionTexture2D };
        IntPtr result = 0;
        var hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, VideoProcessorInputViewDesc*, IntPtr*, int>)Com.Vtbl(videoDevice)[8])(videoDevice, texture, enumerator, &desc, &result);
        view = result;
        return hr;
    }

    /// <summary>ID3D11VideoDevice::CreateVideoProcessorOutputView（槽位 9）：2D 纹理的第 0 层。</summary>
    public static int CreateVideoProcessorOutputView(IntPtr videoDevice, IntPtr texture, IntPtr enumerator, out IntPtr view)
    {
        var desc = new VideoProcessorOutputViewDesc { ViewDimension = ViewDimensionTexture2D };
        IntPtr result = 0;
        var hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, VideoProcessorOutputViewDesc*, IntPtr*, int>)Com.Vtbl(videoDevice)[9])(videoDevice, texture, enumerator, &desc, &result);
        view = result;
        return hr;
    }

    /// <summary>ID3D11VideoContext::VideoProcessorSetOutputTargetRect（槽位 13）。</summary>
    public static void SetOutputTargetRect(IntPtr videoContext, IntPtr processor, NativeRect rect)
        => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, NativeRect*, void>)Com.Vtbl(videoContext)[13])(videoContext, processor, 1, &rect);

    /// <summary>ID3D11VideoContext::VideoProcessorSetOutputColorSpace（槽位 15）：D3D11_VIDEO_PROCESSOR_COLOR_SPACE 是 4 字节位域。</summary>
    public static void SetOutputColorSpace(IntPtr videoContext, IntPtr processor, uint colorSpace)
        => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint*, void>)Com.Vtbl(videoContext)[15])(videoContext, processor, &colorSpace);

    /// <summary>ID3D11VideoContext::VideoProcessorSetStreamFrameFormat（槽位 27）。</summary>
    public static void SetStreamFrameFormat(IntPtr videoContext, IntPtr processor, int format)
        => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int, void>)Com.Vtbl(videoContext)[27])(videoContext, processor, 0, format);

    /// <summary>ID3D11VideoContext::VideoProcessorSetStreamColorSpace（槽位 28）。</summary>
    public static void SetStreamColorSpace(IntPtr videoContext, IntPtr processor, uint colorSpace)
        => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint*, void>)Com.Vtbl(videoContext)[28])(videoContext, processor, 0, &colorSpace);

    /// <summary>ID3D11VideoContext::VideoProcessorSetStreamSourceRect（槽位 30）。</summary>
    public static void SetStreamSourceRect(IntPtr videoContext, IntPtr processor, NativeRect rect)
        => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int, NativeRect*, void>)Com.Vtbl(videoContext)[30])(videoContext, processor, 0, 1, &rect);

    /// <summary>ID3D11VideoContext::VideoProcessorSetStreamDestRect（槽位 31）。</summary>
    public static void SetStreamDestRect(IntPtr videoContext, IntPtr processor, NativeRect rect)
        => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int, NativeRect*, void>)Com.Vtbl(videoContext)[31])(videoContext, processor, 0, 1, &rect);

    /// <summary>ID3D11VideoContext::VideoProcessorSetStreamAutoProcessingMode（槽位 37）。</summary>
    public static void SetStreamAutoProcessingMode(IntPtr videoContext, IntPtr processor, bool enable)
        => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int, void>)Com.Vtbl(videoContext)[37])(videoContext, processor, 0, enable ? 1 : 0);

    /// <summary>ID3D11VideoContext::VideoProcessorBlt（槽位 53）：一路输入、第 0 帧。</summary>
    public static int VideoProcessorBlt(IntPtr videoContext, IntPtr processor, IntPtr outputView, IntPtr inputView)
    {
        var stream = new VideoProcessorStream { Enable = 1, InputSurface = inputView };
        return ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, uint, uint, VideoProcessorStream*, int>)Com.Vtbl(videoContext)[53])(videoContext, processor, outputView, 0, 1, &stream);
    }

    /// <summary>ID3D11VideoContext::VideoProcessorSetStreamRotation（槽位 63）：D3D11_VIDEO_PROCESSOR_ROTATION（0 不转、1 顺时针 90°、2 = 180°、3 = 270°）。</summary>
    public static void SetStreamRotation(IntPtr videoContext, IntPtr processor, bool enable, int rotation)
        => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int, int, void>)Com.Vtbl(videoContext)[63])(videoContext, processor, 0, enable ? 1 : 0, rotation);

    /// <summary>ID3D11VideoContext1::VideoProcessorSetOutputColorSpace1（槽位 70）：DXGI_COLOR_SPACE_TYPE。</summary>
    public static void SetOutputColorSpace1(IntPtr videoContext1, IntPtr processor, int colorSpace)
        => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, void>)Com.Vtbl(videoContext1)[70])(videoContext1, processor, colorSpace);

    /// <summary>ID3D11VideoContext1::VideoProcessorSetStreamColorSpace1（槽位 74）：DXGI_COLOR_SPACE_TYPE。</summary>
    public static void SetStreamColorSpace1(IntPtr videoContext1, IntPtr processor, int colorSpace)
        => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int, void>)Com.Vtbl(videoContext1)[74])(videoContext1, processor, 0, colorSpace);
}

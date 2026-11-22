using System.Runtime.InteropServices;

namespace PrivateChat.Agent.Telemetry;

// DXGI / D3D11 的最小 COM 互操作，写法同 Video/MediaFoundation.cs：只声明用得到的方法。
// [ComImport] 接口不继承基接口的 vtable，基接口（IDXGIObject、IDXGIFactory、IDXGIAdapter、IDXGIOutput）的方法要按顺序重抄一遍，
// 不调用的用无参占位（只占 vtable 槽位，永远不会被调用）。
// 探测（--probe-hardware）用它列显卡、看显示器接在哪张卡上、试桌面复制；以后用桌面复制截屏时接着用。

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct DxgiAdapterDesc1
{
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
    public int VendorId;
    public int DeviceId;
    public int SubSysId;
    public int Revision;
    public nuint DedicatedVideoMemory;
    public nuint DedicatedSystemMemory;
    public nuint SharedSystemMemory;
    public uint LuidLow;
    public int LuidHigh;
    public int Flags;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct DxgiOutputDesc
{
    /// <summary>\\.\DISPLAY1 这种名字，和 Screen.DeviceName 一样。</summary>
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
    public int AttachedToDesktop;
    public int Rotation;
    public IntPtr Monitor;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DxgiOutduplFrameInfo
{
    public long LastPresentTime;
    public long LastMouseUpdateTime;
    public int AccumulatedFrames;
    public int RectsCoalesced;
    public int ProtectedContentMaskedOut;
    public int PointerX;
    public int PointerY;
    public int PointerVisible;
    public int TotalMetadataBufferSize;
    public int PointerShapeBufferSize;
}

[ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDXGIFactory1
{
    // IDXGIObject
    void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
    // IDXGIFactory
    void EnumAdapters(); void MakeWindowAssociation(); void GetWindowAssociation(); void CreateSwapChain(); void CreateSoftwareAdapter();
    // IDXGIFactory1
    [PreserveSig] int EnumAdapters1(int index, out IDXGIAdapter1 adapter);
    [PreserveSig] int IsCurrent();
}

[ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDXGIAdapter1
{
    // IDXGIObject
    void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
    // IDXGIAdapter
    [PreserveSig] int EnumOutputs(int index, out IDXGIOutput output);
    void GetDesc();
    [PreserveSig] int CheckInterfaceSupport(in Guid iid, out long umdVersion);
    // IDXGIAdapter1
    [PreserveSig] int GetDesc1(out DxgiAdapterDesc1 desc);
}

[ComImport, Guid("ae02eedb-c735-4690-8d52-5a8dc20213aa"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDXGIOutput
{
    // IDXGIObject
    void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
    // IDXGIOutput
    [PreserveSig] int GetDesc(out DxgiOutputDesc desc);
}

[ComImport, Guid("00cddea8-939b-4b83-a340-a685226666cc"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDXGIOutput1
{
    // IDXGIObject
    void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
    // IDXGIOutput
    void GetDesc(); void GetDisplayModeList(); void FindClosestMatchingMode(); void WaitForVBlank(); void TakeOwnership(); void ReleaseOwnership();
    void GetGammaControlCapabilities(); void SetGammaControl(); void GetGammaControl(); void SetDisplaySurface(); void GetDisplaySurfaceData(); void GetFrameStatistics();
    // IDXGIOutput1
    void GetDisplayModeList1(); void FindClosestMatchingMode1(); void GetDisplaySurfaceData1();
    /// <param name="device">ID3D11Device*（建在这块显示器所在的那张卡上）。</param>
    [PreserveSig] int DuplicateOutput(IntPtr device, out IDXGIOutputDuplication duplication);
}

[ComImport, Guid("191cfac3-a341-470d-b26e-a864f428319c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDXGIOutputDuplication
{
    // IDXGIObject
    void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
    // IDXGIOutputDuplication
    void GetDesc();
    /// <param name="resource">IDXGIResource*（拿到了要 Release，再 ReleaseFrame）。</param>
    [PreserveSig] int AcquireNextFrame(int timeoutMs, out DxgiOutduplFrameInfo info, out IntPtr resource);
    void GetFrameDirtyRects(); void GetFrameMoveRects(); void GetFramePointerShape(); void MapDesktopSurface(); void UnMapDesktopSurface();
    [PreserveSig] int ReleaseFrame();
}

internal static class Dxgi
{
    public const int ErrorNotFound = unchecked((int)0x887A0002);
    public const int ErrorWaitTimeout = unchecked((int)0x887A0027);
    public const int AdapterFlagSoftware = 2;
    public const int DriverTypeUnknown = 0;          // 指定了显卡时必须用这个
    public const int CreateDeviceBgraSupport = 0x20;
    public const int D3D11SdkVersion = 7;

    private static readonly Guid IID_IDXGIDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    private static readonly Guid IID_ID3D10Device = new("9b7e4c0f-342c-4106-a19f-4f2704f689f0");

    [DllImport("dxgi.dll", ExactSpelling = true)] private static extern int CreateDXGIFactory1(in Guid riid, out IDXGIFactory1 factory);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    public static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, int flags, IntPtr featureLevels, int featureLevelCount,
        int sdkVersion, out IntPtr device, out int featureLevel, out IntPtr context);

    public static IDXGIFactory1 CreateFactory()
    {
        var hr = CreateDXGIFactory1(typeof(IDXGIFactory1).GUID, out var factory);
        if (hr < 0) throw new COMException($"CreateDXGIFactory1 失败：{HResults.Describe(hr)}", hr);
        return factory;
    }

    /// <summary>
    /// 显卡驱动的用户态版本（a.b.c.d，和设备管理器里的驱动版本一样）；问不到是空字符串。
    /// 会把驱动的用户态部分载进进程，所以只在探测子进程里调。
    /// </summary>
    public static string DriverVersion(IDXGIAdapter1 adapter)
    {
        if (adapter.CheckInterfaceSupport(IID_IDXGIDevice, out var umd) < 0 && adapter.CheckInterfaceSupport(IID_ID3D10Device, out umd) < 0) return "";
        var high = (int)(umd >> 32);
        var low = (int)umd;
        return $"{(high >> 16) & 0xFFFF}.{high & 0xFFFF}.{(low >> 16) & 0xFFFF}.{low & 0xFFFF}";
    }
}

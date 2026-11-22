using System.Drawing;
using PrivateChat.Agent.Session;
using PrivateChat.Agent.Telemetry;

namespace PrivateChat.Agent.Video;

/// <summary>
/// 桌面复制（DXGI Desktop Duplication）截屏：每帧 1–2 ms（GDI 要 17–33 ms），交出去的已经是缩放好的 NV12（编码器直接用），
/// 还带着哪些块可能变了（<see cref="CapturedFrame.ChangedHint"/>），采集循环只用比那几块。
/// - 在这块显示器所在的那张卡上建 D3D11 设备（双显卡笔记本上必须是那张卡）、IDXGIOutput5.DuplicateOutput1（只要 BGRA；老系统用 IDXGIOutput1.DuplicateOutput）；
///   第一次 Capture 才建，构造很便宜（AgentService 列显示器时也会建一个）。换了显示器就整个重建。
/// - 每次 Capture：AcquireNextFrame(0)。有新画面：拷进自己留着的 BGRA 纹理，记下变化区域和移动区域（源、目标都算），
///   显卡上转方向 + 缩放 + 转 NV12（<see cref="VideoConverter"/>），读回内存（"干净"的一帧，没有指针）。
///   画面里没有指针：按 GetFramePointerShape / PointerPosition 自己画（<see cref="CursorOverlay"/>，和 GDI 截屏时一样原始大小）。
///   只动了指针：不重新转画面，擦掉旧指针、画上新的。
/// - 没变（DXGI_ERROR_WAIT_TIMEOUT）：交出上一帧，提示全是 false（指针动了就标指针新旧两处）。
/// - 桌面切换（DXGI_ERROR_ACCESS_LOST）：放掉复制，下一次重建；先交出上一帧。重建时 E_ACCESSDENIED 是锁屏 / UAC（安全桌面）：
///   <see cref="SecureDesktop"/> = true，之后每 0.5 秒（Capture 或问 SecureDesktop 时）再试，能复制了就清掉。
///   失去访问之后重建复制失败（不是上面这些暂时的错），或者这块显示器已经不在桌面上了（插拔显示器、Win+P 换投影方式、换了显卡输出）：
///   开着时找到的显卡输出可能已经作废，先把设备整个放掉、重新找一遍输出再开一次（每轮失去访问只这么做一次）。
///   一直 ACCESS_LOST、一幅新画面都拿不到（连着 <see cref="LostLimit"/> 次、至少 <see cref="LostLimitMs"/> 毫秒；Windows 11 上有竖屏显示器
///   AcquireNextFrame 总是返回它的报告）：不再用桌面复制，免得一直交出旧画面。
/// - 找不到这块显示器对应的显卡输出：<see cref="NotFoundGraceMs"/> 毫秒之内当成暂时的（Screen.AllScreens 还没跟上显示器的变化），每 0.5 秒再找；过了才不用。
/// - 显卡驱动重置（DEVICE_REMOVED / RESET）：过 1 秒把设备整个重建一次；再来一次就抛 <see cref="CaptureFallbackException"/>。
///   不支持、被别的程序占满（最多 4 个）、视频处理器建不起来、别的意外错误也抛它：调用方（<see cref="ScreenCaptures"/>）这次远程改用 GDI。
///   这些暂时的情况都不往外抛异常（采集循环里漏出一个异常整个远程就断了）。
/// 只在一个线程上用（采集循环）。坐标都是物理像素（进程是 Per-Monitor V2）。
/// </summary>
public sealed unsafe class DxgiScreenSource : IFrameSource, IAdapterAwareSource
{
    /// <summary>
    /// 刚建好复制时，第一幅桌面画面（整个桌面）一般几毫秒到二十几毫秒就来（之前常先来一帧只有指针的）；最多等这么久（只在刚建好复制时等）。
    /// </summary>
    private const int FirstFrameWaitMs = 100;
    /// <summary>复制建不起来（锁屏、会话断开）时隔多久再试。</summary>
    private const int RetryMs = 500;
    /// <summary>显卡驱动重置后等多久再重建设备（驱动恢复要一两秒）。</summary>
    private const int ReopenMs = 1000;
    /// <summary>
    /// 上次拿到新画面以来 ACCESS_LOST 了这么多次、而且已经持续了 <see cref="LostLimitMs"/>，就当桌面复制在这块显示器上不能用了。
    /// 正常的桌面切换、改分辨率只有一两次（重建后的第一幅就是整个桌面，拿到就从头数）。
    /// </summary>
    internal const int LostLimit = 10;
    internal const int LostLimitMs = 2000;
    /// <summary>找不到显示器对应的显卡输出时，这么久之内当成暂时的。</summary>
    internal const int NotFoundGraceMs = 5000;
    private static readonly int[] Formats = [DxgiNative.FormatB8G8R8A8];

    private enum Clean { None, Black, Image }

    // 现在复制的显示器、所在的显卡
    private string? _deviceName;
    private int _vendorId;
    private IntPtr _output1;
    private IntPtr _output5;
    private IntPtr _device;
    private IntPtr _context;
    private VideoConverter? _converter;
    private IntPtr _duplication;
    private int _rotation = DxgiNative.RotationIdentity;
    private bool _firstAcquire;
    private Dictionary<string, (int VendorId, string Adapter)>? _adapters;

    // 留着的最新桌面画面（显卡上，显示器没转过的方向、原始大小）
    private IntPtr _desktop;
    private Texture2DDesc _desktopDesc;
    private bool _hasImage;
    private bool _imageChanged;

    // 上次交出画面以来变了的区域（人看到的方向、源像素）；_allDirty：全部都算变了（刚建好复制、拿不到区域）
    private readonly List<Rectangle> _dirty = [];
    private bool _allDirty = true;
    private NativeRect[] _dirtyBuffer = new NativeRect[64];
    private OutduplMoveRect[] _moveBuffer = new OutduplMoveRect[16];

    // 指针（位置是指针形状左上角在源画面上的位置，人看到的方向）
    private byte[] _shapeBuffer = new byte[64 * 64 * 4];
    private CursorShape? _shape;
    private bool _pointerVisible;
    private int _pointerX;
    private int _pointerY;
    private bool _pointerChanged;
    private Rectangle _cursorTarget;
    private Rectangle _drawn;

    // 交出去的画面：干净的（没有指针）、画了指针的（交出去的就是它）、变化提示
    private byte[] _clean = [];
    private byte[] _frame = [];
    private bool[] _hint = [];
    private int _width;
    private int _height;
    private Clean _cleanState;
    private (string? DeviceName, int Width, int Height) _returned;

    // 分几步缩放（变化提示按每一步的扩散来标），按 (源宽, 源高, 宽, 高) 记着
    private (int, int, int, int) _passesFor;
    private (int Width, int Height)[] _passes = [];

    // 出错
    private int _deviceLosses;
    private long _reopenAt;
    private long _retryAt;
    private bool _denied;
    private string? _broken;
    private bool _disposed;
    // 上次拿到新画面以来 ACCESS_LOST 的次数、第一次的时间；复制断过（失去访问、被拒）；这一轮里是不是已经整个重开过一次
    private int _lostCount;
    private long _lostSince;
    private bool _interrupted;
    private bool _reopened;
    // 从什么时候起找不到显示器对应的显卡输出（-1：找得到）
    private long _notFoundSince = -1;

    /// <summary>和 GDI 截屏同一份显示器列表（Screen.AllScreens，带 DeviceName）；不枚举 DXGI（每帧都调）。</summary>
    public IReadOnlyList<ScreenInfo> Screens() => GdiScreenSource.List();

    public bool IsRealScreen => true;

    /// <summary>"桌面复制（Intel(R) UHD Graphics）"；还没建设备时是"桌面复制"。</summary>
    public string Name { get; private set; } = "桌面复制";

    /// <summary>
    /// 复制被拒（E_ACCESSDENIED：锁屏、UAC 提示）。是 true 时采集循环一般不再调 Capture，所以这里每 0.5 秒自己再试一次建复制，
    /// 能建了就变回 false（下一次 Capture 拿到整幅新画面）。不抛异常；试的时候发现不能再用了就返回 false，让下一次 Capture 抛出来换 GDI。
    /// </summary>
    public bool SecureDesktop
    {
        get
        {
            if (!_denied || _broken is not null || _disposed) return false;
            var now = Environment.TickCount64;
            if (_duplication == 0 && (_device != 0 ? now >= _retryAt : now >= _reopenAt))
            {
                try
                {
                    // 设备可能在这期间整个放掉了（驱动重置、失去访问后重开）：先重开
                    if (_device == 0) Open();
                    if (_device != 0 && Environment.TickCount64 >= _retryAt) Duplicate();
                }
                catch (CaptureDeviceLostException ex)
                {
                    try { LoseDevice(ex.Message); }
                    catch (CaptureFallbackException) { return false; }
                }
                catch (Exception ex)
                {
                    _broken = ex.Message;
                    return false;
                }
            }
            return _denied;
        }
    }

    /// <summary>拿到过几幅新的桌面画面（探测、自检用：建好了复制却一幅都没拿到也算不能用）。</summary>
    public int Images { get; private set; }

    /// <summary>最近一次没拿到画面的原因（"DuplicateOutput1：E_ACCESSDENIED（0x80070005）"之类）；拿到新画面后清掉。探测、自检用。</summary>
    public string? Problem { get; private set; }

    /// <summary>桌面复制给的画面现在怎么转（DXGI_MODE_ROTATION：1 不转，2 / 3 / 4 = 顺时针 90° / 180° / 270°）。探测用。</summary>
    internal int Rotation => _rotation;

    /// <summary>留着的桌面画面（显示器没转过的方向）多大；还没有画面时是 (0, 0)。探测用。</summary>
    internal (int Width, int Height) DesktopSize => _hasImage ? ((int)_desktopDesc.Width, (int)_desktopDesc.Height) : (0, 0);

    /// <summary>上一次显卡转换分了几步（缩小超过 2 倍时不止一步）。探测用。</summary>
    internal int ConverterPasses => _converter?.PassCount ?? 0;

    /// <summary>
    /// 探测用：把留着的桌面画面按指定的方向（DXGI_MODE_ROTATION）转成 width × height 的 NV12（和远程时同一个 <see cref="VideoConverter"/>，不画指针，
    /// 交出去的画面不受影响）。现在这个方向之外的几种也要先在探测子进程里走一遍：远程中途显示器转了方向就会用到。
    /// 还没有画面时返回 false；出错照常抛（<see cref="CaptureFallbackException"/> 等）。
    /// </summary>
    internal bool ConvertRetained(int rotation, int width, int height, Span<byte> nv12)
    {
        if (!_hasImage || _converter is null || _device == 0) return false;
        _converter.Convert(_desktop, _desktopDesc, rotation, width, height, nv12);
        return true;
    }

    public int AdapterVendorId(ScreenInfo screen)
    {
        if (screen.DeviceName is not { } name) return 0;
        if (_device != 0 && string.Equals(name, _deviceName, StringComparison.OrdinalIgnoreCase)) return _vendorId;
        return Adapter(name)?.VendorId ?? 0;
    }

    /// <summary>这块显示器接在哪张显卡上（名字、厂商）；问不到 null。只枚举一次 DXGI（不建设备）。</summary>
    internal (int VendorId, string Adapter)? Adapter(string deviceName)
    {
        try
        {
            if (_adapters is null)
            {
                var adapters = new Dictionary<string, (int, string)>(StringComparer.OrdinalIgnoreCase);
                foreach (var output in Outputs()) adapters[output.DeviceName] = (output.VendorId, output.Adapter);
                _adapters = adapters;
            }
            return _adapters.TryGetValue(deviceName, out var adapter) ? adapter : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public CapturedFrame Capture(ScreenInfo screen, int width, int height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_broken is { } broken) throw new CaptureFallbackException(broken);
        if (screen.DeviceName is not { } deviceName) throw Broken("这块显示器没有设备名，对不上显卡输出");
        if (width <= 0 || height <= 0 || width % 2 != 0 || height % 2 != 0) throw Broken($"编码尺寸 {width}x{height} 不是正的偶数");
        try
        {
            if (!string.Equals(deviceName, _deviceName, StringComparison.OrdinalIgnoreCase)) Switch(deviceName);
            if (_device == 0 && Environment.TickCount64 >= _reopenAt) Open();
            if (_device != 0 && _duplication == 0 && Environment.TickCount64 >= _retryAt) Duplicate();
            if (_duplication != 0) Acquire();
            return Produce(width, height, gpu: _device != 0);
        }
        catch (CaptureDeviceLostException ex)
        {
            LoseDevice(ex.Message);
            return Produce(width, height, gpu: false);
        }
        catch (CaptureFallbackException ex)
        {
            _broken = ex.Message;
            throw;
        }
        catch (Exception ex)
        {
            throw Broken($"桌面复制出错：{ex.GetType().Name}：{ex.Message}");
        }
    }

    private CaptureFallbackException Broken(string reason)
    {
        _broken = reason;
        return new CaptureFallbackException(reason);
    }

    /// <summary>换了显示器：旧的全部放掉，指针、画面从头来。</summary>
    private void Switch(string deviceName)
    {
        CloseDevice();
        _deviceName = deviceName;
        _cleanState = Clean.None;
        _shape = null;
        _pointerVisible = false;
        _drawn = _cursorTarget = Rectangle.Empty;
        _denied = false;
        Problem = null;
        _retryAt = _reopenAt = 0;
        _lostCount = 0;
        _interrupted = _reopened = false;
        _notFoundSince = -1;
        Name = Adapter(deviceName) is { } adapter ? $"桌面复制（{adapter.Adapter}）" : "桌面复制";
    }

    /// <summary>
    /// 找到这块显示器的 DXGI 输出（每次都新建 DXGI 工厂重新枚举），在它所在的显卡上建设备和视频处理器。
    /// 找不到时先当成暂时的（插拔显示器、换投影方式时 Screen.AllScreens 要过一会儿才跟上，那时采集循环会换到别的显示器）：
    /// 0.5 秒后再找，<see cref="NotFoundGraceMs"/> 毫秒还找不到才不用桌面复制。
    /// </summary>
    private void Open()
    {
        IntPtr adapter = 0, output = 0;
        var adapterName = "";
        var vendor = 0;
        var name = _deviceName!;
        var found = Enumerate((a, ad, o, od) =>
        {
            if (!string.Equals(Com.Text(od.DeviceName, 32), name, StringComparison.OrdinalIgnoreCase)) return false;
            (adapter, output, adapterName, vendor) = (a, o, Com.Text(ad.Description, 128).Trim(), (int)ad.VendorId);
            return true;
        });
        if (!found)
        {
            var now = Environment.TickCount64;
            if (_notFoundSince < 0)
            {
                _notFoundSince = now;
                AgentLog.Info($"桌面复制：找不到显示器 {name} 对应的显卡输出，{NotFoundGraceMs / 1000} 秒内再找找");
            }
            _adapters = null;
            if (now - _notFoundSince >= NotFoundGraceMs) throw Broken($"找不到显示器 {name} 对应的显卡输出（找了 {NotFoundGraceMs / 1000} 秒）");
            _reopenAt = now + RetryMs;
            Problem = $"找不到显示器 {name} 对应的显卡输出";
            return;
        }
        _notFoundSince = -1;
        try
        {
            _output5 = Com.Query(output, DxgiNative.IID_IDXGIOutput5);
            _output1 = Com.Query(output, DxgiNative.IID_IDXGIOutput1);
            if (_output1 == 0 && _output5 == 0) throw Broken("显卡输出不支持桌面复制（没有 IDXGIOutput1）");
            var hr = Dxgi.D3D11CreateDevice(adapter, Dxgi.DriverTypeUnknown, 0, D3D11Native.CreateDeviceVideoSupport | D3D11Native.CreateDeviceBgraSupport,
                0, 0, Dxgi.D3D11SdkVersion, out _device, out var level, out _context);
            CaptureErrors.Check(hr, $"在 {adapterName} 上建 D3D11 设备");
            _converter = new VideoConverter(_device, _context);
            _vendorId = vendor;
            Name = $"桌面复制（{adapterName}）";
            AgentLog.Info($"桌面复制：显示器 {name}，显卡 {adapterName}（D3D 功能级别 {level >> 12}.{(level >> 8) & 0xF}）");
        }
        finally
        {
            Com.Release(ref output);
            Com.Release(ref adapter);
        }
    }

    /// <summary>建复制。锁屏 / UAC、会话断开这类暂时的情况只记下原因（过一会儿再试）；别的失败抛出去。</summary>
    private void Duplicate()
    {
        _retryAt = Environment.TickCount64 + RetryMs;
        int hr;
        IntPtr duplication;
        string what;
        if (_output5 != 0)
        {
            what = "DuplicateOutput1";
            hr = DxgiNative.DuplicateOutput1(_output5, _device, Formats, out duplication);
            // 老驱动 / 没按 Per-Monitor 感知运行时 DuplicateOutput1 可能不认：退回老的那个（它总是给 BGRA）
            if ((hr is DxgiNative.E_INVALIDARG or DxgiNative.DXGI_ERROR_UNSUPPORTED or DxgiNative.E_NOINTERFACE) && _output1 != 0)
            {
                what = "DuplicateOutput";
                hr = DxgiNative.DuplicateOutput(_output1, _device, out duplication);
            }
        }
        else
        {
            what = "DuplicateOutput";
            hr = DxgiNative.DuplicateOutput(_output1, _device, out duplication);
        }
        switch (hr)
        {
            case >= 0:
                _duplication = duplication;
                _rotation = DxgiNative.GetDuplicationDesc(duplication).Rotation;
                if (_denied) AgentLog.Info($"桌面复制：{_deviceName} 又能复制了");
                _denied = false;
                Problem = null;
                _allDirty = true;
                _firstAcquire = true;
                return;
            case DxgiNative.E_ACCESSDENIED or DxgiNative.DXGI_ERROR_ACCESS_DENIED:
                if (!_denied) AgentLog.Info($"桌面复制：{_deviceName} 现在不让复制（锁屏或系统安全提示界面）：{HResults.Describe(hr)}");
                _denied = true;
                Problem = $"{what}：{HResults.Describe(hr)}";
                // 拿不到画面的原因清楚了（安全桌面）：之前的 ACCESS_LOST 不再往上数
                _lostCount = 0;
                _interrupted = true;
                return;
            case DxgiNative.DXGI_ERROR_ACCESS_LOST:
                Problem = $"{what}：{HResults.Describe(hr)}";
                CountLost();
                return;
            case DxgiNative.DXGI_ERROR_SESSION_DISCONNECTED or DxgiNative.DXGI_ERROR_MODE_CHANGE_IN_PROGRESS:
                Problem = $"{what}：{HResults.Describe(hr)}";
                return;
            default:
                // 失去访问（或被拒）之后重建失败：开着时找到的显卡输出可能已经作废了（显示器拔了、换了投影方式、锁着屏时接了扩展坞），
                // 整个重开一次再说。显卡掉了照常走设备重建
                if (_interrupted && !_reopened && !CaptureErrors.DeviceLost(hr))
                {
                    Reopen($"{what}：{HResults.Describe(hr)}");
                    return;
                }
                CaptureErrors.Check(hr, what);
                return;
        }
    }

    /// <summary>
    /// 又一次 ACCESS_LOST（复制已经放掉了）：记一次；一直拿不到新画面就不再用桌面复制（抛 <see cref="CaptureFallbackException"/>）。
    /// 这块显示器已经不在桌面上了就整个重开一次（重新找输出）。
    /// </summary>
    private void CountLost()
    {
        var now = Environment.TickCount64;
        _interrupted = true;
        if (_lostCount == 0) _lostSince = now;
        _lostCount++;
        if (_lostCount >= LostLimit && now - _lostSince >= LostLimitMs)
            throw Broken($"桌面复制一直拿不到画面：{(now - _lostSince) / 1000.0:F1} 秒里 {_lostCount} 次 ACCESS_LOST（最后一次 {Problem}）");
        if (!_reopened && !OutputAttached()) Reopen($"显示器 {_deviceName} 已经不在桌面上了");
    }

    /// <summary>开着时找到的那个显卡输出还接在桌面上（问不到也算不在）。</summary>
    private bool OutputAttached()
    {
        var output = _output1 != 0 ? _output1 : _output5;
        return output != 0 && DxgiNative.GetOutputDesc(output, out var desc) >= 0 && desc.AttachedToDesktop != 0
            && string.Equals(Com.Text(desc.DeviceName, 32), _deviceName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>设备、复制整个放掉，下一次 Capture 重新枚举显卡输出再开（每轮失去访问只一次；拿到新画面后才能再来）。先交出上一帧。</summary>
    private void Reopen(string reason)
    {
        _reopened = true;
        AgentLog.Info($"桌面复制：{_deviceName} 失去访问后接不上了（{reason}），重新找显卡输出、整个重开一次");
        CloseDevice();
        _adapters = null;
        _reopenAt = 0;
        _retryAt = 0;
        Problem = reason;
    }

    /// <summary>
    /// 取一帧：有新画面就拷下来、记下变化区域；指针位置、形状变了也记下。
    /// 刚建好复制时第一次取到的常常只有指针（LastPresentTime = 0，桌面画面下一次才来；Intel UHD、RTX 4060 上实测几乎每次都这样）：
    /// 这时在 <see cref="FirstFrameWaitMs"/> 之内接着取，直到拿到桌面画面，免得远程一开始（和每次失去访问重建之后）先交出一幅黑屏。
    /// </summary>
    private void Acquire()
    {
        if (!_firstAcquire)
        {
            AcquireOnce(0);
            return;
        }
        _firstAcquire = false;
        var deadline = Environment.TickCount64 + FirstFrameWaitMs;
        var images = Images;
        while (AcquireOnce((int)Math.Max(0, deadline - Environment.TickCount64)) && Images == images && _duplication != 0
               && Environment.TickCount64 < deadline)
        {
        }
    }

    /// <summary>AcquireNextFrame 一次（最多等 timeoutMs 毫秒）。拿到了一帧（有没有新画面都算）、复制也还在时返回 true。</summary>
    private bool AcquireOnce(int timeoutMs)
    {
        var hr = DxgiNative.AcquireNextFrame(_duplication, timeoutMs, out var info, out var resource);
        if (hr == DxgiNative.DXGI_ERROR_WAIT_TIMEOUT) return false;
        if (hr is DxgiNative.DXGI_ERROR_ACCESS_LOST or DxgiNative.E_ACCESSDENIED)
        {
            // 桌面切换（锁屏、UAC、全屏程序换模式、改分辨率）：放掉，下一次重建（安全桌面上会被拒）
            Com.Release(ref _duplication);
            _retryAt = 0;
            Problem = $"AcquireNextFrame：{HResults.Describe(hr)}";
            if (hr == DxgiNative.DXGI_ERROR_ACCESS_LOST) CountLost();
            else (_interrupted, _lostCount) = (true, 0); // 安全桌面：拿不到画面的原因清楚了
            return false;
        }
        CaptureErrors.Check(hr, "AcquireNextFrame");
        int released;
        try
        {
            Collect(info, resource);
        }
        finally
        {
            Com.Release(ref resource);
            released = DxgiNative.ReleaseFrame(_duplication);
        }
        if (released == DxgiNative.DXGI_ERROR_ACCESS_LOST)
        {
            Com.Release(ref _duplication);
            _retryAt = 0;
            Problem = $"ReleaseFrame：{HResults.Describe(released)}";
            CountLost();
            return false;
        }
        CaptureErrors.Check(released, "ReleaseFrame");
        return true;
    }

    private void Collect(in OutduplFrameInfo info, IntPtr resource)
    {
        if (info.LastPresentTime != 0 && resource != 0)
        {
            var texture = Com.Query(resource, DxgiNative.IID_ID3D11Texture2D);
            if (texture == 0) throw Broken("桌面画面不是 2D 纹理");
            try
            {
                var desc = D3D11Native.GetTextureDesc(texture);
                if (desc.Format is not (DxgiNative.FormatB8G8R8A8 or DxgiNative.FormatB8G8R8A8Srgb))
                    throw Broken($"桌面画面的格式是 DXGI_FORMAT {desc.Format}，不是 BGRA");
                if (_desktop == 0 || desc.Width != _desktopDesc.Width || desc.Height != _desktopDesc.Height || desc.Format != _desktopDesc.Format)
                    CreateDesktopTexture(desc);
                D3D11Native.CopyResource(_context, _desktop, texture);
            }
            finally
            {
                Com.Release(ref texture);
            }
            CollectRects(info);
            _hasImage = true;
            _imageChanged = true;
            Images++;
            Problem = null;
            // 拿到新画面了：失去访问、重开这一轮算过去了
            _lostCount = 0;
            _interrupted = _reopened = false;
        }
        if (info.LastMouseUpdateTime != 0)
        {
            var visible = info.PointerVisible != 0;
            if (visible != _pointerVisible || visible && (info.PointerX != _pointerX || info.PointerY != _pointerY)) _pointerChanged = true;
            _pointerVisible = visible;
            if (visible) (_pointerX, _pointerY) = (info.PointerX, info.PointerY);
        }
        if (info.PointerShapeBufferSize > 0) ReadShape((int)info.PointerShapeBufferSize);
    }

    /// <summary>自己留一张和桌面画面一样大小、格式的纹理（桌面画面 ReleaseFrame 之后就不归我们了；静止时换尺寸要从它重新转）。</summary>
    private void CreateDesktopTexture(in Texture2DDesc source)
    {
        Com.Release(ref _desktop);
        _hasImage = false;
        var desc = new Texture2DDesc
        {
            Width = source.Width,
            Height = source.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = source.Format,
            SampleCount = 1,
            Usage = D3D11Native.UsageDefault,
            BindFlags = D3D11Native.BindRenderTarget | D3D11Native.BindShaderResource,
        };
        CaptureErrors.Check(D3D11Native.CreateTexture2D(_device, desc, out _desktop), $"建 {source.Width}x{source.Height} 的桌面纹理");
        _desktopDesc = desc;
        _allDirty = true;
    }

    /// <summary>移动区域（源、目标都算）和变化区域，转到人看到的方向。拿不到就当整幅都变了。</summary>
    private void CollectRects(in OutduplFrameInfo info)
    {
        if (_allDirty) return;
        if (info.TotalMetadataBufferSize == 0)
        {
            _allDirty = true;
            return;
        }
        var bytes = (int)info.TotalMetadataBufferSize;
        var (textureWidth, textureHeight) = ((int)_desktopDesc.Width, (int)_desktopDesc.Height);
        var moves = bytes / 24 + 1;
        if (_moveBuffer.Length < moves) _moveBuffer = new OutduplMoveRect[moves];
        if (DxgiNative.GetFrameMoveRects(_duplication, _moveBuffer, out var moveCount) < 0)
        {
            _allDirty = true;
            return;
        }
        for (var i = 0; i < Math.Min(moveCount, _moveBuffer.Length); i++)
        {
            var move = _moveBuffer[i];
            var destination = move.Destination.ToRectangle();
            _dirty.Add(CaptureTiles.Rotate(destination, _rotation, textureWidth, textureHeight));
            _dirty.Add(CaptureTiles.Rotate(new Rectangle(move.SourceX, move.SourceY, destination.Width, destination.Height), _rotation, textureWidth, textureHeight));
        }
        var rects = bytes / 16 + 1;
        if (_dirtyBuffer.Length < rects) _dirtyBuffer = new NativeRect[rects];
        if (DxgiNative.GetFrameDirtyRects(_duplication, _dirtyBuffer, out var dirtyCount) < 0)
        {
            _allDirty = true;
            return;
        }
        for (var i = 0; i < Math.Min(dirtyCount, _dirtyBuffer.Length); i++)
            _dirty.Add(CaptureTiles.Rotate(_dirtyBuffer[i].ToRectangle(), _rotation, textureWidth, textureHeight));
    }

    private void ReadShape(int size)
    {
        if (_shapeBuffer.Length < size) _shapeBuffer = new byte[size];
        // 指针形状只是好看：拿不到就还用原来的
        if (DxgiNative.GetFramePointerShape(_duplication, _shapeBuffer.AsSpan(0, size), out var required, out var shape) < 0) return;
        _shape = CursorShape.FromDxgi((int)shape.Type, (int)shape.Width, (int)shape.Height, (int)shape.Pitch, shape.HotX, shape.HotY,
            _shapeBuffer.AsSpan(0, Math.Min(required, size)));
        _pointerChanged = true;
    }

    /// <summary>交出 width × height 的一帧：需要时在显卡上重新转（新画面、换了尺寸），画上指针，算变化提示。</summary>
    private CapturedFrame Produce(int width, int height, bool gpu)
    {
        if (width != _width || height != _height)
        {
            var size = width * height * 3 / 2;
            _clean = new byte[size];
            _frame = new byte[size];
            _hint = new bool[FrameTiles.Count(width, height)];
            (_width, _height) = (width, height);
            _cleanState = Clean.None;
        }
        var fresh = false;
        if (gpu && _hasImage && _converter is not null && (_imageChanged || _cleanState != Clean.Image))
        {
            _converter.Convert(_desktop, _desktopDesc, _rotation, width, height, _clean);
            _cleanState = Clean.Image;
            _imageChanged = false;
            fresh = true;
        }
        else if (_cleanState == Clean.None)
        {
            // 还没有画面（锁着屏就开始了、显卡刚重置）：先给黑的
            _clean.AsSpan(0, width * height).Fill(16);
            _clean.AsSpan(width * height).Fill(128);
            _cleanState = Clean.Black;
            fresh = true;
        }

        var (sourceWidth, sourceHeight) = CaptureTiles.Rotated(_rotation, (int)_desktopDesc.Width, (int)_desktopDesc.Height);
        var same = _returned == (_deviceName, width, height);
        bool[]? hint = null;
        if (same)
        {
            Array.Clear(_hint);
            if (fresh && _allDirty) Array.Fill(_hint, true);
            else if (fresh)
            {
                // 显卡上缩小超过 2 倍时分了几步（VideoConverter），每一步都会把变化抹开一点：按同样的几步标。
                // 两个方向各算各的，在人看到的方向上算和在显示器没转过的方向上算是同一组尺寸（宽高对调）
                if (_passesFor != (sourceWidth, sourceHeight, width, height))
                {
                    _passes = CaptureTiles.Passes(sourceWidth, sourceHeight, width, height);
                    _passesFor = (sourceWidth, sourceHeight, width, height);
                }
                foreach (var rect in _dirty) CaptureTiles.MarkSource(_hint, rect, sourceWidth, sourceHeight, _passes);
            }
            hint = _hint;
        }
        if (fresh || !same)
        {
            _dirty.Clear();
            _allDirty = false;
        }

        // 指针：位置、形状、显示与否变了，或者画面重新转过，都重画
        var target = Rectangle.Empty;
        if (_pointerVisible && _shape is { } shape && _cleanState == Clean.Image && sourceWidth > 0 && sourceHeight > 0)
        {
            var at = CursorOverlay.Place(_pointerX, _pointerY, shape.HotX, shape.HotY, sourceWidth, sourceHeight, width, height);
            target = new Rectangle(at.X, at.Y, shape.Width, shape.Height);
        }
        var cursorChanged = _pointerChanged || target != _cursorTarget;
        if (fresh || !same || cursorChanged)
        {
            var old = _drawn;
            if (fresh || !same) _clean.AsSpan().CopyTo(_frame);
            else CursorOverlay.Restore(_clean, _frame, width, height, old);
            _drawn = target.IsEmpty || _shape is null ? Rectangle.Empty : CursorOverlay.Draw(_shape, _frame, width, height, target.X, target.Y);
            if (hint is not null && cursorChanged)
            {
                CaptureTiles.MarkDestination(hint, old, width, height);
                CaptureTiles.MarkDestination(hint, _drawn, width, height);
            }
        }
        _pointerChanged = false;
        _cursorTarget = target;
        _returned = (_deviceName, width, height);
        return new CapturedFrame(_frame, width, height, width, FrameFormat.Nv12, hint);
    }

    /// <summary>显卡驱动重置了：设备整个放掉，过 1 秒重建；这已经是第二次就不再用桌面复制。</summary>
    private void LoseDevice(string reason)
    {
        _deviceLosses++;
        CloseDevice();
        _reopenAt = Environment.TickCount64 + ReopenMs;
        Problem = reason;
        if (_deviceLosses > 1) throw Broken($"显卡驱动又重置了一次：{reason}");
        AgentLog.Warn($"桌面复制：显卡驱动重置了（{reason}），1 秒后重建");
    }

    private void CloseDevice()
    {
        Com.Release(ref _duplication);
        _converter?.Dispose();
        _converter = null;
        Com.Release(ref _desktop);
        _desktopDesc = default;
        _hasImage = false;
        _imageChanged = false;
        Com.Release(ref _context);
        Com.Release(ref _device);
        Com.Release(ref _output1);
        Com.Release(ref _output5);
        _dirty.Clear();
        _allDirty = true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CloseDevice();
    }

    /// <summary>一块接在桌面上的显卡输出：名字（\\.\DISPLAY1）、所在显卡的名字和厂商号。</summary>
    internal readonly record struct OutputInfo(string DeviceName, string Adapter, int VendorId);

    /// <summary>列出接在桌面上的显卡输出（不建设备，便宜）。</summary>
    internal static List<OutputInfo> Outputs()
    {
        var outputs = new List<OutputInfo>();
        Enumerate((_, ad, _, od) =>
        {
            outputs.Add(new OutputInfo(Com.Text(od.DeviceName, 32), Com.Text(ad.Description, 128).Trim(), (int)ad.VendorId));
            return false;
        });
        return outputs;
    }

    /// <summary>
    /// 逐个看接在桌面上的显卡输出；visit 返回 true 时停下，那一对 adapter / output 指针归调用方（要它 Release），其余的这里都放掉。
    /// </summary>
    private static bool Enumerate(Func<IntPtr, NativeAdapterDesc1, IntPtr, NativeOutputDesc, bool> visit)
    {
        CaptureErrors.Check(DxgiNative.CreateFactory(out var factory), "CreateDXGIFactory1");
        try
        {
            for (var a = 0; a < 16; a++)
            {
                var hr = DxgiNative.EnumAdapters1(factory, a, out var adapter);
                if (hr == DxgiNative.DXGI_ERROR_NOT_FOUND) break;
                CaptureErrors.Check(hr, "EnumAdapters1");
                var keep = false;
                try
                {
                    if (DxgiNative.GetDesc1(adapter, out var adapterDesc) < 0) continue;
                    for (var o = 0; o < 16 && !keep; o++)
                    {
                        if (DxgiNative.EnumOutputs(adapter, o, out var output) < 0) break;
                        try
                        {
                            if (DxgiNative.GetOutputDesc(output, out var outputDesc) >= 0 && outputDesc.AttachedToDesktop != 0)
                                keep = visit(adapter, adapterDesc, output, outputDesc);
                        }
                        finally
                        {
                            if (!keep) Com.Release(ref output);
                        }
                    }
                    if (keep) return true;
                }
                finally
                {
                    if (!keep) Com.Release(ref adapter);
                }
            }
            return false;
        }
        finally
        {
            Com.Release(ref factory);
        }
    }
}

namespace PrivateChat.Agent.Video;

/// <summary>
/// 选抓屏方式（见 docs/DESIGN.md）。PRIVATECHAT_AGENT_CAPTURE：
/// - auto（默认）：最近一次硬件探测里整条桌面复制流程（建设备、复制、显卡上缩放转 NV12、读回、画指针）验证过的显示器用桌面复制，别的用 GDI；
/// - dxgi：不管探测结果，都先试桌面复制（测试、诊断用）；
/// - gdi：只用 GDI（和以前一样，不碰 DXGI）。
/// 桌面复制用着出了错（不是锁屏、桌面切换这类暂时的情况）就在这次远程里一直用 GDI，名字里写着原因，远程不断。
/// </summary>
public static class ScreenCaptures
{
    public const string SwitchVariable = "PRIVATECHAT_AGENT_CAPTURE";

    internal enum CaptureMode { Auto, Dxgi, Gdi }

    /// <summary>validatedOutputs：最近一次探测里整条桌面复制流程验证过的显示器（\\.\DISPLAYn）。构造要便宜：第一次 Capture 才建 D3D 设备。</summary>
    public static IFrameSource Create(IReadOnlySet<string> validatedOutputs)
        => new ScreenCapture(Mode(Environment.GetEnvironmentVariable(SwitchVariable)), validatedOutputs);

    /// <summary>环境变量的值 → 方式；没设、认不出来都是 auto。</summary>
    internal static CaptureMode Mode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "dxgi" => CaptureMode.Dxgi,
        "gdi" => CaptureMode.Gdi,
        _ => CaptureMode.Auto,
    };
}

/// <summary>抓屏源知道某块显示器接在哪个厂商的显卡上（给编码器挑同一张卡上的）。</summary>
public interface IAdapterAwareSource
{
    int AdapterVendorId(ScreenInfo screen);
}

/// <summary>
/// 真屏幕：每块显示器按规则选桌面复制或 GDI（<see cref="ScreenCaptures"/>），桌面复制坏了就退回 GDI（这个对象以后都用 GDI）。
/// <see cref="Name"/> 是上一次截屏用的方式（还没截过时按 <see cref="AdapterVendorId"/> 问到的那块显示器猜），
/// 不能用桌面复制时括号里写原因："GDI（桌面复制不能用：DuplicateOutput1：DXGI_ERROR_UNSUPPORTED（0x887A0004））"。
/// 名字只在变了的时候换成新的字符串（采集循环可以每帧比一下引用，变了就记进日志和遥测）。
/// 帧的格式跟着变：桌面复制给 NV12（带变化提示），GDI 给 BGRA。只在一个线程上用（采集循环）。
/// </summary>
internal sealed class ScreenCapture(ScreenCaptures.CaptureMode mode, IReadOnlySet<string> validatedOutputs) : IFrameSource, IAdapterAwareSource
{
    private const string NotValidated = "GDI（探测还没验证过这块显示器的桌面复制）";

    private readonly GdiScreenSource _gdi = new();
    private DxgiScreenSource? _dxgi;
    private string? _broken;
    private bool _usingDxgi;
    private string _name = GdiScreenSource.GdiName;

    public IReadOnlyList<ScreenInfo> Screens() => _gdi.Screens();

    public bool IsRealScreen => true;

    public string Name => _name;

    /// <summary>用着桌面复制、而且它被拒了（锁屏、UAC）。GDI 看不出来（截到的是黑屏），采集循环另外查输入桌面。</summary>
    public bool SecureDesktop => _usingDxgi && _dxgi is { } dxgi && dxgi.SecureDesktop;

    public int AdapterVendorId(ScreenInfo screen)
    {
        if (!WantDxgi(screen, out var reason))
        {
            // 不用桌面复制时不碰 DXGI（和以前的 GDI 截屏一样）
            if (!_usingDxgi) SetName(reason ?? GdiScreenSource.GdiName);
            return 0;
        }
        try
        {
            _dxgi ??= new DxgiScreenSource();
            var adapter = screen.DeviceName is { } name ? _dxgi.Adapter(name) : null;
            if (!_usingDxgi) SetName(adapter is { } a ? $"桌面复制（{a.Adapter}）" : "桌面复制");
            return _dxgi.AdapterVendorId(screen);
        }
        catch (Exception)
        {
            return 0;
        }
    }

    public CapturedFrame Capture(ScreenInfo screen, int width, int height)
    {
        if (WantDxgi(screen, out var reason))
        {
            try
            {
                _dxgi ??= new DxgiScreenSource();
                var frame = _dxgi.Capture(screen, width, height);
                _usingDxgi = true;
                SetName(_dxgi.Name);
                return frame;
            }
            catch (Exception ex)
            {
                // 不管是哪种错，桌面复制在这次远程里都不再用了：GDI 总能截
                _broken = ex is CaptureFallbackException ? ex.Message : $"{ex.GetType().Name}：{ex.Message}";
                AgentLog.Warn($"桌面复制不能用了，这次远程改用 GDI 截屏：{_broken}");
                try { _dxgi?.Dispose(); }
                catch (Exception) { }
                _dxgi = null;
                reason = Unavailable(_broken);
            }
        }
        _usingDxgi = false;
        SetName(reason ?? GdiScreenSource.GdiName);
        return _gdi.Capture(screen, width, height);
    }

    /// <summary>这块显示器用不用桌面复制；不用时 reason 是要显示的名字（选了 gdi 时 null，就叫"GDI"）。</summary>
    private bool WantDxgi(ScreenInfo screen, out string? reason)
    {
        reason = null;
        if (mode == ScreenCaptures.CaptureMode.Gdi) return false;
        if (_broken is not null)
        {
            reason = Unavailable(_broken);
            return false;
        }
        if (screen.DeviceName is not { } name)
        {
            reason = "GDI（这块显示器没有设备名）";
            return false;
        }
        if (mode == ScreenCaptures.CaptureMode.Dxgi || validatedOutputs.Contains(name)
            || validatedOutputs.Any(o => string.Equals(o, name, StringComparison.OrdinalIgnoreCase)))
            return true;
        reason = NotValidated;
        return false;
    }

    private static string Unavailable(string why) => $"GDI（桌面复制不能用：{why}）";

    private void SetName(string name)
    {
        if (!string.Equals(name, _name, StringComparison.Ordinal)) _name = name;
    }

    public void Dispose()
    {
        try { _dxgi?.Dispose(); }
        catch (Exception) { }
        _gdi.Dispose();
    }
}

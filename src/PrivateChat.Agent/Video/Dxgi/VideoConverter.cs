using PrivateChat.Agent.Telemetry;

namespace PrivateChat.Agent.Video;

/// <summary>桌面复制这条路走不通了（不支持、被别的程序占满、驱动不配合……）：这次远程改用 GDI 截屏。消息是给日志、遥测看的原因。</summary>
internal sealed class CaptureFallbackException(string message) : Exception(message);

/// <summary>显卡驱动重置了 / 显卡被移除（DXGI_ERROR_DEVICE_REMOVED 等）：把设备整个重建一次再试。</summary>
internal sealed class CaptureDeviceLostException(string message) : Exception(message);

internal static class CaptureErrors
{
    /// <summary>HRESULT 失败时抛：显卡掉了是 <see cref="CaptureDeviceLostException"/>，别的都是 <see cref="CaptureFallbackException"/>。</summary>
    public static void Check(int hr, string what)
    {
        if (hr >= 0) return;
        var text = $"{what}：{HResults.Describe(hr)}";
        throw DeviceLost(hr) ? new CaptureDeviceLostException(text) : new CaptureFallbackException(text);
    }

    public static bool DeviceLost(int hr) => hr is DxgiNative.DXGI_ERROR_DEVICE_REMOVED or DxgiNative.DXGI_ERROR_DEVICE_RESET
        or DxgiNative.DXGI_ERROR_DEVICE_HUNG or DxgiNative.DXGI_ERROR_DRIVER_INTERNAL_ERROR;
}

/// <summary>
/// 在显卡上把桌面画面（BGRA，显示器没转过的方向）转方向、缩放、转 NV12（ID3D11VideoProcessor），再拷回内存，排成紧凑的 NV12。
/// 颜色和 <see cref="H264Encoder"/> 输入类型声明的一致：输入是全范围（0–255）RGB，输出 BT.709、有限范围（16–235）。
/// 关掉驱动的自动画质处理（降噪、锐化之类会改文字的边缘）。缩放用驱动的"画质优先"（D3D11_VIDEO_USAGE_OPTIMAL_QUALITY）。
/// 一步最多缩小 2 倍（<see cref="CaptureTiles.MaxPassRatio"/>：NVIDIA 缩得更多时隔点取样，细线会整条不见）：缩得更多时
/// 先 BGRA → BGRA 缩一半（可能几次，见 <see cref="CaptureTiles.Passes"/>），最后一步再转方向、缩放、转 NV12。
/// 用的设备和上下文归 <see cref="DxgiScreenSource"/>，这里只管自己建的东西。只在采集线程上用。
/// </summary>
internal sealed class VideoConverter : IDisposable
{
    /// <summary>
    /// 一步：一个视频处理器从 Input 读、写进 Output（中间几步是自己建的 BGRA 纹理，最后一步是 NV12 纹理）。
    /// 中间几步的 Output 就是下一步的输入。
    /// </summary>
    private sealed class Pass
    {
        public IntPtr Enumerator;
        public IntPtr Processor;
        public IntPtr InputView;
        public IntPtr Output;
        public IntPtr OutputView;

        public void Release()
        {
            Com.Release(ref OutputView);
            Com.Release(ref InputView);
            Com.Release(ref Processor);
            Com.Release(ref Enumerator);
            Com.Release(ref Output);
        }
    }

    /// <summary>建纹理用的设备、立即上下文（归 <see cref="DxgiScreenSource"/> 管，这里不释放）。</summary>
    private readonly IntPtr _device;
    private readonly IntPtr _context;
    private IntPtr _videoDevice;
    private IntPtr _videoContext;
    private IntPtr _videoContext1;
    private readonly List<Pass> _passes = [];
    private IntPtr _staging;
    private bool _singlePassWarned;
    private (IntPtr Source, uint SourceWidth, uint SourceHeight, int Format, int Rotation, int Width, int Height) _preparedFor;

    /// <summary>上一次转换分了几步（探测、自检看的；还没转过是 0）。</summary>
    public int PassCount => _passes.Count;

    public VideoConverter(IntPtr device, IntPtr context)
    {
        _device = device;
        _context = context;
        _videoDevice = Com.Query(device, DxgiNative.IID_ID3D11VideoDevice);
        _videoContext = Com.Query(context, DxgiNative.IID_ID3D11VideoContext);
        if (_videoDevice == 0 || _videoContext == 0)
        {
            Dispose();
            throw new CaptureFallbackException("显卡驱动没有视频处理器（ID3D11VideoDevice / ID3D11VideoContext）");
        }
        // Windows 8.1 起都有；能用的话按 DXGI 的色彩空间设一遍（比旧的位域说得更清楚）
        _videoContext1 = Com.Query(context, DxgiNative.IID_ID3D11VideoContext1);
    }

    /// <summary>source（<paramref name="sourceDesc"/>）→ nv12（width × height × 1.5 字节，紧凑排列）。rotation：DXGI_MODE_ROTATION。</summary>
    public void Convert(IntPtr source, in Texture2DDesc sourceDesc, int rotation, int width, int height, Span<byte> nv12)
    {
        if (nv12.Length < width * (height + height / 2L)) throw new CaptureFallbackException($"NV12 缓冲区 {nv12.Length} 字节，放不下 {width}x{height}");
        Prepare(source, sourceDesc, rotation, width, height);
        // 立即上下文按顺序执行：后一步读前一步写好的纹理
        for (var i = 0; i < _passes.Count; i++)
        {
            var pass = _passes[i];
            CaptureErrors.Check(D3D11Native.VideoProcessorBlt(_videoContext, pass.Processor, pass.OutputView, pass.InputView),
                _passes.Count == 1 ? "VideoProcessorBlt" : $"VideoProcessorBlt（第 {i + 1}/{_passes.Count} 步）");
        }
        D3D11Native.CopyResource(_context, _staging, _passes[^1].Output);
        CaptureErrors.Check(D3D11Native.Map(_context, _staging, D3D11Native.MapRead, out var mapped), "读回 NV12（Map）");
        try
        {
            mapped.CopyNv12To(width, height, nv12);
        }
        catch (InvalidDataException ex)
        {
            throw new CaptureFallbackException(ex.Message);
        }
        finally
        {
            D3D11Native.Unmap(_context, _staging);
        }
    }

    private void Prepare(IntPtr source, in Texture2DDesc sourceDesc, int rotation, int width, int height)
    {
        var key = (source, sourceDesc.Width, sourceDesc.Height, sourceDesc.Format, rotation, width, height);
        if (_passes.Count > 0 && key == _preparedFor) return;
        ReleaseProcessor();

        var turn = VideoRotation(rotation);
        // 中间几步在显示器没转过的方向上缩（转方向放在最后一步），所以按那个方向的目标尺寸分步
        var (targetWidth, targetHeight) = turn is 1 or 3 ? (height, width) : (width, height);
        var steps = CaptureTiles.Passes((int)sourceDesc.Width, (int)sourceDesc.Height, targetWidth, targetHeight);
        try
        {
            if (!Build(source, sourceDesc, steps, turn, width, height))
            {
                // 这张卡的视频处理器不能输出 BGRA（中间那几步要用）：只好一步缩到底（和以前一样，缩得多时细线可能变淡、不见）
                ReleaseProcessor();
                if (!_singlePassWarned)
                {
                    _singlePassWarned = true;
                    AgentLog.Warn($"桌面复制：显卡的视频处理器不能输出 BGRA，{sourceDesc.Width}x{sourceDesc.Height} → {width}x{height} 只能一步缩（细线可能看不清）");
                }
                Build(source, sourceDesc, [(targetWidth, targetHeight)], turn, width, height);
            }
        }
        catch (Exception)
        {
            ReleaseProcessor();
            throw;
        }
        _preparedFor = key;
    }

    /// <summary>
    /// 按 steps（显示器没转过的方向，最后一个是目标尺寸）一步一个视频处理器建好：中间几步 BGRA → BGRA，
    /// 最后一步转方向、转 NV12（width × height，人看到的方向）。中间几步不能输出 BGRA 时返回 false（建了一半的由调用方放掉）。
    /// </summary>
    private bool Build(IntPtr source, in Texture2DDesc sourceDesc, (int Width, int Height)[] steps, int turn, int width, int height)
    {
        var input = source;
        var (inputWidth, inputHeight, inputFormat) = (sourceDesc.Width, sourceDesc.Height, sourceDesc.Format);
        for (var i = 0; i < steps.Length; i++)
        {
            var last = i == steps.Length - 1;
            var (outputWidth, outputHeight) = last ? ((uint)width, (uint)height) : ((uint)steps[i].Width, (uint)steps[i].Height);
            var outputFormat = last ? DxgiNative.FormatNv12 : DxgiNative.FormatB8G8R8A8;
            var step = steps.Length == 1 ? "" : $"（第 {i + 1}/{steps.Length} 步）";
            var pass = new Pass();
            _passes.Add(pass);

            var content = new VideoProcessorContentDesc
            {
                InputFrameFormat = D3D11Native.FrameFormatProgressive,
                InputRateNumerator = 60,
                InputRateDenominator = 1,
                InputWidth = inputWidth,
                InputHeight = inputHeight,
                OutputRateNumerator = 60,
                OutputRateDenominator = 1,
                OutputWidth = outputWidth,
                OutputHeight = outputHeight,
                Usage = D3D11Native.VideoUsageOptimalQuality,
            };
            CaptureErrors.Check(D3D11Native.CreateVideoProcessorEnumerator(_videoDevice, content, out pass.Enumerator), $"CreateVideoProcessorEnumerator{step}");
            CaptureErrors.Check(D3D11Native.CheckVideoProcessorFormat(pass.Enumerator, inputFormat, out var inputSupport), $"CheckVideoProcessorFormat（BGRA 输入）{step}");
            if ((inputSupport & D3D11Native.FormatSupportInput) == 0) throw new CaptureFallbackException($"显卡的视频处理器不接受 BGRA 输入{step}");
            CaptureErrors.Check(D3D11Native.CheckVideoProcessorFormat(pass.Enumerator, outputFormat, out var outputSupport), $"CheckVideoProcessorFormat（{(last ? "NV12" : "BGRA")} 输出）{step}");
            if ((outputSupport & D3D11Native.FormatSupportOutput) == 0)
            {
                if (!last) return false;
                throw new CaptureFallbackException("显卡的视频处理器不能输出 NV12");
            }
            if (last && turn != 0)
            {
                CaptureErrors.Check(D3D11Native.GetVideoProcessorCaps(pass.Enumerator, out var caps), "GetVideoProcessorCaps");
                if ((caps.FeatureCaps & D3D11Native.FeatureCapsRotation) == 0) throw new CaptureFallbackException("显示器转了方向，这张显卡的视频处理器不支持旋转");
            }
            CaptureErrors.Check(D3D11Native.CreateVideoProcessor(_videoDevice, pass.Enumerator, out pass.Processor), $"CreateVideoProcessor{step}");
            CaptureErrors.Check(D3D11Native.CreateVideoProcessorInputView(_videoDevice, input, pass.Enumerator, out pass.InputView), $"CreateVideoProcessorInputView{step}");

            var target = new Texture2DDesc
            {
                Width = outputWidth,
                Height = outputHeight,
                MipLevels = 1,
                ArraySize = 1,
                Format = outputFormat,
                SampleCount = 1,
                Usage = D3D11Native.UsageDefault,
                // 中间的画面还要给下一步当输入（和桌面纹理一样的用法）
                BindFlags = last ? D3D11Native.BindRenderTarget : D3D11Native.BindRenderTarget | D3D11Native.BindShaderResource,
            };
            CaptureErrors.Check(D3D11Native.CreateTexture2D(_device, target, out pass.Output), $"建 {outputWidth}x{outputHeight} 的 {(last ? "NV12" : "BGRA")} 纹理");
            CaptureErrors.Check(D3D11Native.CreateVideoProcessorOutputView(_videoDevice, pass.Output, pass.Enumerator, out pass.OutputView), $"CreateVideoProcessorOutputView{step}");
            if (last)
            {
                var staging = target with { Usage = D3D11Native.UsageStaging, BindFlags = 0, CpuAccessFlags = D3D11Native.CpuAccessRead };
                CaptureErrors.Check(D3D11Native.CreateTexture2D(_device, staging, out _staging), $"建 {width}x{height} 的 NV12 暂存纹理");
            }

            var all = new NativeRect(0, 0, (int)inputWidth, (int)inputHeight);
            var frame = new NativeRect(0, 0, (int)outputWidth, (int)outputHeight);
            D3D11Native.SetStreamFrameFormat(_videoContext, pass.Processor, D3D11Native.FrameFormatProgressive);
            D3D11Native.SetStreamSourceRect(_videoContext, pass.Processor, all);
            D3D11Native.SetStreamDestRect(_videoContext, pass.Processor, frame);
            D3D11Native.SetOutputTargetRect(_videoContext, pass.Processor, frame);
            // 中间几步 RGB 进、RGB 出（全范围，不转颜色）；最后一步转成 BT.709 有限范围的 YCbCr
            D3D11Native.SetStreamColorSpace(_videoContext, pass.Processor, D3D11Native.ColorSpaceRgbFull);
            D3D11Native.SetOutputColorSpace(_videoContext, pass.Processor, last ? D3D11Native.ColorSpaceYCbCr709Studio : D3D11Native.ColorSpaceRgbFull);
            if (_videoContext1 != 0)
            {
                D3D11Native.SetStreamColorSpace1(_videoContext1, pass.Processor, D3D11Native.ColorSpaceRgbFullG22P709);
                D3D11Native.SetOutputColorSpace1(_videoContext1, pass.Processor, last ? D3D11Native.ColorSpaceYCbCrStudioG22LeftP709 : D3D11Native.ColorSpaceRgbFullG22P709);
            }
            D3D11Native.SetStreamAutoProcessingMode(_videoContext, pass.Processor, false);
            if (last && turn != 0) D3D11Native.SetStreamRotation(_videoContext, pass.Processor, true, turn);

            input = pass.Output;
            (inputWidth, inputHeight, inputFormat) = (outputWidth, outputHeight, outputFormat);
        }
        return true;
    }

    /// <summary>DXGI_MODE_ROTATION（2 = 90°、3 = 180°、4 = 270°）→ D3D11_VIDEO_PROCESSOR_ROTATION（1、2、3，都是顺时针）；不转是 0。</summary>
    private static int VideoRotation(int rotation) => rotation is >= 2 and <= 4 ? rotation - 1 : 0;

    private void ReleaseProcessor()
    {
        foreach (var pass in _passes) pass.Release();
        _passes.Clear();
        Com.Release(ref _staging);
        _preparedFor = default;
    }

    public void Dispose()
    {
        ReleaseProcessor();
        Com.Release(ref _videoContext1);
        Com.Release(ref _videoContext);
        Com.Release(ref _videoDevice);
    }
}

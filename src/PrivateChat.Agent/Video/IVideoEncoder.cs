namespace PrivateChat.Agent.Video;

// 这个文件不碰 Windows（测试项目把它直接编进去，VideoEncodersTests 用到 ValidatedEncoder）。

/// <summary>编好的一帧（Annex B）；IsKey：IDR 帧，前面总带着 SPS / PPS。</summary>
public readonly record struct EncodedFrame(byte[] Data, bool IsKey);

/// <summary>
/// H.264 编码器：Windows 自带的软件编码器（H264Encoder）或显卡的硬件编码器（HardwareH264Encoder），由 VideoEncoders.Create 挑。
/// 只在采集线程上用；Encode 返回的就是这一帧的输出（或者这一帧没有输出时 null），采集循环据此记账。
/// </summary>
public interface IVideoEncoder : IDisposable
{
    int Width { get; }
    int Height { get; }
    int Fps { get; }

    /// <summary>能逐帧定质量（<see cref="SetQuality"/> 有效）；false 时采集循环按码率（<see cref="SetBitrate"/>）走。</summary>
    bool QualityMode { get; }

    /// <summary>"Microsoft H.264（软件）" / "&lt;MFT 友好名&gt;（硬件）"。</summary>
    string Name { get; }

    bool IsHardware { get; }

    /// <summary>下一帧编成关键帧。</summary>
    void RequestKeyFrame();

    /// <summary>之后的帧用这个质量（0–100，和软件编码器一样的刻度）。</summary>
    void SetQuality(int quality);

    /// <summary>按码率编时中途改码率。</summary>
    void SetBitrate(int kbps);

    /// <summary>编一帧紧凑排列的 NV12（宽 × 高 × 1.5 字节）。硬件编码器出错时抛 <see cref="EncoderFailedException"/>（调用方换软件编码器）。</summary>
    EncodedFrame? Encode(ReadOnlySpan<byte> nv12, TimeSpan timestamp);
}

/// <summary>硬件编码器用不了了（驱动出错、超时）：调用方换软件编码器接着编，这次远程不断。</summary>
public sealed class EncoderFailedException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>最近一次探测里试编成功的硬件编码器。</summary>
public sealed record ValidatedEncoder(string Name, int VendorId, bool QualityMode, bool PerFrameQp);

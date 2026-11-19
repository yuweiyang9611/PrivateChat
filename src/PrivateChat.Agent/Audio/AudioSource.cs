using System.Diagnostics;
using Concentus;
using Concentus.Enums;

namespace PrivateChat.Agent.Audio;

/// <summary>声音统一成这个格式再编码：48 kHz、双声道、16 位、左右交错。</summary>
public static class AudioFormat
{
    public const int SampleRate = 48000;
    public const int Channels = 2;
    /// <summary>一个声音包 20 ms。</summary>
    public const int FrameSamples = SampleRate / 50;
    public const int FrameMs = 20;
}

/// <summary>电脑正在播放的声音。</summary>
public interface IAudioSource : IDisposable
{
    /// <summary>
    /// 把上次读之后新到的声音写进 buffer（左右交错），返回写了多少个采样点（每点左右两个数）。
    /// 没有程序在出声时返回 0。设备出了问题（拔了耳机、换了默认设备）抛异常，调用方过一会儿重新打开。
    /// </summary>
    int Read(Span<short> buffer);
}

/// <summary>测试用（--source pattern）：一直响着的 440 Hz 正弦波，不碰声卡。按真实时间出数据。</summary>
public sealed class TestToneSource : IAudioSource
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _produced;

    public int Read(Span<short> buffer)
    {
        var due = _clock.ElapsedMilliseconds * AudioFormat.SampleRate / 1000 - _produced;
        var count = (int)Math.Min(due, buffer.Length / AudioFormat.Channels);
        for (var i = 0; i < count; i++)
        {
            var value = (short)(Math.Sin((_produced + i) * 2 * Math.PI * 440 / AudioFormat.SampleRate) * 8000);
            buffer[i * 2] = value;
            buffer[i * 2 + 1] = value;
        }
        _produced += count;
        return count;
    }

    public void Dispose() { }
}

/// <summary>
/// 20 ms 一包的 Opus 编码（Concentus：libopus 的纯托管移植，不带原生库）。
/// Opus 是为实时传输做的：一包 20 ms、几乎没有额外延迟，32–64 kbps 就够听音乐；浏览器的 AudioDecoder 都能解。
/// </summary>
public sealed class OpusPacketizer
{
    /// <summary>一个包最多这么大（64 kbps 时一包约 160 字节，留足余量）。</summary>
    public const int MaxPacketBytes = 1275;

    private readonly IOpusEncoder _encoder;
    private int _bitrate;

    public OpusPacketizer(int bitrate)
    {
        _encoder = OpusCodecFactory.CreateEncoder(AudioFormat.SampleRate, AudioFormat.Channels, OpusApplication.OPUS_APPLICATION_AUDIO);
        _encoder.Complexity = 5;
        _encoder.UseVBR = true;
        Bitrate = bitrate;
    }

    public int Bitrate
    {
        get => _bitrate;
        set
        {
            if (value == _bitrate) return;
            _bitrate = value;
            _encoder.Bitrate = value;
        }
    }

    /// <summary>编一包：pcm 是 20 ms 的声音（960 点 × 2 声道），返回写进 packet 的字节数。</summary>
    public int Encode(ReadOnlySpan<short> pcm, Span<byte> packet)
        => _encoder.Encode(pcm[..(AudioFormat.FrameSamples * AudioFormat.Channels)], AudioFormat.FrameSamples, packet, packet.Length);
}

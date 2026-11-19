using System.Runtime.InteropServices;

namespace PrivateChat.Agent.Audio;

/// <summary>
/// 电脑正在播放的声音：WASAPI 环回采集（loopback）默认的播放设备——喇叭 / 耳机里出什么，这里就录到什么，不经过麦克风。
/// 先请系统直接转成 48 kHz 双声道 16 位（AUTOCONVERTPCM）；不行就按设备自己的混音格式采，自己转。
/// 没有程序在出声时系统不给数据，<see cref="Read"/> 返回 0，所以安静的时候不占流量。
/// 默认播放设备换了（插上耳机、切到蓝牙）要重新打开：每两秒看一眼，变了就抛出来让调用方重开。
/// 只在一个线程上用（声音线程，多线程套间）。
/// </summary>
public sealed class WasapiLoopback : IAudioSource
{
    private const int EDataFlowRender = 0;
    private const int ERoleConsole = 0;
    private const int ClsCtxAll = 23;
    private const int ShareModeShared = 0;
    private const uint StreamFlagsLoopback = 0x00020000;
    private const uint StreamFlagsAutoConvertPcm = 0x80000000;
    private const uint StreamFlagsSrcDefaultQuality = 0x08000000;
    private const int BufferFlagsSilent = 2;
    private const long BufferDuration = 200 * 10_000; // 200 ms，单位 100 纳秒
    private const ushort FormatPcm = 1;
    private const ushort FormatFloat = 3;
    private const ushort FormatExtensible = 0xFFFE;
    private static readonly Guid SubFormatFloat = new("00000003-0000-0010-8000-00aa00389b71");
    private static readonly Guid SubFormatPcm = new("00000001-0000-0010-8000-00aa00389b71");

    private readonly IMMDeviceEnumerator _enumerator;
    private readonly IAudioClient _client;
    private readonly IAudioCaptureClient _capture;
    private readonly string _deviceId;
    private readonly bool _converted;
    private readonly int _sourceRate;
    private readonly int _sourceChannels;
    private readonly int _sourceBits;
    private readonly bool _sourceFloat;
    private long _lastDeviceCheck = Environment.TickCount64;
    // 自己转采样率时的状态：上一块最后一个采样点、下一个输出点在输入里的位置（小数）
    private float _lastLeft;
    private float _lastRight;
    private double _position;
    private bool _disposed;

    /// <summary>采到的格式，写日志用。</summary>
    public string Description { get; }

    public WasapiLoopback()
    {
        var type = Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"), throwOnError: true)!;
        _enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(type)!;
        Check(_enumerator.GetDefaultAudioEndpoint(EDataFlowRender, ERoleConsole, out var device), "找不到播放设备");
        try
        {
            _deviceId = IdOf(device);
            var iid = typeof(IAudioClient).GUID;
            Check(device.Activate(in iid, ClsCtxAll, IntPtr.Zero, out var activated), "打开播放设备");
            _client = (IAudioClient)activated;

            var wanted = new WaveFormatEx
            {
                FormatTag = FormatPcm,
                Channels = AudioFormat.Channels,
                SamplesPerSec = AudioFormat.SampleRate,
                BitsPerSample = 16,
                BlockAlign = AudioFormat.Channels * 2,
                AvgBytesPerSec = AudioFormat.SampleRate * AudioFormat.Channels * 2,
            };
            int hr;
            unsafe
            {
                hr = _client.Initialize(ShareModeShared, StreamFlagsLoopback | StreamFlagsAutoConvertPcm | StreamFlagsSrcDefaultQuality, BufferDuration, 0, (IntPtr)(&wanted), IntPtr.Zero);
            }
            if (hr >= 0)
            {
                _converted = true;
                (_sourceRate, _sourceChannels, _sourceBits) = (AudioFormat.SampleRate, AudioFormat.Channels, 16);
                Description = "48 kHz 双声道（系统转换）";
            }
            else
            {
                // 这个设备 / 这版系统不肯转：重新拿一个客户端，按它自己的混音格式采，自己转
                Marshal.ReleaseComObject(_client);
                Check(device.Activate(in iid, ClsCtxAll, IntPtr.Zero, out activated), "打开播放设备");
                _client = (IAudioClient)activated;
                Check(_client.GetMixFormat(out var mix), "读取播放设备的格式");
                try
                {
                    var format = Marshal.PtrToStructure<WaveFormatEx>(mix);
                    var tag = format.FormatTag;
                    if (tag == FormatExtensible && format.Size >= 22)
                    {
                        var sub = Marshal.PtrToStructure<Guid>(mix + 24);
                        tag = sub == SubFormatFloat ? FormatFloat : sub == SubFormatPcm ? FormatPcm : (ushort)0;
                    }
                    _sourceFloat = tag == FormatFloat && format.BitsPerSample == 32;
                    if (!_sourceFloat && !(tag == FormatPcm && format.BitsPerSample == 16))
                        throw new NotSupportedException($"播放设备的声音格式不认识（标记 {format.FormatTag}，{format.BitsPerSample} 位）");
                    (_sourceRate, _sourceChannels, _sourceBits) = ((int)format.SamplesPerSec, format.Channels, format.BitsPerSample);
                    Check(_client.Initialize(ShareModeShared, StreamFlagsLoopback, BufferDuration, 0, mix, IntPtr.Zero), "开始采集声音");
                    Description = $"{_sourceRate} Hz {_sourceChannels} 声道 {_sourceBits} 位{(_sourceFloat ? "浮点" : "")}（自己转换，系统转换失败 0x{hr:X8}）";
                }
                finally
                {
                    Marshal.FreeCoTaskMem(mix);
                }
            }

            var captureIid = typeof(IAudioCaptureClient).GUID;
            Check(_client.GetService(in captureIid, out var service), "取采集接口");
            _capture = (IAudioCaptureClient)service;
            Check(_client.Start(), "开始采集声音");
        }
        catch
        {
            if (_client is not null) Marshal.ReleaseComObject(_client);
            Marshal.ReleaseComObject(_enumerator);
            throw;
        }
        finally
        {
            Marshal.ReleaseComObject(device);
        }
    }

    public int Read(Span<short> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Environment.TickCount64 - _lastDeviceCheck > 2000)
        {
            _lastDeviceCheck = Environment.TickCount64;
            if (CurrentDeviceId() != _deviceId) throw new InvalidOperationException("默认播放设备换了");
        }

        var written = 0;
        var capacity = buffer.Length / AudioFormat.Channels;
        while (true)
        {
            Check(_capture.GetNextPacketSize(out var next), "读取声音");
            if (next == 0) break;
            // 留够地方再取（自己转采样率时一块输入最多变成这么多输出），取出来的必须整块用掉
            var worst = _converted ? next : (int)((long)next * AudioFormat.SampleRate / _sourceRate) + 2;
            if (written + worst > capacity) break;
            Check(_capture.GetBuffer(out var data, out var frames, out var flags, out _, out _), "读取声音");
            try
            {
                var target = buffer[(written * AudioFormat.Channels)..];
                written += (flags & BufferFlagsSilent) != 0 ? Silence(frames, target) : Convert(data, frames, target);
            }
            finally
            {
                _capture.ReleaseBuffer(frames);
            }
        }
        return written;
    }

    private int Silence(int frames, Span<short> target)
    {
        var count = _converted ? frames : (int)((long)frames * AudioFormat.SampleRate / _sourceRate);
        target[..(count * AudioFormat.Channels)].Clear();
        _lastLeft = _lastRight = 0;
        return count;
    }

    private unsafe int Convert(IntPtr data, int frames, Span<short> target)
    {
        if (_converted)
        {
            new ReadOnlySpan<short>((void*)data, frames * AudioFormat.Channels).CopyTo(target);
            return frames;
        }

        // 取前两个声道（单声道就左右一样），线性插值到 48 kHz
        var step = (double)_sourceRate / AudioFormat.SampleRate;
        var written = 0;
        // _position：相对这一块开头的位置，-1..0 之间表示落在上一块最后一点和这一块第一点之间
        while (_position < frames - 1)
        {
            var index = (int)Math.Floor(_position);
            var t = (float)(_position - index);
            var (l0, r0) = At(data, index);
            var (l1, r1) = At(data, index + 1);
            target[written * 2] = ToShort(l0 + (l1 - l0) * t);
            target[written * 2 + 1] = ToShort(r0 + (r1 - r0) * t);
            written++;
            _position += step;
        }
        (_lastLeft, _lastRight) = At(data, frames - 1);
        _position -= frames;
        return written;
    }

    /// <summary>这一块里第 i 个采样点的左右声道（-1 = 上一块的最后一点）。</summary>
    private unsafe (float Left, float Right) At(IntPtr data, int i)
    {
        if (i < 0) return (_lastLeft, _lastRight);
        var o = i * _sourceChannels;
        var floats = (float*)data;
        var shorts = (short*)data;
        var left = _sourceFloat ? floats[o] : shorts[o] / 32768f;
        var right = _sourceChannels < 2 ? left : _sourceFloat ? floats[o + 1] : shorts[o + 1] / 32768f;
        return (left, right);
    }

    private static short ToShort(float v) => (short)Math.Clamp(v * 32767f, short.MinValue, short.MaxValue);

    private string CurrentDeviceId()
    {
        if (_enumerator.GetDefaultAudioEndpoint(EDataFlowRender, ERoleConsole, out var device) < 0) return "";
        try
        {
            return IdOf(device);
        }
        finally
        {
            Marshal.ReleaseComObject(device);
        }
    }

    private static string IdOf(IMMDevice device)
    {
        if (device.GetId(out var p) < 0 || p == IntPtr.Zero) return "";
        try
        {
            return Marshal.PtrToStringUni(p) ?? "";
        }
        finally
        {
            Marshal.FreeCoTaskMem(p);
        }
    }

    private static void Check(int hr, string what)
    {
        if (hr < 0) throw new InvalidOperationException($"{what}失败：0x{hr:X8}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _client.Stop(); } catch (COMException) { }
        Marshal.ReleaseComObject(_capture);
        Marshal.ReleaseComObject(_client);
        Marshal.ReleaseComObject(_enumerator);
    }

    // ---- WASAPI 的最小 COM 互操作：只声明用得到的方法，其余用无参占位保住 vtable 顺序 ----

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct WaveFormatEx
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSec;
        public uint AvgBytesPerSec;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort Size;
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        void EnumAudioEndpoints();
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(in Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        void OpenPropertyStore();
        [PreserveSig] int GetId(out IntPtr id);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        /// <summary>format 指向一个 WAVEFORMATEX（GetMixFormat 给的那块可能带扩展字段，所以按指针传）。</summary>
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
        void GetBufferSize(); void GetStreamLatency(); void GetCurrentPadding(); void IsFormatSupported();
        [PreserveSig] int GetMixFormat(out IntPtr format);
        void GetDevicePeriod();
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        void Reset(); void SetEventHandle();
        [PreserveSig] int GetService(in Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out int frames, out int flags, out long devicePosition, out long qpcPosition);
        [PreserveSig] int ReleaseBuffer(int frames);
        [PreserveSig] int GetNextPacketSize(out int frames);
    }
}

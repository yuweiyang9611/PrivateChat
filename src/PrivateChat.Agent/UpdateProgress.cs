namespace PrivateChat.Agent;

public enum UpdateStage { CheckingCache, Connecting, Downloading, Verifying, CacheHit, Verified, Installing }

/// <summary>BytesReceived 只计本次从网络读到的字节，复用缓存时为 0。计时均为毫秒。</summary>
public sealed record UpdateProgress(UpdateStage Stage, long BytesReceived, long TotalBytes,
    long? BytesPerSecond = null, long? FirstByteMs = null, long? TransferMs = null, long? VerifyMs = null, long? ElapsedMs = null);

public sealed class UpdateIntegrityException(string message) : IOException(message) { }

/// <summary>下载线程计字节，采样定时器每半秒读一次。在等待下一块数据时也能更新速度。</summary>
internal sealed class UpdateTransferMeter(long totalBytes)
{
    public const int IntervalMs = 500;
    private readonly Lock _gate = new();
    private long _bytes;
    private long? _firstByteMs;
    private long _sampleAt;
    private long _sampleBytes;
    private long? _speed;

    public void Received(int count, long nowMs)
    {
        if (count <= 0) return;
        lock (_gate)
        {
            if (_firstByteMs is null)
            {
                _firstByteMs = nowMs;
                _sampleAt = nowMs;
                _sampleBytes = count;
            }
            _bytes += count;
        }
    }

    public UpdateProgress Sample(long nowMs)
    {
        lock (_gate)
        {
            if (_firstByteMs is not { } first)
                return new(UpdateStage.Connecting, 0, totalBytes, ElapsedMs: nowMs);
            var interval = nowMs - _sampleAt;
            if (interval >= IntervalMs)
            {
                _speed = (_bytes - _sampleBytes) * 1000 / interval;
                _sampleAt = nowMs;
                _sampleBytes = _bytes;
            }
            return new(UpdateStage.Downloading, _bytes, totalBytes, _speed, first, nowMs - first, ElapsedMs: nowMs);
        }
    }
}

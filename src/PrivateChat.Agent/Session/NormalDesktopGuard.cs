using PrivateChat.Contracts.Remote;

namespace PrivateChat.Agent.Session;

/// <summary>普通会话只有在当前控制端连上后确认过普通桌面，才可操作。调用方在同一把锁内更新状态、执行输入或提交状态帧。</summary>
internal sealed class NormalDesktopGuard(RemoteSessionMode mode)
{
    private volatile int _peerGeneration;
    private volatile int _generation;
    private bool? _secure;
    private bool _statusPending;

    public int PeerGeneration => _peerGeneration;
    public int Generation => _generation;

    public void ResetPeer()
    {
        _peerGeneration++;
        _generation++;
        _secure = null;
        _statusPending = false;
    }

    public bool Observe(bool secure)
    {
        if (_secure == secure) return false;
        _secure = secure;
        _generation++;
        _statusPending = true;
        return true;
    }

    public bool CanControl(int generation) =>
        mode == RemoteSessionMode.Control && _secure is false && generation == _generation;

    public void Report(Func<bool, bool> send)
    {
        if (_statusPending && _secure is { } secure && send(secure)) _statusPending = false;
    }
}

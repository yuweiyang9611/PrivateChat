using PrivateChat.Contracts.Remote;

namespace PrivateChat.Agent.Realtime;

/// <summary>同一控制端的一轮接通中，若普通 ICE 检查超时，允许下一次明确使用已配置的 TURN 一次。</summary>
internal sealed class PublisherIceRetry
{
    private enum RetryState { Normal, RelayNext, RelayTried }
    private RetryState _state;

    public void Reset() => _state = RetryState.Normal;

    public bool TakeRelayOnly(IReadOnlyList<RemoteIceServer> servers)
    {
        if (_state != RetryState.RelayNext) return false;
        var hasTurn = HasTurn(servers);
        _state = hasTurn ? RetryState.RelayTried : RetryState.Normal;
        return hasTurn;
    }

    public void IceTimedOut(IReadOnlyList<RemoteIceServer> servers)
    {
        if (_state == RetryState.Normal && HasTurn(servers)) _state = RetryState.RelayNext;
    }

    internal static bool HasTurn(IReadOnlyList<RemoteIceServer> servers) => servers.Any(server => server.Urls.Any(IsTurn));

    internal static bool IsTurn(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "turn" or "turns" && uri.AbsolutePath.Length > 0;
}

using PrivateChat.Agent.Realtime;
using PrivateChat.Contracts.Remote;

namespace PrivateChat.Api.Tests;

public class PublisherIceRetryTests
{
    private static readonly RemoteIceServer[] Servers =
        [new(["stun:example.test:3478"]), new(["turn:example.test:3478?transport=udp"], "test", "test")];

    [Fact]
    public void Publisher_ice_timeout_uses_an_advertised_turn_relay_for_one_retry()
    {
        var retry = new PublisherIceRetry();
        Assert.False(retry.TakeRelayOnly(Servers));
        retry.IceTimedOut(Servers);
        Assert.True(retry.TakeRelayOnly(Servers));
        retry.IceTimedOut(Servers);
        Assert.False(retry.TakeRelayOnly(Servers));
        retry.IceTimedOut(Servers);
        Assert.False(retry.TakeRelayOnly(Servers));
    }

    [Fact]
    public void Losing_turn_or_changing_peer_does_not_carry_a_relay_only_request_forward()
    {
        RemoteIceServer[] stunOnly = [new(["stun:example.test:3478"])];
        var retry = new PublisherIceRetry();
        retry.IceTimedOut(stunOnly);
        Assert.False(retry.TakeRelayOnly(Servers));
        retry.IceTimedOut(Servers);
        Assert.False(retry.TakeRelayOnly(stunOnly));
        Assert.False(retry.TakeRelayOnly(Servers));
        retry.IceTimedOut(Servers);
        retry.Reset();
        Assert.False(retry.TakeRelayOnly(Servers));
    }
}

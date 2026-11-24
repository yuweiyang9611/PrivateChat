using PrivateChat.Agent.Session;
using PrivateChat.Contracts.Remote;

namespace PrivateChat.Api.Tests;

public class NormalDesktopGuardTests
{
    [Theory]
    [InlineData(RemoteSessionMode.Control, true)]
    [InlineData(RemoteSessionMode.View, false)]
    public void Control_requires_the_current_peer_and_a_confirmed_normal_desktop(RemoteSessionMode mode, bool canControl)
    {
        var guard = new NormalDesktopGuard(mode);
        guard.ResetPeer();
        Assert.False(guard.CanControl(guard.Generation));
        guard.Observe(false);
        var beforeLock = guard.Generation;
        Assert.Equal(canControl, guard.CanControl(beforeLock));

        guard.Observe(true);
        Assert.False(guard.CanControl(guard.Generation));
        guard.Observe(false);
        Assert.False(guard.CanControl(beforeLock)); // 锁屏前排队的输入、剪贴板仍然作废
        Assert.Equal(canControl, guard.CanControl(guard.Generation));
        var priorPeer = guard.Generation;
        guard.ResetPeer();
        Assert.False(guard.CanControl(guard.Generation));
        guard.Observe(false);
        Assert.False(guard.CanControl(priorPeer));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_replacement_peer_gets_the_observed_status_even_when_the_desktop_has_not_changed(bool secure)
    {
        var guard = new NormalDesktopGuard(RemoteSessionMode.Control);
        var reports = new List<bool>();
        bool Send(bool value) { reports.Add(value); return true; }
        guard.ResetPeer();
        guard.Observe(secure);
        guard.Report(Send);
        guard.ResetPeer(); // 新标签页直接替换旧标签页，中间没有断开通知
        guard.Report(Send);
        Assert.Single(reports);
        guard.Observe(secure);
        guard.Report(_ => false); // 发送队列满
        guard.Observe(secure);
        guard.Report(Send);
        guard.Report(Send);
        Assert.Equal(new[] { secure, secure }, reports);
    }
}

using PrivateChat.Agent.Session;
using PrivateChat.Contracts.Remote;

namespace PrivateChat.Api.Tests;

/// <summary>
/// 远程时能不能跟到 UAC、锁屏这些界面上（DesktopPolicy）：拿不准就不跟；锁屏不给"电脑前的人点同意"的那种；
/// 服务器只能在本机上限以内收紧；只看模式能看、不能操作。
/// </summary>
public class DesktopPolicyTests
{
    [Theory]
    [InlineData("Default", false, true, DesktopKind.Normal)]
    [InlineData("default", false, true, DesktopKind.Normal)]
    [InlineData("Winlogon", false, true, DesktopKind.SecurePrompt)]
    [InlineData("Screen-saver", false, true, DesktopKind.LockScreen)]
    // Windows 11 锁屏时输入桌面有时还叫 Default：以锁屏标志为准
    [InlineData("Default", true, true, DesktopKind.LockScreen)]
    [InlineData("Winlogon", true, true, DesktopKind.LockScreen)]
    // 问不到锁屏标志：Default 也不敢当成平常桌面
    [InlineData("Default", null, true, DesktopKind.Unknown)]
    [InlineData(null, false, true, DesktopKind.Unknown)]
    [InlineData("Disconnect", false, true, DesktopKind.Unknown)]
    [InlineData("Winlogon", false, false, DesktopKind.SignIn)]
    [InlineData("Default", true, false, DesktopKind.SignIn)]
    public void Classify(string? name, bool? locked, bool loggedOn, DesktopKind expected)
        => Assert.Equal(expected, DesktopPolicy.Classify(name, locked, loggedOn));

    private static DesktopAccess Decide(DesktopKind kind, RemoteConsentType consent = RemoteConsentType.Prompt, RemoteSessionMode mode = RemoteSessionMode.Control,
        SecureDesktopLevel server = SecureDesktopLevel.LockScreen, SecureDesktopLevel local = SecureDesktopLevel.LockScreen, bool allowUac = false)
        => DesktopPolicy.Decide(kind, mode, consent, server, local, allowUac);

    [Fact]
    public void The_normal_desktop_is_always_allowed_and_view_mode_cannot_operate()
    {
        Assert.Equal(new DesktopAccess(true, true), Decide(DesktopKind.Normal, server: SecureDesktopLevel.None, local: SecureDesktopLevel.None));
        Assert.Equal(new DesktopAccess(true, false), Decide(DesktopKind.Normal, mode: RemoteSessionMode.View));
    }

    [Fact]
    public void Uac_with_prompt_consent_needs_the_checkbox()
    {
        Assert.Equal(DesktopAccess.None, Decide(DesktopKind.SecurePrompt, RemoteConsentType.Prompt, allowUac: false));
        Assert.Equal(new DesktopAccess(true, true), Decide(DesktopKind.SecurePrompt, RemoteConsentType.Prompt, allowUac: true));
    }

    [Theory]
    [InlineData(RemoteConsentType.Unattended)]
    [InlineData(RemoteConsentType.Trusted)]
    public void Uac_with_pin_or_trusted_needs_no_checkbox(RemoteConsentType consent)
        => Assert.Equal(new DesktopAccess(true, true), Decide(DesktopKind.SecurePrompt, consent, server: SecureDesktopLevel.Uac, local: SecureDesktopLevel.Uac));

    [Fact]
    public void The_lock_screen_is_never_reached_through_prompt_consent()
        => Assert.Equal(DesktopAccess.None, Decide(DesktopKind.LockScreen, RemoteConsentType.Prompt, allowUac: true));

    [Theory]
    [InlineData(RemoteConsentType.Unattended)]
    [InlineData(RemoteConsentType.Trusted)]
    public void The_lock_screen_needs_lock_screen_level_on_both_sides(RemoteConsentType consent)
    {
        Assert.Equal(new DesktopAccess(true, true), Decide(DesktopKind.LockScreen, consent));
        Assert.Equal(DesktopAccess.None, Decide(DesktopKind.LockScreen, consent, server: SecureDesktopLevel.Uac));
        // 服务器放开了也没用：本机上限只到 UAC
        Assert.Equal(DesktopAccess.None, Decide(DesktopKind.LockScreen, consent, local: SecureDesktopLevel.Uac));
    }

    [Fact]
    public void The_server_cannot_raise_the_local_ceiling()
    {
        Assert.Equal(DesktopAccess.None, Decide(DesktopKind.SecurePrompt, RemoteConsentType.Unattended, server: SecureDesktopLevel.LockScreen, local: SecureDesktopLevel.None));
        Assert.Equal(DesktopAccess.None, Decide(DesktopKind.SecurePrompt, RemoteConsentType.Unattended, server: SecureDesktopLevel.None, local: SecureDesktopLevel.LockScreen));
    }

    [Fact]
    public void View_mode_can_see_but_not_operate_secure_screens()
    {
        Assert.Equal(new DesktopAccess(true, false), Decide(DesktopKind.SecurePrompt, RemoteConsentType.Unattended, RemoteSessionMode.View));
        Assert.Equal(new DesktopAccess(true, false), Decide(DesktopKind.LockScreen, RemoteConsentType.Trusted, RemoteSessionMode.View));
    }

    [Theory]
    [InlineData(DesktopKind.SignIn)]
    [InlineData(DesktopKind.Unknown)]
    public void Sign_in_and_unknown_screens_are_never_followed(DesktopKind kind)
    {
        foreach (var consent in Enum.GetValues<RemoteConsentType>())
            Assert.Equal(DesktopAccess.None, Decide(kind, consent, allowUac: true));
    }
}

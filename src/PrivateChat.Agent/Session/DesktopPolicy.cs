using PrivateChat.Contracts.Remote;

namespace PrivateChat.Agent.Session;

// 这个文件不碰 Windows（测试项目把它直接编进去）。

/// <summary>屏幕上现在是哪一种界面（<see cref="DesktopPolicy.Classify"/> 认出来的）。</summary>
public enum DesktopKind
{
    /// <summary>平常的桌面。</summary>
    Normal,
    /// <summary>没锁屏，但在安全桌面上：管理员确认（UAC）或 Ctrl+Alt+Del 界面。</summary>
    SecurePrompt,
    /// <summary>锁着屏（含带密码的屏保）。</summary>
    LockScreen,
    /// <summary>没人登录（开机后的登录界面、注销后）。</summary>
    SignIn,
    /// <summary>认不出来（打不开输入桌面、名字没见过）：一律不跟。</summary>
    Unknown,
}

/// <summary>这次远程能跟到哪些界面上（截屏 / 注入线程切过去），以及到了那儿能不能操作。</summary>
public readonly record struct DesktopAccess(bool See, bool Operate)
{
    public static readonly DesktopAccess None = new(false, false);
}

/// <summary>
/// 每台电脑、每次远程允许到哪一层：服务器上主人设的、本机管理员设的上限（服务器只能在上限以内收紧，不能放开），取两者都允许的。
/// </summary>
public enum SecureDesktopLevel
{
    /// <summary>只有平常桌面（和以前一样）。</summary>
    None = 0,
    /// <summary>加上管理员确认（UAC）和 Ctrl+Alt+Del 界面。</summary>
    Uac = 1,
    /// <summary>再加上锁屏（以后还有登录界面）。</summary>
    LockScreen = 2,
}

/// <summary>
/// 远程时截屏、注入线程能不能跟着输入桌面切过去（docs/DESIGN.md"远程协助：UAC、锁屏"）。纯函数，单元测试覆盖。
/// 原则：拿不准就不跟——线程留在平常桌面上，Windows 自己就不让它截、不让它点安全桌面，这里的规则写错了也漏不出去（"关着失效"）。
/// - 平常桌面：照旧（看、操作都行；只看模式不能操作）。
/// - UAC / Ctrl+Alt+Del：本机上限和服务器设置都到了 Uac；电脑前的人点"同意"的那种（Prompt）还得在同意窗口里勾了"也允许处理管理员确认"；
///   PIN、免同意名单进来的不用勾（同意那一步本来就跳过了）。只看模式能看、不能操作。
/// - 锁屏：本机上限和服务器设置都到了 LockScreen，而且只给 PIN、免同意名单进来的（锁着屏时电脑前多半没人，"同意"不算数）。只看模式能看、不能操作。
/// - 登录界面（没人登录）：还没做（第二期），不跟。
/// - 认不出来的：不跟。
/// </summary>
public static class DesktopPolicy
{
    public const string DefaultDesktop = "Default";
    public const string WinlogonDesktop = "Winlogon";
    public const string ScreenSaverDesktop = "Screen-saver";

    /// <summary>
    /// 认出现在是哪一种界面。锁屏标志优先于桌面的名字（Windows 11 锁屏时输入桌面有时还叫 Default）。
    /// desktopName：OpenInputDesktop 的名字（打不开时 null）；locked：WTS 会话标志（null = 问不到）；userLoggedOn：控制台会话里有没有人登录。
    /// </summary>
    public static DesktopKind Classify(string? desktopName, bool? locked, bool userLoggedOn)
    {
        if (!userLoggedOn) return DesktopKind.SignIn;
        if (locked == true) return DesktopKind.LockScreen;
        return desktopName switch
        {
            null => DesktopKind.Unknown,
            _ when string.Equals(desktopName, DefaultDesktop, StringComparison.OrdinalIgnoreCase) => locked is null ? DesktopKind.Unknown : DesktopKind.Normal,
            _ when string.Equals(desktopName, WinlogonDesktop, StringComparison.OrdinalIgnoreCase) => DesktopKind.SecurePrompt,
            _ when string.Equals(desktopName, ScreenSaverDesktop, StringComparison.OrdinalIgnoreCase) => DesktopKind.LockScreen,
            _ => DesktopKind.Unknown,
        };
    }

    /// <summary>
    /// 这次远程在这种界面上能不能看、能不能操作。serverLevel：服务器上这台电脑的设置（会话开始时定下的快照）；
    /// localCeiling：本机管理员设的上限；allowUac：Prompt 的那种，电脑前的人在同意窗口里勾了"也允许处理管理员确认"。
    /// </summary>
    public static DesktopAccess Decide(DesktopKind kind, RemoteSessionMode mode, RemoteConsentType consent,
        SecureDesktopLevel serverLevel, SecureDesktopLevel localCeiling, bool allowUac)
    {
        var level = (SecureDesktopLevel)Math.Min((int)serverLevel, (int)localCeiling);
        var control = mode == RemoteSessionMode.Control;
        var unattended = consent is RemoteConsentType.Unattended or RemoteConsentType.Trusted;
        var allowed = kind switch
        {
            DesktopKind.Normal => true,
            DesktopKind.SecurePrompt => level >= SecureDesktopLevel.Uac && (unattended || allowUac),
            DesktopKind.LockScreen => level >= SecureDesktopLevel.LockScreen && unattended,
            _ => false,
        };
        return allowed ? new DesktopAccess(true, control) : DesktopAccess.None;
    }
}

namespace PrivateChat.Api.Infrastructure;

/// <summary>环境变量 App__PublicUrl / App__AdminEmail / App__DataRoot。</summary>
public sealed class AppOptions
{
    public const string Section = "App";
    public string PublicUrl { get; set; } = "http://localhost:8080";
    /// <summary>该邮箱通过 Access 登录后自动成为 Admin。</summary>
    public string AdminEmail { get; set; } = "";
    /// <summary>可选：管理员本地密码，用于局域网直连 / 本地开发的密码登录引导。</summary>
    public string AdminPassword { get; set; } = "";
    /// <summary>keys / blobs / derived / tus 的根目录。</summary>
    public string DataRoot { get; set; } = "data";
    /// <summary>ffmpeg / ffprobe 可执行文件；容器镜像里在 PATH 上。</summary>
    public string FfmpegPath { get; set; } = "ffmpeg";
    public string FfprobePath { get; set; } = "ffprobe";
    /// <summary>家庭所在时区（IANA），用于解释没有时区信息的照片拍摄时间。</summary>
    public string TimeZone { get; set; } = "Asia/Shanghai";
}

/// <summary>环境变量 CloudflareAccess__TeamDomain / CloudflareAccess__Audience。</summary>
public sealed class CloudflareAccessOptions
{
    public const string Section = "CloudflareAccess";
    /// <summary>形如 https://yourteam.cloudflareaccess.com，也是 JWT 的 iss。</summary>
    public string TeamDomain { get; set; } = "";
    /// <summary>Access 应用的 Application Audience (AUD) Tag。</summary>
    public string Audience { get; set; } = "";
    public bool Enabled => !string.IsNullOrWhiteSpace(TeamDomain) && !string.IsNullOrWhiteSpace(Audience);
}

/// <summary>环境变量 CloudflareRealtime__AppId / AppSecret / TurnKeyId / TurnApiToken。</summary>
public sealed class CloudflareRealtimeOptions
{
    public const string Section = "CloudflareRealtime";
    public string BaseUrl { get; set; } = "https://rtc.live.cloudflare.com/v1";
    public string AppId { get; set; } = "";
    public string AppSecret { get; set; } = "";
    public string TurnKeyId { get; set; } = "";
    public string TurnApiToken { get; set; } = "";
    public int TurnCredentialTtlSeconds { get; set; } = 86400;
}

/// <summary>环境变量 WebPush__Subject（mailto:）。密钥对由 KeyStore 自动生成。</summary>
public sealed class WebPushOptions
{
    public const string Section = "WebPush";
    public string Subject { get; set; } = "mailto:admin@example.com";
}

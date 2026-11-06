namespace PrivateChat.Agent;

/// <summary>Agent 配置：安装向导写入 appsettings 或用户机密；设备令牌在 Windows 上建议用 DPAPI 加密保存（TODO）。</summary>
public sealed class AgentOptions
{
    public const string Section = "Agent";

    /// <summary>服务器地址，如 https://chat.example.com</summary>
    public string ServerUrl { get; set; } = "";

    /// <summary>Cloudflare Access 服务令牌（CF-Access-Client-Id / CF-Access-Client-Secret）。</summary>
    public string AccessClientId { get; set; } = "";
    public string AccessClientSecret { get; set; } = "";

    /// <summary>设备码登录后获得的长期设备令牌（Bearer）。为空则进入设备码登录流程。</summary>
    public string DeviceToken { get; set; } = "";

    public string DeviceName { get; set; } = Environment.MachineName;

    /// <summary>ffmpeg.exe 路径（随安装包分发）。</summary>
    public string FfmpegPath { get; set; } = "ffmpeg.exe";
}

using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;

namespace PrivateChat.Agent;

/// <summary>
/// 远程控制被控端骨架（Windows）。v1 计划（docs/DESIGN.md 4.8）：
///  1. 无设备令牌 → 调 /api/auth/device/code 显示 8 位设备码并轮询 /poll，直到用户在网页确认。
///  2. 带 Access 服务令牌头 + Bearer 设备令牌连接 /hubs/chat，上报在线、分辨率、显示器数。
///  3. 收到 RemoteSessionChanged（请求）→ 弹窗同意 / 校验无人值守 PIN → 连接 /ws/remote/{sessionId}/agent。
///  4. 子进程 ffmpeg -f ddagrab ... -c:v libx264 -tune zerolatency -f h264 pipe:1，按 NAL 切帧经 WebSocket 发送；
///     收到键鼠帧用 SendInput 注入；每 15 s 心跳。
/// </summary>
public sealed class AgentWorker(IOptions<AgentOptions> options, ILogger<AgentWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        if (string.IsNullOrWhiteSpace(o.ServerUrl))
        {
            logger.LogWarning("未配置 Agent:ServerUrl，Agent 空转。请通过安装向导填写服务器地址与 Access 服务令牌。");
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
            return;
        }

        if (string.IsNullOrWhiteSpace(o.DeviceToken))
        {
            // TODO: 设备码登录流程（POST /api/auth/device/code → 显示设备码 → 轮询 /api/auth/device/poll），拿到令牌后 DPAPI 加密保存
            logger.LogWarning("未配置设备令牌，设备码登录流程尚未实现。");
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
            return;
        }

        var connection = new HubConnectionBuilder()
            .WithUrl($"{o.ServerUrl.TrimEnd('/')}/hubs/chat", http =>
            {
                http.Headers["CF-Access-Client-Id"] = o.AccessClientId;
                http.Headers["CF-Access-Client-Secret"] = o.AccessClientSecret;
                http.AccessTokenProvider = () => Task.FromResult<string?>(o.DeviceToken);
            })
            .WithAutomaticReconnect()
            .Build();

        connection.On<object>("RemoteSessionChanged", payload =>
        {
            // TODO: 解析会话请求，弹出同意窗 / 校验 PIN，然后启动数据面（ffmpeg + WebSocket 中继）
            logger.LogInformation("收到远程控制会话事件: {Payload}", payload);
        });

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await connection.StartAsync(stoppingToken);
                logger.LogInformation("已连接到 {Server}，设备 {Device}", o.ServerUrl, o.DeviceName);
                break;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "连接失败，10 秒后重试");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }

        await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
    }
}

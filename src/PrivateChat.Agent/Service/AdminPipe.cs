using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json.Nodes;
using PrivateChat.Agent.Platform;

namespace PrivateChat.Agent.Service;

/// <summary>
/// 管理管道（\\.\pipe\PrivateChat.Agent.Admin）：安装程序、"重新设置"（--configure）、卸载程序跟服务说话的地方。
/// 只有 SYSTEM 和提权后的管理员能连（DACL 里的 Administrators 只对提权后的令牌生效；连上后还以对方身份再核对一次是不是提权的管理员）。
/// 服务器地址决定服务装哪个版本、以 SYSTEM 跑什么，所以只能从这里改。命令：
///   status → {version, state}；setup {text} → 换成这个配置串（重新登记设备码）。
/// 一次只处理一个连接；名字被别人先占了（服务重启的空档里）建不起来就过一会儿再试，不让服务崩。
/// </summary>
internal sealed class AdminPipe(HelperBridgeUi bridge, Func<string> state)
{
    public const string Name = "PrivateChat.Agent.Admin";

    public async Task RunAsync(CancellationToken ct)
    {
        var security = PipeChannel.Security(PipeChannel.LocalSystem, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        while (!ct.IsCancellationRequested)
        {
            PipeChannel channel;
            try
            {
                channel = await PipeChannel.ListenAsync(Name, security, expectedClientPid: null, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                AgentLog.Warn($"管理管道建不起来（{ex.Message}），30 秒后再试");
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                continue;
            }
            await using (channel)
            {
                try
                {
                    if (!IsElevatedAdministrator(channel))
                    {
                        AgentLog.Warn($"管理管道：进程 {channel.RemoteProcessId} 不是提权后的管理员，断开");
                        continue;
                    }
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromMinutes(2));
                    var peer = new PipePeer(channel, HandleAsync);
                    await peer.RunAsync(timeout.Token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    AgentLog.Warn($"管理管道出错：{ex.Message}");
                }
            }
        }
    }

    private Task<JsonObject?> HandleAsync(string cmd, JsonObject message)
    {
        switch (cmd)
        {
            case "status":
                return Task.FromResult<JsonObject?>(new JsonObject { ["version"] = AgentService.Version, ["state"] = state() });
            case "setup":
            {
                var text = message["text"]?.GetValue<string>() ?? "";
                if (!AgentConfig.TryParseSetup(text, out _, out _, out _)) throw new InvalidOperationException("配置串或服务器地址不对");
                AgentLog.Info("管理员给了新的配置串，重新设置");
                bridge.AdminSetup(text);
                return Task.FromResult<JsonObject?>([]);
            }
            default:
                throw new InvalidOperationException($"不认识的命令 {cmd}");
        }
    }

    /// <summary>以连上来的进程的身份看它的令牌：提权了、而且是管理员组的。</summary>
    private static bool IsElevatedAdministrator(PipeChannel channel)
    {
        var ok = false;
        channel.RunAsClient(() =>
        {
            using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
            ok = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator) || identity.IsSystem;
        });
        return ok;
    }

    /// <summary>管理员这边（安装程序、--configure）：连上服务的管理管道（等它起来，最多 timeout）。</summary>
    public static async Task<PipePeer> ConnectAsync(TimeSpan timeout, CancellationToken ct)
    {
        // 服务端必须是服务进程本身：服务没在跑的空档里别人先占了这个名字，配置串（含 Access 密钥）就会交到它手里
        var servicePid = ServiceControl.ProcessId(ServicePaths.ServiceName) ?? throw new InvalidOperationException("远程协助服务没在运行");
        var channel = await PipeChannel.ConnectAsync(Name, servicePid, TokenImpersonationLevel.Identification, (int)timeout.TotalMilliseconds, ct);
        var peer = new PipePeer(channel, (_, _) => Task.FromResult<JsonObject?>(null));
        _ = peer.RunAsync(ct);
        return peer;
    }
}

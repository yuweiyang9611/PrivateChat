using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace PrivateChat.Agent;

public enum UpdateOutcome
{
    /// <summary>服务器上没有更新的版本（或者那台服务器还没有清单）。</summary>
    UpToDate,
    /// <summary>有新版本，但现在不方便换（正在被远程）：过一会儿再来。</summary>
    Deferred,
    /// <summary>新版本已经启动，这个进程该退出了。</summary>
    Restarting,
}

/// <summary>
/// 自动升级：从自己连的那台服务器下载新的安装包，核对无误后把自己换掉、重新启动。不用每次去电脑上重装。
///
/// 信任谁：只信自己配置里的那台服务器，走的是和平时一样的加密连接（证书照常校验、带 Access 通行令牌）。
/// 这台服务器本来就能让被控端不经同意开始远程（PIN、免同意名单都是它说了算），升级没有给它更多的权力。
/// 清单里的 SHA-256 防的是下载坏了、截断了、半路被代理改了，不是防服务器本身。
///
/// 怎么换：正在运行的 exe 在 Windows 上可以改名，所以先把自己改名成 .old，把新文件放到原来的位置
/// （开机自启登记的就是这个路径，不用动），启动新版本，等它报"我起来了"才退出；它起不来就换回去接着跑。
/// 只往高版本升；正在被远程的时候不换；只有发布出来的单文件程序才升级（开发时直接跑的不升）。
/// </summary>
public sealed class Updater(string dataDir)
{
    /// <summary>设成 off / 0 / false 就不自动升级。</summary>
    public const string SwitchVariable = "PRIVATECHAT_AGENT_AUTOUPDATE";
    public const string ManifestPath = "downloads/PrivateChat.Agent.json";
    public const string InstallerPath = "downloads/PrivateChat.Agent.exe";

    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(30);

    private string UpdateDir => Path.Combine(dataDir, "update");

    /// <summary>发布出来的单文件程序里，程序集没有自己的文件路径。</summary>
    public static bool IsPublishedSingleFile => string.IsNullOrEmpty(typeof(Updater).Assembly.Location);

    /// <summary>为什么不自动升级；能升级返回 null。</summary>
    public static string? DisabledReason()
    {
        if (Environment.GetEnvironmentVariable(SwitchVariable)?.Trim().ToLowerInvariant() is "off" or "0" or "false")
            return $"已用环境变量 {SwitchVariable} 关闭";
        if (!IsPublishedSingleFile) return "这是开发时直接运行的版本，不是发布出来的安装包";
        if (string.IsNullOrEmpty(Environment.ProcessPath)) return "找不到自己的程序文件";
        return null;
    }

    /// <summary>新旧进程握手用的名字：新版本起来之后置位，旧版本看到了才退出。和单实例互斥量一样按数据目录区分。</summary>
    public static string ReadySignalName(string dataDir)
        => @"Local\PrivateChat.Agent.Updated." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(dataDir).ToLowerInvariant())))[..16];

    private static HttpClient Client(ServerInfo server) => new(AgentTransport.CreateHandler(new Uri(server.Url)))
    {
        BaseAddress = AgentTransport.Address(new Uri(server.Url.TrimEnd('/') + "/")),
        Timeout = Timeout.InfiniteTimeSpan, // 安装包几十 MB，慢网络上要下很久；卡住不动由下面的 StallTimeout 管
    };

    /// <summary>服务器上现在发布的版本；那台服务器没有清单（旧版本的服务器）返回 null。</summary>
    public async Task<UpdateManifest?> FetchManifestAsync(ServerInfo server, CancellationToken ct)
    {
        using var http = Client(server);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(HttpMethod.Get, ManifestPath);
        server.Apply(request.Headers, withToken: false);
        using var response = await http.SendAsync(request, timeout.Token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode) throw new ServerException(response.StatusCode, "读取版本清单失败");
        var text = await response.Content.ReadAsStringAsync(timeout.Token);
        return UpdateManifest.Parse(text) ?? throw new InvalidDataException("服务器上的版本清单读不懂");
    }

    /// <summary>
    /// 把清单里的那个版本下载到数据目录，核对大小和 SHA-256。对不上就删掉并报错，绝不留下一个没核对过的文件。
    /// 之前下好、核对过的（当时正在被远程没换成）直接用，不重复下载。
    /// </summary>
    public async Task<string> DownloadAsync(ServerInfo server, UpdateManifest manifest, CancellationToken ct)
    {
        Directory.CreateDirectory(UpdateDir);
        var target = Path.Combine(UpdateDir, $"PrivateChat.Agent-{manifest.Version}.exe");
        if (await manifest.MatchesAsync(target, ct)) return target;

        var partial = target + ".part";
        try
        {
            using var http = Client(server);
            using var request = new HttpRequestMessage(HttpMethod.Get, InstallerPath);
            server.Apply(request.Headers, withToken: false);
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
            stall.CancelAfter(StallTimeout);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stall.Token);
            if (!response.IsSuccessStatusCode) throw new ServerException(response.StatusCode, "下载安装包失败");
            if (response.Content.Headers.ContentLength is { } length && length != manifest.Size)
                throw new InvalidDataException($"安装包大小和清单对不上（{length} / {manifest.Size}）");

            await using (var source = await response.Content.ReadAsStreamAsync(stall.Token))
            await using (var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buffer = new byte[1 << 16];
                long total = 0;
                while (true)
                {
                    stall.CancelAfter(StallTimeout); // 每读到一块就重新计时：慢没关系，一分钟一个字节都没有才算卡住
                    var read = await source.ReadAsync(buffer, stall.Token);
                    if (read == 0) break;
                    total += read;
                    if (total > manifest.Size) throw new InvalidDataException("安装包比清单里写的大");
                    await file.WriteAsync(buffer.AsMemory(0, read), stall.Token);
                }
            }
            if (!await manifest.MatchesAsync(partial, ct))
                throw new InvalidDataException("下载到的安装包和清单里的 SHA-256 对不上（没下完整，或者半路被改了）");
            File.Move(partial, target, overwrite: true);
            return target;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("下载安装包时一分钟没有收到数据");
        }
        finally
        {
            TryDelete(partial);
        }
    }

    /// <summary>
    /// 把自己换成下载好的新版本并启动它。新版本在 30 秒内报了"我起来了"才算成功（调用方随后退出）；
    /// 起不来就换回原来的文件并抛出异常，这个进程接着跑。
    /// </summary>
    public void SwapAndRelaunch(string downloaded, string fromVersion, IReadOnlyList<string> originalArguments)
    {
        var current = Environment.ProcessPath ?? throw new InvalidOperationException("找不到自己的程序文件");
        var old = current + ".old";
        TryDelete(old);
        if (File.Exists(old)) old = $"{current}.{DateTime.Now.Ticks}.old"; // 上次留下的还删不掉（被占着）：换个名字

        try
        {
            File.Move(current, old); // 正在运行的 exe 可以改名，不能覆盖
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            throw new InvalidOperationException($"程序所在的文件夹不让改（{Path.GetDirectoryName(current)}）：把程序放到自己的文件夹里（比如桌面或文档）才能自动升级", ex);
        }
        try
        {
            File.Move(downloaded, current);
        }
        catch
        {
            File.Move(old, current);
            throw;
        }

        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, ReadySignalName(dataDir));
        Process? child = null;
        try
        {
            var start = new ProcessStartInfo(current) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(current)! };
            foreach (var argument in UpdateManifest.RelaunchArguments(originalArguments, fromVersion)) start.ArgumentList.Add(argument);
            child = Process.Start(start) ?? throw new InvalidOperationException("新版本启动不了");
            // 等它报"我起来了"；它要是直接退出了就不用干等
            var deadline = DateTime.UtcNow + ReadyTimeout;
            while (!ready.WaitOne(200))
            {
                if (child.HasExited) throw new InvalidOperationException($"新版本一启动就退出了（退出码 {child.ExitCode}）");
                if (DateTime.UtcNow > deadline) throw new InvalidOperationException("新版本 30 秒没有就绪");
            }
        }
        catch
        {
            // 换回去：新版本要是还活着先停掉，再把原来的文件放回原位
            try
            {
                if (child is { HasExited: false }) child.Kill(entireProcessTree: true);
                child?.WaitForExit(5000);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
            var bad = current + ".bad";
            TryDelete(bad);
            File.Move(current, bad);
            File.Move(old, current);
            TryDelete(bad);
            throw;
        }
        finally
        {
            child?.Dispose();
        }
    }

    /// <summary>新版本启动后：告诉还在等的旧进程"我起来了"。</summary>
    public static void SignalReady(string dataDir)
    {
        if (EventWaitHandle.TryOpenExisting(ReadySignalName(dataDir), out var ready))
            using (ready) ready.Set();
    }

    /// <summary>清掉升级留下的旧文件（改了名的旧程序、下载目录）。旧进程可能还没退干净，删不掉的下次启动再删。</summary>
    public void CleanUp()
    {
        if (!IsPublishedSingleFile || Environment.ProcessPath is not { } current) return;
        try
        {
            var dir = Path.GetDirectoryName(current)!;
            var name = Path.GetFileName(current);
            foreach (var file in Directory.EnumerateFiles(dir, name + ".*"))
                if (file.EndsWith(".old", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".bad", StringComparison.OrdinalIgnoreCase))
                    TryDelete(file);
            if (Directory.Exists(UpdateDir))
                foreach (var file in Directory.EnumerateFiles(UpdateDir))
                    TryDelete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 下次再清
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 还被占着
        }
    }
}

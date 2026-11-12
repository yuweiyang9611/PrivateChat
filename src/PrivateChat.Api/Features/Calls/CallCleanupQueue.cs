using System.Diagnostics;
using System.Threading.Channels;

namespace PrivateChat.Api.Features.Calls;

/// <summary>要让 Cloudflare 停止转发的一批轨道：某个会话里的若干 mid。</summary>
public sealed record CallCleanup(string SessionId, List<string> Mids);

/// <summary>
/// 让 Cloudflare 关闭轨道（离开、被顶掉、超时、结束、发布回滚、不再订阅）是尽力而为的事：
/// 关不掉也无妨，浏览器断开连接 30 秒后 Cloudflare 自己会回收。所以它不在通话锁里、也不在 HTTP 请求里同步等——
/// 实测关闭"刚断开的真实连接上的轨道"有时要等十秒，期间同一通通话里别人的加入、离开都会被拖住。
/// 这里只入队，由 <see cref="CallCleanupWorker"/> 在后台有限并发地执行。
/// </summary>
public sealed class CallCleanupQueue
{
    private readonly Channel<CallCleanup> _channel = Channel.CreateUnbounded<CallCleanup>(new UnboundedChannelOptions { SingleReader = false });
    private int _pending;

    public void Enqueue(IEnumerable<CallCleanup> items)
    {
        foreach (var item in items)
        {
            if (item.Mids.Count == 0) continue;
            Interlocked.Increment(ref _pending);
            if (!_channel.Writer.TryWrite(item)) Interlocked.Decrement(ref _pending);
        }
    }

    public ChannelReader<CallCleanup> Reader => _channel.Reader;

    /// <summary>还没处理完的条数（排队中 + 正在请求 Cloudflare）。测试用来等队列清空。</summary>
    public int Pending => Volatile.Read(ref _pending);

    internal void Done() => Interlocked.Decrement(ref _pending);
}

/// <summary>消费 <see cref="CallCleanupQueue"/>：每条单独的短超时，有限并发，失败只记日志。</summary>
public sealed class CallCleanupWorker(CallCleanupQueue queue, IServiceScopeFactory scopes, ILogger<CallCleanupWorker> logger) : BackgroundService
{
    /// <summary>强制关闭不需要协商，正常几百毫秒；比 Cloudflare 客户端默认的 10 秒短得多，卡住就放弃。</summary>
    public static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(5);
    private const int Concurrency = 4;
    private static readonly TimeSpan SlowThreshold = TimeSpan.FromSeconds(2);

    protected override Task ExecuteAsync(CancellationToken ct)
        => Task.WhenAll(Enumerable.Range(0, Concurrency).Select(_ => ConsumeAsync(ct)));

    private async Task ConsumeAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var item in queue.Reader.ReadAllAsync(ct))
            {
                try { await CloseAsync(item, ct); }
                finally { queue.Done(); }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 停机：没关的交给 Cloudflare 自己回收
        }
    }

    private async Task CloseAsync(CallCleanup item, CancellationToken stopping)
    {
        using var scope = scopes.CreateScope();
        // typed HttpClient：每次从作用域里取，不让单例长期持有
        var realtime = scope.ServiceProvider.GetRequiredService<CloudflareRealtimeClient>();
        // 凭据被拿掉之后（换了配置重启）留下的旧会话：没法关，也不用关
        if (!realtime.IsConfigured) return;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        timeout.CancelAfter(CloseTimeout);
        var watch = Stopwatch.StartNew();
        string outcome;
        try
        {
            var res = await realtime.CloseTracksAsync(item.SessionId, item.Mids, null, timeout.Token);
            outcome = res.ErrorCode is null ? "ok" : $"error {res.ErrorCode}";
        }
        catch (OperationCanceledException) when (!stopping.IsCancellationRequested)
        {
            outcome = $"timeout after {CloseTimeout.TotalSeconds:0}s";
        }
        catch (Exception ex) when (ex is CloudflareRealtimeException or HttpRequestException)
        {
            outcome = ex.Message;
        }
        watch.Stop();
        // 用 Information：这是 Cloudflare 侧的偶发慢，不是我们的错误；记下来用于弄清它什么时候会卡
        if (watch.Elapsed >= SlowThreshold || outcome != "ok")
            logger.LogInformation("关闭 Cloudflare 轨道 {Session} {Count} 条：{Outcome}，用时 {Elapsed} ms",
                item.SessionId, item.Mids.Count, outcome, watch.ElapsedMilliseconds);
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;

namespace PrivateChat.Agent.Platform;

/// <summary>
/// 一条 <see cref="PipeChannel"/> 的两头对等：哪头都能发请求、发通知，也都处理对方发来的。
/// - 请求：{"id":n,"cmd":"…",…} → 对方回 {"re":n,…}（处理出错回 {"re":n,"error":"…"}，调用方抛 <see cref="PipeCallException"/>）；
/// - 通知：{"cmd":"…",…}，不等回。
/// 收的循环只有一个（<see cref="RunAsync"/>）；收到的请求 / 通知各自在线程池上处理（处理慢的不挡后面的）。
/// 管道断了：<see cref="RunAsync"/> 返回，所有还在等回答的调用抛 IOException。
/// 消息正文用 System.Text.Json 的 Web 默认设置（驼峰、枚举按字符串），<see cref="Body{T}"/> / <see cref="With{T}"/> 在类型和 JSON 之间转。
/// </summary>
internal sealed class PipePeer(PipeChannel channel, Func<string, JsonObject, Task<JsonObject?>> handler)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private readonly Dictionary<int, TaskCompletionSource<JsonObject>> _pending = [];
    private readonly Lock _gate = new();
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _nextId;
    private bool _disconnected;

    public PipeChannel Channel => channel;

    /// <summary>管道断了（收的循环结束了）。</summary>
    public Task Closed => _closed.Task;

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (await channel.ReceiveAsync(ct) is { } message)
            {
                if (message["re"] is { } re)
                {
                    TaskCompletionSource<JsonObject>? waiter;
                    lock (_gate) _pending.Remove(re.GetValue<int>(), out waiter);
                    waiter?.TrySetResult(message);
                    continue;
                }
                if (message["cmd"]?.GetValue<string>() is not { } cmd) continue;
                var id = message["id"]?.GetValue<int>();
                _ = Task.Run(() => DispatchAsync(cmd, id, message, ct), CancellationToken.None);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or InvalidDataException or JsonException)
        {
            if (ex is InvalidDataException or JsonException) AgentLog.Warn($"管道上收到看不懂的消息，断开：{ex.Message}");
        }
        finally
        {
            lock (_gate)
            {
                _disconnected = true;
                foreach (var waiter in _pending.Values) waiter.TrySetException(new IOException("管道断了"));
                _pending.Clear();
            }
            _closed.TrySetResult();
        }
    }

    private async Task DispatchAsync(string cmd, int? id, JsonObject message, CancellationToken ct)
    {
        JsonObject reply;
        try
        {
            reply = await handler(cmd, message) ?? [];
        }
        catch (Exception ex)
        {
            if (id is null) AgentLog.Warn($"处理管道通知 {cmd} 出错：{ex.Message}");
            reply = new JsonObject { ["error"] = $"{ex.GetType().Name}：{ex.Message}" };
        }
        if (id is not { } requestId) return;
        reply["re"] = requestId;
        try
        {
            await channel.SendAsync(reply, ct);
        }
        catch (Exception) { }
    }

    /// <summary>发请求等回答（超时抛 TimeoutException，对方处理出错抛 PipeCallException，管道断了抛 IOException）。</summary>
    public async Task<JsonObject> CallAsync(string cmd, JsonObject? body, TimeSpan timeout, CancellationToken ct)
    {
        var waiter = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        int id;
        lock (_gate)
        {
            if (_disconnected) throw new IOException("管道断了");
            id = ++_nextId;
            _pending[id] = waiter;
        }
        var message = body ?? [];
        message["cmd"] = cmd;
        message["id"] = id;
        try
        {
            await channel.SendAsync(message, ct);
            var reply = await waiter.Task.WaitAsync(timeout, ct);
            if (reply["error"]?.GetValue<string>() is { } error) throw new PipeCallException($"{cmd}：{error}");
            reply.Remove("re");
            return reply;
        }
        finally
        {
            lock (_gate) _pending.Remove(id);
        }
    }

    public Task NotifyAsync(string cmd, JsonObject? body, CancellationToken ct)
    {
        var message = body ?? [];
        message["cmd"] = cmd;
        return channel.SendAsync(message, ct);
    }

    /// <summary>类型 → 消息正文（JSON 对象）。</summary>
    public static JsonObject With<T>(T value) => JsonSerializer.SerializeToNode(value, Json) as JsonObject ?? throw new InvalidOperationException($"{typeof(T).Name} 不是对象");

    /// <summary>消息正文 → 类型（多出来的 cmd / id 字段不管）。</summary>
    public static T Body<T>(JsonObject message) => message.Deserialize<T>(Json) ?? throw new InvalidDataException($"消息不是 {typeof(T).Name}");
}

/// <summary>管道对面处理请求时出错了（消息里带着它的说明）。</summary>
internal sealed class PipeCallException(string message) : Exception(message);

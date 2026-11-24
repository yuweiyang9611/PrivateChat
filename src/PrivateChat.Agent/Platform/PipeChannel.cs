using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PrivateChat.Agent.Platform;

/// <summary>
/// 服务和它起的进程之间的命名管道：一条消息 = 4 字节小端长度 + UTF-8 JSON 对象，最长 <see cref="MaxMessageBytes"/>。
/// 管道只建一个实例（.NET 这时会带上 FILE_FLAG_FIRST_PIPE_INSTANCE：名字被别人先占了就建不起来，不会连到假的服务上），
/// 拒绝网络上来的连接（PIPE_REJECT_REMOTE_CLIENTS，.NET 总是带；DACL 里另外拒绝 NETWORK），DACL 只放给指定的账号。
/// 两头都核对对方的进程号：服务端核对连上来的是不是自己起的那个进程（<see cref="ClientProcessId"/>），客户端核对服务端是不是命令行里给的那个（<see cref="ServerProcessId"/>）。
/// 发送可以多个线程同时调（排队写）；接收只能一个地方在收。
/// </summary>
internal sealed class PipeChannel : IAsyncDisposable, IDisposable
{
    public const int MaxMessageBytes = 2 << 20;

    private readonly PipeStream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private PipeChannel(PipeStream stream)
    {
        _stream = stream;
    }

    /// <summary>服务端：建管道等对方连上；expectedClientPid 不为 null 时连上来的必须是这个进程，不是就断开接着等（直到 ct）。</summary>
    public static async Task<PipeChannel> ListenAsync(string name, PipeSecurity security, int? expectedClientPid, CancellationToken ct)
    {
        while (true)
        {
            var server = NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
                64 * 1024, 64 * 1024, security);
            try
            {
                await server.WaitForConnectionAsync(ct);
                if (expectedClientPid is { } pid && ClientProcessId(server) != pid)
                {
                    AgentLog.Warn($"管道 {name}：连上来的是进程 {ClientProcessId(server)}，不是 {pid}，断开");
                    await server.DisposeAsync();
                    continue;
                }
                return new PipeChannel(server);
            }
            catch
            {
                await server.DisposeAsync();
                throw;
            }
        }
    }

    /// <summary>
    /// 客户端：连 name（最多等 timeoutMs）；expectedServerPid 不为 null 时服务端必须是这个进程。
    /// level：服务端能拿我们的身份做什么（Identification 只能看；Impersonation 能以我们的身份调系统函数，比如 SendSAS 要的）。
    /// </summary>
    public static async Task<PipeChannel> ConnectAsync(string name, int? expectedServerPid, TokenImpersonationLevel level, int timeoutMs, CancellationToken ct)
    {
        var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous, level);
        try
        {
            await client.ConnectAsync(timeoutMs, ct);
            if (expectedServerPid is { } pid && ServerProcessId(client) != pid)
                throw new UnauthorizedAccessException($"管道 {name} 的服务端是进程 {ServerProcessId(client)}，不是 {pid}");
            return new PipeChannel(client);
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    /// <summary>只放给这些账号读写（每个都可以连，服务端自己建管道的账号也要在里面），拒绝网络登录的。</summary>
    public static PipeSecurity Security(params SecurityIdentifier[] allowed)
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        foreach (var sid in allowed) security.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.FullControl, AccessControlType.Allow));
        return security;
    }

    public static SecurityIdentifier LocalSystem => new(WellKnownSidType.LocalSystemSid, null);

    public PipeStream Stream => _stream;

    public int RemoteProcessId => _stream is NamedPipeServerStream server ? ClientProcessId(server) : ServerProcessId((NamedPipeClientStream)_stream);

    /// <summary>服务端：以连上来的进程的身份跑 action（对方要用 Impersonation 级别连），跑完换回自己。</summary>
    public void RunAsClient(Action action)
    {
        if (_stream is not NamedPipeServerStream server) throw new InvalidOperationException("只有服务端能代表对方");
        server.RunAsClient(() => action());
    }

    public async Task SendAsync(JsonObject message, CancellationToken ct)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(message);
        if (body.Length > MaxMessageBytes) throw new InvalidOperationException($"消息太大（{body.Length} 字节）");
        var frame = new byte[4 + body.Length];
        BitConverter.TryWriteBytes(frame.AsSpan(0, 4), body.Length);
        body.CopyTo(frame, 4);
        await _writeLock.WaitAsync(ct);
        try
        {
            await _stream.WriteAsync(frame, ct);
            await _stream.FlushAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>收一条；对方关了管道时 null。格式不对（太长、不是 JSON 对象）抛 InvalidDataException。</summary>
    public async Task<JsonObject?> ReceiveAsync(CancellationToken ct)
    {
        var header = new byte[4];
        if (!await ReadExactlyAsync(header, ct)) return null;
        var length = BitConverter.ToInt32(header);
        if (length is <= 0 or > MaxMessageBytes) throw new InvalidDataException($"消息长度不对：{length}");
        var body = new byte[length];
        if (!await ReadExactlyAsync(body, ct)) return null;
        return JsonNode.Parse(body) as JsonObject ?? throw new InvalidDataException("消息不是 JSON 对象");
    }

    private async Task<bool> ReadExactlyAsync(byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await _stream.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }

    public static int ClientProcessId(NamedPipeServerStream server)
        => Native.GetNamedPipeClientProcessId(server.SafePipeHandle, out var pid) ? (int)pid : throw new Win32Exception(Marshal.GetLastWin32Error());

    public static int ClientSessionId(NamedPipeServerStream server)
        => Native.GetNamedPipeClientSessionId(server.SafePipeHandle, out var session) ? (int)session : throw new Win32Exception(Marshal.GetLastWin32Error());

    public static int ServerProcessId(NamedPipeClientStream client)
        => Native.GetNamedPipeServerProcessId(client.SafePipeHandle, out var pid) ? (int)pid : throw new Win32Exception(Marshal.GetLastWin32Error());

    public void Dispose()
    {
        _stream.Dispose();
        _writeLock.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync();
        _writeLock.Dispose();
    }

    private static class Native
    {
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint processId);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GetNamedPipeClientSessionId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint sessionId);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint processId);
    }
}

/// <summary>
/// 管道上的一问一答：发 {"id":n,"cmd":...}，等 {"id":n,...} 回来。另一头（<see cref="Serve"/>）收到一条处理一条、按 id 回。
/// 收的循环在 <see cref="RunAsync"/> 里（只一个），回答按 id 交给等着的调用。
/// </summary>
internal sealed class PipeRpc(PipeChannel channel)
{
    private readonly Dictionary<int, TaskCompletionSource<JsonObject>> _pending = [];
    private readonly Lock _gate = new();
    private int _nextId;

    public PipeChannel Channel => channel;

    /// <summary>调用方：一直收回答直到管道断了（之后所有等着的调用都抛 IOException）。</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (await channel.ReceiveAsync(ct) is { } message)
            {
                if (message["id"]?.GetValue<int>() is not { } id) continue;
                TaskCompletionSource<JsonObject>? waiter;
                lock (_gate) _pending.Remove(id, out waiter);
                waiter?.TrySetResult(message);
            }
        }
        finally
        {
            lock (_gate)
            {
                foreach (var waiter in _pending.Values) waiter.TrySetException(new IOException("管道断了"));
                _pending.Clear();
            }
        }
    }

    public async Task<JsonObject> CallAsync(JsonObject request, int timeoutMs, CancellationToken ct)
    {
        var waiter = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        int id;
        lock (_gate)
        {
            id = ++_nextId;
            _pending[id] = waiter;
        }
        request["id"] = id;
        try
        {
            await channel.SendAsync(request, ct);
            // 超时抛 TimeoutException（和 ct 取消分得开）
            return await waiter.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), ct);
        }
        finally
        {
            lock (_gate) _pending.Remove(id);
        }
    }

    /// <summary>被调用方：收到一条交给 handle（各自在线程池上跑，可以同时处理几条），把它的回答带上 id 发回去；管道断了返回。</summary>
    public static async Task Serve(PipeChannel channel, Func<JsonObject, Task<JsonObject>> handle, CancellationToken ct)
    {
        while (await channel.ReceiveAsync(ct) is { } request)
        {
            var id = request["id"]?.GetValue<int>() ?? 0;
            _ = Task.Run(async () =>
            {
                JsonObject reply;
                try
                {
                    reply = await handle(request);
                }
                catch (Exception ex)
                {
                    reply = new JsonObject { ["ok"] = false, ["error"] = $"{ex.GetType().Name}：{ex.Message}" };
                }
                reply["id"] = id;
                try { await channel.SendAsync(reply, ct); }
                catch (Exception) { }
            }, ct);
        }
    }
}

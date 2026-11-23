using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PrivateChat.Contracts.Remote;

namespace PrivateChat.Agent.Telemetry;

/// <summary>仅保留当前设备最后一条待送达的检查状态。慢网络、离线或服务器尚未升级时不阻塞更新。</summary>
internal sealed class UpdateStatusReporter(string dataDir)
{
    private sealed record Pending(Guid Device, string OriginHash, AgentUpdateStatusReport Status);

    private readonly string _path = Path.Combine(dataDir, "pending-update-status.json");
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private ServerInfo? _server;
    private Guid _device;
    private string _originHash = "";
    private Pending? _pending;
    private bool _unsupported;

    public void Connected(Guid device, ServerInfo server)
    {
        lock (_gate)
        {
            Bind(device, server);
            _unsupported = false;
            if (_pending is not null) Wake();
        }
    }

    public Action<AgentUpdateStatusReport> ForCheck(Guid device, ServerInfo server)
    {
        lock (_gate) Bind(device, server);
        return status =>
        {
            lock (_gate)
            {
                if (_device != device || _server?.Url != server.Url) return;
                _pending = new(device, _originHash, status);
                try
                {
                    File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(_pending));
                    File.Move(_path + ".tmp", _path, overwrite: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                if (!_unsupported) Wake();
            }
        };
    }

    private void Bind(Guid device, ServerInfo server)
    {
        if (_device != device || _server?.Url != server.Url)
        {
            _device = device;
            _originHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(server.Url)));
            _pending = null;
            _unsupported = false;
            try
            {
                if (File.Exists(_path) && new FileInfo(_path).Length <= 4096)
                {
                    var saved = JsonSerializer.Deserialize<Pending>(File.ReadAllText(_path));
                    if (saved?.Device == device && saved.OriginHash == _originHash && saved.Status is not null) _pending = saved;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        }
        _server = server;
    }

    public void Forget()
    {
        lock (_gate)
        {
            _server = null;
            _pending = null;
            DeletePending();
        }
    }

    private void DeletePending()
    {
        try { File.Delete(_path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private void Wake()
    {
        try { _wake.Release(); } catch (SemaphoreFullException) { }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var wait = Timeout.InfiniteTimeSpan;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _wake.WaitAsync(wait, ct);
                wait = Timeout.InfiniteTimeSpan;
                ServerInfo? server;
                Pending? pending;
                lock (_gate) { server = _server; pending = _unsupported ? null : _pending; }
                if (server is null || pending is null) continue;
                try
                {
                    using var api = new ServerApi(server);
                    var supported = await api.PutUpdateStatusAsync(pending.Status, ct);
                    lock (_gate)
                    {
                        if (_server != server) continue;
                        if (!supported) _unsupported = true;
                        else if (_pending == pending) { _pending = null; DeletePending(); }
                    }
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    // 未登记、已吊销或旧版不收时，等待下次连接；临时传输故障则稍后再送最新状态。
                    if (ex is ServerException { Status: HttpStatusCode.BadRequest })
                    {
                        lock (_gate)
                        {
                            if (_pending == pending) { _pending = null; DeletePending(); }
                        }
                    }
                    else if (ex is ServerException { Status: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden })
                    {
                        lock (_gate) { if (_server == server && _pending == pending) _unsupported = true; }
                    }
                    else wait = TimeSpan.FromMinutes(1);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
}

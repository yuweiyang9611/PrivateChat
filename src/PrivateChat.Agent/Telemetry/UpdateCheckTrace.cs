using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using PrivateChat.Contracts.Remote;

namespace PrivateChat.Agent.Telemetry;

/// <summary>一次检查的状态。托盘获得字节进度，管理员只收到阶段变化。</summary>
internal sealed class UpdateCheckTrace(string source, string currentVersion, Action<AgentUpdateStatusReport>? publish,
    IProgress<UpdateProgress>? display) : IProgress<UpdateProgress>
{
    private readonly Guid _id = Guid.NewGuid();
    private readonly DateTimeOffset _started = DateTimeOffset.UtcNow;
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private readonly string? _current = UpdateManifest.ParseVersion(currentVersion)?.ToString();
    private string? _lastState;
    private string _stage = "check";
    private string? _target;
    private UpdateProgress? _lastProgress;

    public void Found(string version) => _target = UpdateManifest.ParseVersion(version)?.ToString();

    public void State(string state)
    {
        if (_lastState == state) return;
        _lastState = state;
        Send(state, null, null);
    }

    public void Report(UpdateProgress progress)
    {
        _lastProgress = progress;
        switch (progress.Stage)
        {
            case UpdateStage.CheckingCache:
                _stage = "verify";
                break;
            case UpdateStage.Connecting:
            case UpdateStage.Downloading:
                _stage = "download";
                State("downloading");
                break;
            case UpdateStage.CacheHit:
            case UpdateStage.Verifying:
            case UpdateStage.Verified:
                _stage = "verify";
                State("verifying");
                break;
        }
        display?.Report(progress);
    }

    public void Installing()
    {
        _stage = "install";
        State("restarting");
        if (_lastProgress is { } p) display?.Report(p with { Stage = UpdateStage.Installing });
    }

    public void Failed(Exception ex)
    {
        if (_lastState == "failed") return;
        _lastState = "failed";
        string code;
        if (ex is UpdateIntegrityException) code = "integrity";
        else if (ex is ReleaseSignatureException) code = "signature"; // 服务模式拒装没签名 / 签名不对的版本（2026-11-26 虚拟机里原来报成 unknown）
        else if (ex is OperationCanceledException or TimeoutException) code = "timeout";
        else if (_stage == "check" && ex is InvalidDataException) code = "invalidManifest";
        else if (ex is ServerException { Status: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden }) code = "permission";
        else if (ex is ServerException) code = "server";
        else if (ex.GetBaseException() is UnauthorizedAccessException) code = "permission";
        else if (ex is HttpRequestException or HttpIOException || ex.GetBaseException() is SocketException) code = "network";
        else if (ex.GetBaseException() is IOException) code = "storage";
        else if (_stage == "install" && ex is Win32Exception or InvalidOperationException) code = "launch";
        else code = "unknown";
        Send("failed", _stage, code);
    }

    private void Send(string state, string? failureStage, string? errorCode)
    {
        if (_current is not null)
            publish?.Invoke(new(_id, _started, source, _current, _target, state,
                (int)Math.Min(_elapsed.ElapsedMilliseconds, int.MaxValue), failureStage, errorCode));
    }
}

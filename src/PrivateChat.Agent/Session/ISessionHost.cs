namespace PrivateChat.Agent.Session;

/// <summary>
/// 一次远程会话在哪儿跑：普通模式下就在 Agent 进程里（<see cref="SessionRunner"/>）；
/// 装成系统服务后在服务起的 SYSTEM 工作进程里（服务这边是个代理，转发开始、结束、结束原因）。AgentService 只认这个接口。
/// </summary>
public interface ISessionHost : IAsyncDisposable
{
    Guid Id { get; }

    /// <summary>会话自己结束了（服务端关闭、连不上、工作进程没了），值是原因。</summary>
    Task<string> Finished { get; }

    void Start();
}

using Microsoft.Extensions.Logging;

namespace PrivateChat.Agent.Realtime;

/// <summary>
/// 排查用：把 SIPSorcery 自己的日志（ICE 选路、STUN 探测、DTLS）也写进被控端日志。
/// 平时不开——它很啰嗦；环境变量 PRIVATECHAT_AGENT_REALTIME_LIBLOG=debug（或 trace）时才接上。
/// </summary>
internal sealed class LibraryLog : ILoggerFactory
{
    public const string Variable = "PRIVATECHAT_AGENT_REALTIME_LIBLOG";
    private readonly LogLevel _min;

    private LibraryLog(LogLevel min) => _min = min;

    public static void EnableIfAsked()
    {
        var level = Environment.GetEnvironmentVariable(Variable)?.Trim().ToLowerInvariant() switch
        {
            "trace" => LogLevel.Trace,
            "debug" or "1" => LogLevel.Debug,
            _ => LogLevel.None,
        };
        if (level != LogLevel.None) SIPSorcery.LogFactory.Set(new LibraryLog(level));
    }

    public ILogger CreateLogger(string categoryName) => new Category(categoryName[(categoryName.LastIndexOf('.') + 1)..], _min);

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
    }

    private sealed class Category(string name, LogLevel min) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= min;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel < min) return;
            var text = formatter(state, exception);
            if (exception is not null) text += $"（{exception.GetType().Name}：{exception.Message}）";
            AgentLog.Info($"[库 {name} {logLevel}] {text}");
        }
    }
}

using Microsoft.Extensions.Logging;

namespace RemoteCmd.Client;

/// <summary>
/// Routes ILogger output into the dashboard's <see cref="LogRing"/> instead of the console,
/// so the header stays put and log lines scroll underneath it. Also mirrors the newest line
/// into the stats header. Registered only in --dashboard mode.
/// </summary>
public sealed class DashboardLoggerProvider(LogRing ring, ClientStats stats) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new DashboardLogger(ring, stats);
    public void Dispose() { }

    private sealed class DashboardLogger(LogRing ring, ClientStats stats) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? ex,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level)) return;
            var msg = formatter(state, ex);
            if (ex != null) msg += " " + ex.Message;
            var line = $"{DateTime.Now:HH:mm:ss} {Tag(level)} {msg}";
            ring.Add(line);
            stats.SetLastLine(msg);
        }

        private static string Tag(LogLevel l) => l switch
        {
            LogLevel.Warning => "WARN",
            LogLevel.Error or LogLevel.Critical => "ERR ",
            _ => "info",
        };
    }
}

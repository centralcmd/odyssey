using Microsoft.Extensions.Logging;

namespace Odyssey.Api.Tests.Infrastructure;

/// <summary>
/// Collects formatted log messages so a test can assert on what a component told operators — used
/// where the log line *is* the observable behaviour (a skipped email send, a fail-open warning).
/// </summary>
public sealed class CapturingLogger<T> : ILogger<T>
{
    public List<LogEntry> Entries { get; } = [];

    public List<string> Messages => Entries.ConvertAll(entry => entry.Message);

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));

    public sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose() { }
    }
}

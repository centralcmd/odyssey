// Linked (not referenced) into the test projects that assert on log output, the way TestTierGate.cs is.

using Microsoft.Extensions.Logging;

namespace Odyssey.Testing.Logging;

/// <summary>One log call as a <see cref="RecordingLogger"/> saw it, rendered through the caller's formatter.</summary>
public sealed record LogEntry(
    string Category,
    LogLevel Level,
    EventId EventId,
    string Message,
    Exception? Exception,
    IReadOnlyList<KeyValuePair<string, object?>> State)
{
    /// <summary>The structured arguments, without the message template itself.</summary>
    public IReadOnlyList<object?> Arguments =>
        [.. State.Where(pair => pair.Key != "{OriginalFormat}").Select(pair => pair.Value)];

    /// <summary>Every argument as it would be rendered — what a test scanning for a leaked value also has to read.</summary>
    public IReadOnlyList<string> ArgumentText => [.. Arguments.Select(argument => argument?.ToString() ?? string.Empty)];
}

/// <summary>
/// Collects every log call so a test can assert on what a component told operators — used where the log line
/// <em>is</em> the observable behaviour (a skipped send, a fail-open warning, a value that must never be logged).
/// Every level is enabled. Hand one straight to a component, or reach a host's loggers through
/// <see cref="RecordingLoggerProvider"/>.
/// </summary>
public class RecordingLogger : ILogger
{
    private readonly LogSink sink;
    private readonly string category;

    public RecordingLogger(string category = "")
        : this(new LogSink(), category)
    {
    }

    internal RecordingLogger(LogSink sink, string category)
    {
        this.sink = sink;
        this.category = category;
    }

    public IReadOnlyList<LogEntry> Entries => sink.Entries;

    public IReadOnlyList<string> Messages => [.. Entries.Select(entry => entry.Message)];

    /// <summary>The state of every scope begun, in order, whether or not anything was logged inside it.</summary>
    public IReadOnlyList<object> Scopes => sink.Scopes;

    /// <summary>Forgets every line and scope so far.</summary>
    public void Clear() => sink.Clear();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        sink.AddScope(state);
        return null;
    }

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        sink.Add(new LogEntry(
            category,
            logLevel,
            eventId,
            formatter(state, exception),
            exception,
            state as IReadOnlyList<KeyValuePair<string, object?>> ?? []));
}

/// <summary>A <see cref="RecordingLogger"/> that satisfies an <see cref="ILogger{TCategoryName}"/> dependency.</summary>
public sealed class RecordingLogger<T>() : RecordingLogger(typeof(T).FullName ?? typeof(T).Name), ILogger<T>;

/// <summary>
/// Collects every line a host writes, with its category, so a test can assert on what a component logged from
/// inside a real host. Register it with <c>services.AddSingleton&lt;ILoggerProvider&gt;(provider)</c>; the logger
/// factory picks up every registered provider. Safe for concurrent writers.
/// </summary>
public sealed class RecordingLoggerProvider : ILoggerProvider
{
    private readonly LogSink sink = new();

    public IReadOnlyList<LogEntry> Entries => sink.Entries;

    public IReadOnlyList<string> Messages => [.. Entries.Select(entry => entry.Message)];

    public IReadOnlyList<object> Scopes => sink.Scopes;

    /// <summary>The lines whose category contains <paramref name="category"/> (ordinal).</summary>
    public IEnumerable<LogEntry> ForCategory(string category) =>
        Entries.Where(entry => entry.Category.Contains(category, StringComparison.Ordinal));

    /// <summary>Forgets every line so far — what a host shared across tests does between them.</summary>
    public void Clear() => sink.Clear();

    public ILogger CreateLogger(string categoryName) => new RecordingLogger(sink, categoryName);

    public void Dispose()
    {
    }
}

internal sealed class LogSink
{
    private readonly Lock gate = new();
    private readonly List<LogEntry> entries = [];
    private readonly List<object> scopes = [];

    public IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (gate)
            {
                return [.. entries];
            }
        }
    }

    public IReadOnlyList<object> Scopes
    {
        get
        {
            lock (gate)
            {
                return [.. scopes];
            }
        }
    }

    public void Add(LogEntry entry)
    {
        lock (gate)
        {
            entries.Add(entry);
        }
    }

    public void AddScope(object state)
    {
        lock (gate)
        {
            scopes.Add(state);
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            entries.Clear();
            scopes.Clear();
        }
    }
}

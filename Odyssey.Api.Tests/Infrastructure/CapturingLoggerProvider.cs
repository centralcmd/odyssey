using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Odyssey.Api.Tests.Infrastructure;

/// <summary>
/// Collects every log line the host writes, with its category, so a test can assert on what a
/// component told operators from inside a real <c>WebApplicationFactory</c> host — the counterpart to
/// <see cref="CapturingLogger{T}"/>, which only reaches a hand-constructed component.
/// </summary>
/// <remarks>
/// Register it with <c>services.AddSingleton&lt;ILoggerProvider&gt;(provider)</c> in
/// <c>ConfigureTestServices</c>; the logger factory picks up every registered provider.
/// </remarks>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<Entry> entries = new();

    public IReadOnlyCollection<Entry> Entries => entries;

    public IEnumerable<Entry> ForCategory(string category) =>
        entries.Where(entry => entry.Category.Contains(category, StringComparison.Ordinal));

    public ILogger CreateLogger(string categoryName) => new CategoryLogger(this, categoryName);

    public void Dispose() { }

    public sealed record Entry(string Category, LogLevel Level, string Message, Exception? Exception);

    private sealed class CategoryLogger(CapturingLoggerProvider owner, string category) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            owner.entries.Enqueue(new Entry(category, logLevel, formatter(state, exception), exception));
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose() { }
    }
}

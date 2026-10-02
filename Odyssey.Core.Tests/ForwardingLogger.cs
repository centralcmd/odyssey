using Microsoft.Extensions.Logging;

namespace Odyssey.Core.Tests;

/// <summary>
/// Presents one recording logger under another category type. The contract party writes moved from
/// <c>ContractService</c> to <c>ContractPartyService</c> (issue #287 M1), and a test that records a
/// contract write and a party write through one logger keeps asserting on one list of lines.
/// </summary>
internal sealed class ForwardingLogger<T>(ILogger inner) : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);

    public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        inner.Log(logLevel, eventId, state, exception, formatter);
}

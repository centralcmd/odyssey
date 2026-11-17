using Odyssey.ApiClient;
using Odyssey.Dtos.Internet;

namespace Odyssey.Client.Services;

/// <summary>What a news detail or revision read came back as, for its inline state (issue #473 §3.2.7, §3.5, §7.2).</summary>
public enum NewsStoreOutcome
{
    Ok,

    /// <summary><c>503 news-storage-unavailable</c>: the article text is a file the store cannot serve right now.</summary>
    StorageUnavailable,

    /// <summary>Any other failure.</summary>
    Failed,
}

/// <summary>
/// The one place that reads <c>news-storage-unavailable</c> on the article detail and revision calls (issue #473
/// §4.1, §7.2, §7.3). It classifies a result and remembers the server's <c>Retry-After</c>, so every Retry facing it
/// stays enabled, says how long is left and, pressed early, waits the rest out rather than sending a request the
/// server has asked not to receive. The wait is store-wide: one outage covers every article. Image failures never
/// reach it — an <c>&lt;img&gt;</c> cannot read the error key (§6.1) — and <c>news-busy</c> is handled per context
/// (§7.1).
/// </summary>
public sealed class NewsStoreGate(TimeProvider time)
{
    /// <summary>The wait when a storage answer carries no <c>Retry-After</c> (the server sends 30 s).</summary>
    public static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(30);

    private DateTimeOffset availableAt = DateTimeOffset.MinValue;

    /// <summary>Classifies <paramref name="result"/> and, for a storage outage, starts its wait.</summary>
    public NewsStoreOutcome Observe<T>(ApiResult<T> result)
    {
        if (result.IsSuccess && result.Value is not null)
        {
            return NewsStoreOutcome.Ok;
        }

        if (string.Equals(result.Problem?.Code, NewsErrorCodes.StorageUnavailable, StringComparison.Ordinal))
        {
            availableAt = time.GetUtcNow() + (result.RetryAfter ?? DefaultWait);
            return NewsStoreOutcome.StorageUnavailable;
        }

        return NewsStoreOutcome.Failed;
    }

    /// <summary>How long is left before the store may be asked again, or <c>null</c> when it may be now.</summary>
    public TimeSpan? Remaining
    {
        get
        {
            var left = availableAt - time.GetUtcNow();
            return left > TimeSpan.Zero ? left : null;
        }
    }

    /// <summary>Whole seconds left, for "Available in N s" and "Retrying in N seconds".</summary>
    public int? RemainingSeconds => Remaining is { } left ? (int)Math.Ceiling(left.TotalSeconds) : null;

    /// <summary>Waits out <see cref="Remaining"/>; completes at once when nothing is left.</summary>
    public Task WaitAsync(CancellationToken ct = default) =>
        Remaining is { } left ? Task.Delay(left, time, ct) : Task.CompletedTask;
}

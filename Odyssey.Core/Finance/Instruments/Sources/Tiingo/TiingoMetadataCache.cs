namespace Odyssey.Core.Finance.Instruments.Sources.Tiingo;

/// <summary>
/// The source's private, per-process metadata store (issue #447 §3.3) — not the shared <c>IMemoryCache</c>, which has
/// no size limit. Keyed by source symbol (case-insensitive, as Tiingo tickers are); at most <see cref="Capacity"/>
/// entries, the oldest evicted first. A positive entry holds the venue's currency for <see cref="PositiveTtl"/>; a
/// negative one (unmapped venue, ticker mismatch, unknown symbol) suppresses metadata requests for
/// <see cref="NegativeTtl"/>. Nothing is persisted. Thread-safe.
/// </summary>
public sealed class TiingoMetadataCache(TimeProvider timeProvider)
{
    public const int Capacity = 1000;

    public static readonly TimeSpan PositiveTtl = TimeSpan.FromDays(7);

    public static readonly TimeSpan NegativeTtl = TimeSpan.FromHours(24);

    private readonly object sync = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);
    private long sequence;

    /// <summary>A cached lookup: <see cref="CurrencyCode"/> is <c>null</c> for a negative entry.</summary>
    public readonly record struct Hit(string? CurrencyCode);

    public int Count
    {
        get
        {
            lock (sync)
            {
                return entries.Count;
            }
        }
    }

    public Hit? Find(string symbol)
    {
        lock (sync)
        {
            if (!entries.TryGetValue(symbol, out var entry))
            {
                return null;
            }

            if (entry.ExpiresAt <= timeProvider.GetUtcNow())
            {
                entries.Remove(symbol);
                return null;
            }

            return new Hit(entry.CurrencyCode);
        }
    }

    public void SetPositive(string symbol, string currencyCode) => Set(symbol, currencyCode, PositiveTtl);

    public void SetNegative(string symbol) => Set(symbol, null, NegativeTtl);

    public void Evict(string symbol)
    {
        lock (sync)
        {
            entries.Remove(symbol);
        }
    }

    private void Set(string symbol, string? currencyCode, TimeSpan ttl)
    {
        var now = timeProvider.GetUtcNow();
        lock (sync)
        {
            entries.Remove(symbol);
            if (entries.Count >= Capacity)
            {
                foreach (var expired in entries.Where(pair => pair.Value.ExpiresAt <= now).Select(pair => pair.Key).ToList())
                {
                    entries.Remove(expired);
                }
            }

            while (entries.Count >= Capacity)
            {
                entries.Remove(entries.MinBy(pair => pair.Value.Sequence).Key);
            }

            entries[symbol] = new Entry(currencyCode, ++sequence, now + ttl);
        }
    }

    private sealed record Entry(string? CurrencyCode, long Sequence, DateTimeOffset ExpiresAt);
}

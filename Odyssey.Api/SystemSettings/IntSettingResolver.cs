using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Odyssey.Context;

namespace Odyssey.Api.SystemSettings;

/// <summary>Which way a degraded read moves an int setting: toward the safer end of its pair.</summary>
public enum ConservativeDirection
{
    /// <summary>A cap — a smaller value is safer, so a degraded read is <c>min(last-known-good, default)</c>.</summary>
    Smaller,

    /// <summary>A floor — a larger value is safer, so a degraded read is <c>max(last-known-good, default)</c>.</summary>
    Larger,
}

/// <summary>How one stored int setting resolved on the read path.</summary>
public enum IntSettingOutcome
{
    /// <summary>Absent (the shipped default) or present and inside its pair.</summary>
    Healthy,

    /// <summary>Parsed but outside its pair, so it resolved to the nearer bound. Reported, not degraded.</summary>
    Clamped,

    /// <summary>Unparseable, or the query failed. Resolved conservatively from the watermark.</summary>
    Degraded,
}

/// <summary>The value an int setting resolved to, and which of the stored states produced it.</summary>
public readonly record struct ResolvedIntSetting(int Value, IntSettingOutcome Outcome)
{
    public bool IsDegraded => Outcome == IntSettingOutcome.Degraded;
}

/// <summary>
/// One int setting as the read path sees it: its key, shipped default, bound pair and conservative
/// direction. <paramref name="Min"/>/<paramref name="Max"/> are the <c>SystemSettingsBounds</c> pair —
/// the same one the <c>[Range]</c> enforces on write — so a row written by a hand edit or a restore
/// cannot carry a value past either end into the consumer.
/// </summary>
/// <param name="ColdFallback">
/// What a degraded read resolves against when this instance has no watermark yet. Defaults to
/// <paramref name="Default"/>; a surface with a stricter cold floor (the import/export sizes) names it.
/// </param>
public sealed record IntSettingSpec(
    string Key,
    int Default,
    int Min,
    int Max,
    ConservativeDirection Direction = ConservativeDirection.Smaller,
    int? ColdFallback = null);

/// <summary>
/// The single implementation of the read-path contract for an int setting (issue #287 H1), shared by
/// every settings lookup. Before it each lookup carried its own parse/clamp/degrade copy, and the copies
/// had drifted: two never clamped to the ceiling, one treated <c>"0"</c> as degraded while another
/// clamped it, and six stored the last-known-good watermark with no expiry.
///
/// <para>The five stored states, mutually exclusive and exhaustive:</para>
/// <list type="table">
/// <item><term>absent</term><description>the shipped default — <strong>healthy</strong>, not degraded</description></item>
/// <item><term>parses, within the pair</term><description>the stored value</description></item>
/// <item><term>parses, outside the pair</term><description>the nearer bound, <c>"0"</c> included — a warning, not degraded</description></item>
/// <item><term>does not parse</term><description>the conservative end of (watermark, default) — an error, degraded</description></item>
/// <item><term>query failed</term><description>the same — degraded</description></item>
/// </list>
///
/// <para>
/// <strong>The watermark carries the TTL.</strong> A watermark older than the TTL is "last known", not
/// "last known good", and letting one outlive every other bound is how a degraded read stops being
/// bounded at all.
/// </para>
///
/// <para>
/// <strong>Whether a degraded result is cached is the caller's call</strong>, not this type's: the
/// lookups disagree about it on principle (thundering herd against immediate recovery), and each
/// records why.
/// </para>
///
/// <para>
/// Log lines are throttled to one per key per TTL window and never carry the stored value — the same
/// no-echo rule the caller-facing advisory follows. The key is enough for an operator to find the row.
/// </para>
/// </summary>
public sealed class IntSettingResolver(IMemoryCache cache, ILogger logger, string cachePrefix)
{
    public static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    private string WatermarkKey(string key) => cachePrefix + ":lkg:" + key;

    private string LoggedKey(string key) => cachePrefix + ":logged:" + key;

    /// <summary>
    /// Reads the rows for one key set, returning an explicit <c>readFailed</c> signal alongside them. An
    /// empty dictionary from a successful query means "absent", which is healthy; only the flag can say
    /// "degraded", which is why it is returned rather than inferred.
    /// </summary>
    public async Task<(IReadOnlyDictionary<string, string> Values, bool ReadFailed)> ReadAsync(
        OdysseyContext context, IReadOnlyCollection<string> keys, string what, CancellationToken cancellationToken)
    {
        try
        {
            var values = await context.SystemSettings.AsNoTracking()
                .Where(row => keys.Contains(row.Key))
                .ToDictionaryAsync(row => row.Key, row => row.Value, cancellationToken);
            return (values, false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Degrade rather than throw: these sit on ordinary read and write paths, and a settings read
            // fault must not turn a user's save into a 500.
            logger.LogError(exception, "Reading the {What} failed; falling back conservatively.", what);
            return (new Dictionary<string, string>(), true);
        }
    }

    public ResolvedIntSetting Resolve(
        IntSettingSpec spec, IReadOnlyDictionary<string, string> values, bool readFailed)
    {
        if (!readFailed)
        {
            if (!values.TryGetValue(spec.Key, out var stored))
            {
                cache.Set(WatermarkKey(spec.Key), spec.Default, CacheTtl);
                return new ResolvedIntSetting(spec.Default, IntSettingOutcome.Healthy);
            }

            if (int.TryParse(stored, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                var clamped = Math.Clamp(parsed, spec.Min, spec.Max);
                cache.Set(WatermarkKey(spec.Key), clamped, CacheTtl);

                if (clamped == parsed)
                {
                    return new ResolvedIntSetting(clamped, IntSettingOutcome.Healthy);
                }

                LogThrottled(spec.Key, LogLevel.Warning,
                    "The stored system setting '{Key}' is outside its allowed range; reading the nearer bound {Bound}.",
                    clamped);
                return new ResolvedIntSetting(clamped, IntSettingOutcome.Clamped);
            }

            LogThrottled(spec.Key, LogLevel.Error,
                "The stored system setting '{Key}' could not be parsed; falling back conservatively.");
        }

        var watermark = cache.TryGetValue(WatermarkKey(spec.Key), out int lastKnownGood)
            ? lastKnownGood
            : spec.ColdFallback ?? spec.Default;

        var value = spec.Direction == ConservativeDirection.Smaller
            ? Math.Min(watermark, spec.Default)
            : Math.Max(watermark, spec.Default);

        return new ResolvedIntSetting(value, IntSettingOutcome.Degraded);
    }

    private void LogThrottled(string key, LogLevel level, string message, params object[] extra)
    {
        var marker = LoggedKey(key);
        if (cache.TryGetValue(marker, out _))
        {
            return;
        }

        cache.Set(marker, true, CacheTtl);

#pragma warning disable CA2254 // Template is a compile-time constant at each call site.
        logger.Log(level, message, [key, .. extra]);
#pragma warning restore CA2254
    }
}

using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Odyssey.Context;
using Odyssey.Core.Journal;
using Odyssey.Dtos;

namespace Odyssey.Api.SystemSettings;

/// <summary>
/// Backs <see cref="IJournalLimitsLookup"/> (issue #421 Wave 3, extended by issue #434): the photo,
/// journal and calendar per-request caps, on the same 30s <see cref="IMemoryCache"/> TTL as its
/// siblings, evicted by <see cref="SystemSettingsService"/> the moment any of the eight changes.
///
/// <para>
/// Separate from <see cref="SystemSettingsLookup"/> because the caps span two domain modules and a
/// lookup interface lives with the code that consumes it — that is what lets <c>Odyssey.Core.Tests</c>
/// fake each one without referencing <c>Odyssey.Context</c>. The Finance and Journal
/// services were separate projects when this split was made; they are now folders in
/// <c>Odyssey.Core</c>, so the boundary is a module convention rather than a compile-time one.
/// </para>
///
/// <para>
/// <strong>Degradation matches <see cref="ImportExportLimitsLookup"/>, deliberately and on purpose.</strong>
/// Every value here is a cap, so the conservative direction is <c>min(last-known-good, compiled
/// default)</c> for all eight, and <see cref="JournalLimits.IsDegraded"/> reports when any of them fell
/// back. Before issue #434 this record kept no watermark and had no flag at all: a corrupt row silently
/// yielded the compiled default and the result was cached as if it were healthy. That mattered once the
/// two link caps started being read from here on the ICS import path as well as the create/update path —
/// one setting resolving by two different rules depending on the reader is precisely the divergence
/// §9-A exists to remove.
/// </para>
///
/// <para>
/// <strong>Where it deliberately differs: a degraded result IS cached here.</strong>
/// <see cref="ImportExportLimitsLookup"/> skips the cache while degraded so recovery is immediate, and
/// it can afford to because its surface sits behind a two-permit global import limiter. These caps sit
/// on ordinary photo/journal/task create and update requests with no limiter at all, so re-querying per
/// request while the database is already unhealthy would be a thundering herd at exactly the wrong
/// moment. Recovery therefore lingers for up to the 30s TTL, which is the same staleness bound every
/// other value here already carries. This is also the behaviour this lookup has always had; the
/// watermark is what is new.
/// </para>
///
/// <para>
/// The watermarks live in <see cref="IMemoryCache"/> rather than <c>static</c> fields. Both have the
/// same lifetime in production (the cache is a singleton), but the cache is container-scoped, so a
/// watermark cannot leak between test classes running in parallel.
/// </para>
/// </summary>
public sealed class JournalLimitsLookup(
    OdysseyContext context,
    IMemoryCache cache,
    ILogger<JournalLimitsLookup> logger) : IJournalLimitsLookup
{
    internal const string CacheKey = "system-settings:journal-request-caps";
    private const string LastKnownGoodPrefix = "system-settings:journal-request-caps:lkg:";
    private const long BytesPerMegabyte = 1024 * 1024;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    private static readonly string[] Keys =
    [
        SystemSettingsKeys.PhotoMaxLinksPerKind,
        SystemSettingsKeys.PhotoMaxAlbumMembers,
        SystemSettingsKeys.JournalEntryMaxLinksPerKind,
        SystemSettingsKeys.JournalTaskMaxLinksPerKind,
        SystemSettingsKeys.PhotoMetadataReadMegabytes,
        SystemSettingsKeys.PhotoMetadataExtractionTimeoutSeconds,
        SystemSettingsKeys.CalendarMaxWindowDays,
        SystemSettingsKeys.CalendarMaxEventDurationDays,
        SystemSettingsKeys.RecurrenceMaxGeneratedOccurrences,
    ];

    public async Task<JournalLimits> GetAsync(CancellationToken cancellationToken = default)
    {
        if (cache.TryGetValue(CacheKey, out JournalLimits? cached) && cached is not null)
        {
            return cached;
        }

        Dictionary<string, string>? values;
        try
        {
            values = await context.SystemSettings.AsNoTracking()
                .Where(row => Keys.Contains(row.Key))
                .ToDictionaryAsync(row => row.Key, row => row.Value, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // These sit on ordinary create/update paths, so a settings read fault must degrade rather
            // than turn a user's save into a 500.
            logger.LogError(exception, "Reading the journal request caps failed; falling back conservatively.");
            values = null;
        }

        var readFailed = values is null;
        var rows = values ?? [];
        var degraded = readFailed;

        var limits = new JournalLimits(
            Cap(rows, SystemSettingsKeys.PhotoMaxLinksPerKind,
                SystemSettingsDefaults.PhotoMaxLinksPerKind, readFailed, ref degraded),
            Cap(rows, SystemSettingsKeys.PhotoMaxAlbumMembers,
                SystemSettingsDefaults.PhotoMaxAlbumMembers, readFailed, ref degraded),
            Cap(rows, SystemSettingsKeys.JournalEntryMaxLinksPerKind,
                SystemSettingsDefaults.JournalEntryMaxLinksPerKind, readFailed, ref degraded),
            Cap(rows, SystemSettingsKeys.JournalTaskMaxLinksPerKind,
                SystemSettingsDefaults.JournalTaskMaxLinksPerKind, readFailed, ref degraded),
            Cap(rows, SystemSettingsKeys.PhotoMetadataReadMegabytes,
                SystemSettingsDefaults.PhotoMetadataReadMegabytes, readFailed, ref degraded) * BytesPerMegabyte,
            Cap(rows, SystemSettingsKeys.PhotoMetadataExtractionTimeoutSeconds,
                SystemSettingsDefaults.PhotoMetadataExtractionTimeoutSeconds, readFailed, ref degraded),
            Cap(rows, SystemSettingsKeys.CalendarMaxWindowDays,
                SystemSettingsDefaults.CalendarMaxWindowDays, readFailed, ref degraded),
            Cap(rows, SystemSettingsKeys.CalendarMaxEventDurationDays,
                SystemSettingsDefaults.CalendarMaxEventDurationDays, readFailed, ref degraded),
            // Clamped to the shipped default even on a clean read: this key is tighten-only, and
            // [Range] on the write DTO is the only write-side bound — which runs on the HTTP path
            // alone. A row written by config adoption, a hand edit or a restore would otherwise carry
            // a value above the pinned bound straight into the generator, re-opening exactly the write
            // amplification the tighten-only conversion closed (issue #434 §9, V3-S1).
            Math.Min(
                Cap(rows, SystemSettingsKeys.RecurrenceMaxGeneratedOccurrences,
                    SystemSettingsDefaults.RecurrenceMaxGeneratedOccurrences, readFailed, ref degraded),
                SystemSettingsDefaults.RecurrenceMaxGeneratedOccurrences),
            degraded);

        cache.Set(CacheKey, limits, CacheTtl);
        return limits;
    }

    /// <summary>
    /// Resolves one cap. Absent-but-query-succeeded is <strong>healthy</strong> and yields the compiled
    /// default; only a failed query or a present-but-unusable value is degraded, and a degraded one
    /// resolves to <c>min(last-known-good, default)</c> — <c>min</c> because every value here is a cap.
    /// </summary>
    private int Cap(
        IReadOnlyDictionary<string, string> values, string key, int fallback, bool readFailed, ref bool degraded)
    {
        if (!readFailed)
        {
            if (!values.TryGetValue(key, out var stored))
            {
                cache.Set(LastKnownGoodPrefix + key, fallback);
                return fallback;
            }

            if (int.TryParse(stored, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                && parsed > 0)
            {
                cache.Set(LastKnownGoodPrefix + key, parsed);
                return parsed;
            }

            logger.LogError(
                "The stored journal request cap '{Key}' has an unusable value '{Value}'; falling back conservatively.",
                key, stored);
        }

        degraded = true;
        var lastKnownGood = cache.TryGetValue(LastKnownGoodPrefix + key, out int watermark) ? watermark : fallback;
        return Math.Min(lastKnownGood, fallback);
    }
}

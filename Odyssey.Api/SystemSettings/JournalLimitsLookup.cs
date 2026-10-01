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
/// Parsing, the read-path clamp to each key's <c>SystemSettingsBounds</c> pair, the watermark (which
/// carries the TTL) and the logging are <see cref="IntSettingResolver"/>'s (issue #287 H1).
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
    private const long BytesPerMegabyte = 1024 * 1024;

    private static readonly IntSettingSpec PhotoMaxLinksPerKind = new(
        SystemSettingsKeys.PhotoMaxLinksPerKind, SystemSettingsDefaults.PhotoMaxLinksPerKind,
        SystemSettingsBounds.PhotoMaxLinksPerKindMin, SystemSettingsBounds.PhotoMaxLinksPerKindMax);

    private static readonly IntSettingSpec PhotoMaxAlbumMembers = new(
        SystemSettingsKeys.PhotoMaxAlbumMembers, SystemSettingsDefaults.PhotoMaxAlbumMembers,
        SystemSettingsBounds.PhotoMaxAlbumMembersMin, SystemSettingsBounds.PhotoMaxAlbumMembersMax);

    private static readonly IntSettingSpec JournalEntryMaxLinksPerKind = new(
        SystemSettingsKeys.JournalEntryMaxLinksPerKind, SystemSettingsDefaults.JournalEntryMaxLinksPerKind,
        SystemSettingsBounds.JournalEntryMaxLinksPerKindMin, SystemSettingsBounds.JournalEntryMaxLinksPerKindMax);

    private static readonly IntSettingSpec JournalTaskMaxLinksPerKind = new(
        SystemSettingsKeys.JournalTaskMaxLinksPerKind, SystemSettingsDefaults.JournalTaskMaxLinksPerKind,
        SystemSettingsBounds.JournalTaskMaxLinksPerKindMin, SystemSettingsBounds.JournalTaskMaxLinksPerKindMax);

    private static readonly IntSettingSpec PhotoMetadataReadMegabytes = new(
        SystemSettingsKeys.PhotoMetadataReadMegabytes, SystemSettingsDefaults.PhotoMetadataReadMegabytes,
        SystemSettingsBounds.PhotoMetadataReadMegabytesMin, SystemSettingsBounds.PhotoMetadataReadMegabytesMax);

    private static readonly IntSettingSpec PhotoMetadataExtractionTimeoutSeconds = new(
        SystemSettingsKeys.PhotoMetadataExtractionTimeoutSeconds,
        SystemSettingsDefaults.PhotoMetadataExtractionTimeoutSeconds,
        SystemSettingsBounds.PhotoMetadataExtractionTimeoutSecondsMin,
        SystemSettingsBounds.PhotoMetadataExtractionTimeoutSecondsMax);

    private static readonly IntSettingSpec CalendarMaxWindowDays = new(
        SystemSettingsKeys.CalendarMaxWindowDays, SystemSettingsDefaults.CalendarMaxWindowDays,
        SystemSettingsBounds.CalendarMaxWindowDaysMin, SystemSettingsBounds.CalendarMaxWindowDaysMax);

    private static readonly IntSettingSpec CalendarMaxEventDurationDays = new(
        SystemSettingsKeys.CalendarMaxEventDurationDays, SystemSettingsDefaults.CalendarMaxEventDurationDays,
        SystemSettingsBounds.CalendarMaxEventDurationDaysMin, SystemSettingsBounds.CalendarMaxEventDurationDaysMax);

    // Tighten-only: the pair's maximum IS the shipped default, so the clamp holds a row written by a
    // hand edit or a restore at the pinned bound — re-opening the write amplification the tighten-only
    // conversion closed is exactly what it prevents (issue #434 §9, V3-S1).
    private static readonly IntSettingSpec RecurrenceMaxGeneratedOccurrences = new(
        SystemSettingsKeys.RecurrenceMaxGeneratedOccurrences, SystemSettingsDefaults.RecurrenceMaxGeneratedOccurrences,
        SystemSettingsBounds.RecurrenceMaxGeneratedOccurrencesMin,
        SystemSettingsBounds.RecurrenceMaxGeneratedOccurrencesMax);

    private static readonly IntSettingSpec[] Specs =
    [
        PhotoMaxLinksPerKind,
        PhotoMaxAlbumMembers,
        JournalEntryMaxLinksPerKind,
        JournalTaskMaxLinksPerKind,
        PhotoMetadataReadMegabytes,
        PhotoMetadataExtractionTimeoutSeconds,
        CalendarMaxWindowDays,
        CalendarMaxEventDurationDays,
        RecurrenceMaxGeneratedOccurrences,
    ];

    private static readonly string[] Keys = [.. Specs.Select(spec => spec.Key)];

    private readonly IntSettingResolver resolver = new(cache, logger, CacheKey);

    public async Task<JournalLimits> GetAsync(CancellationToken cancellationToken = default)
    {
        if (cache.TryGetValue(CacheKey, out JournalLimits? cached) && cached is not null)
        {
            return cached;
        }

        var (values, readFailed) = await resolver.ReadAsync(context, Keys, "journal request caps", cancellationToken);
        var degraded = false;

        int Cap(IntSettingSpec spec)
        {
            var resolved = resolver.Resolve(spec, values, readFailed);
            degraded |= resolved.IsDegraded;
            return resolved.Value;
        }

        var limits = new JournalLimits(
            Cap(PhotoMaxLinksPerKind),
            Cap(PhotoMaxAlbumMembers),
            Cap(JournalEntryMaxLinksPerKind),
            Cap(JournalTaskMaxLinksPerKind),
            Cap(PhotoMetadataReadMegabytes) * BytesPerMegabyte,
            Cap(PhotoMetadataExtractionTimeoutSeconds),
            Cap(CalendarMaxWindowDays),
            Cap(CalendarMaxEventDurationDays),
            Cap(RecurrenceMaxGeneratedOccurrences),
            degraded);

        cache.Set(CacheKey, limits, IntSettingResolver.CacheTtl);
        return limits;
    }
}

using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Odyssey.Context;
using Odyssey.Core.Journal;
using Odyssey.Dtos;

namespace Odyssey.Api.SystemSettings;

/// <summary>
/// Backs <see cref="IImportExportLimitsLookup"/> for the four import/export services' hot read paths
/// (issue #343 §5 item 4): a 30s <see cref="IMemoryCache"/> TTL bounds cross-instance staleness,
/// mirroring <see cref="SystemSettingsLookup"/>. <see cref="SystemSettingsService.UpdateAsync"/>
/// evicts <see cref="CacheKey"/> the moment any import/export field actually changes.
/// <para>
/// On a read failure — the whole query throwing, or an individual row's value failing to parse — this
/// degrades per §11's <b>monotonic</b> fail-safe rather than either throwing or silently reverting to
/// the (sometimes far more permissive) compiled defaults: <c>effective = min(LKG-or-floor,
/// seeded-or-floor)</c>, evaluated independently per key, then all four export/import count pairs are
/// re-clamped (<c>export' = min(export', import')</c>) so a degraded read can never itself violate the
/// round-trip invariant nor loosen a healthy sibling (AC 28b). "LKG" (last known good) is the most
/// recent value this instance itself successfully read for that key, held for the same 30s TTL as the
/// values themselves — a watermark older than that is "last known", not "last known good" — so a
/// value not read successfully within the window behaves as "missing" and falls to the cold floor.
/// </para>
/// <para>
/// The sizes and the five plain bounds resolve through <see cref="IntSettingResolver"/> (issue #287
/// H1), which also clamps each to its <c>SystemSettingsBounds</c> pair on a clean read. The counts stay
/// bespoke: they have an "unlimited" spelling and no bound pair.
/// </para>
/// </summary>
public sealed class ImportExportLimitsLookup(
    OdysseyContext context, IMemoryCache cache, ILogger<ImportExportLimitsLookup> logger)
    : IImportExportLimitsLookup
{
    internal const string CacheKey = "system-settings:import-export-limits";
    private const string LastKnownGoodPrefix = "system-settings:import-export-limits:lkg:";
    private static readonly TimeSpan CacheTtl = IntSettingResolver.CacheTtl;

    // §11 cold floors — the least permissive value a degraded read can ever fall back to, regardless
    // of how permissive the last-known-good or seeded value was.
    private const int SizeFloorMb = 5;
    private const int IcsCountFloor = 2000;
    private const int ContactCountFloor = 100_000;

    private readonly IntSettingResolver resolver = new(cache, logger, CacheKey);

    private static readonly string[] Keys =
    [
        SystemSettingsKeys.ContactVCardMaxExportRows,
        SystemSettingsKeys.ContactVCardMaxImportEntries,
        SystemSettingsKeys.ContactVCardMaxImportMegabytes,
        SystemSettingsKeys.ContactVCardMaxExportMegabytes,
        SystemSettingsKeys.CalendarIcsMaxExportEvents,
        SystemSettingsKeys.CalendarIcsMaxImportEvents,
        SystemSettingsKeys.CalendarIcsMaxImportMegabytes,
        SystemSettingsKeys.CalendarIcsMaxExportMegabytes,
        SystemSettingsKeys.TaskIcsMaxExportTasks,
        SystemSettingsKeys.TaskIcsMaxImportTasks,
        SystemSettingsKeys.TaskIcsMaxImportMegabytes,
        SystemSettingsKeys.TaskIcsMaxExportMegabytes,
        SystemSettingsKeys.JournalIcsMaxExportRows,
        SystemSettingsKeys.JournalIcsMaxImportEntries,
        SystemSettingsKeys.JournalIcsMaxImportMegabytes,
        SystemSettingsKeys.JournalIcsMaxExportMegabytes,
        SystemSettingsKeys.CalendarIcsMaxAggregateExportRows,
        SystemSettingsKeys.CalendarIcsMaxAggregateOccurrences,
        SystemSettingsKeys.CalendarIcsMaxAggregateExportWindowDays,
        SystemSettingsKeys.ContactVCardMaxRepeatablePropertiesPerEntry,
        SystemSettingsKeys.ImportMaxSamplesPerSkipReason,
    ];

    public async Task<ImportExportLimits> GetAsync(CancellationToken cancellationToken = default)
    {
        if (cache.TryGetValue(CacheKey, out ImportExportLimits? cached) && cached is not null)
        {
            return cached;
        }

        var (raw, readFailed) = await resolver.ReadAsync(context, Keys, "import/export limits", cancellationToken);
        var resolved = Resolve(raw, readFailed);
        var degraded = resolved.IsDegraded;

        // A degraded resolution is intentionally NOT cached under the normal 30s TTL — recomputing it
        // per call is cheap (no DB round trip once the read itself is failing) and means recovery is
        // immediate on the next successful read rather than lingering for up to 30s.
        if (!degraded)
        {
            cache.Set(CacheKey, resolved, CacheTtl);
        }

        return resolved;
    }

    private ImportExportLimits Resolve(IReadOnlyDictionary<string, string> raw, bool wholeReadFailed)
    {
        var anyDegraded = wholeReadFailed;

        int? ResolveCount(string key, int floor, int? seededDefault)
        {
            if (!wholeReadFailed && !raw.TryGetValue(key, out _))
            {
                // Row absent but the query itself succeeded — "should never happen post-migration",
                // same defensive posture as SystemSettingsService: use the compiled default as a
                // healthy value, not a degraded one. Corrupt/unparseable (row present, bad value) is
                // the AC 28 case below, and is treated differently.
                cache.Set(LastKnownGoodPrefix + key, seededDefault, IntSettingResolver.CacheTtl);
                return seededDefault;
            }

            if (!wholeReadFailed && raw.TryGetValue(key, out var value) && SystemSettingsKeys.TryParseCount(value, out var parsed))
            {
                cache.Set(LastKnownGoodPrefix + key, parsed, IntSettingResolver.CacheTtl);
                return parsed;
            }

            if (!wholeReadFailed)
            {
                logger.LogError(
                    "Import/export limit '{Key}' has an unparseable stored value; falling back to a degraded value.", key);
            }

            anyDegraded = true;
            var lkgOrFloor = cache.TryGetValue(LastKnownGoodPrefix + key, out int? lkg) && lkg is { } finite ? finite : floor;
            var seededOrFloor = seededDefault ?? floor;
            return Math.Min(lkgOrFloor, seededOrFloor);
        }

        // A size is a bounded int with one twist: a degraded read with no watermark resolves against the
        // §11 cold floor rather than the compiled default, which is why the spec names ColdFallback.
        long ResolveSizeBytes(string key, int seededDefaultMb, int min, int max) =>
            Bound(new IntSettingSpec(key, seededDefaultMb, min, max, ColdFallback: SizeFloorMb)) * 1024L * 1024L;

        // A plain positive-integer bound with no "unlimited" spelling and no cold floor: the compiled
        // default IS the floor, because every one of these shipped as a const that nobody could raise.
        int ResolveBound(string key, int seededDefault, int min, int max) =>
            Bound(new IntSettingSpec(key, seededDefault, min, max));

        int Bound(IntSettingSpec spec)
        {
            var result = resolver.Resolve(spec, raw, wholeReadFailed);
            anyDegraded |= result.IsDegraded;
            return result.Value;
        }

        var contactExport = ResolveCount(SystemSettingsKeys.ContactVCardMaxExportRows, ContactCountFloor, seededDefault: null);
        var contactImport = ResolveCount(SystemSettingsKeys.ContactVCardMaxImportEntries, ContactCountFloor, seededDefault: null);
        contactExport = ClampExport(contactExport, contactImport);

        var calendarExport = ResolveCount(
            SystemSettingsKeys.CalendarIcsMaxExportEvents, IcsCountFloor, SystemSettingsDefaults.CalendarIcsMaxExportEvents);
        var calendarImport = ResolveCount(
            SystemSettingsKeys.CalendarIcsMaxImportEvents, IcsCountFloor, SystemSettingsDefaults.CalendarIcsMaxImportEvents);
        calendarExport = ClampExport(calendarExport, calendarImport);

        var journalExport = ResolveCount(
            SystemSettingsKeys.JournalIcsMaxExportRows, IcsCountFloor, SystemSettingsDefaults.JournalIcsMaxExportRows);
        var journalImport = ResolveCount(
            SystemSettingsKeys.JournalIcsMaxImportEntries, IcsCountFloor, SystemSettingsDefaults.JournalIcsMaxImportEntries);
        journalExport = ClampExport(journalExport, journalImport);

        var taskExport = ResolveCount(
            SystemSettingsKeys.TaskIcsMaxExportTasks, IcsCountFloor, SystemSettingsDefaults.TaskIcsMaxExportTasks);
        var taskImport = ResolveCount(
            SystemSettingsKeys.TaskIcsMaxImportTasks, IcsCountFloor, SystemSettingsDefaults.TaskIcsMaxImportTasks);
        taskExport = ClampExport(taskExport, taskImport);

        var contactImportBytes = ResolveSizeBytes(
            SystemSettingsKeys.ContactVCardMaxImportMegabytes, SystemSettingsDefaults.ContactVCardMaxImportMegabytes,
            SystemSettingsBounds.ContactVCardMaxImportMegabytesMin, SystemSettingsBounds.ContactVCardMaxImportMegabytesMax);
        var contactExportBytes = ResolveSizeBytes(
            SystemSettingsKeys.ContactVCardMaxExportMegabytes, SystemSettingsDefaults.ContactVCardMaxExportMegabytes,
            SystemSettingsBounds.ContactVCardMaxExportMegabytesMin, SystemSettingsBounds.ContactVCardMaxExportMegabytesMax);
        var calendarImportBytes = ResolveSizeBytes(
            SystemSettingsKeys.CalendarIcsMaxImportMegabytes, SystemSettingsDefaults.CalendarIcsMaxImportMegabytes,
            SystemSettingsBounds.CalendarIcsMaxImportMegabytesMin, SystemSettingsBounds.CalendarIcsMaxImportMegabytesMax);
        var calendarExportBytes = ResolveSizeBytes(
            SystemSettingsKeys.CalendarIcsMaxExportMegabytes, SystemSettingsDefaults.CalendarIcsMaxExportMegabytes,
            SystemSettingsBounds.CalendarIcsMaxExportMegabytesMin, SystemSettingsBounds.CalendarIcsMaxExportMegabytesMax);
        var taskImportBytes = ResolveSizeBytes(
            SystemSettingsKeys.TaskIcsMaxImportMegabytes, SystemSettingsDefaults.TaskIcsMaxImportMegabytes,
            SystemSettingsBounds.TaskIcsMaxImportMegabytesMin, SystemSettingsBounds.TaskIcsMaxImportMegabytesMax);
        var taskExportBytes = ResolveSizeBytes(
            SystemSettingsKeys.TaskIcsMaxExportMegabytes, SystemSettingsDefaults.TaskIcsMaxExportMegabytes,
            SystemSettingsBounds.TaskIcsMaxExportMegabytesMin, SystemSettingsBounds.TaskIcsMaxExportMegabytesMax);
        var journalImportBytes = ResolveSizeBytes(
            SystemSettingsKeys.JournalIcsMaxImportMegabytes, SystemSettingsDefaults.JournalIcsMaxImportMegabytes,
            SystemSettingsBounds.JournalIcsMaxImportMegabytesMin, SystemSettingsBounds.JournalIcsMaxImportMegabytesMax);
        var journalExportBytes = ResolveSizeBytes(
            SystemSettingsKeys.JournalIcsMaxExportMegabytes, SystemSettingsDefaults.JournalIcsMaxExportMegabytes,
            SystemSettingsBounds.JournalIcsMaxExportMegabytesMin, SystemSettingsBounds.JournalIcsMaxExportMegabytesMax);

        // The five issue #434 bounds that belong to the import/export surfaces. Plain positive-integer caps, so ResolveBound is the whole story:
        // absent-but-query-succeeded is healthy, present-but-unusable or a failed query is degraded, and
        // a degraded read resolves to min(last-known-good, default).
        //
        // Every one is clamped to its bound pair on a clean read. For the tighten-only vCard repeatable-
        // property cap the pair's maximum IS the shipped default, and [Range] on the write DTO is the
        // only write-side bound — which runs on the HTTP path alone. A row written by config adoption, by a hand edit or by a restore would
        // otherwise carry a value above the pinned bound straight into the service, re-opening exactly
        // the write amplification the tighten-only conversion closed (issue #434 §9, V3-S1).
        var aggregateExportRows = ResolveBound(
            SystemSettingsKeys.CalendarIcsMaxAggregateExportRows,
            SystemSettingsDefaults.CalendarIcsMaxAggregateExportRows,
            SystemSettingsBounds.CalendarIcsMaxAggregateExportRowsMin,
            SystemSettingsBounds.CalendarIcsMaxAggregateExportRowsMax);
        var aggregateOccurrences = ResolveBound(
            SystemSettingsKeys.CalendarIcsMaxAggregateOccurrences,
            SystemSettingsDefaults.CalendarIcsMaxAggregateOccurrences,
            SystemSettingsBounds.CalendarIcsMaxAggregateOccurrencesMin,
            SystemSettingsBounds.CalendarIcsMaxAggregateOccurrencesMax);
        var aggregateExportWindowDays = ResolveBound(
            SystemSettingsKeys.CalendarIcsMaxAggregateExportWindowDays,
            SystemSettingsDefaults.CalendarIcsMaxAggregateExportWindowDays,
            SystemSettingsBounds.CalendarIcsMaxAggregateExportWindowDaysMin,
            SystemSettingsBounds.CalendarIcsMaxAggregateExportWindowDaysMax);
        var repeatableProperties = ResolveBound(
            SystemSettingsKeys.ContactVCardMaxRepeatablePropertiesPerEntry,
            SystemSettingsDefaults.ContactVCardMaxRepeatablePropertiesPerEntry,
            SystemSettingsBounds.ContactVCardMaxRepeatablePropertiesPerEntryMin,
            SystemSettingsBounds.ContactVCardMaxRepeatablePropertiesPerEntryMax);
        var samplesPerSkipReason = ResolveBound(
            SystemSettingsKeys.ImportMaxSamplesPerSkipReason,
            SystemSettingsDefaults.ImportMaxSamplesPerSkipReason,
            SystemSettingsBounds.ImportMaxSamplesPerSkipReasonMin,
            SystemSettingsBounds.ImportMaxSamplesPerSkipReasonMax);

        return new ImportExportLimits(
            contactExport, contactImport, contactImportBytes, contactExportBytes,
            calendarExport, calendarImport, calendarImportBytes, calendarExportBytes,
            taskExport, taskImport, taskImportBytes, taskExportBytes,
            journalExport, journalImport, journalImportBytes, journalExportBytes,
            aggregateExportRows, aggregateOccurrences, aggregateExportWindowDays,
            repeatableProperties, samplesPerSkipReason,
            anyDegraded);
    }

    // Applied after BOTH members of a pair have been independently resolved (§11): never raises a
    // healthy import value (AC 28b property (b)), but keeps the pair's own round-trip invariant true
    // (property (a)) even when one member degraded and the other didn't. Treats null (unlimited) as
    // +∞ on both sides, matching the write-time rule (issue #343 §9).
    private static int? ClampExport(int? export, int? import) =>
        import is { } importValue && (export is null || export > importValue) ? importValue : export;
}

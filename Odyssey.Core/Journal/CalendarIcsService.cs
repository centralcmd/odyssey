using Odyssey.Core;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Odyssey.Core.Finance;
using Odyssey.Core.Pagination;
using Odyssey.Context;
using Odyssey.Core.Journal.Interop;
using Odyssey.Core.Locations;
using IcalCalendar = Ical.Net.Calendar;
using IcalEvent = Ical.Net.CalendarComponents.CalendarEvent;
using IcalRecurrencePattern = Ical.Net.DataTypes.RecurrencePattern;
// The RRULE as read back off a parsed VEVENT: CalendarEvent.RecurrenceRule is typed as the base
// RecurrenceRule, which carries every part MapPattern inspects. IcalRecurrencePattern (its subclass)
// stays the build-side type, since that is what BuildRRule constructs.
using IcalRecurrenceRule = Ical.Net.DataTypes.RecurrenceRule;
using CalDateTime = Ical.Net.DataTypes.CalDateTime;
using RecurrenceIdentifier = Ical.Net.DataTypes.RecurrenceIdentifier;
using RecurrenceRange = Ical.Net.DataTypes.RecurrenceRange;
using WeekDay = Ical.Net.DataTypes.WeekDay;
using FrequencyType = Ical.Net.FrequencyType;
using ContextCalendarEvent = Odyssey.Context.CalendarEvent;
using CalendarEventsIcsExportQueryParams = Odyssey.Dtos.Journal.CalendarEventsIcsExportQueryParams;
using CalendarIcsExportSummary = Odyssey.Dtos.Journal.CalendarIcsExportSummary;
using IcsImportResult = Odyssey.Dtos.Journal.IcsImportResult;
using IcsImportSkipGroup = Odyssey.Dtos.Journal.IcsImportSkipGroup;

namespace Odyssey.Core.Journal;

/// <summary>
/// What one calendar <c>.ics</c> export covers (issue #516 §2 Goal 1): one calendar, one event, one series, or the
/// filtered or unfiltered aggregate across calendars.
/// </summary>
public sealed record CalendarIcsExportScope
{
    private CalendarIcsExportScope()
    {
    }

    public Guid? CalendarId { get; private init; }

    public Guid? EventId { get; private init; }

    public Guid? PatternId { get; private init; }

    public CalendarEventsIcsExportQueryParams? Aggregate { get; private init; }

    public static CalendarIcsExportScope ForCalendar(Guid calendarId) => new() { CalendarId = calendarId };

    public static CalendarIcsExportScope ForEvent(Guid eventId) => new() { EventId = eventId };

    public static CalendarIcsExportScope ForPattern(Guid patternId) => new() { PatternId = patternId };

    public static CalendarIcsExportScope ForAggregate(CalendarEventsIcsExportQueryParams query) => new() { Aggregate = query };
}

/// <summary>
/// ICS (RFC 5545) import/export for calendars (issues #330, #340, #516, #519).
/// </summary>
/// <remarks>
/// <para>
/// <b>Export</b> plans up front and streams (issue #516 §3). The planner projects keys and the few columns the collapse
/// decision needs — never whole entities — and captures the ordered ids of the standalone rows, the series that
/// collapse to one <c>RRULE</c>, and each collapsed series' exceptions. It is the single definition of what an export
/// contains, shared by the export and its summary. <b>No export opens a transaction</b> (B1).
/// </para>
/// <para>
/// A keyed series collapses when <see cref="CanExportWithExceptions"/> holds (issue #519 §8.3): one master with
/// <c>RRULE</c>, <c>DTSTART;TZID=…</c> and an <c>EXDATE</c> per exclusion, one <c>RECURRENCE-ID</c> component per row that
/// differs from what the rule generates, and one <c>VTIMEZONE</c> per planned zone in the head. An occurrence moved into
/// another calendar is an <c>EXDATE</c> in its series' calendar file and a standalone event in its own; the series
/// export carries it as a <c>RECURRENCE-ID</c>. The aggregate collapses only a series with no exceptions at all.
/// </para>
/// <para>
/// <b>Import</b> regroups the file by UID first (<see cref="IcsImportGrouper"/>, §3 component 6): pass 1 validates the
/// whole file and caps it before anything is applied; pass 2 yields one UID group at a time, so a master, its
/// <c>EXDATE</c>s and its overrides import as one series in any order. Groups are applied in batches of at most
/// <see cref="ImportBatchSize"/> groups or <see cref="ImportBatchEntityBudget"/> tracked rows, each in its own
/// transaction that resolves outside, locks the series and events it needs first (ascending), re-resolves under the
/// locks and retries if what it needs changed (v4 §2, v5 §2). A change that would remove a stored row or discard a
/// stored exception needs <c>allowRemovals</c> (and <c>calendar.delete</c>); without it the series is left unchanged
/// and counted in <c>gatedSeriesCount</c>.
/// </para>
/// </remarks>
public class CalendarIcsService
{
    /// <summary>UID groups applied per import batch (issue #516 §8.5, #519 v3 §E): a constant, not a setting.</summary>
    public const int ImportBatchSize = 500;

    /// <summary>The rows a batch may track before it is applied early (#519 v3 §E).</summary>
    public const int ImportBatchEntityBudget = 5000;

    /// <summary>
    /// The component characters a batch may span before it is applied early (PR #524 security finding 1). Each
    /// component is parsed and reduced to its bounded <c>ImportItem</c> as it is read, so no parsed <c>Ical.Net</c> graph
    /// outlives its own component.
    /// </summary>
    public const int ImportBatchCharBudget = 16 * 1024 * 1024;

    /// <summary>The overrides one UID group may carry (issue #519 §3 component 6).</summary>
    public const int MaxOverridesPerSeries = 10_000;

    /// <summary>How often one batch re-resolves under its locks before it gives up (v5 §2).</summary>
    public const int MaxBatchAttempts = 3;

    /// <summary>The <c>400</c> code for a file with more components than its size allows (v5 §1).</summary>
    public const string TooManyComponentsCode = IcsImportGrouper.TooManyComponentsCode;

    public const string CancelledUnknownReason = "Event is cancelled.";

    public const string CancelledKeptReason =
        "Event is cancelled in the file; kept, because removing cancelled events was not requested.";

    public const string UidTooLongReason = "UID exceeds the maximum length of 255 characters.";

    public const string GatedReason =
        "Importing this series would remove events or discard separately edited occurrences; tick ‘Allow this import to "
        + "remove events or discard edits’ to apply it.";

    public const string UnknownTimeZoneReason = "Uses a time zone Odyssey doesn't know.";

    public const string OverrideWithoutSeriesReason = "Modified occurrence of a series that is not in this calendar or this file.";

    public const string OverrideMismatchReason = "Modified occurrence does not match the series.";

    public const string ThisAndFutureReason = "Modified ‘this and future’ occurrences are not supported.";

    public const string StrayExclusionReason = "Excluded date is not an occurrence of the series.";

    public const string PastRemovalReason = "Excluded or cancelled occurrence is in the past; it was kept.";

    public const string OverrideCapReason = "Series has more modified occurrences than one import accepts.";

    public const string DuplicateMasterReason = "Series appears more than once in the file; only the last one was applied.";

    public const string SharedUidReason = "Event shares its UID with a recurring series in the file.";

    public const string RecurrenceDatesReason = "Recurrence dates (RDATE) are not supported.";

    /// <summary>Removed ids per audit line, so one batch's removal of a large series never becomes one huge entry.</summary>
    internal const int AuditIdsPerLine = 500;

    private const int MaxUidLength = 255;

    private const string SyntheticUidSuffix = "@odyssey.local";

    // How a UID is compared in memory, matching how the database compares it: the ExternalUid and ImportSourceUid
    // columns use the server's default case- and accent-insensitive collation, so a keyed query returns a case variant
    // and the in-memory pick must accept it too, or it loads the row and then creates a duplicate (architect nit 7).
    // An exact match is still preferred among the candidates.
    internal static readonly StringComparer UidComparer =
        StringComparer.Create(System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.IgnoreNonSpace);
    private const string UntitledEvent = "(untitled)";
    private const string ProductId = "-//Odyssey//Calendar//EN";
    private const int MaxLocationLength = 300;

    private readonly OdysseyContext context;
    private readonly ILogger<CalendarIcsService> logger;
    private readonly IImportExportLimitsLookup limits;
    private readonly IJournalLimitsLookup journalLimits;
    private readonly TimeProvider timeProvider;

    // journalLimits is a SECOND lookup, newly injected in issue #434. The occurrence cap (key 11) and
    // the event-duration bound (key 7) live on JournalLimits because their other consumers are
    // RecurrencePatternService and CalendarEventService, and a setting must have exactly one owner,
    // one cache key, one eviction and one degraded rule. Mirroring them onto ImportExportLimits to
    // save a lookup was tried and abandoned: a descriptor can evict only one cache entry, so a
    // mirrored value would go stale on one of its two readers for up to 30s.
    public CalendarIcsService(
        OdysseyContext context, ILogger<CalendarIcsService> logger, IImportExportLimitsLookup limits,
        IJournalLimitsLookup journalLimits, TimeProvider? timeProvider = null)
    {
        this.context = context;
        this.logger = logger;
        this.limits = limits;
        this.journalLimits = journalLimits;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>The directory the import spill is written to; the process temp directory unless a test points it elsewhere.</summary>
    internal string SpillDirectory { get; set; } = Path.GetTempPath();

    /// <summary>A fixed grouping key, for a test that must know the order groups commit in; random per import otherwise.</summary>
    internal byte[]? GroupingKey { get; set; }

    /// <summary>The grouper's records per run, so a test can force spilled runs and merge rounds on a small file.</summary>
    internal int GroupingRecordsPerRun { get; set; } = IcsImportGrouper.RunBufferBytes / IcsImportGrouper.RecordSize;

    // ---------------------------------------------------------------- Export planning

    // The columns the collapse decision reads, and nothing else: a planner that loaded entities would hold every
    // description of a 100,000-event calendar at once (A5).
    private sealed record RowShape(
        Guid Id, Guid? PatternId, Guid CalendarId, DateTime Start, DateTime End, bool IsAllDay,
        string Title, string? Description, string? Location, Guid? LocationId, DateTime? OccurrenceKey, bool IsOverride,
        string? TimeZoneId);

    // A collapsed series: its master, the rows written as RECURRENCE-ID components, and its EXDATE keys.
    private sealed record PlannedSeries(Guid PatternId, Guid CalendarId, List<Guid> OverrideIds, List<DateTime> ExDates);

    private enum SeriesMode
    {
        CalendarFile,
        SeriesFile,
        Aggregate,
    }

    private sealed record ExportPlan(
        string FileName, bool Aggregate, List<PlannedSeries> Series, List<Guid> StandaloneIds, int OccurrenceCount,
        long MaxBytes, Dictionary<string, (int From, int To)> Zones)
    {
        public int EventCount => Series.Count + Series.Sum(s => s.OverrideIds.Count) + StandaloneIds.Count;
    }

    /// <summary>
    /// What <see cref="ExportStreamingAsync"/> would produce for <paramref name="scope"/>, refusing exactly where it
    /// would: <see cref="ExportLimitExceededException"/> over the event cap, and the aggregate's window and fetch
    /// bounds. Null when the named calendar, event or series does not exist. The byte cap is not judged: text size is
    /// only known as it is written.
    /// </summary>
    public async Task<CalendarIcsExportSummary?> SummarizeExportAsync(
        CalendarIcsExportScope scope, CancellationToken cancellationToken = default)
    {
        var plan = await PlanAsync(scope, cancellationToken);
        return plan is null
            ? null
            : new CalendarIcsExportSummary
            {
                EventCount = plan.EventCount,
                SeriesCount = plan.Series.Count,
                OccurrenceCount = plan.OccurrenceCount,
            };
    }

    /// <summary>
    /// Streams the export of <paramref name="scope"/> to <paramref name="output"/>. Returns false, having written
    /// nothing, when the named calendar, event or series does not exist. <paramref name="onReady"/> is called exactly
    /// once, with the file name and the planned <c>VEVENT</c> count, before the first byte — the caller's only chance
    /// to set headers. Every refusal the summary makes happens before it.
    /// </summary>
    /// <param name="includePlaceNames">Whether the caller holds <c>locations.read</c> (issue #380 §8.4).</param>
    /// <param name="userId">The caller, for the aggregate export's log line.</param>
    public async Task<bool> ExportStreamingAsync(
        CalendarIcsExportScope scope, Stream output, Action<string, int> onReady,
        CancellationToken cancellationToken = default, bool includePlaceNames = false, string? userId = null)
    {
        var plan = await PlanAsync(scope, cancellationToken);
        if (plan is null)
        {
            return false;
        }

        onReady(plan.FileName, plan.EventCount);
        await WriteAsync(plan, output, includePlaceNames, cancellationToken);

        if (plan.Aggregate)
        {
            logger.LogInformation(
                "Aggregate calendar events .ics export by {UserId}: {EventCount} event(s) planned.",
                userId, plan.EventCount);
        }

        return true;
    }

    private async Task<ExportPlan?> PlanAsync(CalendarIcsExportScope scope, CancellationToken cancellationToken)
    {
        var effectiveLimits = await limits.GetAsync(cancellationToken);
        var occurrenceCap = (await journalLimits.GetAsync(cancellationToken)).RecurrenceMaxGeneratedOccurrences;
        var exportDate = timeProvider.GetUtcNow().UtcDateTime;
        var maxEvents = effectiveLimits.CalendarIcsMaxExportEvents;
        var maxBytes = effectiveLimits.CalendarIcsMaxExportBytes;

        ExportPlan? plan;
        if (scope.CalendarId is { } calendarId)
        {
            plan = await PlanCalendarAsync(calendarId, exportDate, occurrenceCap, maxBytes, cancellationToken);
        }
        else if (scope.EventId is { } eventId)
        {
            var title = await context.CalendarEvents.AsNoTracking()
                .Where(e => e.CalendarEventId == eventId)
                .Select(e => e.Title)
                .FirstOrDefaultAsync(cancellationToken);
            plan = title is null
                ? null
                : new ExportPlan(BuildFileName(title, exportDate), false, [], [eventId], 1, maxBytes,
                    await ZonesAsync(context.CalendarEvents.Where(e => e.CalendarEventId == eventId), [], cancellationToken));
        }
        else if (scope.PatternId is { } patternId)
        {
            plan = await PlanPatternAsync(patternId, exportDate, occurrenceCap, maxBytes, cancellationToken);
        }
        else
        {
            plan = await PlanAggregateAsync(scope.Aggregate!, effectiveLimits, exportDate, occurrenceCap, maxBytes, cancellationToken);
        }

        if (plan is not null && maxEvents is { } max && plan.EventCount > max)
        {
            throw new ExportLimitExceededException(max, ExportLimitExceededException.CalendarEventsCode, "events");
        }

        return plan;
    }

    // A bounded number of planning queries whatever the series count (§10, AC 19): the calendar, its series, their
    // exclusions, every row of those series (streamed, one series held at a time), the calendar's own row keys, and the
    // zones.
    private async Task<ExportPlan?> PlanCalendarAsync(
        Guid calendarId, DateTime exportDate, int occurrenceCap, long maxBytes, CancellationToken cancellationToken)
    {
        var name = await context.Calendars.AsNoTracking()
            .Where(c => c.CalendarId == calendarId)
            .Select(c => c.Name)
            .FirstOrDefaultAsync(cancellationToken);
        if (name is null)
        {
            return null;
        }

        var patterns = await context.RecurrencePatterns.AsNoTracking()
            .Where(p => p.CalendarId == calendarId)
            .ToDictionaryAsync(p => p.RecurrencePatternId, cancellationToken);

        var series = new List<(PlannedSeries Series, int Rows)>();
        if (patterns.Count > 0)
        {
            var exclusions = await ExclusionKeysAsync(
                context.RecurrenceExclusions.Where(x => x.RecurrencePattern!.CalendarId == calendarId), cancellationToken);
            var rows = context.CalendarEvents.AsNoTracking()
                .Where(e => e.RecurrencePatternId != null && e.RecurrencePattern!.CalendarId == calendarId);
            series = await DecideSeriesAsync(rows, patterns, exclusions, occurrenceCap, SeriesMode.CalendarFile, cancellationToken);
        }

        // A collapsed series' rows are its master and its RECURRENCE-ID components; only the rows that sit in another
        // calendar stay out of this file entirely (they are EXDATEs here).
        var collapsed = series.Select(s => s.Series.PatternId).ToHashSet();
        var keys = await context.CalendarEvents.AsNoTracking()
            .Where(e => e.CalendarId == calendarId)
            .OrderBy(e => e.StartDateTime).ThenBy(e => e.CalendarEventId)
            .Select(e => new { e.CalendarEventId, e.RecurrencePatternId })
            .ToListAsync(cancellationToken);
        var standalone = keys
            .Where(k => k.RecurrencePatternId is not { } patternId || !collapsed.Contains(patternId))
            .Select(k => k.CalendarEventId)
            .ToList();

        var ordered = series
            .OrderBy(s => patterns[s.Series.PatternId].StartDateTime).ThenBy(s => s.Series.PatternId)
            .ToList();
        var zones = await ZonesAsync(
            context.CalendarEvents.Where(e => e.CalendarId == calendarId),
            ordered.Select(s => patterns[s.Series.PatternId]), cancellationToken);
        return new ExportPlan(
            BuildFileName(name, exportDate), false, ordered.Select(s => s.Series).ToList(), standalone,
            ordered.Sum(s => s.Rows) + standalone.Count, maxBytes, zones);
    }

    private async Task<ExportPlan?> PlanPatternAsync(
        Guid patternId, DateTime exportDate, int occurrenceCap, long maxBytes, CancellationToken cancellationToken)
    {
        var pattern = await context.RecurrencePatterns.AsNoTracking()
            .FirstOrDefaultAsync(p => p.RecurrencePatternId == patternId, cancellationToken);
        if (pattern is null)
        {
            return null;
        }

        var rows = await Shapes(context.CalendarEvents.AsNoTracking()
                .Where(e => e.RecurrencePatternId == patternId)
                .OrderBy(e => e.StartDateTime).ThenBy(e => e.CalendarEventId))
            .ToListAsync(cancellationToken);
        var exclusions = await context.RecurrenceExclusions.AsNoTracking()
            .Where(x => x.RecurrencePatternId == patternId)
            .Select(x => x.OccurrenceKey)
            .ToListAsync(cancellationToken);
        var fileName = BuildFileName(pattern.Title, exportDate);
        var zones = await ZonesAsync(context.CalendarEvents.Where(e => e.RecurrencePatternId == patternId), [pattern], cancellationToken);

        return Decide(pattern, rows, exclusions, occurrenceCap, SeriesMode.SeriesFile, uniformIn: null) is { } planned
            ? new ExportPlan(fileName, false, [planned], [], rows.Count, maxBytes, zones)
            : new ExportPlan(fileName, false, [], rows.Select(r => r.Id).ToList(), rows.Count, maxBytes, zones);
    }

    private async Task<ExportPlan> PlanAggregateAsync(
        CalendarEventsIcsExportQueryParams query, ImportExportLimits effectiveLimits, DateTime exportDate,
        int occurrenceCap, long maxBytes, CancellationToken cancellationToken)
    {
        var (from, to) = ValidateAggregateWindow(query.From, query.To, effectiveLimits.CalendarIcsMaxAggregateExportWindowDays);
        var view = CalendarWindow.ViewZone(query.ViewTimeZoneId);
        var searchTerm = ListQuery.NormalizeSearch(query.Search);
        var calendarIds = query.CalendarIds is { Length: > 0 } ids ? ids.Distinct().ToList() : null;
        var fileName = $"{exportDate:yyyyMMdd}_calendar-events.ics";
        var rowQuery = BuildAggregateRowQuery(calendarIds, from, to, view, searchTerm);
        var maxEvents = effectiveLimits.CalendarIcsMaxExportEvents;

        // A filtered request (From/To and/or Search) never collapses a series: every matched row is its own VEVENT.
        // The id read is bounded to max+1 when a cap is set, so an over-cap export is refused without a full scan.
        if (from is not null || searchTerm is not null)
        {
            var sortKey = CalendarWindow.SortKey(from, to, view);
            var orderedIds = rowQuery
                .OrderBy(sortKey).ThenBy(e => e.CalendarEventId)
                .Select(e => e.CalendarEventId);
            var matched = maxEvents is { } max
                ? await orderedIds.Take(max + 1).ToListAsync(cancellationToken)
                : await orderedIds.ToListAsync(cancellationToken);
            return new ExportPlan(fileName, true, [], matched, matched.Count, maxBytes,
                await ZonesAsync(rowQuery, [], cancellationToken));
        }

        // The unfiltered aggregate stays bounded by CalendarIcsMaxAggregateExportRows (Non-Goal 4): the collapse
        // decision generates every series' occurrences, which is CPU the row cap bounds.
        var maxRows = effectiveLimits.CalendarIcsMaxAggregateExportRows;
        if (await rowQuery.Take(maxRows + 1).CountAsync(cancellationToken) > maxRows)
        {
            throw new DomainValidationException(
                $"The export would need to fetch more than {maxRows} events — narrow the calendar selection.");
        }

        var keys = await rowQuery
            .OrderBy(e => e.StartDateTime).ThenBy(e => e.CalendarEventId)
            .Select(e => new { e.CalendarEventId, e.RecurrencePatternId })
            .ToListAsync(cancellationToken);
        var patternIds = keys.Where(k => k.RecurrencePatternId is not null).Select(k => k.RecurrencePatternId!.Value).Distinct().ToList();
        var series = new List<(PlannedSeries Series, int Rows)>();
        Dictionary<Guid, RecurrencePattern> patterns = [];
        if (patternIds.Count > 0)
        {
            patterns = await context.RecurrencePatterns.AsNoTracking()
                .Where(p => patternIds.Contains(p.RecurrencePatternId))
                .ToDictionaryAsync(p => p.RecurrencePatternId, cancellationToken);
            var exclusions = await ExclusionKeysAsync(
                context.RecurrenceExclusions.Where(x => patternIds.Contains(x.RecurrencePatternId)), cancellationToken);
            series = await DecideSeriesAsync(
                rowQuery.Where(e => e.RecurrencePatternId != null), patterns, exclusions, occurrenceCap, SeriesMode.Aggregate,
                cancellationToken);
        }

        // In the order of each series' first row, so the collapsed blocks keep the read order the file always had.
        var firstRow = new Dictionary<Guid, int>();
        for (var i = 0; i < keys.Count; i++)
        {
            if (keys[i].RecurrencePatternId is { } patternId)
            {
                firstRow.TryAdd(patternId, i);
            }
        }

        var collapsed = series.Select(s => s.Series.PatternId).ToHashSet();
        var standalone = keys
            .Where(k => k.RecurrencePatternId is not { } patternId || !collapsed.Contains(patternId))
            .Select(k => k.CalendarEventId)
            .ToList();
        var ordered = series.OrderBy(s => firstRow[s.Series.PatternId]).ToList();
        var zones = await ZonesAsync(rowQuery, ordered.Select(s => patterns[s.Series.PatternId]), cancellationToken);
        return new ExportPlan(
            fileName, true, ordered.Select(s => s.Series).ToList(), standalone, ordered.Sum(s => s.Rows) + standalone.Count,
            maxBytes, zones);
    }

    private static async Task<Dictionary<Guid, List<DateTime>>> ExclusionKeysAsync(
        IQueryable<RecurrenceExclusion> exclusions, CancellationToken cancellationToken)
    {
        var rows = await exclusions.AsNoTracking()
            .Select(x => new { x.RecurrencePatternId, x.OccurrenceKey })
            .ToListAsync(cancellationToken);
        return rows.GroupBy(x => x.RecurrencePatternId).ToDictionary(g => g.Key, g => g.Select(x => x.OccurrenceKey).ToList());
    }

    // The distinct zones the export writes and the year span each needs a VTIMEZONE for (S8), read before the first
    // byte so the VTIMEZONEs can be written in the head. UTC is written as Z and needs none.
    private static async Task<Dictionary<string, (int From, int To)>> ZonesAsync(
        IQueryable<ContextCalendarEvent> rows, IEnumerable<RecurrencePattern> masters, CancellationToken cancellationToken)
    {
        var spans = await rows.AsNoTracking()
            .Where(e => e.TimeZoneId != null && e.TimeZoneId != CalendarTimeZones.UtcId)
            .GroupBy(e => e.TimeZoneId!)
            .Select(g => new { Zone = g.Key, From = g.Min(e => e.StartDateTime), To = g.Max(e => e.EndDateTime) })
            .ToListAsync(cancellationToken);
        var zones = spans.ToDictionary(s => s.Zone, s => (From: s.From.Year - 1, To: s.To.Year + 1), StringComparer.Ordinal);
        foreach (var master in masters)
        {
            if (master.IsAllDay || master.TimeZoneId is not { } zone || zone == CalendarTimeZones.UtcId)
            {
                continue;
            }

            var year = master.StartDateTime.Year;
            zones[zone] = zones.TryGetValue(zone, out var span)
                ? (Math.Min(span.From, year - 1), Math.Max(span.To, year + 1))
                : (year - 1, year + 1);
        }

        return zones;
    }

    // Streams every row of the given series, grouped by series and ordered within each, holding one series at a time.
    private async Task<List<(PlannedSeries Series, int Rows)>> DecideSeriesAsync(
        IQueryable<ContextCalendarEvent> seriesRows, IReadOnlyDictionary<Guid, RecurrencePattern> patterns,
        IReadOnlyDictionary<Guid, List<DateTime>> exclusions, int occurrenceCap, SeriesMode mode,
        CancellationToken cancellationToken)
    {
        var decided = new List<(PlannedSeries Series, int Rows)>();
        var group = new List<RowShape>();

        void DecideGroup()
        {
            if (group.Count == 0 || !patterns.TryGetValue(group[0].PatternId!.Value, out var pattern))
            {
                return;
            }

            var planned = Decide(
                pattern, group, exclusions.GetValueOrDefault(pattern.RecurrencePatternId) ?? [], occurrenceCap, mode,
                uniformIn: mode == SeriesMode.CalendarFile ? pattern.CalendarId : null);
            if (planned is not null)
            {
                decided.Add((planned, group.Count));
            }
        }

        // Ordered before the projection: a positional record's members cannot be translated into ORDER BY.
        var stream = Shapes(seriesRows
                .OrderBy(e => e.RecurrencePatternId).ThenBy(e => e.StartDateTime).ThenBy(e => e.CalendarEventId))
            .AsAsyncEnumerable();
        await foreach (var row in stream.WithCancellation(cancellationToken))
        {
            if (group.Count > 0 && group[0].PatternId != row.PatternId)
            {
                DecideGroup();
                group.Clear();
            }

            group.Add(row);
        }

        DecideGroup();
        return decided;
    }

    private static IQueryable<RowShape> Shapes(IQueryable<ContextCalendarEvent> rows) =>
        rows.Select(e => new RowShape(
            e.CalendarEventId, e.RecurrencePatternId, e.CalendarId, e.StartDateTime, e.EndDateTime, e.IsAllDay,
            e.Title, e.Description, e.Location, e.LocationId, e.OccurrenceKey, e.IsOverride, e.TimeZoneId));

    // The collapse decision for one series (issue #519 §8.3), or null to flatten it.
    private static PlannedSeries? Decide(
        RecurrencePattern pattern, List<RowShape> rows, List<DateTime> exclusions, int occurrenceCap, SeriesMode mode,
        Guid? uniformIn)
    {
        if (!CanExportWithExceptions(pattern, rows, exclusions, occurrenceCap, out var literal))
        {
            return null;
        }

        var differing = rows.Where(r => r.IsOverride || Differs(r, pattern, literal[r.OccurrenceKey!.Value])).ToList();
        switch (mode)
        {
            case SeriesMode.Aggregate:
                // The aggregate stays flat for any series with an exception; a plain series collapses as before.
                var calendarId = rows.Count > 0 ? rows[0].CalendarId : pattern.CalendarId;
                return exclusions.Count == 0 && differing.Count == 0 && rows.All(r => r.CalendarId == calendarId)
                    ? new PlannedSeries(pattern.RecurrencePatternId, calendarId, [], [])
                    : null;
            case SeriesMode.CalendarFile:
                var moved = differing.Where(r => r.CalendarId != uniformIn).ToList();
                return new PlannedSeries(
                    pattern.RecurrencePatternId, pattern.CalendarId,
                    differing.Except(moved).Select(r => r.Id).ToList(),
                    [.. exclusions, .. moved.Select(r => r.OccurrenceKey!.Value)]);
            default:
                return new PlannedSeries(
                    pattern.RecurrencePatternId, pattern.CalendarId, differing.Select(r => r.Id).ToList(), [.. exclusions]);
        }
    }

    /// <summary>
    /// A series exports as one <c>RRULE</c> with exceptions (issue #519 §8.3, AR2) only when the zone-aware RFC-literal
    /// projection of its rule equals its key set — the keys of its rows plus its exclusions — and every row is keyed.
    /// No past row from an older rule can then carry a key outside the literal set, and a clamped series (the 31st of
    /// a 30-day month) fails the first test and is flattened, as before.
    /// </summary>
    private static bool CanExportWithExceptions(
        RecurrencePattern pattern, List<RowShape> rows, List<DateTime> exclusions, int maxGeneratedOccurrences,
        out Dictionary<DateTime, GeneratedOccurrence> literal)
    {
        literal = [];
        if (pattern.KeysBackfilledAt is null || rows.Any(r => r.OccurrenceKey is null))
        {
            return false;
        }

        List<GeneratedOccurrence>? generated;
        try
        {
            generated = RecurrenceOccurrenceGenerator.GenerateRfcLiteral(pattern, maxGeneratedOccurrences);
        }
        catch (ArgumentOutOfRangeException)
        {
            // A degenerate rule whose requested day never exists (e.g. Yearly, MonthOfYear=2,
            // DayOfMonth=30 — Feb 30 never occurs) climbs years indefinitely without ever tripping
            // GenerateRfcLiteral's own step guard until DateTime.DaysInMonth's year argument exceeds
            // year 9999 (security review, PR #345 F1). Flattening is the documented, safe fallback.
            return false;
        }

        if (generated is null || generated.Count == 0)
        {
            return false;
        }

        literal = generated.ToDictionary(o => o.Key);
        var keys = rows.Select(r => r.OccurrenceKey!.Value).Concat(exclusions).ToList();
        return keys.Count == literal.Count && keys.Distinct().Count() == keys.Count && keys.All(literal.ContainsKey);
    }

    // A row the master would not reproduce on its own: written as a RECURRENCE-ID component.
    private static bool Differs(RowShape row, RecurrencePattern pattern, GeneratedOccurrence occurrence) =>
        row.Start != occurrence.Start || row.End != occurrence.End || row.IsAllDay != pattern.IsAllDay
        || row.Title != pattern.Title || row.Description != pattern.Description || row.Location != pattern.Location
        || row.LocationId != pattern.LocationId || row.CalendarId != pattern.CalendarId
        || row.TimeZoneId != (pattern.IsAllDay ? null : pattern.TimeZoneId);

    private static (DateTime? From, DateTime? To) ValidateAggregateWindow(
        DateTime? from, DateTime? to, int maxAggregateExportWindowDays)
    {
        if (from is null != to is null)
        {
            throw new DomainValidationException("From and To must both be set, or both omitted.");
        }

        if (from is { } f && to is { } t)
        {
            if (t < f)
            {
                throw new DomainValidationException("To must be on or after From.");
            }

            if ((t - f).TotalDays > maxAggregateExportWindowDays)
            {
                throw new DomainValidationException(
                    $"The From/To window cannot span more than {maxAggregateExportWindowDays} days.");
            }
        }

        return (from, to);
    }

    private IQueryable<ContextCalendarEvent> BuildAggregateRowQuery(
        List<Guid>? calendarIds, DateTime? from, DateTime? to, TimeZoneInfo view, string? searchTerm)
    {
        var q = context.CalendarEvents.AsNoTracking();

        if (calendarIds is not null)
        {
            q = q.Where(e => calendarIds.Contains(e.CalendarId));
        }

        // Overlap, not start-only — matches CalendarEventService.ListAsync's semantics (AC 6), zones included.
        if (from is { } f && to is { } t)
        {
            q = CalendarWindow.Overlapping(q, f, t, view);
        }

        if (searchTerm is not null)
        {
            var pattern = ListQuery.ContainsPattern(searchTerm);
            q = q.Where(e => EF.Functions.Like(e.Title, pattern));
        }

        return q;
    }

    // ---------------------------------------------------------------- Export writing

    private async Task WriteAsync(ExportPlan plan, Stream output, bool includePlaceNames, CancellationToken cancellationToken)
    {
        var stamp = new CalDateTime(timeProvider.GetUtcNow().UtcDateTime, hasTime: true);
        var (head, tail) = IcsChunkSerializer.BuildEnvelope(ProductId);

        // One VTIMEZONE per planned zone, from the generator's own tzdata. A zone that cannot be expressed within the
        // cap is written as UTC instants for this file instead (S8).
        var writable = new HashSet<string>(StringComparer.Ordinal);
        var zoneText = new StringBuilder();
        foreach (var (zoneId, span) in plan.Zones.OrderBy(z => z.Key, StringComparer.Ordinal))
        {
            if (CalendarTimeZones.Find(zoneId) is { } zone && IcsTimeZoneWriter.Build(zone, span.From, span.To) is { } text)
            {
                zoneText.Append(text);
                writable.Add(zoneId);
            }
            else
            {
                logger.LogWarning("Calendar .ics export wrote a time zone as UTC instants: its definition exceeds the cap.");
            }
        }

        head += zoneText.ToString();
        await IcsChunkSerializer.WriteAsync(output, head, cancellationToken);
        var writtenBytes = (long)Encoding.UTF8.GetByteCount(head) + Encoding.UTF8.GetByteCount(tail);
        var written = 0;
        var times = new IcsTimes(writable);

        async Task EmitAsync(IcalEvent ev)
        {
            var calendar = new IcalCalendar();
            calendar.Events.Add(ev);
            var text = IcsChunkSerializer.SerializeComponents(calendar);
            var bytes = Encoding.UTF8.GetByteCount(text);
            if (writtenBytes + bytes > plan.MaxBytes)
            {
                logger.LogWarning(
                    "Calendar .ics export stopped at {WrittenBytes} bytes (cap {MaxBytes}); {WrittenRows}/{TotalRows} events delivered.",
                    writtenBytes, plan.MaxBytes, written, plan.EventCount);
                throw new IcsExportSizeExceededException(
                    $"The export grew past the {plan.MaxBytes / (1024 * 1024)} MB export limit while it was being written.");
            }

            await IcsChunkSerializer.WriteAsync(output, text, cancellationToken);
            writtenBytes += bytes;
            written++;
        }

        var occurrenceCap = (await journalLimits.GetAsync(cancellationToken)).RecurrenceMaxGeneratedOccurrences;
        foreach (var batch in plan.Series.Chunk(ExportChunking.ChunkSize))
        {
            var ids = batch.Select(s => s.PatternId).ToList();
            var fetched = await context.RecurrencePatterns.AsNoTracking()
                .Where(p => ids.Contains(p.RecurrencePatternId))
                .ToListAsync(cancellationToken);
            var patterns = ExportChunking.ReorderToSnapshot(ids, fetched, p => p.RecurrencePatternId);
            if (patterns.Count == 0)
            {
                continue; // every series in this batch was deleted between the plan and this fetch
            }

            var names = await IcsPlaces.NamesAsync(context, patterns.Select(p => p.LocationId), includePlaceNames, cancellationToken);
            var calendars = plan.Aggregate
                ? await GetCalendarNamesAsync(batch.Select(s => s.CalendarId), cancellationToken)
                : null;
            var planned = batch.ToDictionary(s => s.PatternId);
            foreach (var pattern in patterns)
            {
                var series = planned[pattern.RecurrencePatternId];
                var uid = plan.Aggregate || pattern.ExternalUid is null
                    ? $"{pattern.RecurrencePatternId}{SyntheticUidSuffix}"
                    : pattern.ExternalUid;
                List<GeneratedOccurrence>? literal;
                try
                {
                    literal = RecurrenceOccurrenceGenerator.GenerateRfcLiteral(pattern, occurrenceCap);
                }
                catch (ArgumentOutOfRangeException)
                {
                    literal = null;
                }

                if (literal is not { Count: > 0 })
                {
                    continue; // the series changed between the plan and this fetch into one that cannot collapse
                }

                var zoneId = pattern.IsAllDay ? null : pattern.TimeZoneId;
                var first = literal[0];
                var ev = BuildVEvent(
                    uid, pattern.Title, pattern.Description, pattern.LocationId, pattern.Location,
                    times.Of(first.Start, pattern.IsAllDay, zoneId), times.Of(first.End, pattern.IsAllDay, zoneId),
                    pattern.CreatedAt, pattern.UpdatedAt, stamp, names,
                    calendars?.GetValueOrDefault(series.CalendarId));
                ev.RecurrenceRule = BuildRRule(pattern, times, zoneId);
                foreach (var key in series.ExDates.Order())
                {
                    ev.ExceptionDates.Add(times.Key(key, pattern.IsAllDay, zoneId));
                }

                await EmitAsync(ev);

                foreach (var chunk in series.OverrideIds.Chunk(ExportChunking.ChunkSize))
                {
                    var overrides = await context.CalendarEvents.AsNoTracking()
                        .Where(e => chunk.Contains(e.CalendarEventId) && e.RecurrencePatternId == pattern.RecurrencePatternId)
                        .OrderBy(e => e.OccurrenceKey).ThenBy(e => e.CalendarEventId)
                        .ToListAsync(cancellationToken);
                    var overrideNames = await IcsPlaces.NamesAsync(
                        context, overrides.Select(r => r.LocationId), includePlaceNames, cancellationToken);
                    foreach (var row in overrides.Where(r => r.OccurrenceKey is not null))
                    {
                        var component = BuildVEvent(
                            uid, row.Title, row.Description, row.LocationId, row.Location,
                            times.Of(row.StartDateTime, row.IsAllDay, row.TimeZoneId),
                            times.Of(row.EndDateTime, row.IsAllDay, row.TimeZoneId),
                            row.CreatedAt, row.UpdatedAt, stamp, overrideNames, null);
                        component.RecurrenceIdentifier = new RecurrenceIdentifier(
                            times.Key(row.OccurrenceKey!.Value, pattern.IsAllDay, zoneId));
                        await EmitAsync(component);
                    }
                }
            }
        }

        foreach (var batch in plan.StandaloneIds.Chunk(ExportChunking.ChunkSize))
        {
            var fetched = await context.CalendarEvents.AsNoTracking()
                .Where(e => batch.Contains(e.CalendarEventId))
                .ToListAsync(cancellationToken);
            var rows = ExportChunking.ReorderToSnapshot(batch, fetched, e => e.CalendarEventId);
            if (rows.Count == 0)
            {
                continue; // every id in this batch was deleted between the plan and this fetch
            }

            var names = await IcsPlaces.NamesAsync(context, rows.Select(r => r.LocationId), includePlaceNames, cancellationToken);
            var calendars = plan.Aggregate
                ? await GetCalendarNamesAsync(rows.Select(r => r.CalendarId), cancellationToken)
                : null;
            foreach (var row in rows)
            {
                // The aggregate always synthesizes the UID: ExternalUid is only unique within its own calendar.
                var uid = plan.Aggregate || row.ExternalUid is null
                    ? $"{row.CalendarEventId}{SyntheticUidSuffix}"
                    : row.ExternalUid;
                await EmitAsync(BuildVEvent(
                    uid, row.Title, row.Description, row.LocationId, row.Location,
                    times.Of(row.StartDateTime, row.IsAllDay, row.TimeZoneId),
                    times.Of(row.EndDateTime, row.IsAllDay, row.TimeZoneId),
                    row.CreatedAt, row.UpdatedAt, stamp, names, calendars?.GetValueOrDefault(row.CalendarId)));
            }
        }

        await IcsChunkSerializer.WriteAsync(output, tail, cancellationToken);
    }

    /// <summary>
    /// How a stored time is written (issue #519 §8.3): all-day as <c>VALUE=DATE</c>, a floating time without <c>Z</c> or
    /// <c>TZID</c>, a zoned one as its wall-clock with <c>TZID</c> — or as a UTC instant when the zone is <c>UTC</c> or
    /// its <c>VTIMEZONE</c> could not be written.
    /// </summary>
    private sealed class IcsTimes(HashSet<string> writableZones)
    {
        public CalDateTime Of(DateTime stored, bool isAllDay, string? zoneId)
        {
            if (isAllDay)
            {
                return new CalDateTime(stored.Year, stored.Month, stored.Day);
            }

            if (zoneId is null)
            {
                return new CalDateTime(DateTime.SpecifyKind(stored, DateTimeKind.Unspecified), null, true);
            }

            if (!writableZones.Contains(zoneId) || CalendarTimeZones.Find(zoneId) is not { } zone)
            {
                return Utc(stored);
            }

            return new CalDateTime(CalendarTimeZones.ToWall(stored, zone), zoneId, true);
        }

        // An occurrence key — a wall-clock in the series' zone — as an EXDATE or RECURRENCE-ID value.
        public CalDateTime Key(DateTime key, bool isAllDay, string? zoneId)
        {
            if (isAllDay)
            {
                return new CalDateTime(key.Year, key.Month, key.Day);
            }

            if (zoneId is null)
            {
                return new CalDateTime(DateTime.SpecifyKind(key, DateTimeKind.Unspecified), null, true);
            }

            if (!writableZones.Contains(zoneId) || CalendarTimeZones.Find(zoneId) is not { } zone)
            {
                return CalendarTimeZones.Find(zoneId) is { } utcZone ? Utc(CalendarTimeZones.ToUtc(key, utcZone)) : Utc(key);
            }

            return new CalDateTime(DateTime.SpecifyKind(key, DateTimeKind.Unspecified), zoneId, true);
        }

        public static CalDateTime Utc(DateTime instant) =>
            new(DateTime.SpecifyKind(instant, DateTimeKind.Utc), hasTime: true);
    }

    // One builder for every VEVENT (standalone, recurring and aggregate), so all of them carry the place codec,
    // STATUS and the stamps (issue #516 §8.3, §8.4). Categories names the source calendar on the aggregate only.
    private static IcalEvent BuildVEvent(
        string uid, string title, string? description, Guid? locationId, string? detail, CalDateTime start, CalDateTime end,
        DateTime createdAt, DateTime updatedAt, CalDateTime stamp, IReadOnlyDictionary<Guid, string> placeNames,
        string? calendarName)
    {
        var ev = new IcalEvent
        {
            Uid = uid,
            Summary = title,
            Description = description,
            Start = start,
            End = end,
            Status = "CONFIRMED",
            Created = new CalDateTime(DateTime.SpecifyKind(createdAt, DateTimeKind.Utc), hasTime: true),
            LastModified = new CalDateTime(DateTime.SpecifyKind(updatedAt, DateTimeKind.Utc), hasTime: true),
            DtStamp = stamp,
        };
        IcsPlaces.WriteEventPlace(ev, locationId, detail, placeNames);
        if (calendarName is not null)
        {
            ev.Categories = [calendarName];
        }

        return ev;
    }

    private async Task<Dictionary<Guid, string>> GetCalendarNamesAsync(IEnumerable<Guid> calendarIds, CancellationToken cancellationToken)
    {
        var ids = calendarIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        return await context.Calendars.AsNoTracking()
            .Where(c => ids.Contains(c.CalendarId))
            .ToDictionaryAsync(c => c.CalendarId, c => c.Name, cancellationToken);
    }

    private static IcalRecurrencePattern BuildRRule(RecurrencePattern pattern, IcsTimes times, string? zoneId)
    {
        var rule = new IcalRecurrencePattern
        {
            Frequency = pattern.Frequency switch
            {
                RecurrenceFrequency.Daily => FrequencyType.Daily,
                RecurrenceFrequency.Weekly => FrequencyType.Weekly,
                RecurrenceFrequency.Monthly => FrequencyType.Monthly,
                _ => FrequencyType.Yearly,
            },
            Interval = pattern.Interval,
        };

        if (pattern.OccurrenceCount is { } count)
        {
            rule.Count = count;
        }
        else if (pattern.RecurrenceEndDate is { } end)
        {
            // UNTIL's value type must match DTSTART's (RFC 5545 §3.3.10) — a DATE-TIME UNTIL under a
            // VALUE=DATE all-day DTSTART is rejected by Google/Apple, so emit a DATE for all-day series. A zoned series
            // writes UNTIL as a UTC instant, a floating one as a local date-time (#519 v3 §C).
            rule.Until = pattern.IsAllDay
                ? new CalDateTime(end.Year, end.Month, end.Day)
                : zoneId is null
                    ? times.Of(end, isAllDay: false, zoneId: null)
                    : IcsTimes.Utc(end);
        }

        var anchor = CalendarTimeZones.WallOf(pattern.StartDateTime, zoneId);
        switch (pattern.Frequency)
        {
            case RecurrenceFrequency.Weekly:
                rule.ByDay = ToWeekDays(pattern.DaysOfWeek ?? DaysOfWeekFlags.None);
                break;
            case RecurrenceFrequency.Monthly:
                rule.ByMonthDay = [pattern.DayOfMonth ?? anchor.Day];
                break;
            case RecurrenceFrequency.Yearly:
                rule.ByMonthDay = [pattern.DayOfMonth ?? anchor.Day];
                rule.ByMonth = [pattern.MonthOfYear ?? anchor.Month];
                break;
        }

        return rule;
    }

    private static List<WeekDay> ToWeekDays(DaysOfWeekFlags flags)
    {
        var days = new List<WeekDay>();
        if (flags.HasFlag(DaysOfWeekFlags.Monday)) days.Add(new WeekDay(DayOfWeek.Monday));
        if (flags.HasFlag(DaysOfWeekFlags.Tuesday)) days.Add(new WeekDay(DayOfWeek.Tuesday));
        if (flags.HasFlag(DaysOfWeekFlags.Wednesday)) days.Add(new WeekDay(DayOfWeek.Wednesday));
        if (flags.HasFlag(DaysOfWeekFlags.Thursday)) days.Add(new WeekDay(DayOfWeek.Thursday));
        if (flags.HasFlag(DaysOfWeekFlags.Friday)) days.Add(new WeekDay(DayOfWeek.Friday));
        if (flags.HasFlag(DaysOfWeekFlags.Saturday)) days.Add(new WeekDay(DayOfWeek.Saturday));
        if (flags.HasFlag(DaysOfWeekFlags.Sunday)) days.Add(new WeekDay(DayOfWeek.Sunday));
        return days;
    }

    // ---------------------------------------------------------------- Import

    private enum ItemKind
    {
        Standalone,
        Master,
        Override,
        Cancelled,
        CancelledOverride,
    }

    // A date read off the file (RECURRENCE-ID), resolved to a key only once the master's zone is known.
    private readonly record struct FileDate(DateTime Value, string? TzId, bool IsUtc, bool HasTime);

    // One VEVENT that passed validation, ready for its batch to resolve and apply.
    private sealed class ImportItem
    {
        public required ItemKind Kind { get; init; }

        public required string SampleTitle { get; init; }

        public required string? Uid { get; init; }

        public long Offset { get; init; }

        public bool HasRule { get; init; }

        public string Title { get; init; } = string.Empty;

        public string? Description { get; init; }

        public ImportedPlace Place { get; init; } = new(null, null, false);

        public DateTime Start { get; init; }

        public DateTime End { get; init; }

        public bool IsAllDay { get; init; }

        public string? ZoneId { get; init; }

        public bool Adjusted { get; init; }

        public RecurrencePattern? Pattern { get; init; }

        public List<GeneratedOccurrence> Occurrences { get; init; } = [];

        /// <summary>The master's <c>EXDATE</c> keys, in its own zone.</summary>
        public List<DateTime> ExDates { get; init; } = [];

        public FileDate? RecurrenceId { get; init; }
    }

    // What spans the whole import: the caps read once, the counters, the place cache and the occurrence budget. Nothing
    // here grows with the file (N1).
    private sealed class ImportRun
    {
        public required Guid CalendarId { get; init; }

        public required string UserId { get; init; }

        public required DateTime Now { get; init; }

        public required IcsPlaces Places { get; set; }

        public required LocationClaims Claims { get; init; }

        public required ImportSkipCollector Skipped { get; init; }

        public required bool AllowRemovals { get; init; }

        public required TimeZoneInfo? DefaultZone { get; init; }

        public required int MaxAggregateOccurrences { get; init; }

        public required int MaxEventDurationDays { get; init; }

        public required int MaxGeneratedOccurrences { get; init; }

        public required int MaxSamples { get; init; }

        public ImportTally Totals { get; } = new();

        public int CommittedBatches { get; set; }

        public int CommittedEvents { get; set; }
    }

    // One attempt's counts, merged into the run only when its batch commits, so a retried batch never counts twice.
    private sealed class ImportTally
    {
        public int Imported { get; set; }

        public int Updated { get; set; }

        public int Removed { get; set; }

        public List<string> RemovedSamples { get; } = [];

        public bool AnySeriesRegenerated { get; set; }

        public int Aggregate { get; set; }

        public int OverridesApplied { get; set; }

        public int ExclusionsApplied { get; set; }

        public int DroppedOverrides { get; set; }

        public int DroppedExclusions { get; set; }

        public int Gated { get; set; }

        public int Adjusted { get; set; }

        public List<(string Reason, string Sample)> Skips { get; } = [];

        public List<Guid> RemovedEventIds { get; } = [];

        public List<Guid> RemovedSeriesIds { get; } = [];

        public List<DateTime> DroppedOverrideKeys { get; } = [];

        public List<DateTime> DroppedExclusionKeys { get; } = [];

        public void MergeInto(ImportRun run)
        {
            var totals = run.Totals;
            totals.Imported += Imported;
            totals.Updated += Updated;
            totals.Removed += Removed;
            foreach (var sample in RemovedSamples)
            {
                if (totals.RemovedSamples.Count < run.MaxSamples)
                {
                    totals.RemovedSamples.Add(sample);
                }
            }

            totals.AnySeriesRegenerated |= AnySeriesRegenerated;
            totals.Aggregate += Aggregate;
            totals.OverridesApplied += OverridesApplied;
            totals.ExclusionsApplied += ExclusionsApplied;
            totals.DroppedOverrides += DroppedOverrides;
            totals.DroppedExclusions += DroppedExclusions;
            totals.Gated += Gated;
            totals.Adjusted += Adjusted;
            foreach (var (reason, sample) in Skips)
            {
                run.Skipped.Add(reason, sample);
            }
        }
    }

    // One UID's components, reduced: what a batch applies together.
    private sealed record ImportGroup(string? Uid, List<ImportItem> Items, int Components)
    {
        public bool IsSeries => Items.Any(i => i.Kind is ItemKind.Master or ItemKind.Override or ItemKind.CancelledOverride
            || (i.Kind == ItemKind.Cancelled && i.HasRule));

        public int EstimatedRows => Items.Count + Items.Sum(i => i.Occurrences.Count);
    }

    /// <summary>
    /// Imports an <c>.ics</c> file into <paramref name="targetCalendarId"/> (issues #516 §3, #519 §3). The whole file is
    /// validated and regrouped by UID before anything is applied; groups then commit in batches. A failure after a batch
    /// committed is a <see cref="CalendarIcsImportPartiallyAppliedException"/> carrying the original status; re-running
    /// the same file is idempotent and completes it. A cancellation stops at the next batch boundary and is never wrapped.
    /// </summary>
    /// <param name="claims">
    /// The importer's location claims (issue #380 §8.4): places are linked or created only with both
    /// <c>locations.read</c> and <c>locations.create</c>. Null imports every place as detail text.
    /// </param>
    /// <param name="allowRemovals">
    /// Whether the import may remove what a cancelled component matches, and apply a series change that removes a stored
    /// row or discards a stored exception (issue #519 §5.7, the renamed and broadened opt-in).
    /// </param>
    /// <param name="canDelete">
    /// Whether the importer holds <c>calendar.delete</c>, evaluated once from the request principal. With
    /// <paramref name="allowRemovals"/> and without it the whole import is refused before anything is read.
    /// </param>
    /// <param name="defaultTimeZoneId">The zone a floating time is read in (§5.7); absent, floating times stay floating.</param>
    public async Task<IcsImportResult> ImportAsync(
        Guid targetCalendarId, Stream icsFile, long contentLength, string? contentType, string userId,
        CancellationToken cancellationToken = default, LocationClaims? claims = null,
        bool allowRemovals = false, bool canDelete = false, string? defaultTimeZoneId = null)
    {
        if (!IsAcceptedContentType(contentType))
        {
            throw new DomainValidationException("The uploaded file must be a calendar file (text/calendar).");
        }

        if (allowRemovals && !canDelete)
        {
            throw new DomainForbiddenException("Removing events or discarding edits needs permission to delete calendar events.");
        }

        var defaultZone = CalendarTimeZones.ResolveRequest(defaultTimeZoneId, "defaultTimeZoneId");
        var cap = await limits.GetAsync(cancellationToken);
        var journalCap = await journalLimits.GetAsync(cancellationToken);
        var maxImportBytes = cap.CalendarIcsMaxImportBytes;
        var maxImportEvents = cap.CalendarIcsMaxImportEvents;

        if (contentLength > maxImportBytes)
        {
            throw new DomainValidationException($"The .ics file exceeds the {maxImportBytes / (1024 * 1024)} MB limit.");
        }

        if (!await context.Calendars.AnyAsync(c => c.CalendarId == targetCalendarId, cancellationToken))
        {
            throw new DomainNotFoundException($"Calendar ID {targetCalendarId} not found.");
        }

        // One snapshot of every cap, taken before any component is processed, so a concurrent admin write can never
        // split one import across two values of the same setting.
        var run = new ImportRun
        {
            CalendarId = targetCalendarId,
            UserId = userId,
            Now = timeProvider.GetUtcNow().UtcDateTime,
            Places = await IcsPlaces.ForImportAsync(context, claims ?? LocationClaims.None, userId, timeProvider, cancellationToken),
            Claims = claims ?? LocationClaims.None,
            Skipped = new ImportSkipCollector(cap.ImportMaxSamplesPerSkipReason),
            AllowRemovals = allowRemovals,
            DefaultZone = defaultZone,
            MaxAggregateOccurrences = cap.CalendarIcsMaxAggregateOccurrences,
            MaxEventDurationDays = journalCap.CalendarMaxEventDurationDays,
            MaxGeneratedOccurrences = journalCap.RecurrenceMaxGeneratedOccurrences,
            MaxSamples = cap.ImportMaxSamplesPerSkipReason,
        };

        await using var upload = await SeekableUpload.OpenAsync(icsFile, maxImportBytes, SpillDirectory, cancellationToken);
        using var reservation = IcsSpillReservation.Reserve(SpillDirectory, upload.Stream.Length);
        await using var grouper = await IcsImportGrouper.BuildAsync(
            upload.Stream, SpillDirectory, maxImportEvents, cancellationToken, GroupingRecordsPerRun, GroupingKey);

        var batch = new List<ImportGroup>(ImportBatchSize);
        var batchComponents = 0;
        var batchRows = 0;
        long batchChars = 0;
        try
        {
            await foreach (var refs in grouper.GroupsAsync(cancellationToken))
            {
                var (groups, chars) = await ReduceAsync(grouper, refs, run, cancellationToken);
                batchChars += chars;
                batchComponents += refs.Count;
                foreach (var group in groups)
                {
                    batch.Add(group);
                    batchRows += group.EstimatedRows;
                }

                if (batch.Count >= ImportBatchSize || batchRows >= ImportBatchEntityBudget || batchChars >= ImportBatchCharBudget)
                {
                    await ApplyBatchAndClearAsync(batch, batchComponents, run, cancellationToken);
                    batch.Clear();
                    batchComponents = 0;
                    batchRows = 0;
                    batchChars = 0;
                }
            }

            if (batchComponents > 0)
            {
                await ApplyBatchAndClearAsync(batch, batchComponents, run, cancellationToken);
            }
        }
        catch (Exception ex) when (run.CommittedBatches > 0 && ex is not OperationCanceledException)
        {
            var status = ex is DomainException domain ? domain.StatusCode : StatusCodes500(ex);
            if (ex is not DomainException)
            {
                logger.LogError(ex,
                    "Calendar .ics import into {CalendarId} by {UserId} failed after {CommittedBatches} committed batch(es).",
                    targetCalendarId, userId, run.CommittedBatches);
            }

            // Groups commit in UID-hash order (AR3), so the committed events are an arbitrary subset of the file.
            var reason = ex is DomainException ? ex.Message : "The import failed part-way.";
            throw new CalendarIcsImportPartiallyAppliedException(
                $"{reason} {run.CommittedEvents} events of the file were already imported; importing the same "
                + "file again is safe and completes it.",
                status, run.CommittedBatches, run.CommittedEvents, ex);
        }

        var totals = run.Totals;
        return new IcsImportResult
        {
            ImportedCount = totals.Imported,
            UpdatedCount = totals.Updated,
            AnySeriesRegenerated = totals.AnySeriesRegenerated,
            PlacesLinked = run.Places.Linked,
            PlacesCreated = run.Places.Created,
            RemovedCount = totals.Removed,
            RemovedSampleTitles = totals.RemovedSamples,
            OverridesApplied = totals.OverridesApplied,
            ExclusionsApplied = totals.ExclusionsApplied,
            DroppedOverrideCount = totals.DroppedOverrides,
            DroppedExclusionCount = totals.DroppedExclusions,
            GatedSeriesCount = totals.Gated,
            AdjustedCount = totals.Adjusted,
            Skipped = run.Skipped.ToGroups((reason, count, samples) => new IcsImportSkipGroup
            {
                Reason = reason,
                Count = count,
                SampleTitles = samples,
            }),
        };
    }

    // A database outage reads as 503, anything else as 500.
    private static int StatusCodes500(Exception ex) =>
        ex is DbUpdateException or System.Data.Common.DbException or TimeoutException ? 503 : 500;

    // Reads, parses and reduces one hash group's components, then splits a residual hash collision by the UID itself.
    private async Task<(List<ImportGroup> Groups, long Chars)> ReduceAsync(
        IcsImportGrouper grouper, IReadOnlyList<ComponentRef> refs, ImportRun run, CancellationToken cancellationToken)
    {
        var items = new List<ImportItem>();
        var overrides = 0;
        long chars = 0;
        foreach (var component in refs)
        {
            if (component.IsOverride && ++overrides > MaxOverridesPerSeries)
            {
                run.Skipped.Add(OverrideCapReason, UntitledEvent);
                continue;
            }

            var text = await grouper.ReadComponentAsync(component, cancellationToken);
            chars += text.Length;
            if (Classify(ParseComponent(text), run, component.Offset) is { } item)
            {
                items.Add(item);
            }
        }

        var groups = items
            .GroupBy(i => i.Uid is null ? null : IcsImportGrouper.FoldUid(i.Uid))
            .SelectMany(g => g.Key is null
                ? g.Select(i => new ImportGroup(null, [i], 1))
                : [new ImportGroup(g.First().Uid, g.OrderBy(i => i.Offset).ToList(), g.Count())])
            .ToList();
        return (groups, chars);
    }

    // A component Ical.Net cannot read is that event's problem, not the file's: it is skipped (null here).
    private static IcalEvent? ParseComponent(string component)
    {
        try
        {
            return IcalCalendar.Load(component)?.Events.FirstOrDefault();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    // The tracker is cleared whether the batch committed or not, so a failed batch's half-applied entities never
    // outlive it (architect nit 9).
    private async Task ApplyBatchAndClearAsync(List<ImportGroup> groups, int components, ImportRun run, CancellationToken cancellationToken)
    {
        try
        {
            await ApplyBatchAsync(groups, components, run, cancellationToken);
        }
        finally
        {
            context.ChangeTracker.Clear();
        }
    }

    // The ids one batch needs locked, by what its UIDs resolve to.
    private sealed record BatchLocks(IReadOnlySet<Guid> PatternIds, IReadOnlySet<Guid> EventIds)
    {
        public bool SameAs(BatchLocks other) => PatternIds.SetEquals(other.PatternIds) && EventIds.SetEquals(other.EventIds);
    }

    // A DomainException only so the hierarchy guard admits it: it never leaves the batch loop, which retries on it and
    // turns the last one into 409 calendar-series-busy.
    private sealed class ReResolveNeeded(BatchLocks locks) : DomainException("The batch's rows changed before they were locked.")
    {
        public BatchLocks Locks { get; } = locks;

        public override int StatusCode => 409;
    }

    // components counts every VEVENT the batch spans, skipped ones included, for the partial-failure counts.
    private async Task ApplyBatchAsync(List<ImportGroup> groups, int components, ImportRun run, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var uids = groups.Select(g => g.Uid).OfType<string>().Distinct(UidComparer).ToList();

        // Resolve outside the transaction, only to learn what to lock (#519 v4 §2 step 1).
        var locks = LocksOf(await ResolveAsync(run.CalendarId, uids, tracked: false, cancellationToken));
        var placesSnapshot = run.Places.Snapshot();
        for (var attempt = 1; ; attempt++)
        {
            if (attempt > 1)
            {
                // A rolled-back attempt may have created places the index still names.
                run.Places = await IcsPlaces.ForImportAsync(context, run.Claims, run.UserId, timeProvider, cancellationToken);
                run.Places.Restore(placesSnapshot);
            }

            try
            {
                var tally = await RunBatchAttemptAsync(groups, uids, locks, run, cancellationToken);
                tally.MergeInto(run);
                run.CommittedBatches++;
                run.CommittedEvents += components;
                logger.LogInformation(
                    "Calendar .ics import into {CalendarId}: batch committed ({Batch}), {Groups} group(s), {Components} component(s).",
                    run.CalendarId, run.CommittedBatches, groups.Count, components);
                AuditBatch(run, tally);
                return;
            }
            catch (ReResolveNeeded changed) when (attempt < MaxBatchAttempts)
            {
                locks = changed.Locks;
            }
            catch (ReResolveNeeded)
            {
                throw CalendarSeriesConflictException.SeriesBusy();
            }
        }
    }

    // One attempt: inside the execution strategy, the locks first (series, then standalone events, each ascending), then
    // the re-resolve under them, then the groups, then one save (#519 v4 §2, v5 §2).
    private async Task<ImportTally> RunBatchAttemptAsync(
        List<ImportGroup> groups, List<string> uids, BatchLocks locks, ImportRun run, CancellationToken cancellationToken)
    {
        async Task<ImportTally> Work()
        {
            await CalendarSeriesTransactions.LockSeriesAsync(context, locks.PatternIds, cancellationToken);
            await CalendarSeriesTransactions.LockEventsAsync(context, locks.EventIds, cancellationToken);
            var resolver = await ResolveAsync(run.CalendarId, uids, tracked: true, cancellationToken);
            var underLock = LocksOf(resolver);
            if (!underLock.SameAs(locks))
            {
                throw new ReResolveNeeded(underLock);
            }

            var tally = new ImportTally();
            var attempt = new BatchAttempt(resolver, tally);
            foreach (var group in groups)
            {
                await ApplyGroupAsync(group, run, attempt, cancellationToken);
            }

            await run.Places.SaveChangesAsync(context, cancellationToken);
            return tally;
        }

        if (!context.Database.IsRelational())
        {
            return await Work();
        }

        var strategy = context.Database.CreateExecutionStrategy();
        try
        {
            return await strategy.ExecuteAsync(() => CalendarSeriesTransactions.RunAttemptAsync(context, Work, cancellationToken));
        }
        catch (Exception ex) when (ex is not ReResolveNeeded && CalendarSeriesTransactions.IsBusy(ex))
        {
            throw CalendarSeriesConflictException.SeriesBusy();
        }
    }

    private static BatchLocks LocksOf(BatchResolver resolver) => new(
        resolver.Patterns.Select(p => p.RecurrencePatternId)
            .Concat(resolver.Events.Where(e => e.RecurrencePatternId is not null).Select(e => e.RecurrencePatternId!.Value))
            .ToHashSet(),
        resolver.Events.Select(e => e.CalendarEventId).ToHashSet());

    // After each committed batch that removed or discarded anything (§7.7): ids, keys and counts only, never a title or
    // place. The ids are split across lines of at most AuditIdsPerLine, because one removed series can carry thousands
    // of future rows.
    private void AuditBatch(ImportRun run, ImportTally tally)
    {
        var removedCount = tally.RemovedEventIds.Count + tally.RemovedSeriesIds.Count;
        var parts = AuditChunks(tally.RemovedSeriesIds, tally.RemovedEventIds, AuditIdsPerLine);
        for (var part = 0; part < parts.Count; part++)
        {
            logger.LogInformation(
                "Calendar .ics import by {UserId} into calendar {CalendarId} removed {RemovedCount} item(s), "
                + "part {Part} of {Parts}: series {RemovedSeriesIds}; events {RemovedEventIds}.",
                run.UserId, run.CalendarId, removedCount, part + 1, parts.Count, parts[part].SeriesIds, parts[part].EventIds);
        }

        if (tally.DroppedOverrideKeys.Count + tally.DroppedExclusionKeys.Count > 0)
        {
            logger.LogInformation(
                "Calendar .ics import by {UserId} into calendar {CalendarId} discarded {DroppedOverrideCount} override(s) "
                + "and {DroppedExclusionCount} exclusion(s): override keys {DroppedOverrideKeys}; exclusion keys {DroppedExclusionKeys}.",
                run.UserId, run.CalendarId, tally.DroppedOverrideKeys.Count, tally.DroppedExclusionKeys.Count,
                tally.DroppedOverrideKeys.Take(AuditIdsPerLine).Select(k => k.ToString("s")),
                tally.DroppedExclusionKeys.Take(AuditIdsPerLine).Select(k => k.ToString("s")));
        }
    }

    /// <summary>Splits a batch's removed ids into audit lines of at most <paramref name="perLine"/> ids, series first.</summary>
    internal static List<(List<Guid> SeriesIds, List<Guid> EventIds)> AuditChunks(
        IReadOnlyList<Guid> seriesIds, IReadOnlyList<Guid> eventIds, int perLine)
    {
        var lines = new List<(List<Guid> SeriesIds, List<Guid> EventIds)>();
        var all = seriesIds.Select(id => (Series: true, Id: id)).Concat(eventIds.Select(id => (Series: false, Id: id)));
        foreach (var chunk in all.Chunk(perLine))
        {
            lines.Add((chunk.Where(c => c.Series).Select(c => c.Id).ToList(), chunk.Where(c => !c.Series).Select(c => c.Id).ToList()));
        }

        return lines;
    }

    // ----- Classification

    private ImportItem? Classify(IcalEvent? ev, ImportRun run, long offset)
    {
        if (ev is null)
        {
            run.Skipped.Add("Event could not be parsed.", UntitledEvent);
            return null;
        }

        var title = ev.Summary?.Trim();
        var sampleTitle = string.IsNullOrWhiteSpace(title) ? UntitledEvent : title;
        var uid = string.IsNullOrWhiteSpace(ev.Uid) ? null : ev.Uid.Trim();

        // ExternalUid is varchar(255): a longer UID would fail the whole batch's save under strict mode, and the same
        // file could then never complete, so it is that event's skip (architect finding 4).
        if (uid is { Length: > MaxUidLength })
        {
            run.Skipped.Add(UidTooLongReason, sampleTitle);
            return null;
        }

        var cancelled = string.Equals(ev.Status?.Trim(), "CANCELLED", StringComparison.OrdinalIgnoreCase);
        FileDate? recurrenceId = null;
        if (ev.Properties.ContainsKey("RECURRENCE-ID"))
        {
            if (uid is null)
            {
                run.Skipped.Add(OverrideWithoutSeriesReason, sampleTitle);
                return null;
            }

            if (ev.RecurrenceIdentifier is not { } identifier || identifier.StartTime is null)
            {
                run.Skipped.Add(OverrideMismatchReason, sampleTitle);
                return null;
            }

            if (identifier.Range == RecurrenceRange.ThisAndFuture)
            {
                run.Skipped.Add(ThisAndFutureReason, sampleTitle);
                return null;
            }

            var at = identifier.StartTime;
            recurrenceId = new FileDate(at.Value, at.TzId, at.IsUtc, at.HasTime);
            if (cancelled)
            {
                return new ImportItem
                {
                    Kind = ItemKind.CancelledOverride, SampleTitle = sampleTitle, Uid = uid, Offset = offset,
                    RecurrenceId = recurrenceId,
                };
            }
        }
        else if (cancelled)
        {
            if (uid is null)
            {
                run.Skipped.Add(CancelledUnknownReason, sampleTitle);
                return null;
            }

            return new ImportItem
            {
                Kind = ItemKind.Cancelled, SampleTitle = sampleTitle, Uid = uid, Offset = offset,
                HasRule = ev.RecurrenceRule is not null,
            };
        }

        if (ev.Start is null)
        {
            run.Skipped.Add("Event is missing a start date (DTSTART).", sampleTitle);
            return null;
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            run.Skipped.Add("Event is missing a title (SUMMARY).", sampleTitle);
            return null;
        }

        if (title.Length > 200)
        {
            run.Skipped.Add("Title exceeds the maximum length of 200 characters.", sampleTitle);
            return null;
        }

        var description = ev.Description;
        if (description is { Length: > 2000 })
        {
            run.Skipped.Add("Description exceeds the maximum length of 2000 characters.", sampleTitle);
            return null;
        }

        var place = IcsPlaces.ReadEventPlace(ev);
        if (place.Detail is { Length: > MaxLocationLength } || (!place.HasDetailProperty && ev.Location is { Length: > MaxLocationLength }))
        {
            run.Skipped.Add("Location exceeds the maximum length of 300 characters.", sampleTitle);
            return null;
        }

        var isAllDay = !ev.Start.HasTime;
        if (!TryReadTimes(ev, isAllDay, run.DefaultZone, out var times, out var timeReason))
        {
            run.Skipped.Add(timeReason!, sampleTitle);
            return null;
        }

        try
        {
            CalendarEventService.ValidateTimes(times.StartUtc, times.EndUtc, isAllDay, run.MaxEventDurationDays);
        }
        catch (DomainValidationException ex)
        {
            run.Skipped.Add(ex.Message, sampleTitle);
            return null;
        }

        if (recurrenceId is not null)
        {
            return new ImportItem
            {
                Kind = ItemKind.Override, SampleTitle = sampleTitle, Uid = uid, Offset = offset, Title = title,
                Description = description, Place = place, Start = times.Start, End = times.End, IsAllDay = isAllDay,
                ZoneId = times.Zone?.Id, Adjusted = times.Adjusted, RecurrenceId = recurrenceId,
            };
        }

        if (ev.RecurrenceRule is null)
        {
            return new ImportItem
            {
                Kind = ItemKind.Standalone, SampleTitle = sampleTitle, Uid = uid, Offset = offset, Title = title,
                Description = description, Place = place, Start = times.Start, End = times.End, IsAllDay = isAllDay,
                ZoneId = times.Zone?.Id, Adjusted = times.Adjusted,
            };
        }

        if (ev.Properties.ContainsKey("RDATE"))
        {
            run.Skipped.Add(RecurrenceDatesReason, sampleTitle);
            return null;
        }

        // Ical.Net exposes only a single RecurrenceRule, and no EXRULE at all, so "more
        // recurrence than v1 models" has to be read off the raw property list.
        if (ev.Properties.CountOf("RRULE") > 1 || ev.Properties.ContainsKey("EXRULE"))
        {
            run.Skipped.Add("Unsupported recurrence rule.", sampleTitle);
            return null;
        }

        var (pattern, reason) = MapPattern(run.CalendarId, ev.RecurrenceRule, title, description, place.Detail, times, isAllDay,
            run.UserId, run.Now);
        if (pattern is null)
        {
            run.Skipped.Add(reason!, sampleTitle);
            return null;
        }

        List<GeneratedOccurrence> occurrences;
        try
        {
            occurrences = RecurrenceOccurrenceGenerator.Generate(pattern, run.MaxGeneratedOccurrences);
        }
        catch (DomainValidationException ex)
        {
            run.Skipped.Add(ex.Message, sampleTitle);
            return null;
        }

        if (occurrences.Count == 0)
        {
            run.Skipped.Add("Recurrence rule produces no occurrences.", sampleTitle);
            return null;
        }

        var exDates = new List<DateTime>();
        foreach (var excluded in ev.ExceptionDates.GetAllDates())
        {
            if (ToKey(new FileDate(excluded.Value, excluded.TzId, excluded.IsUtc, excluded.HasTime), pattern) is { } key)
            {
                exDates.Add(key);
            }
            else
            {
                run.Skipped.Add(StrayExclusionReason, sampleTitle);
            }
        }

        return new ImportItem
        {
            Kind = ItemKind.Master, SampleTitle = sampleTitle, Uid = uid, Offset = offset, HasRule = true, Title = title,
            Description = description, Place = place, Start = times.Start, End = times.End, IsAllDay = isAllDay,
            ZoneId = times.Zone?.Id, Adjusted = times.Adjusted, Pattern = pattern, Occurrences = occurrences,
            ExDates = exDates,
        };
    }

    // A component's start and end as stored (issue #519 §8.4): an IANA or Windows TZID resolves, Z is zone "UTC", a
    // floating time takes defaultTimeZoneId or stays floating. An unknown TZID is the component's skip.
    private readonly record struct ImportTimes(
        DateTime Start, DateTime End, DateTime StartUtc, DateTime EndUtc, TimeZoneInfo? Zone, bool Adjusted);

    private static bool TryReadTimes(IcalEvent ev, bool isAllDay, TimeZoneInfo? defaultZone, out ImportTimes times, out string? reason)
    {
        times = default;
        reason = null;
        var start = ev.Start!;
        try
        {
            if (isAllDay)
            {
                var day = DateTime.SpecifyKind(start.Value.Date, DateTimeKind.Utc);
                var endDay = ev.End is { } end ? DateTime.SpecifyKind(end.Value.Date, DateTimeKind.Utc)
                    : day + ev.EffectiveDuration.ToTimeSpanUnspecified();
                times = new ImportTimes(day, endDay, day, endDay, null, false);
                return true;
            }

            if (!TryZoneOf(start, defaultZone, out var zone))
            {
                reason = UnknownTimeZoneReason;
                return false;
            }

            var wallStart = DateTime.SpecifyKind(start.Value, DateTimeKind.Unspecified);
            var adjusted = false;
            var startStored = zone is null ? wallStart : CalendarTimeZones.ToUtc(wallStart, zone, out adjusted);
            DateTime endStored;
            if (ev.End is { } endValue)
            {
                if (!TryZoneOf(endValue, defaultZone, out var endZone))
                {
                    reason = UnknownTimeZoneReason;
                    return false;
                }

                var endWall = DateTime.SpecifyKind(endValue.Value, DateTimeKind.Unspecified);
                var endInstant = endZone is null ? endWall : CalendarTimeZones.ToUtc(endWall, endZone);
                endStored = zone is null
                    ? (endZone is null ? endWall : endInstant)
                    : (endZone is null ? CalendarTimeZones.ToUtc(endWall, zone) : endInstant);
            }
            else
            {
                // A nominal duration: local end = local start + duration (S7).
                var endWall = wallStart + ev.EffectiveDuration.ToTimeSpanUnspecified();
                endStored = zone is null ? endWall : CalendarTimeZones.ToUtc(endWall, zone);
            }

            times = new ImportTimes(startStored, endStored, DateTime.SpecifyKind(startStored, DateTimeKind.Utc),
                DateTime.SpecifyKind(endStored, DateTimeKind.Utc), zone, adjusted);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A malformed duration is a per-event problem (skip with a reason), not a 500 for the whole file.
            reason = "Event has an unsupported or unresolvable date/time.";
            return false;
        }
    }

    // The zone a file time is in: null when floating without a default. False for an unknown TZID.
    private static bool TryZoneOf(CalDateTime value, TimeZoneInfo? defaultZone, out TimeZoneInfo? zone)
    {
        if (value.IsUtc)
        {
            zone = TimeZoneInfo.Utc;
            return true;
        }

        if (value.TzId is { Length: > 0 } tzid)
        {
            zone = CalendarTimeZones.ResolveFileZone(tzid);
            return zone is not null;
        }

        zone = defaultZone;
        return true;
    }

    // A file date (EXDATE, RECURRENCE-ID) as a key of the series (issue #519 v3 §C, O10): converted to the master's zone
    // wall-clock, or by date for an all-day series. Null when its TZID is unknown.
    private static DateTime? ToKey(FileDate date, RecurrencePattern series)
    {
        if (series.IsAllDay)
        {
            return DateTime.SpecifyKind(date.Value.Date, DateTimeKind.Unspecified);
        }

        var zone = CalendarTimeZones.Find(series.TimeZoneId);
        var value = DateTime.SpecifyKind(date.Value, DateTimeKind.Unspecified);
        DateTime? instant = null;
        if (date.IsUtc)
        {
            instant = DateTime.SpecifyKind(date.Value, DateTimeKind.Utc);
        }
        else if (date.TzId is { Length: > 0 } tzid)
        {
            if (CalendarTimeZones.ResolveFileZone(tzid) is not { } fileZone)
            {
                return null;
            }

            if (zone is not null && fileZone.Id == zone.Id)
            {
                return value;
            }

            instant = CalendarTimeZones.ToUtc(value, fileZone);
        }

        if (instant is not { } at)
        {
            return value; // floating: already a wall-clock
        }

        return zone is null ? DateTime.SpecifyKind(at, DateTimeKind.Unspecified) : CalendarTimeZones.ToWall(at, zone);
    }

    // ----- UID resolution (§8.2)

    // The batch's rows in the target calendar that any of its UIDs could name, read by one keyed query per table
    // (R1): by ExternalUid, by ImportSourceUid for a synthetic UID, and by primary key for a synthetic UID's guid.
    // Tracked under the locks, because the batch updates them. The whole calendar is never loaded.
    private sealed record BatchResolver(List<ContextCalendarEvent> Events, List<RecurrencePattern> Patterns);

    private async Task<BatchResolver> ResolveAsync(Guid calendarId, List<string> uids, bool tracked, CancellationToken cancellationToken)
    {
        if (uids.Count == 0)
        {
            return new BatchResolver([], []);
        }

        var synthetic = uids.Where(IsSyntheticUid).ToList();
        var guids = synthetic.Select(u => Guid.Parse(u[..^SyntheticUidSuffix.Length])).ToList();

        var events = tracked ? context.CalendarEvents : context.CalendarEvents.AsNoTracking();
        var patterns = tracked ? context.RecurrencePatterns : context.RecurrencePatterns.AsNoTracking();
        return new BatchResolver(
            await events
                .Where(e => e.CalendarId == calendarId
                    && ((e.ExternalUid != null && uids.Contains(e.ExternalUid))
                        || (e.ImportSourceUid != null && synthetic.Contains(e.ImportSourceUid))
                        || guids.Contains(e.CalendarEventId)))
                .OrderBy(e => e.CreatedAt).ThenBy(e => e.CalendarEventId)
                .ToListAsync(cancellationToken),
            await patterns
                .Where(p => p.CalendarId == calendarId
                    && ((p.ExternalUid != null && uids.Contains(p.ExternalUid))
                        || (p.ImportSourceUid != null && synthetic.Contains(p.ImportSourceUid))
                        || guids.Contains(p.RecurrencePatternId)))
                .OrderBy(p => p.CreatedAt).ThenBy(p => p.RecurrencePatternId)
                .ToListAsync(cancellationToken));
    }

    // The first rule that matches wins: a synthetic UID naming a row of the target calendar by its key; a stored
    // ExternalUid; a synthetic UID stored as ImportSourceUid. A row removed earlier in this batch matches nothing.
    private static T? Match<T>(
        string uid, IEnumerable<T> candidates, Func<T, Guid> id, Func<T, string?> externalUid, Func<T, string?> importSourceUid,
        OdysseyContext context)
        where T : class
    {
        var live = candidates.Where(c => context.Entry(c).State is not (EntityState.Deleted or EntityState.Detached)).ToList();
        if (IsSyntheticUid(uid) && Guid.TryParse(uid[..^SyntheticUidSuffix.Length], out var key)
            && live.FirstOrDefault(c => id(c) == key) is { } byKey)
        {
            return byKey;
        }

        if ((live.FirstOrDefault(c => string.Equals(externalUid(c), uid, StringComparison.Ordinal))
            ?? live.FirstOrDefault(c => externalUid(c) is { } stored && UidComparer.Equals(stored, uid))) is { } byExternal)
        {
            return byExternal;
        }

        return IsSyntheticUid(uid)
            ? live.FirstOrDefault(c => importSourceUid(c) is { } stored && UidComparer.Equals(stored, uid))
            : null;
    }

    // ----- Places (§8.3)

    // What the stored row's place becomes. KeepLink: an updated row keeps its link and a created one has none — the
    // file names no place, or the importer may not link one. Map runs only for a row that is about to be stored, so a
    // skipped block never creates a place.
    private static (Guid? LocationId, string? Detail, bool KeepLink) ResolvePlace(IcsPlaces places, ImportedPlace place)
    {
        if (!places.Enabled)
        {
            return (null, place.Detail, true);
        }

        if (!place.HasDetailProperty)
        {
            var (id, detail) = places.Map(place.PlaceText);
            return (id, detail, false);
        }

        if (place.PlaceText is null)
        {
            return (null, place.Detail, true);
        }

        var (locationId, mapped) = places.Map(place.PlaceText);
        return locationId is null ? (null, place.Detail ?? mapped, false) : (locationId, place.Detail, false);
    }

    // ----- Apply

    // What one attempt reads and writes: the resolver, the series it has loaded under its locks, and its tally.
    private sealed class BatchAttempt(BatchResolver resolver, ImportTally tally)
    {
        public BatchResolver Resolver { get; } = resolver;

        public ImportTally Tally { get; } = tally;

        public Dictionary<Guid, RecurrenceSeriesState> Series { get; } = [];

        public void Skip(string reason, string sample) => Tally.Skips.Add((reason, sample));
    }

    private async Task ApplyGroupAsync(ImportGroup group, ImportRun run, BatchAttempt attempt, CancellationToken cancellationToken)
    {
        if (group.IsSeries)
        {
            await ApplySeriesGroupAsync(group, run, attempt, cancellationToken);
            return;
        }

        // Plain events: applied in file order, so a UID seen again resolves to what the earlier one wrote and the last
        // component wins.
        ContextCalendarEvent? written = null;
        var removed = false;
        foreach (var item in group.Items)
        {
            if (item.Kind == ItemKind.Cancelled)
            {
                removed |= await ApplyCancelledEventAsync(item, run, attempt, removed ? null : written, cancellationToken);
                written = null;
                continue;
            }

            written = await ApplyStandaloneAsync(item, run, attempt, removed ? null : written, cancellationToken);
            removed = false;
        }
    }

    private ContextCalendarEvent? MatchEvent(string? uid, BatchAttempt attempt) =>
        uid is null ? null : Match(uid, attempt.Resolver.Events, e => e.CalendarEventId, e => e.ExternalUid, e => e.ImportSourceUid, context);

    private RecurrencePattern? MatchPattern(string? uid, BatchAttempt attempt) =>
        uid is null ? null : Match(uid, attempt.Resolver.Patterns, p => p.RecurrencePatternId, p => p.ExternalUid, p => p.ImportSourceUid, context);

    // A series this attempt reads under its lock (the lock is the batch's first statement), keyed inline if it is not
    // keyed yet (#519 v4 §4).
    private async Task<RecurrenceSeriesState?> SeriesAsync(
        Guid patternId, ImportRun run, BatchAttempt attempt, CancellationToken cancellationToken)
    {
        if (attempt.Series.TryGetValue(patternId, out var known))
        {
            return known;
        }

        var state = await RecurrenceSeriesState.LoadAsync(context, patternId, tracked: true, cancellationToken);
        if (state is null)
        {
            return null;
        }

        if (state.Pattern.KeysBackfilledAt is null)
        {
            var detached = RecurrenceSeries.KeyInPlace(state, run.Now, run.UserId, x => context.RecurrenceExclusions.Add(x));
            logger.LogInformation(
                "Calendar series {RecurrencePatternId} keyed inline by an import: {DetachedCount} row(s) detached.",
                patternId, detached);
        }

        attempt.Series[patternId] = state;
        return state;
    }

    // A standalone event is never counted against CalendarIcsMaxAggregateOccurrences: that cap bounds series
    // expansion (§3 component 5, Non-Goal 4), and counting plain rows would reinstate a file-wide event ceiling. One
    // that matches a generated row edits that occurrence, as an override (#519 v3 §B.5, AC 28).
    private async Task<ContextCalendarEvent> ApplyStandaloneAsync(
        ImportItem item, ImportRun run, BatchAttempt attempt, ContextCalendarEvent? earlier, CancellationToken cancellationToken)
    {
        var (locationId, detail, keepLink) = ResolvePlace(run.Places, item.Place);
        var existing = earlier ?? MatchEvent(item.Uid, attempt);
        if (existing is not null)
        {
            if (existing.RecurrencePatternId is { } seriesId)
            {
                await SeriesAsync(seriesId, run, attempt, cancellationToken);
            }

            existing.Title = item.Title;
            existing.Description = item.Description;
            existing.Location = detail;
            if (!keepLink)
            {
                existing.LocationId = locationId;
            }

            existing.StartDateTime = item.Start;
            existing.EndDateTime = item.End;
            existing.IsAllDay = item.IsAllDay;
            existing.TimeZoneId = item.ZoneId;
            if (existing.RecurrencePatternId is not null && existing.OccurrenceKey is not null)
            {
                existing.IsOverride = true;
            }

            // The stored ExternalUid is kept, never cleared (gap 1): a rule-1 match carries the row's own synthetic
            // UID, and a rule-2 match carries the ExternalUid itself.
            existing.UpdatedByUserId = run.UserId;
            existing.UpdatedAt = run.Now;
            attempt.Tally.Updated++;

            CountAdjusted(attempt, item);
            return existing;
        }

        var created = new ContextCalendarEvent
        {
            CalendarId = run.CalendarId,
            Title = item.Title,
            Description = item.Description,
            Location = detail,
            LocationId = keepLink ? null : locationId,
            StartDateTime = item.Start,
            EndDateTime = item.End,
            IsAllDay = item.IsAllDay,
            TimeZoneId = item.ZoneId,
            ExternalUid = PersistableExternalUid(item.Uid),
            ImportSourceUid = ImportSourceUidFor(item.Uid),
            CreatedByUserId = run.UserId,
            CreatedAt = run.Now,
            UpdatedAt = run.Now,
        };
        context.CalendarEvents.Add(created);
        attempt.Tally.Imported++;
        CountAdjusted(attempt, item);
        return created;
    }

    private static void CountAdjusted(BatchAttempt attempt, ImportItem item)
    {
        if (item.Adjusted)
        {
            attempt.Tally.Adjusted++;
        }
    }

    // §8.4: a cancelled plain component removes what it matches in the target calendar, and only with allowRemovals. A
    // generated row it removes is recorded as an exclusion, as an occurrence delete records one.
    private async Task<bool> ApplyCancelledEventAsync(
        ImportItem item, ImportRun run, BatchAttempt attempt, ContextCalendarEvent? earlier, CancellationToken cancellationToken)
    {
        var matched = earlier ?? MatchEvent(item.Uid, attempt);
        if (matched is null)
        {
            if (MatchPattern(item.Uid, attempt) is { } series)
            {
                await ApplyCancelledSeriesAsync(item, series, run, attempt, cancellationToken);
                return true;
            }

            attempt.Skip(CancelledUnknownReason, item.SampleTitle);
            return false;
        }

        if (!run.AllowRemovals)
        {
            attempt.Skip(CancelledKeptReason, item.SampleTitle);
            return false;
        }

        // A row this batch created and the file then cancels never existed in the database: it is un-created, not
        // removed, so neither the import count nor the removal count reports it (architect nit 9).
        var created = context.Entry(matched).State == EntityState.Added;
        if (!created)
        {
            attempt.Tally.RemovedEventIds.Add(matched.CalendarEventId);
        }

        if (matched.RecurrencePatternId is { } seriesId)
        {
            await SeriesAsync(seriesId, run, attempt, cancellationToken);
            if (matched.RecurrencePatternId is not null && matched.OccurrenceKey is { } key)
            {
                AddExclusion(attempt.Series[seriesId], key, run);
                attempt.Series[seriesId].Rows.Remove(matched);
            }
        }

        context.CalendarEvents.Remove(matched);
        CountRemoval(attempt, run, matched.Title, created);
        return true;
    }

    // Exactly RecurrencePatternService.Delete's semantics (A3): future generated rows are deleted and past ones are
    // detached and kept — wherever they sit — and the exclusions go with the series.
    private async Task ApplyCancelledSeriesAsync(
        ImportItem item, RecurrencePattern series, ImportRun run, BatchAttempt attempt, CancellationToken cancellationToken)
    {
        if (!run.AllowRemovals)
        {
            attempt.Skip(CancelledKeptReason, item.SampleTitle);
            return;
        }

        var state = await SeriesAsync(series.RecurrencePatternId, run, attempt, cancellationToken);
        if (state is null)
        {
            attempt.Skip(CancelledUnknownReason, item.SampleTitle);
            return;
        }

        var created = context.Entry(state.Pattern).State == EntityState.Added;
        attempt.Tally.RemovedEventIds.AddRange(RecurrencePatternService.RemoveSeries(context, state, run.Now));
        if (!created)
        {
            attempt.Tally.RemovedSeriesIds.Add(state.Pattern.RecurrencePatternId);
        }

        attempt.Series.Remove(series.RecurrencePatternId);
        CountRemoval(attempt, run, state.Pattern.Title, created);
    }

    private void AddExclusion(RecurrenceSeriesState state, DateTime key, ImportRun run)
    {
        if (state.Exclusions.Any(x => x.OccurrenceKey == key))
        {
            return;
        }

        var exclusion = new RecurrenceExclusion
        {
            RecurrencePatternId = state.Pattern.RecurrencePatternId,
            OccurrenceKey = key,
            CreatedAt = run.Now,
            CreatedByUserId = run.UserId,
        };
        context.RecurrenceExclusions.Add(exclusion);
        state.Exclusions.Add(exclusion);
    }

    // A series group (issue #519 §3 "import of one UID group"): its master (the last one in the file), its EXDATEs and its
    // overrides applied together, to a new series or to the stored one under its lock and the removal gate.
    private async Task ApplySeriesGroupAsync(ImportGroup group, ImportRun run, BatchAttempt attempt, CancellationToken cancellationToken)
    {
        var masters = group.Items.Where(i => i.Kind == ItemKind.Master || (i.Kind == ItemKind.Cancelled && i.HasRule)).ToList();
        var master = masters.LastOrDefault();
        foreach (var other in masters.Take(Math.Max(0, masters.Count - 1)))
        {
            attempt.Skip(DuplicateMasterReason, other.SampleTitle);
        }

        foreach (var plain in group.Items.Where(i => i.Kind == ItemKind.Standalone || (i.Kind == ItemKind.Cancelled && !i.HasRule)))
        {
            attempt.Skip(SharedUidReason, plain.SampleTitle);
        }

        var overrides = group.Items.Where(i => i.Kind is ItemKind.Override or ItemKind.CancelledOverride).ToList();
        var stored = MatchPattern(group.Uid, attempt);

        if (master is { Kind: ItemKind.Cancelled })
        {
            if (stored is not null)
            {
                await ApplyCancelledSeriesAsync(master, stored, run, attempt, cancellationToken);
            }
            else
            {
                attempt.Skip(CancelledUnknownReason, master.SampleTitle);
            }

            foreach (var item in overrides)
            {
                attempt.Skip(OverrideWithoutSeriesReason, item.SampleTitle);
            }

            return;
        }

        if (master is null && stored is null)
        {
            foreach (var item in overrides)
            {
                attempt.Skip(OverrideWithoutSeriesReason, item.SampleTitle);
            }

            return;
        }

        if (stored is null)
        {
            CreateSeries(master!, overrides, run, attempt);
            return;
        }

        var state = await SeriesAsync(stored.RecurrencePatternId, run, attempt, cancellationToken);
        if (state is null)
        {
            foreach (var item in overrides)
            {
                attempt.Skip(OverrideWithoutSeriesReason, item.SampleTitle);
            }

            return;
        }

        UpdateSeries(state, master, overrides, run, attempt);
    }

    // A series the calendar does not have yet: exclusions on it remove nothing, so they need no opt-in.
    private void CreateSeries(ImportItem master, List<ImportItem> overrides, ImportRun run, BatchAttempt attempt)
    {
        var pattern = master.Pattern!;
        var (locationId, detail, keepLink) = ResolvePlace(run.Places, master.Place);
        pattern.RecurrencePatternId = Guid.NewGuid();
        pattern.Location = detail;
        pattern.LocationId = keepLink ? null : locationId;
        pattern.ExternalUid = PersistableExternalUid(master.Uid);
        pattern.ImportSourceUid = ImportSourceUidFor(master.Uid);
        pattern.Version = Guid.NewGuid();
        pattern.KeysBackfilledAt = run.Now;

        var byKey = master.Occurrences.ToDictionary(o => o.Key);
        var excluded = new HashSet<DateTime>();
        foreach (var key in master.ExDates)
        {
            if (byKey.ContainsKey(key))
            {
                excluded.Add(key);
            }
            else
            {
                attempt.Skip(StrayExclusionReason, master.SampleTitle);
            }
        }

        var applied = new Dictionary<DateTime, ImportItem>();
        foreach (var item in overrides)
        {
            if (item.RecurrenceId is not { } at || ToKey(at, pattern) is not { } key || !byKey.ContainsKey(key))
            {
                attempt.Skip(OverrideMismatchReason, item.SampleTitle);
                continue;
            }

            if (item.Kind == ItemKind.CancelledOverride)
            {
                excluded.Add(key);
                applied.Remove(key);
            }
            else if (!excluded.Contains(key))
            {
                applied[key] = item;
            }
            else
            {
                attempt.Skip(OverrideMismatchReason, item.SampleTitle);
            }
        }

        CountOccurrences(attempt, run, master.Occurrences.Count - excluded.Count);
        context.RecurrencePatterns.Add(pattern);
        var rows = new List<ContextCalendarEvent>();
        foreach (var occurrence in master.Occurrences.Where(o => !excluded.Contains(o.Key)))
        {
            var row = RecurrenceSeries.NewOccurrence(pattern, occurrence, run.UserId, run.Now);
            context.CalendarEvents.Add(row);
            rows.Add(row);
            if (occurrence.Adjusted)
            {
                attempt.Tally.Adjusted++;
            }
        }

        var state = new RecurrenceSeriesState { Pattern = pattern, Rows = rows, Exclusions = [] };
        foreach (var key in excluded)
        {
            AddExclusion(state, key, run);
        }

        foreach (var (key, item) in applied)
        {
            ApplyOverrideFields(rows.First(r => r.OccurrenceKey == key), item, run);
        }

        attempt.Tally.ExclusionsApplied += excluded.Count;
        attempt.Tally.OverridesApplied += applied.Count;
        attempt.Series[pattern.RecurrencePatternId] = state;
        attempt.Tally.Imported++;
    }

    // A stored series: the master (when the file has one) reconciles it like a series edit; the file's EXDATEs and
    // overrides apply on top. Anything that would remove a stored row or discard a stored exception is gated on
    // allowRemovals, and a gated group changes nothing (§3 step 3). Past keys never remove rows (SR5).
    private void UpdateSeries(
        RecurrenceSeriesState state, ImportItem? master, List<ImportItem> overrides, ImportRun run, BatchAttempt attempt)
    {
        RecurrenceSeriesPlan? plan = null;
        RecurrencePattern target = state.Pattern;
        (Guid? LocationId, string? Detail, bool KeepLink) place = default;
        if (master is not null)
        {
            target = RecurrenceSeries.CloneRule(master.Pattern!);
            target.RecurrencePatternId = state.Pattern.RecurrencePatternId;
            place = ResolvePlace(run.Places, master.Place);
            target.Location = place.Detail;
            target.LocationId = place.KeepLink ? state.Pattern.LocationId : place.LocationId;
            try
            {
                plan = RecurrenceSeries.Plan(state, target, run.Now, run.MaxGeneratedOccurrences);
            }
            catch (DomainValidationException ex)
            {
                attempt.Skip(ex.Message, master.SampleTitle);
                return;
            }
        }

        var keys = plan?.KeySet ?? RecurrenceOccurrenceGenerator.Generate(state.Pattern, RecurrenceSeries.KeyingCeiling)
            .Select(o => o.Key).ToHashSet();
        var nowKey = RecurrenceSeries.NowKey(target, run.Now);
        var removedByPlan = plan?.RowRemovals.ToHashSet() ?? [];
        var exclusionsDroppedByPlan = plan?.ExclusionRemovals.ToHashSet() ?? [];
        var rowsByKey = state.Rows
            .Where(r => r.OccurrenceKey is not null && !removedByPlan.Contains(r))
            .ToDictionary(r => r.OccurrenceKey!.Value);
        var storedExclusions = state.Exclusions
            .Where(x => !exclusionsDroppedByPlan.Contains(x))
            .ToDictionary(x => x.OccurrenceKey);
        var sample = master?.SampleTitle ?? overrides.FirstOrDefault()?.SampleTitle ?? UntitledEvent;

        var newExclusions = new HashSet<DateTime>();
        var extraRemovals = new List<ContextCalendarEvent>();
        void Exclude(DateTime key, string title)
        {
            if (!keys.Contains(key))
            {
                attempt.Skip(StrayExclusionReason, title);
                return;
            }

            if (storedExclusions.ContainsKey(key) || !newExclusions.Add(key))
            {
                return;
            }

            if (rowsByKey.TryGetValue(key, out var row))
            {
                if (key < nowKey)
                {
                    newExclusions.Remove(key);
                    attempt.Skip(PastRemovalReason, title);
                    return;
                }

                extraRemovals.Add(row);
            }
        }

        foreach (var key in master?.ExDates ?? [])
        {
            Exclude(key, master!.SampleTitle);
        }

        var applied = new Dictionary<DateTime, ImportItem>();
        var discardedExclusions = new List<RecurrenceExclusion>();
        foreach (var item in overrides)
        {
            if (item.RecurrenceId is not { } at || ToKey(at, target) is not { } key || !keys.Contains(key))
            {
                attempt.Skip(OverrideMismatchReason, item.SampleTitle);
                continue;
            }

            if (item.Kind == ItemKind.CancelledOverride)
            {
                applied.Remove(key);
                Exclude(key, item.SampleTitle);
                continue;
            }

            if (newExclusions.Contains(key))
            {
                attempt.Skip(OverrideMismatchReason, item.SampleTitle);
                continue;
            }

            if (storedExclusions.TryGetValue(key, out var exclusion))
            {
                discardedExclusions.Add(exclusion);
                storedExclusions.Remove(key);
            }

            applied[key] = item;
        }

        var gated = (plan?.Counts.RemovesOrDiscards ?? false) || extraRemovals.Count > 0 || discardedExclusions.Count > 0;
        if (gated && !run.AllowRemovals)
        {
            attempt.Skip(GatedReason, sample);
            attempt.Tally.Gated++;
            return;
        }

        if (plan is not null)
        {
            plan.Inserts.RemoveAll(o => newExclusions.Contains(o.Key));
            CountOccurrences(attempt, run, plan.Inserts.Count + plan.Updates.Count);
            attempt.Tally.RemovedEventIds.AddRange(plan.RowRemovals.Select(r => r.CalendarEventId));
            attempt.Tally.DroppedOverrideKeys.AddRange(plan.RowRemovals.Where(r => r.IsOverride).Select(r => r.OccurrenceKey!.Value));
            attempt.Tally.DroppedExclusionKeys.AddRange(plan.ExclusionRemovals.Select(x => x.OccurrenceKey));
            attempt.Tally.DroppedOverrides += plan.Counts.DroppedOverrides;
            attempt.Tally.DroppedExclusions += plan.Counts.DroppedExclusions;
            attempt.Tally.Adjusted += plan.Counts.Adjusted;
            attempt.Tally.AnySeriesRegenerated |= plan.Counts.DroppedOverrides > 0;
            RecurrenceSeries.Apply(context, state, plan, run.UserId, run.Now);
            attempt.Tally.Updated++;
        }

        foreach (var row in extraRemovals)
        {
            attempt.Tally.RemovedEventIds.Add(row.CalendarEventId);
            context.CalendarEvents.Remove(row);
            state.Rows.Remove(row);
        }

        foreach (var exclusion in discardedExclusions)
        {
            attempt.Tally.DroppedExclusionKeys.Add(exclusion.OccurrenceKey);
            attempt.Tally.DroppedExclusions++;
            context.RecurrenceExclusions.Remove(exclusion);
            state.Exclusions.Remove(exclusion);
        }

        foreach (var key in newExclusions)
        {
            AddExclusion(state, key, run);
        }

        var occurrences = plan?.Occurrences ?? RecurrenceOccurrenceGenerator.Generate(state.Pattern, RecurrenceSeries.KeyingCeiling);
        var occurrenceByKey = occurrences.ToDictionary(o => o.Key);
        foreach (var (key, item) in applied)
        {
            var row = state.Rows.FirstOrDefault(r => r.OccurrenceKey == key);
            if (row is null)
            {
                row = RecurrenceSeries.NewOccurrence(state.Pattern, occurrenceByKey[key], run.UserId, run.Now);
                context.CalendarEvents.Add(row);
                state.Rows.Add(row);
            }

            ApplyOverrideFields(row, item, run);
        }

        attempt.Tally.ExclusionsApplied += newExclusions.Count;
        attempt.Tally.OverridesApplied += applied.Count;
    }

    private void ApplyOverrideFields(ContextCalendarEvent row, ImportItem item, ImportRun run)
    {
        var (locationId, detail, keepLink) = ResolvePlace(run.Places, item.Place);
        row.Title = item.Title;
        row.Description = item.Description;
        row.Location = detail;
        if (!keepLink)
        {
            row.LocationId = locationId;
        }

        row.StartDateTime = item.Start;
        row.EndDateTime = item.End;
        row.IsAllDay = item.IsAllDay;
        row.TimeZoneId = item.IsAllDay ? null : item.ZoneId;
        row.IsOverride = true;
        row.UpdatedByUserId = run.UserId;
        row.UpdatedAt = run.Now;
    }

    private static void CountRemoval(BatchAttempt attempt, ImportRun run, string title, bool createdInThisBatch)
    {
        if (createdInThisBatch)
        {
            attempt.Tally.Imported--;
            return;
        }

        attempt.Tally.Removed++;
        if (attempt.Tally.RemovedSamples.Count < run.MaxSamples)
        {
            attempt.Tally.RemovedSamples.Add(title);
        }
    }

    // Generated series occurrences only, cumulative across the whole import (§3 pipeline step 3).
    private static void CountOccurrences(BatchAttempt attempt, ImportRun run, int count)
    {
        attempt.Tally.Aggregate += count;
        if (run.Totals.Aggregate + attempt.Tally.Aggregate > run.MaxAggregateOccurrences)
        {
            throw new DomainValidationException(
                $"The import would create or regenerate more than {run.MaxAggregateOccurrences} series occurrences.");
        }
    }

    // Maps an ICS RRULE onto the bounded RecurrencePattern model. Returns (pattern, null) on success
    // or (null, reason) when the rule doesn't map — the reason becomes the per-event skip message. The default
    // weekday, day of month and month come from the LOCAL start (AC 2).
    private static (RecurrencePattern? Pattern, string? Reason) MapPattern(
        Guid calendarId, IcalRecurrenceRule rule, string title, string? description, string? location,
        ImportTimes times, bool isAllDay, string userId, DateTime now)
    {
        RecurrenceFrequency frequency;
        switch (rule.Frequency)
        {
            case FrequencyType.Daily: frequency = RecurrenceFrequency.Daily; break;
            case FrequencyType.Weekly: frequency = RecurrenceFrequency.Weekly; break;
            case FrequencyType.Monthly: frequency = RecurrenceFrequency.Monthly; break;
            case FrequencyType.Yearly: frequency = RecurrenceFrequency.Yearly; break;
            default: return (null, "Unsupported recurrence frequency.");
        }

        // WKST (rule.FirstDayOfWeek) is recognized and ignored — Odyssey's generator always uses
        // Monday-start weeks. Every other unrecognized part is a hard skip.
        if (rule.BySetPosition.Count > 0 || rule.ByWeekNo.Count > 0 || rule.ByYearDay.Count > 0
            || rule.ByHour.Count > 0 || rule.ByMinute.Count > 0 || rule.BySecond.Count > 0)
        {
            return (null, "Unsupported recurrence rule.");
        }

        if (rule.ByDay.Any(d => d.Offset.HasValue))
        {
            return (null, "Unsupported BYDAY ordinal.");
        }

        var interval = rule.Interval <= 0 ? 1 : rule.Interval;
        if (interval > 365)
        {
            return (null, "Recurrence interval exceeds the maximum of 365.");
        }

        var hasCount = rule.Count.HasValue;
        var hasUntil = rule.Until is not null;
        if (hasCount == hasUntil)
        {
            return (null, "A recurrence rule must set exactly one of COUNT or UNTIL.");
        }

        int? occurrenceCount = hasCount ? rule.Count : null;
        if (occurrenceCount is { } oc && (oc < 1 || oc > 730))
        {
            return (null, "Recurrence count exceeds the maximum of 730.");
        }

        DateTime? recurrenceEnd = null;
        if (hasUntil && !TryReadUntil(rule.Until!, isAllDay, times.Zone, out recurrenceEnd))
        {
            return (null, UnknownTimeZoneReason);
        }

        var local = isAllDay || times.Zone is null
            ? times.Start
            : CalendarTimeZones.ToWall(times.Start, times.Zone);
        DaysOfWeekFlags? daysOfWeek = null;
        int? dayOfMonth = null;
        int? monthOfYear = null;

        switch (frequency)
        {
            case RecurrenceFrequency.Daily:
                if (rule.ByDay.Count > 0 || rule.ByMonthDay.Count > 0 || rule.ByMonth.Count > 0)
                {
                    return (null, "Unsupported recurrence rule.");
                }

                break;

            case RecurrenceFrequency.Weekly:
                if (rule.ByMonthDay.Count > 0 || rule.ByMonth.Count > 0)
                {
                    return (null, "Unsupported recurrence rule.");
                }

                daysOfWeek = rule.ByDay.Count > 0 ? ToDaysOfWeekFlags(rule.ByDay) : ToSingleDay(local.DayOfWeek);
                break;

            case RecurrenceFrequency.Monthly:
                if (rule.ByDay.Count > 0 || rule.ByMonth.Count > 0)
                {
                    return (null, "Unsupported recurrence rule.");
                }

                if (rule.ByMonthDay.Count > 1)
                {
                    return (null, "Multiple BYMONTHDAY values are not supported.");
                }

                dayOfMonth = rule.ByMonthDay.Count == 1 ? rule.ByMonthDay[0] : local.Day;
                if (dayOfMonth is < 1 or > 31)
                {
                    return (null, "Unsupported BYMONTHDAY value.");
                }

                break;

            case RecurrenceFrequency.Yearly:
                if (rule.ByDay.Count > 0)
                {
                    return (null, "Unsupported recurrence rule.");
                }

                if (rule.ByMonthDay.Count > 1 || rule.ByMonth.Count > 1)
                {
                    return (null, "Multiple BYMONTHDAY/BYMONTH values are not supported.");
                }

                dayOfMonth = rule.ByMonthDay.Count == 1 ? rule.ByMonthDay[0] : local.Day;
                if (dayOfMonth is < 1 or > 31)
                {
                    return (null, "Unsupported BYMONTHDAY value.");
                }

                monthOfYear = rule.ByMonth.Count == 1 ? rule.ByMonth[0] : local.Month;
                if (monthOfYear is < 1 or > 12)
                {
                    return (null, "Unsupported BYMONTH value.");
                }

                break;
        }

        var pattern = new RecurrencePattern
        {
            CalendarId = calendarId,
            Title = title,
            Description = description,
            Location = location,
            StartDateTime = times.Start,
            EndDateTime = times.End,
            IsAllDay = isAllDay,
            TimeZoneId = isAllDay ? null : times.Zone?.Id,
            Frequency = frequency,
            Interval = interval,
            DaysOfWeek = daysOfWeek,
            DayOfMonth = dayOfMonth,
            MonthOfYear = monthOfYear,
            RecurrenceEndDate = recurrenceEnd,
            OccurrenceCount = occurrenceCount,
            CreatedByUserId = userId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        return (pattern, null);
    }

    // UNTIL as stored (#519 v3 §C): inclusive; a UTC instant on a zoned series, a wall-clock on a floating one; a
    // date-only value means the end of that day in the series' zone.
    private static bool TryReadUntil(CalDateTime until, bool isAllDay, TimeZoneInfo? zone, out DateTime? stored)
    {
        stored = null;
        if (isAllDay)
        {
            stored = DateTime.SpecifyKind(until.Value.Date, DateTimeKind.Utc);
            return true;
        }

        if (!until.HasTime)
        {
            var endOfDay = DateTime.SpecifyKind(until.Value.Date.AddDays(1).AddTicks(-1), DateTimeKind.Unspecified);
            stored = zone is null ? endOfDay : CalendarTimeZones.ToUtc(endOfDay, zone);
            return true;
        }

        DateTime instant;
        if (until.IsUtc)
        {
            instant = DateTime.SpecifyKind(until.Value, DateTimeKind.Utc);
        }
        else if (until.TzId is { Length: > 0 } tzid)
        {
            if (CalendarTimeZones.ResolveFileZone(tzid) is not { } untilZone)
            {
                return false;
            }

            instant = CalendarTimeZones.ToUtc(until.Value, untilZone);
        }
        else
        {
            var wall = DateTime.SpecifyKind(until.Value, DateTimeKind.Unspecified);
            stored = zone is null ? wall : CalendarTimeZones.ToUtc(wall, zone);
            return true;
        }

        stored = zone is null ? DateTime.SpecifyKind(instant, DateTimeKind.Unspecified) : instant;
        return true;
    }

    private static DaysOfWeekFlags ToDaysOfWeekFlags(IEnumerable<WeekDay> days)
    {
        var flags = DaysOfWeekFlags.None;
        foreach (var day in days)
        {
            flags |= ToSingleDay(day.DayOfWeek);
        }

        return flags;
    }

    private static DaysOfWeekFlags ToSingleDay(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => DaysOfWeekFlags.Monday,
        DayOfWeek.Tuesday => DaysOfWeekFlags.Tuesday,
        DayOfWeek.Wednesday => DaysOfWeekFlags.Wednesday,
        DayOfWeek.Thursday => DaysOfWeekFlags.Thursday,
        DayOfWeek.Friday => DaysOfWeekFlags.Friday,
        DayOfWeek.Saturday => DaysOfWeekFlags.Saturday,
        _ => DaysOfWeekFlags.Sunday,
    };

    // A VEVENT's UID that is itself one of Odyssey's own synthesized "{primary-key}@odyssey.local"
    // forms (issue #345 security review, F1) must never be persisted as ExternalUid: an export writes
    // ExternalUid verbatim, so a row in calendar B carrying A's synthetic UID would put A's key in B's
    // file, and importing that file into A would overwrite A's unrelated row in place. Such a UID is
    // matched by key only within the target calendar, and kept as ImportSourceUid (issue #516 §8.2),
    // which is never exported and never read as a key.
    private static bool IsSyntheticUid(string uid) =>
        uid.EndsWith(SyntheticUidSuffix, StringComparison.Ordinal)
        && Guid.TryParse(uid[..^SyntheticUidSuffix.Length], out _);

    // The value to persist as ExternalUid for a created row — null when the incoming UID is absent or
    // synthetic; the UID unchanged otherwise.
    private static string? PersistableExternalUid(string? uid) => uid is null || IsSyntheticUid(uid) ? null : uid;

    // The value to persist as ImportSourceUid for a created row: the UID only when it is synthetic (rule 4).
    private static string? ImportSourceUidFor(string? uid) => uid is not null && IsSyntheticUid(uid) ? uid : null;

    /// <summary>Whether the multipart part's content type is acceptable for an <c>.ics</c> upload. The
    /// <c>.ics</c> extension and the parse itself are the real validity gates; the content type is only
    /// a hint, so we accept what browsers/OSes routinely send for calendar files (many map <c>.ics</c>
    /// to <c>application/octet-stream</c> or <c>text/plain</c>, and some omit it) and only reject a
    /// clearly-wrong declared type like <c>application/json</c>. Public so the controller can gate at
    /// the edge (defense-in-depth for direct service callers keeps it here too).</summary>
    public static bool IsAcceptedContentType(string? contentType) =>
        ImportFileReader.IsAcceptedContentType(contentType, ImportLinks.CalendarContentTypes);

    // Builds the download filename "yyyyMMdd_<calendar name>.ics" (date = export date), stripping quotes
    // and control characters from the name for a safe Content-Disposition value.
    private static string BuildFileName(string name, DateTime exportDate)
    {
        var cleaned = new string(name.Where(c => !char.IsControl(c) && c is not ('"' or '\\' or '/')).ToArray()).Trim();
        if (string.IsNullOrEmpty(cleaned))
        {
            cleaned = "calendar";
        }

        return $"{exportDate:yyyyMMdd}_{cleaned}.ics";
    }
}

/// <summary>
/// The upload as the grouper needs it: seekable, with a known length (issue #519 implementation note 5). The buffered
/// form file already is; anything else is copied, under the byte cap, to a temporary file that is deleted on dispose.
/// </summary>
internal sealed class SeekableUpload : IAsyncDisposable
{
    private readonly string? tempPath;

    private SeekableUpload(Stream stream, string? tempPath)
    {
        Stream = stream;
        this.tempPath = tempPath;
    }

    public Stream Stream { get; }

    public static async Task<SeekableUpload> OpenAsync(Stream source, long maxBytes, string directory, CancellationToken cancellationToken)
    {
        if (source.CanSeek)
        {
            if (source.Length > maxBytes)
            {
                throw new DomainValidationException($"The .ics file exceeds the {maxBytes / (1024 * 1024)} MB limit.");
            }

            return new SeekableUpload(source, null);
        }

        var path = Path.Combine(directory, $"odyssey-ics-{Guid.NewGuid():N}.upload");
        var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16,
            FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        try
        {
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                total += read;
                if (total > maxBytes)
                {
                    throw new DomainValidationException($"The .ics file exceeds the {maxBytes / (1024 * 1024)} MB limit.");
                }

                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            file.Seek(0, SeekOrigin.Begin);
            return new SeekableUpload(file, path);
        }
        catch
        {
            await file.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (tempPath is not null)
        {
            await Stream.DisposeAsync();
        }
    }
}

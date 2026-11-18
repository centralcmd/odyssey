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
/// ICS (RFC 5545) import/export for calendars (issues #330, #340, #516).
/// </summary>
/// <remarks>
/// <para>
/// <b>Export</b> plans up front and streams (issue #516 §3). The planner projects keys and the few columns the collapse
/// decision needs — never whole entities — and captures the ordered ids of the standalone rows and the series that
/// collapse to one <c>RRULE</c>. It is the single definition of what an export contains, shared by the export and its
/// summary. The writer then fetches rows in id batches of <see cref="ExportChunking.ChunkSize"/> and writes one
/// <c>VEVENT</c> at a time, counting bytes; past the byte cap it throws <see cref="IcsExportSizeExceededException"/>
/// and the controller aborts the connection. <b>No export opens a transaction</b> (B1): <c>OdysseyContext</c> uses
/// <c>EnableRetryOnFailure</c>, so a retry would re-emit chunks already streamed, and an hour-long snapshot would stall
/// InnoDB purge. An export therefore reflects its plan — a row deleted after planning is skipped, one edited is written
/// as it stands when its batch is read, one created is not included (Non-Goal 7).
/// </para>
/// <para>
/// A series collapses only when <see cref="CanExportAsRule"/> holds and every generated row sits in the series' own
/// calendar (gap 3); otherwise its rows are written one by one, and an occurrence moved into another calendar is
/// written in that calendar's file instead.
/// </para>
/// <para>
/// <b>Import</b> streams too (§3 components 4–6): <see cref="IcsComponentSplitter"/> yields one <c>VEVENT</c> at a
/// time, and events are applied in batches of <see cref="ImportBatchSize"/>, each resolved by keyed queries against the
/// target calendar and saved in its own transaction. No state spans the whole file except the counters, the place
/// cache and the occurrence budget; a UID seen again later resolves to the row the earlier component wrote, so the
/// last component wins.
/// </para>
/// </remarks>
public class CalendarIcsService
{
    /// <summary>Events applied per import batch (issue #516 §8.5): a constant, not a setting.</summary>
    public const int ImportBatchSize = 500;

    public const string CancelledUnknownReason = "Event is cancelled.";

    public const string CancelledKeptReason =
        "Event is cancelled in the file; kept, because removing cancelled events was not requested.";

    public const string RecurrenceIdReason = "Modified occurrence of a series (RECURRENCE-ID) — not supported yet.";

    private const string SyntheticUidSuffix = "@odyssey.local";
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

    // ---------------------------------------------------------------- Export planning

    // The columns the collapse decision reads, and nothing else: a planner that loaded entities would hold every
    // description of a 100,000-event calendar at once (A5).
    private sealed record RowShape(
        Guid Id, Guid? PatternId, Guid CalendarId, DateTime Start, DateTime End, bool IsAllDay,
        string Title, string? Description, string? Location, Guid? LocationId);

    private readonly record struct PlannedSeries(Guid PatternId, Guid CalendarId);

    private sealed record ExportPlan(
        string FileName, bool Aggregate, List<PlannedSeries> Series, List<Guid> StandaloneIds, int OccurrenceCount,
        long MaxBytes)
    {
        public int EventCount => Series.Count + StandaloneIds.Count;
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
                : new ExportPlan(BuildFileName(title, exportDate), false, [], [eventId], 1, maxBytes);
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

    // At most four planning queries whatever the series count (§10, AC 19): the calendar, its series, every row of
    // those series (streamed, one series held at a time), and the calendar's own row keys.
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
            var rows = context.CalendarEvents.AsNoTracking()
                .Where(e => e.RecurrencePatternId != null && e.RecurrencePattern!.CalendarId == calendarId);
            series = await DecideSeriesAsync(rows, patterns, occurrenceCap, uniformIn: calendarId, cancellationToken);
        }

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
        return new ExportPlan(
            BuildFileName(name, exportDate), false, ordered.Select(s => s.Series).ToList(), standalone,
            ordered.Sum(s => s.Rows) + standalone.Count, maxBytes);
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
        var fileName = BuildFileName(pattern.Title, exportDate);

        return CanExportAsRule(pattern, rows, occurrenceCap) && rows.All(r => r.CalendarId == pattern.CalendarId)
            ? new ExportPlan(fileName, false, [new PlannedSeries(patternId, pattern.CalendarId)], [], rows.Count, maxBytes)
            : new ExportPlan(fileName, false, [], rows.Select(r => r.Id).ToList(), rows.Count, maxBytes);
    }

    private async Task<ExportPlan> PlanAggregateAsync(
        CalendarEventsIcsExportQueryParams query, ImportExportLimits effectiveLimits, DateTime exportDate,
        int occurrenceCap, long maxBytes, CancellationToken cancellationToken)
    {
        var (from, to) = ValidateAggregateWindow(query.From, query.To, effectiveLimits.CalendarIcsMaxAggregateExportWindowDays);
        var searchTerm = ListQuery.NormalizeSearch(query.Search);
        var calendarIds = query.CalendarIds is { Length: > 0 } ids ? ids.Distinct().ToList() : null;
        var fileName = $"{exportDate:yyyyMMdd}_calendar-events.ics";
        var rowQuery = BuildAggregateRowQuery(calendarIds, from, to, searchTerm);
        var maxEvents = effectiveLimits.CalendarIcsMaxExportEvents;

        // A filtered request (From/To and/or Search) never collapses a series: every matched row is its own VEVENT.
        // The id read is bounded to max+1 when a cap is set, so an over-cap export is refused without a full scan.
        if (from is not null || searchTerm is not null)
        {
            var orderedIds = rowQuery
                .OrderBy(e => e.StartDateTime).ThenBy(e => e.CalendarEventId)
                .Select(e => e.CalendarEventId);
            var matched = maxEvents is { } max
                ? await orderedIds.Take(max + 1).ToListAsync(cancellationToken)
                : await orderedIds.ToListAsync(cancellationToken);
            return new ExportPlan(fileName, true, [], matched, matched.Count, maxBytes);
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
        if (patternIds.Count > 0)
        {
            var patterns = await context.RecurrencePatterns.AsNoTracking()
                .Where(p => patternIds.Contains(p.RecurrencePatternId))
                .ToDictionaryAsync(p => p.RecurrencePatternId, cancellationToken);
            series = await DecideSeriesAsync(
                rowQuery.Where(e => e.RecurrencePatternId != null), patterns, occurrenceCap, uniformIn: null, cancellationToken);
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
        return new ExportPlan(
            fileName, true, ordered.Select(s => s.Series).ToList(), standalone, ordered.Sum(s => s.Rows) + standalone.Count,
            maxBytes);
    }

    // Streams every row of the given series, grouped by series and ordered within each, holding one series at a time.
    // A series collapses when CanExportAsRule holds and its rows share one calendar — the series' own one when
    // uniformIn names it (the calendar export, gap 3), else any one (the aggregate's HasUniformCalendarId rule), under
    // which it is then attributed.
    private async Task<List<(PlannedSeries Series, int Rows)>> DecideSeriesAsync(
        IQueryable<ContextCalendarEvent> seriesRows, IReadOnlyDictionary<Guid, RecurrencePattern> patterns, int occurrenceCap,
        Guid? uniformIn, CancellationToken cancellationToken)
    {
        var decided = new List<(PlannedSeries Series, int Rows)>();
        var group = new List<RowShape>();

        void Decide()
        {
            if (group.Count == 0 || !patterns.TryGetValue(group[0].PatternId!.Value, out var pattern))
            {
                return;
            }

            var calendarId = group[0].CalendarId;
            if ((uniformIn is null || calendarId == uniformIn) && group.All(r => r.CalendarId == calendarId)
                && CanExportAsRule(pattern, group, occurrenceCap))
            {
                decided.Add((new PlannedSeries(pattern.RecurrencePatternId, calendarId), group.Count));
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
                Decide();
                group.Clear();
            }

            group.Add(row);
        }

        Decide();
        return decided;
    }

    private static IQueryable<RowShape> Shapes(IQueryable<ContextCalendarEvent> rows) =>
        rows.Select(e => new RowShape(
            e.CalendarEventId, e.RecurrencePatternId, e.CalendarId, e.StartDateTime, e.EndDateTime, e.IsAllDay,
            e.Title, e.Description, e.Location, e.LocationId));

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
        List<Guid>? calendarIds, DateTime? from, DateTime? to, string? searchTerm)
    {
        var q = context.CalendarEvents.AsNoTracking();

        if (calendarIds is not null)
        {
            q = q.Where(e => calendarIds.Contains(e.CalendarId));
        }

        // Overlap, not start-only — matches CalendarEventService.ListAsync's semantics (AC 6).
        if (from is { } f && to is { } t)
        {
            q = q.Where(e => e.StartDateTime < t && e.EndDateTime > f);
        }

        if (searchTerm is not null)
        {
            var pattern = ListQuery.ContainsPattern(searchTerm);
            q = q.Where(e => EF.Functions.Like(e.Title, pattern));
        }

        return q;
    }

    // A stored series exports as a single RRULE VEVENT only when its actual generated rows exactly
    // match — on every field — what a strict RFC 5545 reader would compute for the same rule. Any
    // clamped occurrence, individual edit, or deletion breaks the match and forces a per-occurrence
    // flatten, so the exported file never asserts an RRULE another calendar app would read differently.
    private static bool CanExportAsRule(RecurrencePattern pattern, List<RowShape> rows, int maxGeneratedOccurrences)
    {
        List<(DateTime Start, DateTime End)>? literal;
        try
        {
            literal = RecurrenceOccurrenceGenerator.GenerateRfcLiteral(pattern, maxGeneratedOccurrences);
        }
        catch (ArgumentOutOfRangeException)
        {
            // A degenerate rule whose requested day never exists (e.g. Yearly, MonthOfYear=2,
            // DayOfMonth=30 — Feb 30 never occurs) climbs years indefinitely without ever tripping
            // GenerateRfcLiteral's own step guard until DateTime.DaysInMonth's year argument exceeds
            // year 9999 (security review, PR #345 F1). Flattening is the documented, safe fallback.
            return false;
        }

        if (literal is null || literal.Count != rows.Count)
        {
            return false;
        }

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var (start, end) = literal[i];
            if (row.Start != start || row.End != end || row.IsAllDay != pattern.IsAllDay
                || row.Title != pattern.Title || row.Description != pattern.Description || row.Location != pattern.Location
                || row.LocationId != pattern.LocationId)
            {
                return false;
            }
        }

        return true;
    }

    // ---------------------------------------------------------------- Export writing

    private async Task WriteAsync(ExportPlan plan, Stream output, bool includePlaceNames, CancellationToken cancellationToken)
    {
        var stamp = new CalDateTime(timeProvider.GetUtcNow().UtcDateTime, hasTime: true);
        var (head, tail) = IcsChunkSerializer.BuildEnvelope(ProductId);
        await IcsChunkSerializer.WriteAsync(output, head, cancellationToken);
        var writtenBytes = (long)Encoding.UTF8.GetByteCount(head) + Encoding.UTF8.GetByteCount(tail);
        var written = 0;

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
            var attributed = batch.ToDictionary(s => s.PatternId, s => s.CalendarId);
            foreach (var pattern in patterns)
            {
                var uid = plan.Aggregate || pattern.ExternalUid is null
                    ? $"{pattern.RecurrencePatternId}{SyntheticUidSuffix}"
                    : pattern.ExternalUid;
                var ev = BuildVEvent(
                    uid, pattern.Title, pattern.Description, pattern.LocationId, pattern.Location, pattern.StartDateTime,
                    pattern.EndDateTime, pattern.IsAllDay, pattern.CreatedAt, pattern.UpdatedAt, stamp, names,
                    calendars?.GetValueOrDefault(attributed[pattern.RecurrencePatternId]));
                ev.RecurrenceRule = BuildRRule(pattern);
                await EmitAsync(ev);
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
                    uid, row.Title, row.Description, row.LocationId, row.Location, row.StartDateTime, row.EndDateTime,
                    row.IsAllDay, row.CreatedAt, row.UpdatedAt, stamp, names, calendars?.GetValueOrDefault(row.CalendarId)));
            }
        }

        await IcsChunkSerializer.WriteAsync(output, tail, cancellationToken);
    }

    // One builder for every VEVENT (standalone, recurring and aggregate), so all of them carry the place codec,
    // STATUS and the stamps (issue #516 §8.3, §8.4). Categories names the source calendar on the aggregate only.
    private static IcalEvent BuildVEvent(
        string uid, string title, string? description, Guid? locationId, string? detail, DateTime start, DateTime end,
        bool isAllDay, DateTime createdAt, DateTime updatedAt, CalDateTime stamp, IReadOnlyDictionary<Guid, string> placeNames,
        string? calendarName)
    {
        var ev = new IcalEvent
        {
            Uid = uid,
            Summary = title,
            Description = description,
            Start = ToCalDateTime(start, isAllDay),
            End = ToCalDateTime(end, isAllDay),
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

    private static IcalRecurrencePattern BuildRRule(RecurrencePattern pattern)
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
            // VALUE=DATE all-day DTSTART is rejected by Google/Apple, so emit a DATE for all-day series.
            rule.Until = pattern.IsAllDay
                ? new CalDateTime(end.Year, end.Month, end.Day)
                : new CalDateTime(DateTime.SpecifyKind(end, DateTimeKind.Utc), hasTime: true);
        }

        switch (pattern.Frequency)
        {
            case RecurrenceFrequency.Weekly:
                rule.ByDay = ToWeekDays(pattern.DaysOfWeek ?? DaysOfWeekFlags.None);
                break;
            case RecurrenceFrequency.Monthly:
                rule.ByMonthDay = [pattern.DayOfMonth ?? pattern.StartDateTime.Day];
                break;
            case RecurrenceFrequency.Yearly:
                rule.ByMonthDay = [pattern.DayOfMonth ?? pattern.StartDateTime.Day];
                rule.ByMonth = [pattern.MonthOfYear ?? pattern.StartDateTime.Month];
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

    private static CalDateTime ToCalDateTime(DateTime utc, bool isAllDay) => isAllDay
        ? new CalDateTime(utc.Year, utc.Month, utc.Day)
        : new CalDateTime(DateTime.SpecifyKind(utc, DateTimeKind.Utc), hasTime: true);

    // ---------------------------------------------------------------- Import

    private enum ItemKind
    {
        Standalone,
        Series,
        Cancelled,
    }

    // One VEVENT that passed validation, ready for its batch to resolve and apply.
    private sealed class ImportItem
    {
        public required ItemKind Kind { get; init; }

        public required string SampleTitle { get; init; }

        public required string? Uid { get; init; }

        public bool HasRule { get; init; }

        public string Title { get; init; } = string.Empty;

        public string? Description { get; init; }

        public ImportedPlace Place { get; init; } = new(null, null, false);

        public DateTime Start { get; init; }

        public DateTime End { get; init; }

        public bool IsAllDay { get; init; }

        public RecurrencePattern? Pattern { get; init; }

        public List<(DateTime Start, DateTime End)> Occurrences { get; init; } = [];
    }

    // What spans the whole import: the caps read once, the counters, the place cache and the occurrence budget. Nothing
    // here grows with the file (N1).
    private sealed class ImportRun
    {
        public required Guid CalendarId { get; init; }

        public required string UserId { get; init; }

        public required DateTime Now { get; init; }

        public required IcsPlaces Places { get; init; }

        public required ImportSkipCollector Skipped { get; init; }

        public required bool AllowRemovals { get; init; }

        public required int MaxAggregateOccurrences { get; init; }

        public required int MaxEventDurationDays { get; init; }

        public required int MaxGeneratedOccurrences { get; init; }

        public required int MaxSamples { get; init; }

        public int Imported { get; set; }

        public int Updated { get; set; }

        public int Removed { get; set; }

        public List<string> RemovedSamples { get; } = [];

        public bool AnySeriesRegenerated { get; set; }

        public int Aggregate { get; set; }

        public int CommittedBatches { get; set; }

        public int CommittedEvents { get; set; }
    }

    // The per-batch view of what this batch already wrote, keyed by UID (R2): a later component for the same UID in the
    // same batch sees the earlier one's row, or null when that component removed it, so the last component wins.
    private sealed class BatchState
    {
        public Dictionary<string, ContextCalendarEvent?> Events { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, RecurrencePattern?> Patterns { get; } = new(StringComparer.Ordinal);

        // Occurrences this batch added, per series, so regenerating or removing a series created or regenerated
        // earlier in the batch reaches rows the database does not hold yet.
        public Dictionary<Guid, List<ContextCalendarEvent>> AddedOccurrences { get; } = [];

        public List<Guid> RemovedEventIds { get; } = [];

        public List<Guid> RemovedSeriesIds { get; } = [];
    }

    /// <summary>
    /// Imports an <c>.ics</c> file into <paramref name="targetCalendarId"/>, streaming it one <c>VEVENT</c> at a time
    /// and committing every <see cref="ImportBatchSize"/> events (issue #516 §3). A failure after a batch committed is
    /// a <see cref="CalendarIcsImportPartiallyAppliedException"/> carrying the original status; re-running the same file
    /// is idempotent and completes it. A cancellation stops at the next batch boundary and is never wrapped.
    /// </summary>
    /// <param name="claims">
    /// The importer's location claims (issue #380 §8.4): places are linked or created only with both
    /// <c>locations.read</c> and <c>locations.create</c>. Null imports every place as detail text.
    /// </param>
    /// <param name="allowRemovals">Whether a cancelled component may remove the event or series it matches (§8.4).</param>
    /// <param name="canDelete">
    /// Whether the importer holds <c>calendar.delete</c>, evaluated once from the request principal. With
    /// <paramref name="allowRemovals"/> and without it the whole import is refused before anything is read.
    /// </param>
    public async Task<IcsImportResult> ImportAsync(
        Guid targetCalendarId, Stream icsFile, long contentLength, string? contentType, string userId,
        CancellationToken cancellationToken = default, LocationClaims? claims = null,
        bool allowRemovals = false, bool canDelete = false)
    {
        if (!IsAcceptedContentType(contentType))
        {
            throw new DomainValidationException("The uploaded file must be a calendar file (text/calendar).");
        }

        if (allowRemovals && !canDelete)
        {
            throw new DomainForbiddenException("Removing cancelled events needs permission to delete calendar events.");
        }

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
            Skipped = new ImportSkipCollector(cap.ImportMaxSamplesPerSkipReason),
            AllowRemovals = allowRemovals,
            MaxAggregateOccurrences = cap.CalendarIcsMaxAggregateOccurrences,
            MaxEventDurationDays = journalCap.CalendarMaxEventDurationDays,
            MaxGeneratedOccurrences = journalCap.RecurrenceMaxGeneratedOccurrences,
            MaxSamples = cap.ImportMaxSamplesPerSkipReason,
        };

        using var reader = ImportFileReader.OpenBoundedTextReader(icsFile, maxImportBytes, ".ics");
        var splitter = new IcsComponentSplitter(reader);
        var batch = new List<IcalEvent?>(ImportBatchSize);
        var seen = 0;
        try
        {
            await foreach (var component in splitter.ReadEventsAsync(cancellationToken))
            {
                seen++;
                if (maxImportEvents is { } max && seen > max)
                {
                    throw new DomainValidationException($"The file contains more than {max} events.");
                }

                batch.Add(ParseComponent(component));
                if (batch.Count == ImportBatchSize)
                {
                    await ApplyBatchAsync(batch, run, cancellationToken);
                    batch.Clear();
                }
            }

            if (batch.Count > 0)
            {
                await ApplyBatchAsync(batch, run, cancellationToken);
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

            var reason = ex is DomainException ? ex.Message : "The import failed part-way.";
            throw new CalendarIcsImportPartiallyAppliedException(
                $"{reason} The first {run.CommittedEvents} events of the file were already imported; importing the same "
                + "file again is safe and completes it.",
                status, run.CommittedBatches, run.CommittedEvents, ex);
        }

        return new IcsImportResult
        {
            ImportedCount = run.Imported,
            UpdatedCount = run.Updated,
            AnySeriesRegenerated = run.AnySeriesRegenerated,
            PlacesLinked = run.Places.Linked,
            PlacesCreated = run.Places.Created,
            RemovedCount = run.Removed,
            RemovedSampleTitles = run.RemovedSamples,
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

    private async Task ApplyBatchAsync(List<IcalEvent?> events, ImportRun run, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var items = new List<ImportItem>(events.Count);
        foreach (var ev in events)
        {
            if (Classify(ev, run) is { } item)
            {
                items.Add(item);
            }
        }

        var state = new BatchState();
        var uids = items.Select(i => i.Uid).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        var resolver = await ResolveAsync(run.CalendarId, uids, cancellationToken);

        foreach (var item in items)
        {
            switch (item.Kind)
            {
                case ItemKind.Standalone:
                    ApplyStandalone(item, run, state, resolver);
                    break;
                case ItemKind.Series:
                    await ApplySeriesAsync(item, run, state, resolver, cancellationToken);
                    break;
                default:
                    await ApplyCancelledAsync(item, run, state, resolver, cancellationToken);
                    break;
            }
        }

        await SaveBatchAsync(run, cancellationToken);
        run.CommittedBatches++;
        run.CommittedEvents += events.Count;

        // One line per committed batch that removed anything (§7.7): ids and counts only, never a title or place, so
        // an import that later fails is audited for exactly what it removed.
        if (state.RemovedEventIds.Count > 0 || state.RemovedSeriesIds.Count > 0)
        {
            logger.LogInformation(
                "Calendar .ics import by {UserId} into calendar {CalendarId} removed {RemovedCount} cancelled item(s): "
                + "series {RemovedSeriesIds}; events {RemovedEventIds}.",
                run.UserId, run.CalendarId, state.RemovedEventIds.Count + state.RemovedSeriesIds.Count,
                state.RemovedSeriesIds, state.RemovedEventIds);
        }

        context.ChangeTracker.Clear();
    }

    // One transaction per batch, inside the execution strategy, because EnableRetryOnFailure makes a bare
    // BeginTransactionAsync throw. The EF InMemory provider honours neither, so there the save simply runs.
    private async Task SaveBatchAsync(ImportRun run, CancellationToken cancellationToken)
    {
        if (!context.Database.IsRelational())
        {
            await run.Places.SaveChangesAsync(context, cancellationToken);
            return;
        }

        var strategy = context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            await run.Places.SaveChangesAsync(context, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        });
    }

    private ImportItem? Classify(IcalEvent? ev, ImportRun run)
    {
        if (ev is null)
        {
            run.Skipped.Add("Event could not be parsed.", UntitledEvent);
            return null;
        }

        var title = ev.Summary?.Trim();
        var sampleTitle = string.IsNullOrWhiteSpace(title) ? UntitledEvent : title;
        var uid = string.IsNullOrWhiteSpace(ev.Uid) ? null : ev.Uid.Trim();

        // Until Spec B models exceptions, a RECURRENCE-ID component is never applied — cancelled or not — so a
        // cancelled occurrence can never act on a whole series (S1).
        if (ev.Properties.ContainsKey("RECURRENCE-ID"))
        {
            run.Skipped.Add(RecurrenceIdReason, sampleTitle);
            return null;
        }

        if (string.Equals(ev.Status?.Trim(), "CANCELLED", StringComparison.OrdinalIgnoreCase))
        {
            if (uid is null)
            {
                run.Skipped.Add(CancelledUnknownReason, sampleTitle);
                return null;
            }

            return new ImportItem
            {
                Kind = ItemKind.Cancelled, SampleTitle = sampleTitle, Uid = uid, HasRule = ev.RecurrenceRule is not null,
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

        var isAllDay = ev.IsAllDay;

        // Resolving DTSTART/DTEND to UTC can throw for an unresolvable TZID or a malformed
        // duration — that's a per-event problem (skip with a reason), not a 500 for the whole file.
        DateTime startUtc, endUtc;
        try
        {
            startUtc = ev.Start.AsUtc;
            endUtc = ev.End?.AsUtc ?? startUtc + ev.EffectiveDuration.ToTimeSpanUnspecified();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            run.Skipped.Add("Event has an unsupported or unresolvable date/time.", sampleTitle);
            return null;
        }

        try
        {
            CalendarEventService.ValidateTimes(startUtc, endUtc, isAllDay, run.MaxEventDurationDays);
        }
        catch (DomainValidationException ex)
        {
            run.Skipped.Add(ex.Message, sampleTitle);
            return null;
        }

        if (ev.RecurrenceRule is null)
        {
            return new ImportItem
            {
                Kind = ItemKind.Standalone, SampleTitle = sampleTitle, Uid = uid, Title = title, Description = description,
                Place = place, Start = startUtc, End = endUtc, IsAllDay = isAllDay,
            };
        }

        if (ev.Properties.ContainsKey("EXDATE") || ev.Properties.ContainsKey("RDATE"))
        {
            run.Skipped.Add("Recurrence exceptions (EXDATE/RDATE) are not supported in v1.", sampleTitle);
            return null;
        }

        // Ical.Net exposes only a single RecurrenceRule, and no EXRULE at all, so "more
        // recurrence than v1 models" has to be read off the raw property list.
        if (ev.Properties.CountOf("RRULE") > 1 || ev.Properties.ContainsKey("EXRULE"))
        {
            run.Skipped.Add("Unsupported recurrence rule.", sampleTitle);
            return null;
        }

        var (pattern, reason) = MapPattern(
            run.CalendarId, ev.RecurrenceRule, title, description, place.Detail, startUtc, endUtc, isAllDay, run.UserId, run.Now);
        if (pattern is null)
        {
            run.Skipped.Add(reason!, sampleTitle);
            return null;
        }

        List<(DateTime Start, DateTime End)> occurrences;
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

        return new ImportItem
        {
            Kind = ItemKind.Series, SampleTitle = sampleTitle, Uid = uid, HasRule = true, Title = title,
            Description = description, Place = place, Start = startUtc, End = endUtc, IsAllDay = isAllDay,
            Pattern = pattern, Occurrences = occurrences,
        };
    }

    // ----- UID resolution (§8.2)

    // The batch's rows in the target calendar that any of its UIDs could name, read by one keyed query per table
    // (R1): by ExternalUid, by ImportSourceUid for a synthetic UID, and by primary key for a synthetic UID's guid.
    // Tracked, because the batch updates them. The whole calendar is never loaded.
    private sealed record BatchResolver(List<ContextCalendarEvent> Events, List<RecurrencePattern> Patterns);

    private async Task<BatchResolver> ResolveAsync(Guid calendarId, List<string> uids, CancellationToken cancellationToken)
    {
        if (uids.Count == 0)
        {
            return new BatchResolver([], []);
        }

        var synthetic = uids.Where(IsSyntheticUid).ToList();
        var guids = synthetic.Select(u => Guid.Parse(u[..^SyntheticUidSuffix.Length])).ToList();

        var events = await context.CalendarEvents
            .Where(e => e.CalendarId == calendarId
                && ((e.ExternalUid != null && uids.Contains(e.ExternalUid))
                    || (e.ImportSourceUid != null && synthetic.Contains(e.ImportSourceUid))
                    || guids.Contains(e.CalendarEventId)))
            .OrderBy(e => e.CreatedAt).ThenBy(e => e.CalendarEventId)
            .ToListAsync(cancellationToken);
        var patterns = await context.RecurrencePatterns
            .Where(p => p.CalendarId == calendarId
                && ((p.ExternalUid != null && uids.Contains(p.ExternalUid))
                    || (p.ImportSourceUid != null && synthetic.Contains(p.ImportSourceUid))
                    || guids.Contains(p.RecurrencePatternId)))
            .OrderBy(p => p.CreatedAt).ThenBy(p => p.RecurrencePatternId)
            .ToListAsync(cancellationToken);
        return new BatchResolver(events, patterns);
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

        if (live.FirstOrDefault(c => string.Equals(externalUid(c), uid, StringComparison.Ordinal)) is { } byExternal)
        {
            return byExternal;
        }

        return IsSyntheticUid(uid)
            ? live.FirstOrDefault(c => string.Equals(importSourceUid(c), uid, StringComparison.Ordinal))
            : null;
    }

    private ContextCalendarEvent? MatchEvent(string? uid, BatchState state, BatchResolver resolver)
    {
        if (uid is null)
        {
            return null;
        }

        return state.Events.TryGetValue(uid, out var written)
            ? written
            : Match(uid, resolver.Events, e => e.CalendarEventId, e => e.ExternalUid, e => e.ImportSourceUid, context);
    }

    private RecurrencePattern? MatchPattern(string? uid, BatchState state, BatchResolver resolver)
    {
        if (uid is null)
        {
            return null;
        }

        return state.Patterns.TryGetValue(uid, out var written)
            ? written
            : Match(uid, resolver.Patterns, p => p.RecurrencePatternId, p => p.ExternalUid, p => p.ImportSourceUid, context);
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

    private void ApplyStandalone(ImportItem item, ImportRun run, BatchState state, BatchResolver resolver)
    {
        CountOccurrences(run, 1);
        var (locationId, detail, keepLink) = ResolvePlace(run.Places, item.Place);
        var existing = MatchEvent(item.Uid, state, resolver);
        if (existing is not null)
        {
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
            // The stored ExternalUid is kept, never cleared (gap 1): a rule-1 match carries the row's own synthetic
            // UID, and a rule-2 match carries the ExternalUid itself.
            existing.UpdatedByUserId = run.UserId;
            existing.UpdatedAt = run.Now;
            run.Updated++;
            Remember(state.Events, item.Uid, existing);
            return;
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
            ExternalUid = PersistableExternalUid(item.Uid),
            ImportSourceUid = ImportSourceUidFor(item.Uid),
            CreatedByUserId = run.UserId,
            CreatedAt = run.Now,
            UpdatedAt = run.Now,
        };
        context.CalendarEvents.Add(created);
        run.Imported++;
        Remember(state.Events, item.Uid, created);
    }

    private async Task ApplySeriesAsync(
        ImportItem item, ImportRun run, BatchState state, BatchResolver resolver, CancellationToken cancellationToken)
    {
        var pattern = item.Pattern!;
        var existing = MatchPattern(item.Uid, state, resolver);
        var (locationId, detail, keepLink) = ResolvePlace(run.Places, item.Place);
        pattern.Location = detail;
        pattern.LocationId = keepLink ? existing?.LocationId : locationId;

        if (existing is not null)
        {
            var futureOccurrences = item.Occurrences.Where(o => o.Start >= run.Now).ToList();
            CountOccurrences(run, futureOccurrences.Count);
            ApplyPatternFields(existing, pattern, run.UserId, run.Now);

            // Regenerating loads only this series' future rows in the target calendar, as it always did.
            if (context.Entry(existing).State != EntityState.Added)
            {
                var futureRows = await context.CalendarEvents
                    .Where(e => e.RecurrencePatternId == existing.RecurrencePatternId && e.CalendarId == run.CalendarId
                        && e.StartDateTime >= run.Now)
                    .ToListAsync(cancellationToken);
                context.CalendarEvents.RemoveRange(futureRows);
            }

            if (state.AddedOccurrences.Remove(existing.RecurrencePatternId, out var added))
            {
                context.CalendarEvents.RemoveRange(added.Where(o => o.StartDateTime >= run.Now));
                added.RemoveAll(o => o.StartDateTime >= run.Now);
                state.AddedOccurrences[existing.RecurrencePatternId] = added;
            }

            AddOccurrences(existing, futureOccurrences, run, state);
            run.Updated++;
            run.AnySeriesRegenerated = true;
            Remember(state.Patterns, item.Uid, existing);
            return;
        }

        CountOccurrences(run, item.Occurrences.Count);
        pattern.ExternalUid = PersistableExternalUid(item.Uid);
        pattern.ImportSourceUid = ImportSourceUidFor(item.Uid);
        context.RecurrencePatterns.Add(pattern);
        AddOccurrences(pattern, item.Occurrences, run, state);
        run.Imported++;
        Remember(state.Patterns, item.Uid, pattern);
    }

    private void AddOccurrences(
        RecurrencePattern pattern, List<(DateTime Start, DateTime End)> occurrences, ImportRun run, BatchState state)
    {
        if (!state.AddedOccurrences.TryGetValue(pattern.RecurrencePatternId, out var added))
        {
            added = [];
            state.AddedOccurrences[pattern.RecurrencePatternId] = added;
        }

        foreach (var (start, end) in occurrences)
        {
            var occurrence = NewOccurrence(pattern, start, end, run.UserId, run.Now);
            context.CalendarEvents.Add(occurrence);
            added.Add(occurrence);
        }
    }

    // §8.4: a cancelled component acts on what it matches in the target calendar, and only with allowRemovals.
    private async Task ApplyCancelledAsync(
        ImportItem item, ImportRun run, BatchState state, BatchResolver resolver, CancellationToken cancellationToken)
    {
        ContextCalendarEvent? matchedEvent = null;
        RecurrencePattern? matchedPattern = null;
        if (item.HasRule)
        {
            matchedPattern = MatchPattern(item.Uid, state, resolver);
            matchedEvent = matchedPattern is null ? MatchEvent(item.Uid, state, resolver) : null;
        }
        else
        {
            matchedEvent = MatchEvent(item.Uid, state, resolver);
            matchedPattern = matchedEvent is null ? MatchPattern(item.Uid, state, resolver) : null;
        }

        if (matchedEvent is null && matchedPattern is null)
        {
            run.Skipped.Add(CancelledUnknownReason, item.SampleTitle);
            return;
        }

        if (!run.AllowRemovals)
        {
            run.Skipped.Add(CancelledKeptReason, item.SampleTitle);
            return;
        }

        if (matchedEvent is not null)
        {
            if (context.Entry(matchedEvent).State != EntityState.Added)
            {
                state.RemovedEventIds.Add(matchedEvent.CalendarEventId);
            }

            context.CalendarEvents.Remove(matchedEvent);
            Removed(run, matchedEvent.Title);
            state.Events[item.Uid!] = null;
            return;
        }

        await RemoveSeriesAsync(matchedPattern!, run, state, cancellationToken);
        Removed(run, matchedPattern!.Title);
        state.Patterns[item.Uid!] = null;
    }

    // Exactly RecurrencePatternService.Delete's semantics (A3): future generated rows are deleted and past ones are
    // detached and kept — wherever they sit, rows moved into other calendars included, as the UI's "Delete series"
    // does. Every deleted row's id goes into the batch's audit line.
    private async Task RemoveSeriesAsync(RecurrencePattern pattern, ImportRun run, BatchState state, CancellationToken cancellationToken)
    {
        var rows = new List<ContextCalendarEvent>();
        if (context.Entry(pattern).State != EntityState.Added)
        {
            rows.AddRange(await context.CalendarEvents
                .Where(e => e.RecurrencePatternId == pattern.RecurrencePatternId)
                .ToListAsync(cancellationToken));
            state.RemovedSeriesIds.Add(pattern.RecurrencePatternId);
        }

        if (state.AddedOccurrences.Remove(pattern.RecurrencePatternId, out var added))
        {
            rows.AddRange(added);
        }

        foreach (var row in rows.Distinct())
        {
            if (context.Entry(row).State is EntityState.Deleted or EntityState.Detached)
            {
                continue;
            }

            if (row.StartDateTime >= run.Now)
            {
                if (context.Entry(row).State != EntityState.Added)
                {
                    state.RemovedEventIds.Add(row.CalendarEventId);
                }

                context.CalendarEvents.Remove(row);
            }
            else
            {
                row.RecurrencePatternId = null;
            }
        }

        context.RecurrencePatterns.Remove(pattern);
    }

    private static void Removed(ImportRun run, string title)
    {
        run.Removed++;
        if (run.RemovedSamples.Count < run.MaxSamples)
        {
            run.RemovedSamples.Add(title);
        }
    }

    private static void Remember<T>(Dictionary<string, T?> written, string? uid, T row)
        where T : class
    {
        if (uid is not null)
        {
            written[uid] = row;
        }
    }

    private static void CountOccurrences(ImportRun run, int count)
    {
        run.Aggregate += count;
        if (run.Aggregate > run.MaxAggregateOccurrences)
        {
            throw new DomainValidationException(
                $"The import would create or regenerate more than {run.MaxAggregateOccurrences} occurrences.");
        }
    }

    // Maps an ICS RRULE onto the bounded RecurrencePattern model. Returns (pattern, null) on success
    // or (null, reason) when the rule doesn't map — the reason becomes the per-event skip message.
    private static (RecurrencePattern? Pattern, string? Reason) MapPattern(
        Guid calendarId, IcalRecurrenceRule rule, string title, string? description, string? location,
        DateTime startUtc, DateTime endUtc, bool isAllDay, string userId, DateTime now)
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

        var recurrenceEnd = hasUntil ? rule.Until!.AsUtc : (DateTime?)null;

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

                daysOfWeek = rule.ByDay.Count > 0 ? ToDaysOfWeekFlags(rule.ByDay) : ToSingleDay(startUtc.DayOfWeek);
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

                dayOfMonth = rule.ByMonthDay.Count == 1 ? rule.ByMonthDay[0] : startUtc.Day;
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

                dayOfMonth = rule.ByMonthDay.Count == 1 ? rule.ByMonthDay[0] : startUtc.Day;
                if (dayOfMonth is < 1 or > 31)
                {
                    return (null, "Unsupported BYMONTHDAY value.");
                }

                monthOfYear = rule.ByMonth.Count == 1 ? rule.ByMonth[0] : startUtc.Month;
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
            StartDateTime = startUtc,
            EndDateTime = endUtc,
            IsAllDay = isAllDay,
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

    // The stored ExternalUid and ImportSourceUid are kept (gap 1): what matched the row is what names it.
    private static void ApplyPatternFields(RecurrencePattern target, RecurrencePattern source, string userId, DateTime now)
    {
        target.Title = source.Title;
        target.Description = source.Description;
        target.Location = source.Location;
        target.LocationId = source.LocationId;
        target.StartDateTime = source.StartDateTime;
        target.EndDateTime = source.EndDateTime;
        target.IsAllDay = source.IsAllDay;
        target.Frequency = source.Frequency;
        target.Interval = source.Interval;
        target.DaysOfWeek = source.DaysOfWeek;
        target.DayOfMonth = source.DayOfMonth;
        target.MonthOfYear = source.MonthOfYear;
        target.RecurrenceEndDate = source.RecurrenceEndDate;
        target.OccurrenceCount = source.OccurrenceCount;
        target.UpdatedByUserId = userId;
        target.UpdatedAt = now;
    }

    private static ContextCalendarEvent NewOccurrence(RecurrencePattern pattern, DateTime start, DateTime end, string userId, DateTime now) => new()
    {
        CalendarId = pattern.CalendarId,
        Title = pattern.Title,
        Description = pattern.Description,
        Location = pattern.Location,
        LocationId = pattern.LocationId,
        StartDateTime = start,
        EndDateTime = end,
        IsAllDay = pattern.IsAllDay,
        RecurrencePatternId = pattern.RecurrencePatternId,
        CreatedByUserId = userId,
        CreatedAt = now,
        UpdatedAt = now,
    };

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

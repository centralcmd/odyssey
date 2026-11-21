using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Odyssey.Core.Pagination;
using Odyssey.Context;
using Odyssey.Core.Journal.Interop;
using IcalCalendar = Ical.Net.Calendar;
using IcalEvent = Ical.Net.CalendarComponents.CalendarEvent;
using IcalRecurrencePattern = Ical.Net.DataTypes.RecurrencePattern;
using CalDateTime = Ical.Net.DataTypes.CalDateTime;
using RecurrenceIdentifier = Ical.Net.DataTypes.RecurrenceIdentifier;
using WeekDay = Ical.Net.DataTypes.WeekDay;
using FrequencyType = Ical.Net.FrequencyType;
using ContextCalendarEvent = Odyssey.Context.CalendarEvent;
using CalendarEventsIcsExportQueryParams = Odyssey.Dtos.Journal.CalendarEventsIcsExportQueryParams;
using CalendarIcsExportSummary = Odyssey.Dtos.Journal.CalendarIcsExportSummary;

namespace Odyssey.Core.Journal;

public partial class CalendarIcsService
{
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

        /// <summary>The planned attachments (issue #521 §8.2): none for the aggregate or with <c>attachments=omit</c>.</summary>
        public CalendarExportAttachments Attachments { get; set; } = CalendarExportAttachments.None;
    }

    /// <summary>
    /// What <see cref="ExportStreamingAsync"/> would produce for <paramref name="scope"/>, refusing exactly where it
    /// would: <see cref="ExportLimitExceededException"/> over the event cap, and the aggregate's window and fetch
    /// bounds. Null when the named calendar, event or series does not exist. The byte cap is not judged: text size is
    /// only known as it is written.
    /// </summary>
    public async Task<CalendarIcsExportSummary?> SummarizeExportAsync(
        CalendarIcsExportScope scope, CancellationToken cancellationToken = default, CalendarIcsAttachmentAccess? access = null)
    {
        var plan = await PlanAsync(scope, access ?? CalendarIcsAttachmentAccess.Default, cancellationToken);
        return plan is null
            ? null
            : new CalendarIcsExportSummary
            {
                EventCount = plan.EventCount,
                SeriesCount = plan.Series.Count,
                OccurrenceCount = plan.OccurrenceCount,
                AttachmentCount = plan.Attachments.FileCount,
                PhotoCount = plan.Attachments.PhotoCount,
                AttachmentBytes = plan.Attachments.ContentBytes,
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
        CancellationToken cancellationToken = default, bool includePlaceNames = false, string? userId = null,
        CalendarIcsAttachmentAccess? access = null)
    {
        var plan = await PlanAsync(scope, access ?? CalendarIcsAttachmentAccess.Default, cancellationToken);
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

    private async Task<ExportPlan?> PlanAsync(
        CalendarIcsExportScope scope, CalendarIcsAttachmentAccess access, CancellationToken cancellationToken)
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

        // The aggregate carries no attachments (Non-Goal 2); omit needs no claim (SF8). A master carries its series'
        // links, an override none, and a standalone row its own — or its series' when it is a generated row.
        if (plan is { Aggregate: false } && access.Mode == Odyssey.Dtos.Journal.IcsAttachmentsMode.Include)
        {
            plan.Attachments = await CalendarExportAttachments.PlanAsync(
                context, plan.StandaloneIds, plan.Series.Select(s => s.PatternId).ToList(), access.CanReadFiles,
                access.CanReadPhotos, canWriteFiles: fileContent is not null, canWritePhotos: photoContent is not null,
                cancellationToken);
            if (plan.Attachments.EncodedBytes > maxBytes)
            {
                throw new DomainValidationException(
                    $"The export would be at least {plan.Attachments.EncodedBytes / (1024 * 1024)} MB with its attached files "
                    + $"and photos, over the {maxBytes / (1024 * 1024)} MB export limit — export without attachments instead.");
            }
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

        async Task EmitAsync(IcalEvent ev, CalendarExportAttachments.Owner? owner = null)
        {
            var calendar = new IcalCalendar();
            calendar.Events.Add(ev);
            var text = IcsChunkSerializer.SerializeComponents(calendar);
            var bytes = (long)Encoding.UTF8.GetByteCount(text) + (owner is null ? 0 : plan.Attachments.BytesFor(owner));
            if (writtenBytes + bytes > plan.MaxBytes)
            {
                logger.LogWarning(
                    "Calendar .ics export stopped at {WrittenBytes} bytes (cap {MaxBytes}); {WrittenRows}/{TotalRows} events delivered.",
                    writtenBytes, plan.MaxBytes, written, plan.EventCount);
                throw new IcsExportSizeExceededException(
                    $"The export grew past the {plan.MaxBytes / (1024 * 1024)} MB export limit while it was being written.");
            }

            if (owner is { IsEmpty: false })
            {
                // The attachments are spliced in before END:VEVENT; Ical.Net never holds a binary value (#515).
                var split = text.LastIndexOf("END:VEVENT", StringComparison.Ordinal);
                await IcsChunkSerializer.WriteAsync(output, text[..split], cancellationToken);
                await plan.Attachments.WriteAsync(output, owner, fileContent, photoContent, context, cancellationToken);
                await IcsChunkSerializer.WriteAsync(output, text[split..], cancellationToken);
            }
            else
            {
                await IcsChunkSerializer.WriteAsync(output, text, cancellationToken);
            }

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

                await EmitAsync(ev, plan.Attachments.ForSeries(pattern.RecurrencePatternId));

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
                    row.CreatedAt, row.UpdatedAt, stamp, names, calendars?.GetValueOrDefault(row.CalendarId)),
                    plan.Attachments.ForEvent(row.CalendarEventId));
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
        if (flags.HasFlag(DaysOfWeekFlags.Monday))
        {
            days.Add(new WeekDay(DayOfWeek.Monday));
        }

        if (flags.HasFlag(DaysOfWeekFlags.Tuesday))
        {
            days.Add(new WeekDay(DayOfWeek.Tuesday));
        }

        if (flags.HasFlag(DaysOfWeekFlags.Wednesday))
        {
            days.Add(new WeekDay(DayOfWeek.Wednesday));
        }

        if (flags.HasFlag(DaysOfWeekFlags.Thursday))
        {
            days.Add(new WeekDay(DayOfWeek.Thursday));
        }

        if (flags.HasFlag(DaysOfWeekFlags.Friday))
        {
            days.Add(new WeekDay(DayOfWeek.Friday));
        }

        if (flags.HasFlag(DaysOfWeekFlags.Saturday))
        {
            days.Add(new WeekDay(DayOfWeek.Saturday));
        }

        if (flags.HasFlag(DaysOfWeekFlags.Sunday))
        {
            days.Add(new WeekDay(DayOfWeek.Sunday));
        }

        return days;
    }
}

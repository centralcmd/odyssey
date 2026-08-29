using Odyssey.Core;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Odyssey.Core.Finance;
using Odyssey.Core.Pagination;
using Odyssey.Context;
using Odyssey.Core.Journal.Interop;
using Ical.Net.Serialization;
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
using IcsImportResult = Odyssey.Dtos.Journal.IcsImportResult;
using IcsImportSkipGroup = Odyssey.Dtos.Journal.IcsImportSkipGroup;

namespace Odyssey.Core.Journal;

/// <summary>
/// ICS (RFC 5545) import/export for a single calendar (issue #330). Export emits an unmodified,
/// unclamped recurring series as a single <c>RRULE</c> <c>VEVENT</c> (carrying a stable UID so a
/// re-import is idempotent) and flattens everything else to standalone <c>VEVENT</c>s. Import maps
/// each <c>VEVENT</c> onto the bounded <see cref="RecurrencePattern"/> model where the rule maps
/// cleanly, skipping (with a per-event reason) the ones that don't, and matches existing rows by UID
/// so re-importing the same file creates no duplicates.
/// </summary>
public class CalendarIcsService
{
    // The three aggregate bounds — occurrences, fetched export rows and the export window — were
    // compile-time constants here until issue #434 (keys 8, 9, 10). They are now read from the
    // IImportExportLimitsLookup snapshot this service already takes once per request, and threaded
    // into the static helpers below as parameters so one request can never observe two values.
    private const string SyntheticUidSuffix = "@odyssey.local";
    private const string UntitledEvent = "(untitled)";

    private static readonly string[] AcceptedContentTypes =
        ["text/calendar", "application/octet-stream", "text/plain"];

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

    // ---------------------------------------------------------------- Export

    /// <summary>Serializes a calendar's events to an ICS document. Returns null if the calendar
    /// doesn't exist (the controller maps that to 404).</summary>
    public async Task<IcsExport?> ExportAsync(Guid calendarId, CancellationToken cancellationToken = default)
    {
        var calendar = await context.Calendars.AsNoTracking()
            .FirstOrDefaultAsync(c => c.CalendarId == calendarId, cancellationToken);
        if (calendar is null)
        {
            return null;
        }

        var ical = new IcalCalendar { ProductId = "-//Odyssey//Calendar//EN" };
        var occurrenceCap = (await journalLimits.GetAsync(cancellationToken)).RecurrenceMaxGeneratedOccurrences;

        var patterns = await context.RecurrencePatterns.AsNoTracking()
            .Where(p => p.CalendarId == calendarId)
            .OrderBy(p => p.StartDateTime).ThenBy(p => p.RecurrencePatternId)
            .ToListAsync(cancellationToken);

        foreach (var pattern in patterns)
        {
            var rows = await context.CalendarEvents.AsNoTracking()
                .Where(e => e.RecurrencePatternId == pattern.RecurrencePatternId)
                .OrderBy(e => e.StartDateTime).ThenBy(e => e.CalendarEventId)
                .ToListAsync(cancellationToken);

            if (CanExportAsRule(pattern, rows, occurrenceCap))
            {
                ical.Events.Add(BuildRecurringVEvent(pattern));
            }
            else
            {
                foreach (var row in rows)
                {
                    ical.Events.Add(BuildStandaloneVEvent(row));
                }
            }
        }

        var standalone = await context.CalendarEvents.AsNoTracking()
            .Where(e => e.CalendarId == calendarId && e.RecurrencePatternId == null)
            .OrderBy(e => e.StartDateTime).ThenBy(e => e.CalendarEventId)
            .ToListAsync(cancellationToken);

        foreach (var row in standalone)
        {
            ical.Events.Add(BuildStandaloneVEvent(row));
        }

        var content = new CalendarSerializer(ical).SerializeToString() ?? string.Empty;
        var exportDate = timeProvider.GetUtcNow().UtcDateTime;
        return new IcsExport(BuildFileName(calendar.Name, exportDate), content);
    }

    /// <summary>Exports a single event as a standalone VEVENT (issue #340). Returns null if the event
    /// doesn't exist (the controller maps that to 404).</summary>
    public async Task<IcsExport?> ExportEventAsync(Guid eventId, CancellationToken cancellationToken = default)
    {
        var row = await context.CalendarEvents.AsNoTracking()
            .FirstOrDefaultAsync(e => e.CalendarEventId == eventId, cancellationToken);
        if (row is null)
        {
            return null;
        }

        var ical = new IcalCalendar { ProductId = "-//Odyssey//Calendar//EN" };
        ical.Events.Add(BuildStandaloneVEvent(row));

        var content = new CalendarSerializer(ical).SerializeToString() ?? string.Empty;
        var exportDate = timeProvider.GetUtcNow().UtcDateTime;
        return new IcsExport(BuildFileName(row.Title, exportDate), content);
    }

    /// <summary>Exports a recurring series as one RRULE VEVENT, or its documented per-occurrence
    /// fallback (issue #340). Returns null if the pattern doesn't exist (the controller maps that to
    /// 404).</summary>
    public async Task<IcsExport?> ExportPatternAsync(Guid patternId, CancellationToken cancellationToken = default)
    {
        var pattern = await context.RecurrencePatterns.AsNoTracking()
            .FirstOrDefaultAsync(p => p.RecurrencePatternId == patternId, cancellationToken);
        if (pattern is null)
        {
            return null;
        }

        var rows = await context.CalendarEvents.AsNoTracking()
            .Where(e => e.RecurrencePatternId == patternId)
            .OrderBy(e => e.StartDateTime).ThenBy(e => e.CalendarEventId)
            .ToListAsync(cancellationToken);

        var ical = new IcalCalendar { ProductId = "-//Odyssey//Calendar//EN" };
        var occurrenceCap = (await journalLimits.GetAsync(cancellationToken)).RecurrenceMaxGeneratedOccurrences;
        if (CanExportAsRule(pattern, rows, occurrenceCap))
        {
            ical.Events.Add(BuildRecurringVEvent(pattern));
        }
        else
        {
            foreach (var row in rows)
            {
                ical.Events.Add(BuildStandaloneVEvent(row));
            }
        }

        var content = new CalendarSerializer(ical).SerializeToString() ?? string.Empty;
        var exportDate = timeProvider.GetUtcNow().UtcDateTime;
        return new IcsExport(BuildFileName(pattern.Title, exportDate), content);
    }

    /// <summary>
    /// Exports every <see cref="ContextCalendarEvent"/> the caller can read, across every calendar, as
    /// one multi-VEVENT .ics file — optionally filtered by date range, calendar, and/or search term
    /// (issue #340). Every VEVENT carries a synthesized UID (never the row's <c>ExternalUid</c>, which
    /// is only unique within its own calendar) and a <c>Categories</c> value naming its source
    /// calendar.
    ///
    /// A filtered request (From/To and/or Search present) never collapses a series to a single RRULE —
    /// it flattens every matched row individually, bounded by the configured
    /// <c>CalendarIcsMaxExportEvents</c> cap (issue #343 §6). A no-filter
    /// request (CalendarIds may still be present) fetches rows by their own CalendarId first — not by
    /// selecting "patterns in scope" and fetching only their rows — so a CalendarId filter correctly
    /// includes an occurrence moved into scope and excludes one moved out, in both directions; a
    /// pattern's rows only collapse to one RRULE VEVENT when they exactly satisfy the existing
    /// <see cref="CanExportAsRule"/> AND every fetched row for that pattern shares one identical
    /// CalendarId (CanExportAsRule alone never compares CalendarId, so a relocated-but-otherwise-intact
    /// row could otherwise collapse under the wrong calendar's attribution).
    /// </summary>
    /// <summary>
    /// Streams the aggregate export directly to <paramref name="output"/> (issue #343 §5 Goal 8).
    /// <paramref name="onReady"/> is called exactly once, with the file name and the count of VEVENTs
    /// the response will contain, after the pre-fetch/precomputed count is known but before any byte is
    /// written — the caller's only chance to set response headers (including
    /// <c>X-Odyssey-Export-Rows</c>). The <b>filtered</b> path (never collapses, 1:1 rows→VEVENTs) is
    /// chunked; the <b>no-filter aggregate</b> path keeps its existing fully-materialized shape — see
    /// the class doc and issue #343 §5/§6 for why a row-keyset chunk can't preserve its per-series
    /// collapse step.
    /// </summary>
    public async Task ExportAggregateStreamingAsync(
        CalendarEventsIcsExportQueryParams query, string userId, Stream output, Action<string, int> onReady,
        CancellationToken cancellationToken = default)
    {
        var effectiveLimits = await limits.GetAsync(cancellationToken);
        var occurrenceCap = (await journalLimits.GetAsync(cancellationToken)).RecurrenceMaxGeneratedOccurrences;
        var (from, to) = ValidateAggregateWindow(
            query.From, query.To, effectiveLimits.CalendarIcsMaxAggregateExportWindowDays);
        var searchTerm = ListQuery.NormalizeSearch(query.Search);
        var calendarIds = query.CalendarIds is { Length: > 0 } ids ? ids.Distinct().ToList() : null;
        var isFiltered = from is not null || searchTerm is not null;
        var maxExportEvents = effectiveLimits.CalendarIcsMaxExportEvents;
        var maxExportBytes = effectiveLimits.CalendarIcsMaxExportBytes;
        var exportDate = timeProvider.GetUtcNow().UtcDateTime;
        var fileName = $"{exportDate:yyyyMMdd}_calendar-events.ics";

        int matchedRowCount;
        if (isFiltered)
        {
            matchedRowCount = await ExportAggregateFilteredStreamingAsync(
                output, count => onReady(fileName, count), calendarIds, from, to, searchTerm, maxExportEvents,
                maxExportBytes, cancellationToken);
        }
        else
        {
            matchedRowCount = await ExportAggregateUnfilteredStreamingAsync(
                output, count => onReady(fileName, count), calendarIds, maxExportEvents, maxExportBytes,
                effectiveLimits.CalendarIcsMaxAggregateExportRows, occurrenceCap, cancellationToken);
        }

        logger.LogInformation(
            "Aggregate calendar events .ics export by {UserId}: {MatchedRowCount} matched row(s).",
            userId, matchedRowCount);
    }

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

    // Filtered export (From/To and/or Search present): never collapses, so matched rows and output
    // VEVENTs are 1:1 — bounded and chunked (issue #343 §5 Goal 8), so peak memory is proportional to
    // the chunk size, not the matched row count.
    //
    // No explicit transaction: OdysseyContext enables EnableRetryOnFailure() in production, which
    // forbids a bare Database.BeginTransactionAsync unless the ENTIRE unit of work (begin, every
    // query, commit) runs inside one CreateExecutionStrategy().ExecuteAsync call (verified against
    // real MariaDB, not just from the docs) — that doesn't compose with a chunked read that yields
    // output as it goes (a retry would re-emit chunks already streamed to the client). Instead, the
    // ordered CalendarEventId set is captured in one cheap up-front read (bounded to max+1 when a cap
    // is configured, so an over-cap export still rejects without a full scan), and each chunk is then
    // fetched independently by a fixed id batch — see ExportChunking.ReorderToSnapshot for the
    // consistency trade-off this makes relative to the RepeatableRead snapshot this replaced (PR #403
    // review fix).
    private async Task<int> ExportAggregateFilteredStreamingAsync(
        Stream output, Action<int> onCountKnown, List<Guid>? calendarIds, DateTime? from, DateTime? to, string? searchTerm,
        int? maxExportEvents, long maxExportBytes, CancellationToken cancellationToken)
    {
        var baseQuery = BuildAggregateRowQuery(calendarIds, from, to, searchTerm);
        var orderedIdsQuery = baseQuery
            .OrderBy(e => e.StartDateTime).ThenBy(e => e.CalendarEventId)
            .Select(e => e.CalendarEventId);

        List<Guid> orderedIds;
        int count;
        if (maxExportEvents is { } max)
        {
            orderedIds = await orderedIdsQuery.Take(max + 1).ToListAsync(cancellationToken);
            if (orderedIds.Count > max)
            {
                throw new DomainValidationException(
                    $"The filtered export would exceed {max} events — narrow the date range, calendar selection, or search term.");
            }

            count = orderedIds.Count;
        }
        else
        {
            orderedIds = await orderedIdsQuery.ToListAsync(cancellationToken);
            count = orderedIds.Count;
        }

        onCountKnown(count);

        var (head, tail) = IcsChunkSerializer.BuildEnvelope("-//Odyssey//Calendar//EN");
        await IcsChunkSerializer.WriteAsync(output, head, cancellationToken);
        var writtenBytes = (long)Encoding.UTF8.GetByteCount(head);

        var written = 0;
        foreach (var idBatch in orderedIds.Chunk(ExportChunking.ChunkSize))
        {
            var rows = await baseQuery.Where(e => idBatch.Contains(e.CalendarEventId)).ToListAsync(cancellationToken);
            var ordered = ExportChunking.ReorderToSnapshot(idBatch, rows, e => e.CalendarEventId);
            if (ordered.Count == 0)
            {
                continue; // every id in this batch was deleted between the snapshot and this fetch
            }

            var calendarNames = await GetCalendarNamesAsync(ordered.Select(r => r.CalendarId), cancellationToken);
            var chunk = new IcalCalendar();
            foreach (var row in ordered)
            {
                chunk.Events.Add(BuildAggregateStandaloneVEvent(row, calendarNames[row.CalendarId]));
            }

            var chunkText = IcsChunkSerializer.SerializeComponents(chunk);
            var chunkBytes = Encoding.UTF8.GetByteCount(chunkText);

            // The byte-size cap can't be enforced up front like the row-count cap above (total output
            // size isn't knowable until it's generated) — once writing this chunk would cross it, stop
            // without writing it. The response then has fewer rows than X-Odyssey-Export-Rows already
            // promised, which the API client's existing completeness check treats as a failed download.
            if (writtenBytes + chunkBytes > maxExportBytes)
            {
                logger.LogWarning(
                    "Filtered calendar events .ics export truncated at {WrittenBytes} bytes (cap {MaxBytes}); " +
                    "{WrittenRows}/{TotalRows} events delivered.",
                    writtenBytes, maxExportBytes, written, count);
                break;
            }

            await IcsChunkSerializer.WriteAsync(output, chunkText, cancellationToken);
            writtenBytes += chunkBytes;
            written += ordered.Count;
        }

        await IcsChunkSerializer.WriteAsync(output, tail, cancellationToken);
        return written;
    }

    // No-filter export (CalendarIds may still be present): row-first fetch, then a referenced-only
    // pattern lookup and a per-pattern collapse decision, computed entirely before any VEVENT is built.
    // Exempted from Goal 8 chunking (issue #343 §5/§6): the per-series collapse decision needs a
    // pattern's COMPLETE row set, which a row-keyset chunk can't guarantee stays together, so this path
    // keeps its existing fully-materialized shape and bounded fetch guard
    // (CalendarIcsMaxAggregateExportRows).
    private async Task<int> ExportAggregateUnfilteredStreamingAsync(
        Stream output, Action<int> onCountKnown, List<Guid>? calendarIds, int? maxExportEvents, long maxExportBytes,
        int maxAggregateExportRows, int maxGeneratedOccurrences, CancellationToken cancellationToken)
    {
        var ical = new IcalCalendar { ProductId = "-//Odyssey//Calendar//EN" };
        var rowQuery = BuildAggregateRowQuery(calendarIds, from: null, to: null, searchTerm: null);

        var boundedFetchCount = await rowQuery.Take(maxAggregateExportRows + 1).CountAsync(cancellationToken);
        if (boundedFetchCount > maxAggregateExportRows)
        {
            throw new DomainValidationException(
                $"The export would need to fetch more than {maxAggregateExportRows} events — narrow the calendar selection.");
        }

        var rows = await rowQuery.OrderBy(e => e.StartDateTime).ThenBy(e => e.CalendarEventId).ToListAsync(cancellationToken);

        var standaloneRows = rows.Where(r => r.RecurrencePatternId is null).ToList();
        var patternGroups = rows.Where(r => r.RecurrencePatternId is not null)
            .GroupBy(r => r.RecurrencePatternId!.Value)
            .ToList();

        var patternIds = patternGroups.Select(g => g.Key).ToList();
        var patterns = patternIds.Count == 0
            ? new Dictionary<Guid, RecurrencePattern>()
            : await context.RecurrencePatterns.AsNoTracking()
                .Where(p => patternIds.Contains(p.RecurrencePatternId))
                .ToDictionaryAsync(p => p.RecurrencePatternId, cancellationToken);

        var decisions = new List<(RecurrencePattern Pattern, List<ContextCalendarEvent> Rows, bool Collapse)>();
        var eventualCount = standaloneRows.Count;
        foreach (var group in patternGroups)
        {
            var groupRows = group.OrderBy(r => r.StartDateTime).ThenBy(r => r.CalendarEventId).ToList();
            if (!patterns.TryGetValue(group.Key, out var pattern))
            {
                eventualCount += groupRows.Count; // pattern missing (shouldn't happen via FK) — flatten
                decisions.Add((null!, groupRows, false));
                continue;
            }

            var collapse = CanExportAsRule(pattern, groupRows, maxGeneratedOccurrences)
                && HasUniformCalendarId(groupRows);
            decisions.Add((pattern, groupRows, collapse));
            eventualCount += collapse ? 1 : groupRows.Count;
        }

        if (maxExportEvents is { } maxEvents && eventualCount > maxEvents)
        {
            throw new DomainValidationException(
                $"The export would produce more than {maxEvents} events — narrow the calendar selection.");
        }

        var calendarIdsNeeded = standaloneRows.Select(r => r.CalendarId)
            .Concat(decisions.SelectMany(d => d.Rows.Select(r => r.CalendarId)));
        var calendarNames = await GetCalendarNamesAsync(calendarIdsNeeded, cancellationToken);

        foreach (var (pattern, groupRows, collapse) in decisions)
        {
            if (collapse)
            {
                ical.Events.Add(BuildAggregateRecurringVEvent(pattern, calendarNames[groupRows[0].CalendarId]));
            }
            else
            {
                foreach (var row in groupRows)
                {
                    ical.Events.Add(BuildAggregateStandaloneVEvent(row, calendarNames[row.CalendarId]));
                }
            }
        }

        foreach (var row in standaloneRows)
        {
            ical.Events.Add(BuildAggregateStandaloneVEvent(row, calendarNames[row.CalendarId]));
        }

        // Not chunked (see the class doc above), so — unlike the filtered/streamed path's byte cap,
        // which can only be enforced by truncating mid-stream — the whole document is already built
        // in memory at this point, and no header has been sent yet. A too-large result can therefore
        // still be rejected cleanly with a 400, the same way the row-count caps above are, rather than
        // truncated.
        var content = new CalendarSerializer(ical).SerializeToString() ?? string.Empty;
        var contentBytes = Encoding.UTF8.GetByteCount(content);
        if (contentBytes > maxExportBytes)
        {
            throw new DomainValidationException(
                $"The export would produce a {contentBytes / (1024 * 1024)} MB file, which exceeds the "
                + $"configured maximum of {maxExportBytes / (1024 * 1024)} MB. Narrow the calendar selection.");
        }

        // eventualCount (events emitted after collapse), not the raw row count — the completeness
        // header must match what the body actually contains (issue #343 §11).
        onCountKnown(eventualCount);
        await IcsChunkSerializer.WriteAsync(output, content, cancellationToken);

        return rows.Count;
    }

    private IQueryable<ContextCalendarEvent> BuildAggregateRowQuery(
        List<Guid>? calendarIds, DateTime? from, DateTime? to, string? searchTerm)
    {
        // Export-only, so the rows are never written back: skip the change tracker for what are the
        // largest result sets in the module.
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

    // CanExportAsRule never compares CalendarId, so a series where every fetched row still passes it
    // but the rows don't all share the same CalendarId (an occurrence individually relocated) must not
    // collapse — it would misattribute the whole RRULE and its Categories to the wrong calendar.
    private static bool HasUniformCalendarId(List<ContextCalendarEvent> rows) =>
        rows.Select(r => r.CalendarId).Distinct().Count() == 1;

    private async Task<Dictionary<Guid, string>> GetCalendarNamesAsync(IEnumerable<Guid> calendarIds, CancellationToken cancellationToken)
    {
        var ids = calendarIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        return await context.Calendars
            .Where(c => ids.Contains(c.CalendarId))
            .ToDictionaryAsync(c => c.CalendarId, c => c.Name, cancellationToken);
    }

    // A stored series exports as a single RRULE VEVENT only when its actual generated rows exactly
    // match — on every field — what a strict RFC 5545 reader would compute for the same rule. Any
    // clamped occurrence, individual edit, or deletion breaks the match and forces a per-occurrence
    // flatten, so the exported file never asserts an RRULE another calendar app would read differently.
    private static bool CanExportAsRule(
        RecurrencePattern pattern, List<ContextCalendarEvent> rows, int maxGeneratedOccurrences)
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
            // GenerateRfcLiteral's own step guard (every step is skipped as "day doesn't exist", so
            // the count/end-date break is never reached) until DateTime.DaysInMonth's year argument
            // exceeds year 9999 (security review, PR #345 F1). Un-exportable-as-RRULE is exactly what
            // this method already returns false for in every other unrepresentable case — flattening
            // is the documented, safe fallback (the pattern's stored rows are unaffected either way).
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
            if (row.StartDateTime != start || row.EndDateTime != end || row.IsAllDay != pattern.IsAllDay
                || row.Title != pattern.Title || row.Description != pattern.Description || row.Location != pattern.Location)
            {
                return false;
            }
        }

        return true;
    }

    private static IcalEvent BuildRecurringVEvent(RecurrencePattern pattern)
    {
        var ev = new IcalEvent
        {
            Uid = pattern.ExternalUid ?? $"{pattern.RecurrencePatternId}{SyntheticUidSuffix}",
            Summary = pattern.Title,
            Description = pattern.Description,
            Location = pattern.Location,
            Start = ToCalDateTime(pattern.StartDateTime, pattern.IsAllDay),
            End = ToCalDateTime(pattern.EndDateTime, pattern.IsAllDay),
        };
        ev.RecurrenceRule = BuildRRule(pattern);
        return ev;
    }

    private static IcalEvent BuildStandaloneVEvent(ContextCalendarEvent row) => new()
    {
        Uid = row.ExternalUid ?? $"{row.CalendarEventId}{SyntheticUidSuffix}",
        Summary = row.Title,
        Description = row.Description,
        Location = row.Location,
        Start = ToCalDateTime(row.StartDateTime, row.IsAllDay),
        End = ToCalDateTime(row.EndDateTime, row.IsAllDay),
    };

    // Aggregate-export builders (issue #340): unlike BuildStandaloneVEvent/BuildRecurringVEvent, the
    // UID is ALWAYS synthesized (never ExternalUid, which is only unique within its own calendar — see
    // ExportAggregateAsync's XML doc), and Categories names the source calendar so a caller can tell
    // events from different calendars apart in the merged file.
    private static IcalEvent BuildAggregateStandaloneVEvent(ContextCalendarEvent row, string calendarName) => new()
    {
        Uid = $"{row.CalendarEventId}{SyntheticUidSuffix}",
        Summary = row.Title,
        Description = row.Description,
        Location = row.Location,
        Start = ToCalDateTime(row.StartDateTime, row.IsAllDay),
        End = ToCalDateTime(row.EndDateTime, row.IsAllDay),
        Categories = [calendarName],
    };

    private static IcalEvent BuildAggregateRecurringVEvent(RecurrencePattern pattern, string calendarName)
    {
        var ev = new IcalEvent
        {
            Uid = $"{pattern.RecurrencePatternId}{SyntheticUidSuffix}",
            Summary = pattern.Title,
            Description = pattern.Description,
            Location = pattern.Location,
            Start = ToCalDateTime(pattern.StartDateTime, pattern.IsAllDay),
            End = ToCalDateTime(pattern.EndDateTime, pattern.IsAllDay),
            Categories = [calendarName],
        };
        ev.RecurrenceRule = BuildRRule(pattern);
        return ev;
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

    public async Task<IcsImportResult> ImportAsync(
        Guid targetCalendarId, Stream icsFile, long contentLength, string? contentType, string userId,
        CancellationToken cancellationToken = default)
    {
        if (!IsAcceptedContentType(contentType))
        {
            throw new DomainValidationException("The uploaded file must be a calendar file (text/calendar).");
        }

        var cap = await limits.GetAsync(cancellationToken);
        var journalCap = await journalLimits.GetAsync(cancellationToken);
        var maxImportBytes = cap.CalendarIcsMaxImportBytes;
        var maxImportEvents = cap.CalendarIcsMaxImportEvents ?? int.MaxValue;
        // One snapshot each, taken before any component is processed, so a concurrent admin write can
        // never split one import across two values of the same setting.
        var maxAggregateOccurrences = cap.CalendarIcsMaxAggregateOccurrences;
        var maxEventDurationDays = journalCap.CalendarMaxEventDurationDays;
        var maxGeneratedOccurrences = journalCap.RecurrenceMaxGeneratedOccurrences;

        if (contentLength > maxImportBytes)
        {
            throw new DomainValidationException($"The .ics file exceeds the {maxImportBytes / (1024 * 1024)} MB limit.");
        }

        var calendarExists = await context.Calendars.AnyAsync(c => c.CalendarId == targetCalendarId, cancellationToken);
        if (!calendarExists)
        {
            throw new DomainNotFoundException($"Calendar ID {targetCalendarId} not found.");
        }

        using var reader = ImportFileReader.OpenBoundedTextReader(icsFile, maxImportBytes, ".ics");

        IcalCalendar? parsed;
        try
        {
            parsed = IcalCalendar.Load(reader);
        }
        // DomainValidationException is excluded here (and re-thrown unchanged below by not being
        // caught) because OpenBoundedTextReader's byte-cap check now runs INSIDE this parse — lazily,
        // as Ical.Net reads through the wrapped stream — rather than fully before it. Catching it here
        // would replace the specific "exceeds the N MB limit" message with the generic parse-failure
        // one (issue #343 §5).
        catch (Exception ex) when (ex is not OperationCanceledException and not DomainValidationException)
        {
            throw new DomainValidationException("The file could not be parsed as a valid iCalendar (.ics) file.");
        }

        if (parsed is null)
        {
            throw new DomainValidationException("The file could not be parsed as a valid iCalendar (.ics) file.");
        }

        var events = parsed.Events;
        if (events.Count > maxImportEvents)
        {
            throw new DomainValidationException($"The file contains more than {maxImportEvents} events.");
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;

        var patternIndex = BuildIndex(
            await context.RecurrencePatterns.Where(p => p.CalendarId == targetCalendarId).ToListAsync(cancellationToken),
            p => p.ExternalUid, p => p.RecurrencePatternId);
        var existingEvents = await context.CalendarEvents
            .Where(e => e.CalendarId == targetCalendarId).ToListAsync(cancellationToken);
        var eventIndex = BuildIndex(existingEvents, e => e.ExternalUid, e => e.CalendarEventId);

        var seenUids = new HashSet<string>(StringComparer.Ordinal);
        var skipped = new ImportSkipCollector(cap.ImportMaxSamplesPerSkipReason);
        var imported = 0;
        var updated = 0;
        var anySeriesRegenerated = false;
        var aggregate = 0;

        foreach (var ev in events)
        {
            var title = ev.Summary?.Trim();
            var sampleTitle = string.IsNullOrWhiteSpace(title) ? UntitledEvent : title;

            var uid = string.IsNullOrWhiteSpace(ev.Uid) ? null : ev.Uid.Trim();
            if (uid is not null && !seenUids.Add(uid))
            {
                skipped.Add("Duplicate UID within this file is not supported.", sampleTitle);
                continue;
            }

            if (ev.Start is null)
            {
                skipped.Add("Event is missing a start date (DTSTART).", sampleTitle);
                continue;
            }

            if (string.IsNullOrWhiteSpace(title))
            {
                skipped.Add("Event is missing a title (SUMMARY).", sampleTitle);
                continue;
            }

            if (title.Length > 200)
            {
                skipped.Add("Title exceeds the maximum length of 200 characters.", sampleTitle);
                continue;
            }

            var description = ev.Description;
            if (description is { Length: > 2000 })
            {
                skipped.Add("Description exceeds the maximum length of 2000 characters.", sampleTitle);
                continue;
            }

            var location = ev.Location;
            if (location is { Length: > 300 })
            {
                skipped.Add("Location exceeds the maximum length of 300 characters.", sampleTitle);
                continue;
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
                skipped.Add("Event has an unsupported or unresolvable date/time.", sampleTitle);
                continue;
            }

            try
            {
                CalendarEventService.ValidateTimes(startUtc, endUtc, isAllDay, maxEventDurationDays);
            }
            catch (DomainValidationException ex)
            {
                skipped.Add(ex.Message, sampleTitle);
                continue;
            }

            if (ev.RecurrenceRule is null)
            {
                ImportStandalone(targetCalendarId, ev, uid, title, description, location, startUtc, endUtc, isAllDay,
                    userId, now, eventIndex, maxAggregateOccurrences, ref imported, ref updated, ref aggregate);
                continue;
            }

            // ----- recurring VEVENT -----
            if (ev.Properties.ContainsKey("EXDATE") || ev.Properties.ContainsKey("RDATE"))
            {
                skipped.Add("Recurrence exceptions (EXDATE/RDATE) are not supported in v1.", sampleTitle);
                continue;
            }

            // Ical.Net exposes only a single RecurrenceRule, and no EXRULE at all, so "more
            // recurrence than v1 models" has to be read off the raw property list.
            if (ev.Properties.CountOf("RRULE") > 1 || ev.Properties.ContainsKey("EXRULE"))
            {
                skipped.Add("Unsupported recurrence rule.", sampleTitle);
                continue;
            }

            var (pattern, reason) = MapPattern(
                targetCalendarId, ev.RecurrenceRule, title, description, location, startUtc, endUtc, isAllDay, userId, now);
            if (pattern is null)
            {
                skipped.Add(reason!, sampleTitle);
                continue;
            }

            List<(DateTime Start, DateTime End)> occurrences;
            try
            {
                occurrences = RecurrenceOccurrenceGenerator.Generate(pattern, maxGeneratedOccurrences);
            }
            catch (DomainValidationException ex)
            {
                skipped.Add(ex.Message, sampleTitle);
                continue;
            }

            if (occurrences.Count == 0)
            {
                skipped.Add("Recurrence rule produces no occurrences.", sampleTitle);
                continue;
            }

            if (uid is not null && patternIndex.TryGetValue(uid, out var existingPattern))
            {
                var futureOccurrences = occurrences.Where(o => o.Start >= now).ToList();
                aggregate += futureOccurrences.Count;
                if (aggregate > maxAggregateOccurrences)
                {
                    throw AggregateExceeded(maxAggregateOccurrences);
                }

                ApplyPatternFields(existingPattern, pattern, uid, userId, now);

                var futureRows = existingEvents
                    .Where(e => e.RecurrencePatternId == existingPattern.RecurrencePatternId && e.StartDateTime >= now)
                    .ToList();
                context.CalendarEvents.RemoveRange(futureRows);

                foreach (var (start, end) in futureOccurrences)
                {
                    context.CalendarEvents.Add(NewOccurrence(existingPattern, start, end, userId, now));
                }

                updated++;
                anySeriesRegenerated = true;
            }
            else
            {
                aggregate += occurrences.Count;
                if (aggregate > maxAggregateOccurrences)
                {
                    throw AggregateExceeded(maxAggregateOccurrences);
                }

                pattern.ExternalUid = PersistableExternalUid(uid);
                context.RecurrencePatterns.Add(pattern);
                foreach (var (start, end) in occurrences)
                {
                    context.CalendarEvents.Add(NewOccurrence(pattern, start, end, userId, now));
                }

                imported++;
            }
        }

        await context.SaveChangesAsync(cancellationToken);

        return new IcsImportResult
        {
            ImportedCount = imported,
            UpdatedCount = updated,
            AnySeriesRegenerated = anySeriesRegenerated,
            Skipped = skipped.ToGroups((reason, count, samples) => new IcsImportSkipGroup
            {
                Reason = reason,
                Count = count,
                SampleTitles = samples,
            }),
        };
    }

    private void ImportStandalone(
        Guid calendarId, IcalEvent ev, string? uid, string title, string? description, string? location,
        DateTime startUtc, DateTime endUtc, bool isAllDay, string userId, DateTime now,
        Dictionary<string, ContextCalendarEvent> eventIndex, int maxAggregateOccurrences,
        ref int imported, ref int updated, ref int aggregate)
    {
        if (uid is not null && eventIndex.TryGetValue(uid, out var existing))
        {
            aggregate++;
            if (aggregate > maxAggregateOccurrences)
            {
                throw AggregateExceeded(maxAggregateOccurrences);
            }

            existing.Title = title;
            existing.Description = description;
            existing.Location = location;
            existing.StartDateTime = startUtc;
            existing.EndDateTime = endUtc;
            existing.IsAllDay = isAllDay;
            existing.ExternalUid = PersistableExternalUid(uid);
            existing.UpdatedByUserId = userId;
            existing.UpdatedAt = now;
            updated++;
            return;
        }

        aggregate++;
        if (aggregate > maxAggregateOccurrences)
        {
            throw AggregateExceeded(maxAggregateOccurrences);
        }

        context.CalendarEvents.Add(new ContextCalendarEvent
        {
            CalendarId = calendarId,
            Title = title,
            Description = description,
            Location = location,
            StartDateTime = startUtc,
            EndDateTime = endUtc,
            IsAllDay = isAllDay,
            ExternalUid = PersistableExternalUid(uid),
            CreatedByUserId = userId,
            CreatedAt = now,
            UpdatedAt = now,
        });
        imported++;
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

    private static void ApplyPatternFields(RecurrencePattern target, RecurrencePattern source, string uid, string userId, DateTime now)
    {
        target.Title = source.Title;
        target.Description = source.Description;
        target.Location = source.Location;
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
        target.ExternalUid = PersistableExternalUid(uid);
        target.UpdatedByUserId = userId;
        target.UpdatedAt = now;
    }

    private static ContextCalendarEvent NewOccurrence(RecurrencePattern pattern, DateTime start, DateTime end, string userId, DateTime now) => new()
    {
        CalendarId = pattern.CalendarId,
        Title = pattern.Title,
        Description = pattern.Description,
        Location = pattern.Location,
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
    // forms (issue #345 security review, F1) must never be persisted as ExternalUid. Same-calendar
    // round-trip idempotency doesn't need it — BuildIndex already indexes every row by its own
    // PK-derived synthetic key regardless of ExternalUid. Persisting it anyway lets an aggregate
    // export's cross-calendar synthetic UID (embedded because ExternalUid is only unique within its
    // own calendar) get carried by a *different* row into a *different* calendar; if that row's
    // calendar is later exported and the file re-imported into the UID's originating calendar,
    // BuildIndex's synthetic-key entry for the true owner would match it and silently overwrite the
    // unrelated original row in place.
    private static bool IsSyntheticUid(string uid) =>
        uid.EndsWith(SyntheticUidSuffix, StringComparison.Ordinal)
        && Guid.TryParse(uid[..^SyntheticUidSuffix.Length], out _);

    // The value to persist as ExternalUid for a given parsed VEVENT UID — null when the incoming UID
    // is absent, or is one of Odyssey's own synthetic forms (see IsSyntheticUid); the UID unchanged
    // otherwise.
    private static string? PersistableExternalUid(string? uid) => uid is null || IsSyntheticUid(uid) ? null : uid;

    // Indexes existing rows by both their native ExternalUid (when set) and their synthesized
    // "{primary-key}@odyssey.local" form, so a re-imported file matches whether the VEVENT carries the
    // original UID or the synthesized one Odyssey emits for app-native rows.
    private static Dictionary<string, T> BuildIndex<T>(IEnumerable<T> rows, Func<T, string?> externalUid, Func<T, Guid> id)
    {
        var index = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var external = externalUid(row);
            if (!string.IsNullOrEmpty(external))
            {
                index.TryAdd(external, row);
            }

            index.TryAdd($"{id(row)}{SyntheticUidSuffix}", row);
        }

        return index;
    }

    private static DomainValidationException AggregateExceeded(int maxAggregateOccurrences) =>
        new($"The import would create or regenerate more than {maxAggregateOccurrences} occurrences.");

    /// <summary>Whether the multipart part's content type is acceptable for an <c>.ics</c> upload. The
    /// <c>.ics</c> extension and the parse itself are the real validity gates; the content type is only
    /// a hint, so we accept what browsers/OSes routinely send for calendar files (many map <c>.ics</c>
    /// to <c>application/octet-stream</c> or <c>text/plain</c>, and some omit it) and only reject a
    /// clearly-wrong declared type like <c>application/json</c>. Public so the controller can gate at
    /// the edge (defense-in-depth for direct service callers keeps it here too).</summary>
    public static bool IsAcceptedContentType(string? contentType) =>
        ImportFileReader.IsAcceptedContentType(contentType, AcceptedContentTypes);

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

public sealed record IcsExport(string FileName, string Content);

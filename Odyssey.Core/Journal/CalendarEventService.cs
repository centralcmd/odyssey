using Odyssey.Core;
using Mapster;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Odyssey.Core.Finance;
using Odyssey.Core.Locations;
using Odyssey.Core.Pagination;
using Odyssey.Context;
using Odyssey.Dtos.Journal;
using Odyssey.Dtos;
using Context = Odyssey.Context;

namespace Odyssey.Core.Journal;

/// <summary>
/// CRUD for individual calendar events (standalone or a single generated occurrence). List queries
/// match on overlap, not start-only, so an event spanning the window boundary (e.g. across midnight)
/// is still returned.
/// </summary>
/// <remarks>
/// <para>
/// <b>Times are wall-clock plus zone</b> (issue #519 §5): the server alone derives the instant, and a read returns the
/// wall-clock in the event's zone beside the instant. A row with no zone is floating — stored and shown as a
/// wall-clock — which is every row written before #519.
/// </para>
/// <para>
/// <b>A write to a generated occurrence takes its series lock</b> (§3 component 4): an edit marks the row an override,
/// a delete records an exclusion for its key, and a legacy series is keyed inline first (v4 §4). If keying detaches the
/// very row being written, the write applies to that standalone event and reports <c>detached</c> (v5 §3).
/// </para>
/// </remarks>
public class CalendarEventService
{
    private static readonly DateTime MinAllowedDateTime = new(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime MaxAllowedDateTime = new(2200, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly OdysseyContext context;
    private readonly IJournalLimitsLookup limits;
    private readonly TimeProvider timeProvider;
    private readonly ILogger? logger;

    // The window and duration bounds were `private const` here until issue #434 (keys 6 and 7). This
    // service held no settings lookup at all, so it gains one — one cached read per request, on a path
    // that already performs at least one database round-trip.
    public CalendarEventService(
        OdysseyContext context, IJournalLimitsLookup limits, TimeProvider? timeProvider = null,
        ILogger<CalendarEventService>? logger = null)
    {
        this.context = context;
        this.limits = limits;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.logger = logger;
    }

    /// <param name="claims">Decides whether the <c>locationRef</c> embed is filled; null is no capability at all.</param>
    public async Task<PagedResult<ExistingCalendarEvent>> ListAsync(
        CalendarEventsQueryParams query,
        CancellationToken cancellationToken = default,
        LocationClaims? claims = null)
    {
        if (query.From is not { } from || query.To is not { } to)
        {
            throw new DomainValidationException("From and To are both required.");
        }

        if (to < from)
        {
            throw new DomainValidationException("To must be on or after From.");
        }

        var effectiveLimits = await limits.GetAsync(cancellationToken);
        if ((to - from).TotalDays > effectiveLimits.CalendarMaxWindowDays)
        {
            throw new DomainValidationException(
                $"The From/To window cannot span more than {effectiveLimits.CalendarMaxWindowDays} days.");
        }

        // Overlap, not start-only: an event that begins before From but is still in progress
        // (e.g. spans midnight) is still returned. Floating rows match by wall-clock in the view zone (#519 v3 §A).
        var view = CalendarWindow.ViewZone(query.ViewTimeZoneId);
        var q = CalendarWindow.Overlapping(context.CalendarEvents.AsNoTracking(), from, to, view);

        if (query.CalendarIds is { Length: > 0 } calendarIds)
        {
            var ids = calendarIds.Distinct().ToList();
            q = q.Where(e => ids.Contains(e.CalendarId));
        }

        if (query.LocationId is { } locationId)
        {
            q = q.Where(e => e.LocationId == locationId);
        }

        var term = ListQuery.NormalizeSearch(query.Search);
        if (term is not null)
        {
            var pattern = ListQuery.ContainsPattern(term);
            q = q.Where(e => EF.Functions.Like(e.Title, pattern));
        }

        var ascending = ListQuery.Ascending(query.SortDir, naturalDefaultAscending: query.SortBy is not CalendarEventSortBy.Title);
        var startKey = CalendarWindow.SortKey(from, to, view);
        IOrderedQueryable<Context.CalendarEvent> sorted = query.SortBy switch
        {
            CalendarEventSortBy.Title => ascending ? q.OrderBy(e => e.Title) : q.OrderByDescending(e => e.Title),
            CalendarEventSortBy.CreatedAt => ascending ? q.OrderBy(e => e.CreatedAt) : q.OrderByDescending(e => e.CreatedAt),
            _ => ascending ? q.OrderBy(startKey) : q.OrderByDescending(startKey),
        };
        q = sorted.ThenBy(e => e.CalendarEventId);

        var page = await q.ToPagedResultAsync(query.Offset, query.Limit, ToDto, cancellationToken);
        await FillLocationRefsAsync(context, page.Items, claims, cancellationToken);
        await CalendarLinkProjection.FillCountsAsync(context, page.Items, cancellationToken);
        return page;
    }

    public async Task<ExistingCalendarEvent?> Get(Guid id, CancellationToken cancellationToken = default, LocationClaims? claims = null)
    {
        var calendarEvent = await context.CalendarEvents.AsNoTracking()
            .FirstOrDefaultAsync(e => e.CalendarEventId == id, cancellationToken);
        return calendarEvent is null ? null : await ToDtoAsync(calendarEvent, claims, cancellationToken);
    }

    /// <summary>
    /// The read shape of a row (issue #519 §5): the wall-clock in its zone, the instant beside it, and its series facts.
    /// The one projection every calendar read goes through.
    /// </summary>
    internal static ExistingCalendarEvent ToDto(Context.CalendarEvent calendarEvent)
    {
        var dto = calendarEvent.Adapt<ExistingCalendarEvent>();
        dto.StartDateTime = CalendarTimeZones.ReadValue(calendarEvent.StartDateTime, calendarEvent.TimeZoneId);
        dto.EndDateTime = CalendarTimeZones.ReadValue(calendarEvent.EndDateTime, calendarEvent.TimeZoneId);
        dto.StartUtc = CalendarTimeZones.InstantOf(calendarEvent.StartDateTime, calendarEvent.TimeZoneId);
        dto.EndUtc = CalendarTimeZones.InstantOf(calendarEvent.EndDateTime, calendarEvent.TimeZoneId);
        dto.IsFloating = calendarEvent.TimeZoneId is null;
        dto.IsKeyed = calendarEvent.RecurrencePatternId is not null && calendarEvent.OccurrenceKey is not null;
        dto.Detached = false;
        dto.DetachedCount = 0;
        return dto;
    }

    /// <summary>Fills each row's <see cref="ExistingCalendarEvent.LocationRef"/> in one query (issue #380 §5.1).</summary>
    internal static async Task FillLocationRefsAsync(
        OdysseyContext context, IReadOnlyCollection<ExistingCalendarEvent> rows, LocationClaims? claims,
        CancellationToken cancellationToken)
    {
        var refs = await LocationRefs.LoadAsync(context, rows.Select(r => r.LocationId), claims ?? LocationClaims.None, cancellationToken);
        foreach (var row in rows)
        {
            row.LocationRef = refs.For(row.LocationId);
        }
    }

    // A detail read: the links' lists and counts as well (issue #521 §5.3).
    private async Task<ExistingCalendarEvent> ToDtoAsync(Context.CalendarEvent calendarEvent, LocationClaims? claims, CancellationToken cancellationToken)
    {
        var dto = ToDto(calendarEvent);
        await FillLocationRefsAsync(context, [dto], claims, cancellationToken);
        await CalendarLinkProjection.FillDetailAsync(context, dto, cancellationToken);
        return dto;
    }

    /// <param name="claims">
    /// The caller's location claims: linking a place by id needs <c>locations.read</c> (issue #380 §5.2).
    /// Null is no capability at all; a trusted, non-HTTP caller passes <see cref="LocationClaims.Trusted"/> explicitly.
    /// </param>
    /// <param name="linkClaims">Whether the caller may ADD a file or a photo (issue #521 §3); null is neither.</param>
    public async Task<ExistingCalendarEvent> Create(
        NewCalendarEvent request, string userId, CancellationToken cancellationToken = default, LocationClaims? claims = null,
        CalendarLinkClaims? linkClaims = null)
    {
        var (start, end, zoneId) = ResolveWriteTimes(
            request.StartDateTime, request.EndDateTime, request.IsAllDay, request.TimeZoneId, storedZoneId: null);
        await EnsureCalendarExists(request.CalendarId, cancellationToken);
        var effectiveLimits = await limits.GetAsync(cancellationToken);
        ValidateTimes(start, end, request.IsAllDay, effectiveLimits.CalendarMaxEventDurationDays);

        var resolver = new LocationResolver(context, timeProvider);
        var locationId = await resolver.ResolveRecordLinkAsync(request.LocationId, null, claims ?? LocationClaims.None, cancellationToken);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var calendarEvent = new Context.CalendarEvent
        {
            CalendarId = request.CalendarId,
            Title = request.Title,
            Description = request.Description,
            Location = request.Location,
            LocationId = locationId,
            StartDateTime = start,
            EndDateTime = end,
            IsAllDay = request.IsAllDay,
            TimeZoneId = zoneId,
            CreatedByUserId = userId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        calendarEvent.CalendarEventId = Guid.NewGuid();
        context.CalendarEvents.Add(calendarEvent);
        await new CalendarLinkService(context).ApplyToEventAsync(
            calendarEvent.CalendarEventId, isNew: true, request.AttachmentFileIds, request.PhotoIds,
            linkClaims ?? CalendarLinkClaims.None, effectiveLimits.CalendarEventMaxLinksPerKind, now, cancellationToken);
        await SaveLinkedAsync(resolver, cancellationToken);

        return await ToDtoAsync(calendarEvent, claims, cancellationToken);
    }

    // Edits this occurrence only. RecurrencePatternId is immutable — it isn't part of NewCalendarEvent,
    // so there is no request-body path that can set or clear it (structurally enforced, spec §7/§9).
    // PUT is a full replacement (issue #380 §5.2): an omitted locationId clears the link, an unchanged one
    // is kept (even to a place archived since) with no claim, and a changed one needs locations.read.
    public async Task<ExistingCalendarEvent?> Update(
        Guid id, NewCalendarEvent request, string userId, CancellationToken cancellationToken = default, LocationClaims? claims = null,
        CalendarLinkClaims? linkClaims = null)
    {
        await EnsureCalendarExists(request.CalendarId, cancellationToken);
        var effectiveLimits = await limits.GetAsync(cancellationToken);

        // Read outside the transaction only to learn which series to lock (#519 v4 §2): the lock comes first inside it.
        var patternId = await context.CalendarEvents.AsNoTracking()
            .Where(e => e.CalendarEventId == id)
            .Select(e => new { e.RecurrencePatternId })
            .FirstOrDefaultAsync(cancellationToken);
        if (patternId is null)
        {
            return null;
        }

        var carriesLinks = request.AttachmentFileIds is not null || request.PhotoIds is not null;
        var written = await CalendarSeriesTransactions.ExecuteAsync(context, async () =>
        {
            var touched = await LockAndKeyAsync(
                id, patternId.RecurrencePatternId, userId, cancellationToken, lockStandalone: carriesLinks);
            if (touched.Row is not { } calendarEvent)
            {
                return touched;
            }

            var (start, end, zoneId) = ResolveWriteTimes(
                request.StartDateTime, request.EndDateTime, request.IsAllDay, request.TimeZoneId, calendarEvent.TimeZoneId);
            ValidateTimes(start, end, request.IsAllDay, effectiveLimits.CalendarMaxEventDurationDays);

            var resolver = new LocationResolver(context, timeProvider);
            var locationId = await resolver.ResolveRecordLinkAsync(
                request.LocationId, calendarEvent.LocationId, claims ?? LocationClaims.None, cancellationToken);

            calendarEvent.CalendarId = request.CalendarId;
            calendarEvent.Title = request.Title;
            calendarEvent.Description = request.Description;
            calendarEvent.Location = request.Location;
            calendarEvent.LocationId = locationId;
            calendarEvent.StartDateTime = start;
            calendarEvent.EndDateTime = end;
            calendarEvent.IsAllDay = request.IsAllDay;
            calendarEvent.TimeZoneId = zoneId;
            calendarEvent.UpdatedByUserId = userId;
            calendarEvent.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;

            // A PUT on a keyed generated row — a retime from the grid included — makes it an override (§3 component 4).
            if (calendarEvent.RecurrencePatternId is not null && calendarEvent.OccurrenceKey is not null)
            {
                calendarEvent.IsOverride = true;
            }

            // A series member's links are its series' (issue #521 §3 step 5); a standalone row's are its own, and a row
            // the keying above detached is standalone now, its copied links included.
            if (calendarEvent.RecurrencePatternId is not null)
            {
                CalendarLinkService.EnsureNoOccurrenceLinks(request.AttachmentFileIds, request.PhotoIds);
            }
            else
            {
                await new CalendarLinkService(context).ApplyToEventAsync(
                    calendarEvent.CalendarEventId, isNew: false, request.AttachmentFileIds, request.PhotoIds,
                    linkClaims ?? CalendarLinkClaims.None, effectiveLimits.CalendarEventMaxLinksPerKind,
                    calendarEvent.UpdatedAt, cancellationToken);
            }

            await SaveLinkedAsync(resolver, cancellationToken);
            return touched;
        }, cancellationToken, logger,
            busy: patternId.RecurrencePatternId is null && carriesLinks ? CalendarAttachmentsException.LinksBusy : null);

        if (written.Row is not { } saved)
        {
            return null;
        }

        var dto = await ToDtoAsync(saved, claims, cancellationToken);
        dto.Detached = written.Detached;
        dto.DetachedCount = written.DetachedCount;
        return dto;
    }

    /// <summary>
    /// Deletes this occurrence only. On a row of a series it also records an exclusion for the row's key in the same
    /// transaction, so a later series edit or re-import does not bring it back (issue #519 §5 endpoint 2). Null when the
    /// row does not exist.
    /// </summary>
    public async Task<CalendarEventDeleteResult?> Delete(Guid id, CancellationToken cancellationToken = default, string? userId = null)
    {
        var patternId = await context.CalendarEvents.AsNoTracking()
            .Where(e => e.CalendarEventId == id)
            .Select(e => new { e.RecurrencePatternId })
            .FirstOrDefaultAsync(cancellationToken);
        if (patternId is null)
        {
            return null;
        }

        return await CalendarSeriesTransactions.ExecuteAsync(context, async () =>
        {
            var touched = await LockAndKeyAsync(id, patternId.RecurrencePatternId, userId, cancellationToken);
            if (touched.Row is not { } calendarEvent)
            {
                return null;
            }

            if (calendarEvent.RecurrencePatternId is { } seriesId && calendarEvent.OccurrenceKey is { } key
                && !await context.RecurrenceExclusions.AnyAsync(
                    x => x.RecurrencePatternId == seriesId && x.OccurrenceKey == key, cancellationToken)
                && !context.RecurrenceExclusions.Local.Any(x => x.RecurrencePatternId == seriesId && x.OccurrenceKey == key))
            {
                context.RecurrenceExclusions.Add(new RecurrenceExclusion
                {
                    RecurrencePatternId = seriesId,
                    OccurrenceKey = key,
                    CreatedAt = timeProvider.GetUtcNow().UtcDateTime,
                    CreatedByUserId = userId,
                });
            }

            // Its links go with it: the CASCADE on MariaDB, this tracked removal on the InMemory tiers (issue #521 §4).
            if (calendarEvent.RecurrencePatternId is null)
            {
                await CalendarLinkService.StageRemoveForEventAsync(context, calendarEvent.CalendarEventId, cancellationToken);
            }

            context.CalendarEvents.Remove(calendarEvent);
            await context.SaveChangesAsync(cancellationToken);
            return new CalendarEventDeleteResult { Detached = touched.Detached, DetachedCount = touched.DetachedCount };
        }, cancellationToken, logger);
    }

    private sealed record TouchedRow(Context.CalendarEvent? Row, bool Detached, int DetachedCount);

    // Inside the transaction: the series lock first, then the row, keying a legacy series inline (#519 v4 §4).
    private async Task<TouchedRow> LockAndKeyAsync(
        Guid eventId, Guid? patternId, string? userId, CancellationToken cancellationToken, bool lockStandalone = false)
    {
        if (patternId is not { } seriesId)
        {
            // A standalone event's link write locks its row before the diff (issue #521 v3 §C).
            if (lockStandalone)
            {
                await CalendarSeriesTransactions.LockEventsAsync(context, [eventId], cancellationToken);
            }

            var standalone = await context.CalendarEvents.FirstOrDefaultAsync(e => e.CalendarEventId == eventId, cancellationToken);
            if (standalone?.RecurrencePatternId is not null)
            {
                // It joined a series between the pre-read and the transaction: nothing does that, so treat it as a race.
                throw CalendarSeriesConflictException.SeriesBusy();
            }

            return new TouchedRow(standalone, false, 0);
        }

        await CalendarSeriesTransactions.LockSeriesAsync(context, [seriesId], cancellationToken);
        var state = await RecurrenceSeriesState.LoadAsync(context, seriesId, tracked: true, cancellationToken);
        var row = state?.Rows.FirstOrDefault(r => r.CalendarEventId == eventId)
            ?? await context.CalendarEvents.FirstOrDefaultAsync(e => e.CalendarEventId == eventId, cancellationToken);
        if (state is null || row is null)
        {
            return new TouchedRow(row, false, 0);
        }

        var detachedCount = 0;
        if (state.Pattern.KeysBackfilledAt is null)
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;
            var detachedRows = new List<Context.CalendarEvent>();
            detachedCount = RecurrenceSeries.KeyInPlace(state, now, userId, x => context.RecurrenceExclusions.Add(x), detachedRows);
            await CalendarLinkService.StageCopyToDetachedAsync(context, seriesId, detachedRows, cancellationToken);
            logger?.LogInformation(
                "Calendar series {RecurrencePatternId} keyed inline by an occurrence write: {DetachedCount} row(s) detached, {ExclusionCount} exclusion(s).",
                seriesId, detachedCount, state.Exclusions.Count);
        }

        return new TouchedRow(row, Detached: row.RecurrencePatternId is null, detachedCount);
    }

    // A save that may carry link rows: a concurrent identical add or a file deleted under it is mapped, never a 500.
    private static async Task SaveLinkedAsync(LocationResolver resolver, CancellationToken cancellationToken)
    {
        try
        {
            await resolver.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (CalendarLinkService.Translate(ex) is { } mapped)
        {
            throw mapped;
        }
    }

    /// <summary>
    /// The stored start, end and zone of a write (issue #519 §8.1). All-day rows are zone-less and keep the UTC-midnight
    /// rule. With a zone sent, the times are its wall-clock (their <c>Kind</c> ignored, except that a value that carried
    /// an offset is already an instant); with none sent, a <c>PUT</c> keeps <paramref name="storedZoneId"/>, where an
    /// unspecified time is its wall-clock and a <c>Z</c> or offset time an instant. With no zone at all the time is
    /// normalized to UTC and stored floating, exactly as before #519.
    /// </summary>
    internal static (DateTime Start, DateTime End, string? ZoneId) ResolveWriteTimes(
        DateTime start, DateTime end, bool isAllDay, string? requestedZoneId, string? storedZoneId)
    {
        if (isAllDay)
        {
            return (DateTimeNormalization.NormalizeToUtc(start), DateTimeNormalization.NormalizeToUtc(end), null);
        }

        var requested = CalendarTimeZones.ResolveRequest(requestedZoneId);
        var zone = requested ?? CalendarTimeZones.Find(storedZoneId);
        if (zone is null)
        {
            return (DateTimeNormalization.NormalizeToUtc(start), DateTimeNormalization.NormalizeToUtc(end), null);
        }

        return (ToStored(start, zone, kindIgnored: requested is not null),
            ToStored(end, zone, kindIgnored: requested is not null), zone.Id);
    }

    // A request time as the stored instant of a zoned row.
    internal static DateTime ToStored(DateTime value, TimeZoneInfo zone, bool kindIgnored) => value.Kind switch
    {
        DateTimeKind.Local => value.ToUniversalTime(),
        DateTimeKind.Utc when !kindIgnored => value,
        _ => CalendarTimeZones.ToUtc(value, zone),
    };

    /// <remarks>
    /// <paramref name="maxEventDurationDays"/> is a parameter rather than something this helper reads
    /// for itself (issue #434 key 7). It is <c>static</c> and shared by three services —
    /// <see cref="CalendarEventService"/>, <c>RecurrencePatternService</c> and <c>CalendarIcsService</c>
    /// — each of which resolves its own <c>JournalLimits</c> snapshot once per request; passing the
    /// number in keeps that one snapshot authoritative for the whole request rather than letting a
    /// concurrent admin write split a single operation across two values.
    /// </remarks>
    internal static void ValidateTimes(DateTime start, DateTime end, bool isAllDay, int maxEventDurationDays)
    {
        if (end <= start)
        {
            throw new DomainValidationException("EndDateTime must be after StartDateTime.");
        }

        if (start < MinAllowedDateTime || end > MaxAllowedDateTime)
        {
            throw new DomainValidationException("StartDateTime and EndDateTime must fall between 1900 and 2200.");
        }

        if ((end - start).TotalDays > maxEventDurationDays)
        {
            throw new DomainValidationException(
                $"A single event cannot span more than {maxEventDurationDays} days.");
        }

        // All-day contract: an all-day event is anchored to the UTC calendar day. Start/End are the
        // exclusive-end [00:00 UTC, 00:00 UTC) day span (End is the midnight AFTER the last whole day),
        // so a one-day event is Start = D, End = D+1. The client renders all-day events by their date
        // component only (CalendarGridMath.CoversDay uses DateOnly.FromDateTime, no timezone shift), so
        // the day a user picks is the day shown regardless of browser timezone. All-day rows stay zone-less
        // (issue #519 Goal 3); the robust fix, if a far-east/west user near midnight ever matters, is to model
        // all-day start/end as DateOnly rather than DateTime — a schema change deferred out of v1.
        if (isAllDay && (start.TimeOfDay != TimeSpan.Zero || end.TimeOfDay != TimeSpan.Zero))
        {
            throw new DomainValidationException("StartDateTime and EndDateTime must fall on a UTC midnight boundary when IsAllDay is true.");
        }
    }

    private async Task EnsureCalendarExists(Guid calendarId, CancellationToken cancellationToken)
    {
        var exists = await context.Calendars.AnyAsync(c => c.CalendarId == calendarId, cancellationToken);
        if (!exists)
        {
            throw new DomainNotFoundException($"Calendar ID {calendarId} not found.");
        }
    }
}

using Odyssey.Core;
using Mapster;
using Microsoft.EntityFrameworkCore;
using Odyssey.Core.Finance;
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
public class CalendarEventService
{
    private static readonly DateTime MinAllowedDateTime = new(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime MaxAllowedDateTime = new(2200, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly OdysseyContext context;
    private readonly IJournalLimitsLookup limits;
    private readonly TimeProvider timeProvider;

    // The window and duration bounds were `private const` here until issue #434 (keys 6 and 7). This
    // service held no settings lookup at all, so it gains one — one cached read per request, on a path
    // that already performs at least one database round-trip.
    public CalendarEventService(
        OdysseyContext context, IJournalLimitsLookup limits, TimeProvider? timeProvider = null)
    {
        this.context = context;
        this.limits = limits;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<PagedResult<ExistingCalendarEvent>> ListAsync(
        CalendarEventsQueryParams query,
        CancellationToken cancellationToken = default)
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
        // (e.g. spans midnight) is still returned.
        var q = context.CalendarEvents.AsNoTracking()
            .Where(e => e.StartDateTime < to && e.EndDateTime > from);

        if (query.CalendarIds is { Length: > 0 } calendarIds)
        {
            var ids = calendarIds.Distinct().ToList();
            q = q.Where(e => ids.Contains(e.CalendarId));
        }

        var term = ListQuery.NormalizeSearch(query.Search);
        if (term is not null)
        {
            var pattern = ListQuery.ContainsPattern(term);
            q = q.Where(e => EF.Functions.Like(e.Title, pattern));
        }

        var ascending = ListQuery.Ascending(query.SortDir, naturalDefaultAscending: query.SortBy is not CalendarEventSortBy.Title);
        IOrderedQueryable<Context.CalendarEvent> sorted = query.SortBy switch
        {
            CalendarEventSortBy.Title => ascending ? q.OrderBy(e => e.Title) : q.OrderByDescending(e => e.Title),
            CalendarEventSortBy.CreatedAt => ascending ? q.OrderBy(e => e.CreatedAt) : q.OrderByDescending(e => e.CreatedAt),
            _ => ascending ? q.OrderBy(e => e.StartDateTime) : q.OrderByDescending(e => e.StartDateTime),
        };
        q = sorted.ThenBy(e => e.CalendarEventId);

        return await q.ToPagedResultAsync(query.Offset, query.Limit, e => e.Adapt<ExistingCalendarEvent>(), cancellationToken);
    }

    public async Task<ExistingCalendarEvent?> Get(Guid id, CancellationToken cancellationToken = default)
    {
        var calendarEvent = await context.CalendarEvents.AsNoTracking()
            .FirstOrDefaultAsync(e => e.CalendarEventId == id, cancellationToken);
        return calendarEvent?.Adapt<ExistingCalendarEvent>();
    }

    public async Task<ExistingCalendarEvent> Create(NewCalendarEvent request, string userId, CancellationToken cancellationToken = default)
    {
        await EnsureCalendarExists(request.CalendarId, cancellationToken);
        var effectiveLimits = await limits.GetAsync(cancellationToken);
        ValidateTimes(request.StartDateTime, request.EndDateTime, request.IsAllDay,
            effectiveLimits.CalendarMaxEventDurationDays);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var calendarEvent = new Context.CalendarEvent
        {
            CalendarId = request.CalendarId,
            Title = request.Title,
            Description = request.Description,
            Location = request.Location,
            StartDateTime = request.StartDateTime,
            EndDateTime = request.EndDateTime,
            IsAllDay = request.IsAllDay,
            CreatedByUserId = userId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        context.CalendarEvents.Add(calendarEvent);
        await context.SaveChangesAsync(cancellationToken);

        return calendarEvent.Adapt<ExistingCalendarEvent>();
    }

    // Edits this occurrence only. RecurrencePatternId is immutable — it isn't part of NewCalendarEvent,
    // so there is no request-body path that can set or clear it (structurally enforced, spec §7/§9).
    public async Task<ExistingCalendarEvent?> Update(Guid id, NewCalendarEvent request, string userId, CancellationToken cancellationToken = default)
    {
        var calendarEvent = await context.CalendarEvents.FirstOrDefaultAsync(e => e.CalendarEventId == id, cancellationToken);
        if (calendarEvent is null)
        {
            return null;
        }

        await EnsureCalendarExists(request.CalendarId, cancellationToken);
        var effectiveLimits = await limits.GetAsync(cancellationToken);
        ValidateTimes(request.StartDateTime, request.EndDateTime, request.IsAllDay,
            effectiveLimits.CalendarMaxEventDurationDays);

        calendarEvent.CalendarId = request.CalendarId;
        calendarEvent.Title = request.Title;
        calendarEvent.Description = request.Description;
        calendarEvent.Location = request.Location;
        calendarEvent.StartDateTime = request.StartDateTime;
        calendarEvent.EndDateTime = request.EndDateTime;
        calendarEvent.IsAllDay = request.IsAllDay;
        calendarEvent.UpdatedByUserId = userId;
        calendarEvent.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;

        await context.SaveChangesAsync(cancellationToken);

        return calendarEvent.Adapt<ExistingCalendarEvent>();
    }

    public async Task<bool> Delete(Guid id, CancellationToken cancellationToken = default)
    {
        var calendarEvent = await context.CalendarEvents.FirstOrDefaultAsync(e => e.CalendarEventId == id, cancellationToken);
        if (calendarEvent is null)
        {
            return false;
        }

        context.CalendarEvents.Remove(calendarEvent);
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

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
        // the day a user picks is the day shown regardless of browser timezone. This deliberately does
        // NOT track a per-event timezone (issue #323 Non-Goal); a far-east/west user near midnight sees
        // the UTC day, not their local day. The robust fix, if that ever matters, is to model all-day
        // start/end as DateOnly rather than DateTime — a schema change deferred out of v1.
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

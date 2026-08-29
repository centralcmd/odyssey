using Odyssey.Core;
using Mapster;
using Microsoft.EntityFrameworkCore;
using Odyssey.Core.Finance;
using Odyssey.Core.Pagination;
using Odyssey.Context;
using Odyssey.Dtos.Journal;
using Odyssey.Dtos;
using ContextRecurrenceFrequency = Odyssey.Context.RecurrenceFrequency;
using ContextDaysOfWeekFlags = Odyssey.Context.DaysOfWeekFlags;
using DtoRecurrenceFrequency = Odyssey.Dtos.Journal.RecurrenceFrequency;
using DtoDaysOfWeekFlags = Odyssey.Dtos.Journal.DaysOfWeekFlags;
using Context = Odyssey.Context;

namespace Odyssey.Core.Journal;

/// <summary>
/// Creates and maintains recurring event series. Recurrence is eagerly materialized into concrete
/// <see cref="Context.CalendarEvent"/> rows at create time (and re-materialized for the future portion
/// on update), which is what makes single-occurrence edit/delete possible without exception-tracking
/// fields — see <see cref="RecurrenceOccurrenceGenerator"/>.
/// </summary>
public class RecurrencePatternService
{
    private readonly OdysseyContext context;
    private readonly IJournalLimitsLookup limits;
    private readonly TimeProvider timeProvider;

    // Newly injected in issue #434: this service held no settings lookup, and now needs two values from
    // one snapshot — the tighten-only occurrence cap (key 11) and the event-duration bound the shared
    // CalendarEventService.ValidateTimes helper enforces (key 7). One cached read per request, on a
    // path that already writes one calendar row per generated occurrence.
    public RecurrencePatternService(
        OdysseyContext context, IJournalLimitsLookup limits, TimeProvider? timeProvider = null)
    {
        this.context = context;
        this.limits = limits;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<PagedResult<ExistingRecurrencePattern>> ListAsync(
        RecurrencePatternsQueryParams query,
        CancellationToken cancellationToken = default)
    {
        var q = context.RecurrencePatterns.AsNoTracking();

        if (query.CalendarId is { } calendarId)
        {
            q = q.Where(p => p.CalendarId == calendarId);
        }

        var term = ListQuery.NormalizeSearch(query.Search);
        if (term is not null)
        {
            var pattern = ListQuery.ContainsPattern(term);
            q = q.Where(p => EF.Functions.Like(p.Title, pattern));
        }

        var ascending = ListQuery.Ascending(query.SortDir, naturalDefaultAscending: query.SortBy is not RecurrencePatternSortBy.Title);
        IOrderedQueryable<RecurrencePattern> sorted = query.SortBy switch
        {
            RecurrencePatternSortBy.Title => ascending ? q.OrderBy(p => p.Title) : q.OrderByDescending(p => p.Title),
            RecurrencePatternSortBy.CreatedAt => ascending ? q.OrderBy(p => p.CreatedAt) : q.OrderByDescending(p => p.CreatedAt),
            _ => ascending ? q.OrderBy(p => p.StartDateTime) : q.OrderByDescending(p => p.StartDateTime),
        };
        q = sorted.ThenBy(p => p.RecurrencePatternId);

        var page = await q.ToPagedResultAsync(query.Offset, query.Limit, cancellationToken);
        return await ToDtoPageAsync(page, cancellationToken);
    }

    public async Task<ExistingRecurrencePattern?> Get(Guid id, CancellationToken cancellationToken = default)
    {
        var pattern = await context.RecurrencePatterns.AsNoTracking()
            .FirstOrDefaultAsync(p => p.RecurrencePatternId == id, cancellationToken);
        return pattern is null ? null : await ToDtoAsync(pattern, cancellationToken);
    }

    public async Task<PagedResult<ExistingCalendarEvent>?> ListEventsAsync(
        Guid patternId, int offset, int limit, CancellationToken cancellationToken = default)
    {
        var exists = await context.RecurrencePatterns.AnyAsync(p => p.RecurrencePatternId == patternId, cancellationToken);
        if (!exists)
        {
            return null;
        }

        var q = context.CalendarEvents
            .AsNoTracking()
            .Where(e => e.RecurrencePatternId == patternId)
            .OrderBy(e => e.StartDateTime)
            .ThenBy(e => e.CalendarEventId);

        return await q.ToPagedResultAsync(offset, limit, e => e.Adapt<ExistingCalendarEvent>(), cancellationToken);
    }

    public async Task<ExistingRecurrencePattern> Create(NewRecurrencePattern request, string userId, CancellationToken cancellationToken = default)
    {
        await EnsureCalendarExists(request.CalendarId, cancellationToken);
        var effectiveLimits = await limits.GetAsync(cancellationToken);
        ValidateFields(request, effectiveLimits.CalendarMaxEventDurationDays);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var pattern = new RecurrencePattern
        {
            CalendarId = request.CalendarId,
            Title = request.Title,
            Description = request.Description,
            Location = request.Location,
            StartDateTime = request.StartDateTime,
            EndDateTime = request.EndDateTime,
            IsAllDay = request.IsAllDay,
            Frequency = request.Frequency.Adapt<ContextRecurrenceFrequency>(),
            Interval = request.Interval,
            DaysOfWeek = request.DaysOfWeek?.Adapt<ContextDaysOfWeekFlags>(),
            DayOfMonth = request.DayOfMonth,
            MonthOfYear = request.MonthOfYear,
            RecurrenceEndDate = request.RecurrenceEndDate,
            OccurrenceCount = request.OccurrenceCount,
            CreatedByUserId = userId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        // Computed (and capped) before any row is written — a rejected pattern leaves no partial state.
        var occurrences = RecurrenceOccurrenceGenerator.Generate(
            pattern, effectiveLimits.RecurrenceMaxGeneratedOccurrences);

        context.RecurrencePatterns.Add(pattern);
        foreach (var (start, end) in occurrences)
        {
            context.CalendarEvents.Add(new Context.CalendarEvent
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
            });
        }

        await context.SaveChangesAsync(cancellationToken);

        var dto = pattern.Adapt<ExistingRecurrencePattern>();
        dto.GeneratedEventCount = occurrences.Count;
        return dto;
    }

    // Updates the template/rule fields and regenerates ONLY the future (>= now) generated events;
    // past/current ones are left untouched, even if they no longer match the new rule.
    public async Task<ExistingRecurrencePattern?> Update(Guid id, NewRecurrencePattern request, string userId, CancellationToken cancellationToken = default)
    {
        var pattern = await context.RecurrencePatterns.FirstOrDefaultAsync(p => p.RecurrencePatternId == id, cancellationToken);
        if (pattern is null)
        {
            return null;
        }

        await EnsureCalendarExists(request.CalendarId, cancellationToken);
        var effectiveLimits = await limits.GetAsync(cancellationToken);
        ValidateFields(request, effectiveLimits.CalendarMaxEventDurationDays);

        var now = timeProvider.GetUtcNow().UtcDateTime;

        pattern.CalendarId = request.CalendarId;
        pattern.Title = request.Title;
        pattern.Description = request.Description;
        pattern.Location = request.Location;
        pattern.StartDateTime = request.StartDateTime;
        pattern.EndDateTime = request.EndDateTime;
        pattern.IsAllDay = request.IsAllDay;
        pattern.Frequency = request.Frequency.Adapt<ContextRecurrenceFrequency>();
        pattern.Interval = request.Interval;
        pattern.DaysOfWeek = request.DaysOfWeek?.Adapt<ContextDaysOfWeekFlags>();
        pattern.DayOfMonth = request.DayOfMonth;
        pattern.MonthOfYear = request.MonthOfYear;
        pattern.RecurrenceEndDate = request.RecurrenceEndDate;
        pattern.OccurrenceCount = request.OccurrenceCount;
        pattern.UpdatedByUserId = userId;
        pattern.UpdatedAt = now;

        var occurrences = RecurrenceOccurrenceGenerator.Generate(
            pattern, effectiveLimits.RecurrenceMaxGeneratedOccurrences);

        var futureExisting = await context.CalendarEvents
            .Where(e => e.RecurrencePatternId == id && e.StartDateTime >= now)
            .ToListAsync(cancellationToken);
        context.CalendarEvents.RemoveRange(futureExisting);

        foreach (var (start, end) in occurrences.Where(o => o.Start >= now))
        {
            context.CalendarEvents.Add(new Context.CalendarEvent
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
            });
        }

        await context.SaveChangesAsync(cancellationToken);

        return await ToDtoAsync(pattern, cancellationToken);
    }

    // Hard-deletes future (>= now) generated events; past/current ones are detached (RecurrencePatternId
    // set to null) and preserved, so calendar history isn't destroyed when a series is removed. Applied
    // explicitly rather than relying on the DB-level SetNull FK, so behaviour is identical on the
    // InMemory provider used by tests and on MariaDB.
    public async Task<bool> Delete(Guid id, CancellationToken cancellationToken = default)
    {
        var pattern = await context.RecurrencePatterns.FirstOrDefaultAsync(p => p.RecurrencePatternId == id, cancellationToken);
        if (pattern is null)
        {
            return false;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var linkedEvents = await context.CalendarEvents
            .Where(e => e.RecurrencePatternId == id)
            .ToListAsync(cancellationToken);

        foreach (var linkedEvent in linkedEvents)
        {
            if (linkedEvent.StartDateTime >= now)
            {
                context.CalendarEvents.Remove(linkedEvent);
            }
            else
            {
                linkedEvent.RecurrencePatternId = null;
            }
        }

        context.RecurrencePatterns.Remove(pattern);
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static void ValidateFields(NewRecurrencePattern request, int maxEventDurationDays)
    {
        CalendarEventService.ValidateTimes(
            request.StartDateTime, request.EndDateTime, request.IsAllDay, maxEventDurationDays);

        var hasEndDate = request.RecurrenceEndDate is not null;
        var hasCount = request.OccurrenceCount is not null;
        if (hasEndDate == hasCount)
        {
            throw new DomainValidationException("Exactly one of RecurrenceEndDate or OccurrenceCount must be set.");
        }

        if (hasEndDate && request.RecurrenceEndDate!.Value < request.StartDateTime)
        {
            throw new DomainValidationException("RecurrenceEndDate must be on or after StartDateTime.");
        }

        var isWeekly = request.Frequency == DtoRecurrenceFrequency.Weekly;
        var isMonthly = request.Frequency == DtoRecurrenceFrequency.Monthly;
        var isYearly = request.Frequency == DtoRecurrenceFrequency.Yearly;

        if ((request.DaysOfWeek is not null && request.DaysOfWeek != DtoDaysOfWeekFlags.None) != isWeekly)
        {
            throw new DomainValidationException("DaysOfWeek is required for Weekly recurrence and must be unset otherwise.");
        }

        if (request.DayOfMonth is not null != (isMonthly || isYearly))
        {
            throw new DomainValidationException("DayOfMonth is required for Monthly/Yearly recurrence and must be unset otherwise.");
        }

        if (request.MonthOfYear is not null != isYearly)
        {
            throw new DomainValidationException("MonthOfYear is required for Yearly recurrence and must be unset otherwise.");
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

    private async Task<ExistingRecurrencePattern> ToDtoAsync(RecurrencePattern pattern, CancellationToken cancellationToken)
    {
        var dto = pattern.Adapt<ExistingRecurrencePattern>();
        dto.GeneratedEventCount = await context.CalendarEvents.CountAsync(e => e.RecurrencePatternId == pattern.RecurrencePatternId, cancellationToken);
        return dto;
    }

    private async Task<PagedResult<ExistingRecurrencePattern>> ToDtoPageAsync(PagedResult<RecurrencePattern> page, CancellationToken cancellationToken)
    {
        var items = new List<ExistingRecurrencePattern>(page.Items.Count);
        foreach (var pattern in page.Items)
        {
            items.Add(await ToDtoAsync(pattern, cancellationToken));
        }

        return new PagedResult<ExistingRecurrencePattern>
        {
            Items = items,
            Offset = page.Offset,
            Limit = page.Limit,
            TotalCount = page.TotalCount,
        };
    }
}

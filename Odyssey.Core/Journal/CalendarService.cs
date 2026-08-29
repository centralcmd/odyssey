using Odyssey.Core;
using Mapster;
using Microsoft.EntityFrameworkCore;
using Odyssey.Core.Finance;
using Odyssey.Core.Pagination;
using Odyssey.Context;
using Odyssey.Dtos.Journal;
using Odyssey.Dtos;
using CalendarEntity = Odyssey.Context.Calendar;

namespace Odyssey.Core.Journal;

/// <summary>
/// CRUD for named calendars: name search + allowlisted sort, case-insensitive uniqueness, and a
/// delete-if-empty guard (409 while the calendar still owns any events or recurrence patterns).
/// </summary>
public class CalendarService
{
    private readonly OdysseyContext context;
    private readonly TimeProvider timeProvider;

    public CalendarService(OdysseyContext context, TimeProvider? timeProvider = null)
    {
        this.context = context;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<PagedResult<ExistingCalendar>> ListAsync(
        CalendarsQueryParams query,
        CancellationToken cancellationToken = default)
    {
        var q = context.Calendars.AsNoTracking();

        var term = ListQuery.NormalizeSearch(query.Search);
        if (term is not null)
        {
            var pattern = ListQuery.ContainsPattern(term);
            q = q.Where(c => EF.Functions.Like(c.Name, pattern));
        }

        var ascending = ListQuery.Ascending(query.SortDir, naturalDefaultAscending: true);
        IOrderedQueryable<CalendarEntity> sorted = query.SortBy switch
        {
            CalendarSortBy.CreatedAt => ascending ? q.OrderBy(c => c.CreatedAt) : q.OrderByDescending(c => c.CreatedAt),
            _ => ascending ? q.OrderBy(c => c.Name) : q.OrderByDescending(c => c.Name),
        };
        q = sorted.ThenBy(c => c.CalendarId);

        return await q.ToPagedResultAsync(query.Offset, query.Limit, c => c.Adapt<ExistingCalendar>(), cancellationToken);
    }

    public async Task<ExistingCalendar?> Get(Guid id, CancellationToken cancellationToken = default)
    {
        var calendar = await context.Calendars.AsNoTracking()
            .FirstOrDefaultAsync(c => c.CalendarId == id, cancellationToken);
        return calendar?.Adapt<ExistingCalendar>();
    }

    public async Task<ExistingCalendar> Create(NewCalendar request, string userId, CancellationToken cancellationToken = default)
    {
        var name = request.Name.Trim();
        EnsureColorInPalette(request.Color);
        await EnsureNameAvailable(name, null, cancellationToken);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var calendar = new CalendarEntity
        {
            Name = name,
            Description = request.Description,
            Color = request.Color,
            CreatedByUserId = userId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        context.Calendars.Add(calendar);
        await context.SaveChangesAsync(cancellationToken);

        return calendar.Adapt<ExistingCalendar>();
    }

    public async Task<ExistingCalendar?> Update(Guid id, NewCalendar request, string userId, CancellationToken cancellationToken = default)
    {
        var calendar = await context.Calendars.FirstOrDefaultAsync(c => c.CalendarId == id, cancellationToken);
        if (calendar is null)
        {
            return null;
        }

        var name = request.Name.Trim();
        EnsureColorInPalette(request.Color);
        await EnsureNameAvailable(name, id, cancellationToken);

        calendar.Name = name;
        calendar.Description = request.Description;
        calendar.Color = request.Color;
        calendar.UpdatedByUserId = userId;
        calendar.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;

        await context.SaveChangesAsync(cancellationToken);

        return calendar.Adapt<ExistingCalendar>();
    }

    public async Task<bool> Delete(Guid id, CancellationToken cancellationToken = default)
    {
        var calendar = await context.Calendars.FirstOrDefaultAsync(c => c.CalendarId == id, cancellationToken);
        if (calendar is null)
        {
            return false;
        }

        var hasEvents = await context.CalendarEvents.AnyAsync(e => e.CalendarId == id, cancellationToken);
        var hasPatterns = await context.RecurrencePatterns.AnyAsync(p => p.CalendarId == id, cancellationToken);
        if (hasEvents || hasPatterns)
        {
            throw new DomainConflictException(
                "Calendar has events or recurrence patterns; delete or move them before deleting the calendar.");
        }

        context.Calendars.Remove(calendar);
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    // Case-insensitive uniqueness, without a stored normalized column: candidate names are compared in
    // memory with OrdinalIgnoreCase, which behaves identically on the relational store (utf8mb4 *_ci
    // collation) and the InMemory provider used by tests. Mirrors JournalTagService.EnsureNameAvailable.
    private async Task EnsureNameAvailable(string name, Guid? excludeId, CancellationToken cancellationToken)
    {
        var existingNames = await context.Calendars
            .Where(c => excludeId == null || c.CalendarId != excludeId)
            .Select(c => c.Name)
            .ToListAsync(cancellationToken);

        if (existingNames.Any(existing => string.Equals(existing, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new DomainConflictException($"A calendar named '{name}' already exists.");
        }
    }

    private static void EnsureColorInPalette(string color)
    {
        if (!CalendarColors.IsValid(color))
        {
            throw new DomainValidationException($"'{color}' is not a supported calendar colour.");
        }
    }
}

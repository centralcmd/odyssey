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
using ContextRecurrenceFrequency = Odyssey.Context.RecurrenceFrequency;
using ContextDaysOfWeekFlags = Odyssey.Context.DaysOfWeekFlags;
using DtoRecurrenceFrequency = Odyssey.Dtos.Journal.RecurrenceFrequency;
using DtoDaysOfWeekFlags = Odyssey.Dtos.Journal.DaysOfWeekFlags;
using Context = Odyssey.Context;

namespace Odyssey.Core.Journal;

/// <summary>
/// Creates and maintains recurring event series. Recurrence is eagerly materialized into concrete
/// <see cref="Context.CalendarEvent"/> rows, each carrying its occurrence key — its local wall-clock start under the rule
/// (issue #519 §3) — so an edited occurrence (an override) and a deleted one (an exclusion) are remembered by key.
/// </summary>
/// <remarks>
/// <para>
/// <b>A series edit reconciles in place by key</b> (§3 component 2), under the series lock: future rows whose key
/// survives are updated, new keys get rows, and an override or exclusion survives exactly while its key does. Past keys
/// and rows are never touched. The <c>PUT</c> checks <c>expectedVersion</c> first and <c>confirmedDrops</c> second,
/// both under the lock (v4 §3).
/// </para>
/// <para>
/// <b>A legacy series is keyed inline</b> by its first write, with the backfill job's routine (v3 §D), before anything
/// else; its preview keys in memory only.
/// </para>
/// </remarks>
public class RecurrencePatternService
{
    private readonly OdysseyContext context;
    private readonly IJournalLimitsLookup limits;
    private readonly TimeProvider timeProvider;
    private readonly ILogger? logger;

    // Newly injected in issue #434: this service held no settings lookup, and now needs two values from
    // one snapshot — the tighten-only occurrence cap (key 11) and the event-duration bound the shared
    // CalendarEventService.ValidateTimes helper enforces (key 7). One cached read per request, on a path
    // that already writes one calendar row per generated occurrence.
    public RecurrencePatternService(
        OdysseyContext context, IJournalLimitsLookup limits, TimeProvider? timeProvider = null,
        ILogger<RecurrencePatternService>? logger = null)
    {
        this.context = context;
        this.limits = limits;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.logger = logger;
    }

    public async Task<PagedResult<ExistingRecurrencePattern>> ListAsync(
        RecurrencePatternsQueryParams query,
        CancellationToken cancellationToken = default,
        LocationClaims? claims = null)
    {
        var q = context.RecurrencePatterns.AsNoTracking();

        if (query.CalendarId is { } calendarId)
        {
            q = q.Where(p => p.CalendarId == calendarId);
        }

        if (query.LocationId is { } locationId)
        {
            q = q.Where(p => p.LocationId == locationId);
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
        return await ToDtoPageAsync(page, claims, cancellationToken);
    }

    public async Task<ExistingRecurrencePattern?> Get(Guid id, CancellationToken cancellationToken = default, LocationClaims? claims = null)
    {
        var pattern = await context.RecurrencePatterns.AsNoTracking()
            .FirstOrDefaultAsync(p => p.RecurrencePatternId == id, cancellationToken);
        return pattern is null ? null : await ToDtoAsync(pattern, claims, cancellationToken);
    }

    public async Task<PagedResult<ExistingCalendarEvent>?> ListEventsAsync(
        Guid patternId, int offset, int limit, CancellationToken cancellationToken = default, LocationClaims? claims = null)
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

        var page = await q.ToPagedResultAsync(offset, limit, CalendarEventService.ToDto, cancellationToken);
        await CalendarEventService.FillLocationRefsAsync(context, page.Items, claims, cancellationToken);
        await CalendarLinkProjection.FillCountsAsync(context, page.Items, cancellationToken);
        return page;
    }

    /// <summary>
    /// The place is a template field (issue #380 §4): copied onto every generated occurrence, and compared
    /// in the series diff. Linking by id needs <c>locations.read</c> when the link changes; null claims are a
    /// trusted, non-HTTP caller. A series is keyed at creation (issue #519 §4).
    /// </summary>
    /// <param name="linkClaims">Whether the caller may ADD a file or a photo (issue #521 §3); null is neither.</param>
    public async Task<ExistingRecurrencePattern> Create(
        NewRecurrencePattern request, string userId, CancellationToken cancellationToken = default, LocationClaims? claims = null,
        CalendarLinkClaims? linkClaims = null)
    {
        var times = ResolveTimes(request, storedZoneId: null);
        await EnsureCalendarExists(request.CalendarId, cancellationToken);
        var effectiveLimits = await limits.GetAsync(cancellationToken);
        ValidateFields(request, times, effectiveLimits.CalendarMaxEventDurationDays);
        var resolver = new LocationResolver(context, timeProvider);
        var locationId = await resolver.ResolveRecordLinkAsync(request.LocationId, null, claims ?? LocationClaims.None, cancellationToken);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var pattern = new RecurrencePattern
        {
            RecurrencePatternId = Guid.NewGuid(),
            CalendarId = request.CalendarId,
            Title = request.Title,
            Description = request.Description,
            Location = request.Location,
            LocationId = locationId,
            CreatedByUserId = userId,
            CreatedAt = now,
            UpdatedAt = now,
            Version = Guid.NewGuid(),
            KeysBackfilledAt = now,
        };
        ApplyRule(pattern, request, times);

        // Computed (and capped) before any row is written — a rejected pattern leaves no partial state.
        var occurrences = RecurrenceOccurrenceGenerator.Generate(
            pattern, effectiveLimits.RecurrenceMaxGeneratedOccurrences);

        context.RecurrencePatterns.Add(pattern);
        foreach (var occurrence in occurrences)
        {
            context.CalendarEvents.Add(RecurrenceSeries.NewOccurrence(pattern, occurrence, userId, now));
        }

        await new CalendarLinkService(context).ApplyToSeriesAsync(
            pattern.RecurrencePatternId, isNew: true, request.AttachmentFileIds, request.PhotoIds,
            linkClaims ?? CalendarLinkClaims.None, effectiveLimits.CalendarEventMaxLinksPerKind, now, cancellationToken);
        await SaveLinkedAsync(resolver, cancellationToken);

        var dto = await ToDtoAsync(pattern, claims, cancellationToken);
        dto.GeneratedEventCount = occurrences.Count;
        return dto;
    }

    /// <summary>
    /// Updates the template, rule and zone and reconciles the future rows in place by key (issue #519 §3). Null when the
    /// series does not exist. A stale <see cref="NewRecurrencePattern.ExpectedVersion"/> is a
    /// <c>409 calendar-series-version-conflict</c>; drops beyond <see cref="NewRecurrencePattern.ConfirmedDrops"/> a
    /// <c>409 calendar-series-drops-changed</c>, checked in that order and both under the lock.
    /// </summary>
    public async Task<RecurrencePatternUpdateResult?> Update(
        Guid id, NewRecurrencePattern request, string userId, CancellationToken cancellationToken = default, LocationClaims? claims = null,
        CalendarLinkClaims? linkClaims = null)
    {
        if (!await context.RecurrencePatterns.AsNoTracking().AnyAsync(p => p.RecurrencePatternId == id, cancellationToken))
        {
            return null;
        }

        await EnsureCalendarExists(request.CalendarId, cancellationToken);
        var effectiveLimits = await limits.GetAsync(cancellationToken);
        CalendarTimeZones.ResolveRequest(request.TimeZoneId);

        var outcome = await CalendarLinkService.WithOneLinkRetryAsync(() => CalendarSeriesTransactions.ExecuteAsync(context, async () =>
        {
            await CalendarSeriesTransactions.LockSeriesAsync(context, [id], cancellationToken);
            var state = await RecurrenceSeriesState.LoadAsync(context, id, tracked: true, cancellationToken);
            if (state is null)
            {
                return ((RecurrenceSeriesCounts Counts, int Detached)?)null;
            }

            if (request.ExpectedVersion is { } expected && expected != state.Pattern.Version)
            {
                throw CalendarSeriesConflictException.Stale();
            }

            var now = timeProvider.GetUtcNow().UtcDateTime;
            var detachedRows = new List<Context.CalendarEvent>();
            var detached = RecurrenceSeries.KeyInPlace(state, now, userId, x => context.RecurrenceExclusions.Add(x), detachedRows);
            await CalendarLinkService.StageCopyToDetachedAsync(context, id, detachedRows, cancellationToken);
            if (detached > 0)
            {
                logger?.LogInformation(
                    "Calendar series {RecurrencePatternId} keyed inline by a series edit: {DetachedCount} row(s) detached.",
                    id, detached);
            }

            var resolver = new LocationResolver(context, timeProvider);
            var target = await BuildTargetAsync(state.Pattern, request, effectiveLimits, resolver, claims, cancellationToken);
            var plan = RecurrenceSeries.Plan(state, target, now, effectiveLimits.RecurrenceMaxGeneratedOccurrences);

            if (request.ConfirmedDrops is { } confirmed
                && (plan.Counts.DroppedOverrides > confirmed.Overrides || plan.Counts.DroppedExclusions > confirmed.Exclusions))
            {
                throw CalendarSeriesConflictException.Drops(plan.Counts.DroppedOverrides, plan.Counts.DroppedExclusions);
            }

            if (request.ConfirmedDrops is null && plan.Counts.DroppedOverrides + plan.Counts.DroppedExclusions > 0)
            {
                // A caller that bound no consent still learns the counts from the response; the log keeps them too.
                logger?.LogInformation(
                    "Calendar series {RecurrencePatternId} edited without confirmedDrops: {DroppedOverrideCount} override(s) and "
                    + "{DroppedExclusionCount} exclusion(s) dropped.",
                    id, plan.Counts.DroppedOverrides, plan.Counts.DroppedExclusions);
            }

            RecurrenceSeries.Apply(context, state, plan, userId, now);
            // A template-only edit sends null arrays and never touches the links (issue #521 §5.2).
            await new CalendarLinkService(context).ApplyToSeriesAsync(
                id, isNew: false, request.AttachmentFileIds, request.PhotoIds, linkClaims ?? CalendarLinkClaims.None,
                effectiveLimits.CalendarEventMaxLinksPerKind, now, cancellationToken);
            await SaveLinkedAsync(resolver, cancellationToken);
            return (Counts: plan.Counts, Detached: detached);
        }, cancellationToken, logger));

        if (outcome is not { } done)
        {
            return null;
        }

        var saved = await context.RecurrencePatterns.AsNoTracking().FirstAsync(p => p.RecurrencePatternId == id, cancellationToken);
        return new RecurrencePatternUpdateResult
        {
            Pattern = await ToDtoAsync(saved, claims, cancellationToken),
            DroppedOverrideCount = done.Counts.DroppedOverrides,
            DroppedExclusionCount = done.Counts.DroppedExclusions,
            DetachedCount = done.Detached,
            AdjustedCount = done.Counts.Adjusted,
        };
    }

    /// <summary>
    /// What <see cref="Update"/> would discard for the same body (issue #519 §5 endpoint 4): nothing is saved and no lock
    /// is taken, so the counts are advisory; a legacy series is keyed in memory only (v3 §D). Null when the series does
    /// not exist.
    /// </summary>
    public async Task<RecurrencePatternPreview?> PreviewAsync(
        Guid id, NewRecurrencePattern request, CancellationToken cancellationToken = default, LocationClaims? claims = null)
    {
        var state = await RecurrenceSeriesState.LoadAsync(context, id, tracked: false, cancellationToken);
        if (state is null)
        {
            return null;
        }

        await EnsureCalendarExists(request.CalendarId, cancellationToken);
        var effectiveLimits = await limits.GetAsync(cancellationToken);
        var version = state.Pattern.Version;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var detached = RecurrenceSeries.KeyInPlace(state, now, userId: null, addExclusion: null);
        var target = await BuildTargetAsync(state.Pattern, request, effectiveLimits, resolver: null, claims, cancellationToken);
        var plan = RecurrenceSeries.Plan(state, target, now, effectiveLimits.RecurrenceMaxGeneratedOccurrences);
        return new RecurrencePatternPreview
        {
            DroppedOverrideCount = plan.Counts.DroppedOverrides,
            DroppedExclusionCount = plan.Counts.DroppedExclusions,
            DetachedCount = detached,
            AdjustedCount = plan.Counts.Adjusted,
            Version = version,
        };
    }

    // The series as the request would leave it, on a detached copy: the stored pattern is only changed by Apply.
    private async Task<RecurrencePattern> BuildTargetAsync(
        RecurrencePattern stored, NewRecurrencePattern request, JournalLimits effectiveLimits, LocationResolver? resolver,
        LocationClaims? claims, CancellationToken cancellationToken)
    {
        var times = ResolveTimes(request, stored.TimeZoneId);
        ValidateFields(request, times, effectiveLimits.CalendarMaxEventDurationDays);
        var locationId = resolver is null
            ? request.LocationId
            : await resolver.ResolveRecordLinkAsync(request.LocationId, stored.LocationId, claims ?? LocationClaims.None, cancellationToken);

        var target = RecurrenceSeries.CloneRule(stored);
        target.CalendarId = request.CalendarId;
        target.Title = request.Title;
        target.Description = request.Description;
        target.Location = request.Location;
        target.LocationId = locationId;
        ApplyRule(target, request, times);
        return target;
    }

    // Hard-deletes future (>= now) generated events; past/current ones are detached — their key and override flag
    // cleared (issue #519 §4 invariant 4) — and preserved, so calendar history isn't destroyed when a series is removed.
    // The exclusions go by tracked RemoveRange as well as the CASCADE, so the InMemory tiers match (S4).
    public async Task<bool> Delete(Guid id, CancellationToken cancellationToken = default)
    {
        if (!await context.RecurrencePatterns.AsNoTracking().AnyAsync(p => p.RecurrencePatternId == id, cancellationToken))
        {
            return false;
        }

        return await CalendarSeriesTransactions.ExecuteAsync(context, async () =>
        {
            await CalendarSeriesTransactions.LockSeriesAsync(context, [id], cancellationToken);
            var state = await RecurrenceSeriesState.LoadAsync(context, id, tracked: true, cancellationToken);
            if (state is null)
            {
                return false;
            }

            // Loaded inside the delegate, so a retried attempt starts clean (issue #521 v4 §6). The past rows keep copies of
            // the series' links; the series' own link rows go by tracked RemoveRange as well as the CASCADE.
            var links = await CalendarLinkService.SeriesLinksAsync(context, id, cancellationToken);
            var now = timeProvider.GetUtcNow().UtcDateTime;
            var detachedIds = state.Rows.Where(r => r.StartDateTime < now).Select(r => r.CalendarEventId).ToList();
            RemoveSeries(context, state, now);
            await CalendarLinkService.StageRemoveForSeriesAsync(context, id, cancellationToken);
            await context.SaveChangesAsync(cancellationToken);
            context.ChangeTracker.Clear();
            await CalendarLinkService.CopyToDetachedChunkedAsync(context, links, detachedIds, cancellationToken);
            return true;
        }, cancellationToken, logger);
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
    /// Stages a series delete on a tracked state: future rows deleted, past rows detached, exclusions and the series
    /// removed. Returns the ids of the deleted rows. Shared with the import's cancelled-series removal.
    /// </summary>
    internal static List<Guid> RemoveSeries(OdysseyContext context, RecurrenceSeriesState state, DateTime nowUtc)
    {
        var removed = new List<Guid>();
        foreach (var row in state.Rows)
        {
            if (row.StartDateTime >= nowUtc)
            {
                removed.Add(row.CalendarEventId);
                context.CalendarEvents.Remove(row);
            }
            else
            {
                RecurrenceSeries.Detach(row);
            }
        }

        context.RecurrenceExclusions.RemoveRange(state.Exclusions);
        context.RecurrencePatterns.Remove(state.Pattern);
        return removed;
    }

    private sealed record PatternTimes(DateTime Start, DateTime End, DateTime? Until, string? ZoneId);

    // The stored times and zone of a write (issue #519 §8.1), by the same rules as an event's; UNTIL is an instant on a
    // zoned series and a wall-clock on a floating one (v3 §C).
    private static PatternTimes ResolveTimes(NewRecurrencePattern request, string? storedZoneId)
    {
        var (start, end, zoneId) = CalendarEventService.ResolveWriteTimes(
            request.StartDateTime, request.EndDateTime, request.IsAllDay, request.TimeZoneId, storedZoneId);
        DateTime? until = null;
        if (request.RecurrenceEndDate is { } requestedUntil)
        {
            until = CalendarTimeZones.Find(zoneId) is { } zone
                ? CalendarEventService.ToStored(requestedUntil, zone, kindIgnored: request.TimeZoneId is not null, "recurrenceEndDate")
                : DateTimeNormalization.NormalizeToUtc(requestedUntil);
        }

        return new PatternTimes(start, end, until, zoneId);
    }

    private static void ApplyRule(RecurrencePattern pattern, NewRecurrencePattern request, PatternTimes times)
    {
        pattern.StartDateTime = times.Start;
        pattern.EndDateTime = times.End;
        pattern.TimeZoneId = times.ZoneId;
        pattern.IsAllDay = request.IsAllDay;
        pattern.Frequency = request.Frequency.Adapt<ContextRecurrenceFrequency>();
        pattern.Interval = request.Interval;
        pattern.DaysOfWeek = request.DaysOfWeek?.Adapt<ContextDaysOfWeekFlags>();
        pattern.DayOfMonth = request.DayOfMonth;
        pattern.MonthOfYear = request.MonthOfYear;
        pattern.RecurrenceEndDate = times.Until;
        pattern.OccurrenceCount = request.OccurrenceCount;
    }

    private static void ValidateFields(NewRecurrencePattern request, PatternTimes times, int maxEventDurationDays)
    {
        CalendarEventService.ValidateTimes(times.Start, times.End, request.IsAllDay, maxEventDurationDays);

        var hasEndDate = request.RecurrenceEndDate is not null;
        var hasCount = request.OccurrenceCount is not null;
        if (hasEndDate == hasCount)
        {
            throw new DomainValidationException("Exactly one of RecurrenceEndDate or OccurrenceCount must be set.");
        }

        if (hasEndDate && times.Until!.Value < times.Start)
        {
            throw new DomainValidationException("RecurrenceEndDate must be on or after StartDateTime.");
        }

        var isWeekly = request.Frequency == DtoRecurrenceFrequency.Weekly;
        var isMonthly = request.Frequency == DtoRecurrenceFrequency.Monthly;
        var isYearly = request.Frequency == DtoRecurrenceFrequency.Yearly;

        // Defense in depth behind the DTO's [EnumDataType] for non-HTTP callers: an undefined bit names
        // no weekday, so a mask with no valid day would never match and the weekly generator would
        // step until DateTime overflows (issue #243).
        if (request.DaysOfWeek is { } days && !days.HasOnlyDefinedDays())
        {
            throw new DomainValidationException("DaysOfWeek contains a value that is not a day of the week.");
        }

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

    /// <summary>The read shape of a series (issue #519 §5): wall-clock and instant, version, exclusion count, keyed.</summary>
    internal static ExistingRecurrencePattern ToDtoShape(RecurrencePattern pattern)
    {
        var dto = pattern.Adapt<ExistingRecurrencePattern>();
        var zoneId = pattern.IsAllDay ? null : pattern.TimeZoneId;
        dto.StartDateTime = CalendarTimeZones.ReadValue(pattern.StartDateTime, zoneId);
        dto.EndDateTime = CalendarTimeZones.ReadValue(pattern.EndDateTime, zoneId);
        dto.RecurrenceEndDate = pattern.RecurrenceEndDate is { } until ? CalendarTimeZones.ReadValue(until, zoneId) : null;
        dto.StartUtc = CalendarTimeZones.InstantOf(pattern.StartDateTime, zoneId);
        dto.EndUtc = CalendarTimeZones.InstantOf(pattern.EndDateTime, zoneId);
        dto.IsKeyed = pattern.KeysBackfilledAt is not null;
        return dto;
    }

    private async Task<ExistingRecurrencePattern> ToDtoAsync(RecurrencePattern pattern, LocationClaims? claims, CancellationToken cancellationToken)
    {
        var page = await ToDtoPageAsync(
            new PagedResult<RecurrencePattern> { Items = [pattern], Offset = 0, Limit = 1, TotalCount = 1 }, claims, cancellationToken);
        await CalendarLinkProjection.FillDetailAsync(context, page.Items[0], cancellationToken);
        return page.Items[0];
    }

    // Counts by one grouped query each, for the whole page (S5): no per-series round trip.
    private async Task<PagedResult<ExistingRecurrencePattern>> ToDtoPageAsync(
        PagedResult<RecurrencePattern> page, LocationClaims? claims, CancellationToken cancellationToken)
    {
        var ids = page.Items.Select(p => p.RecurrencePatternId).ToList();
        var rowCounts = await context.CalendarEvents.AsNoTracking()
            .Where(e => e.RecurrencePatternId != null && ids.Contains(e.RecurrencePatternId.Value))
            .GroupBy(e => e.RecurrencePatternId!.Value)
            .Select(g => new { Id = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Id, g => g.Count, cancellationToken);
        var exclusionCounts = await context.RecurrenceExclusions.AsNoTracking()
            .Where(x => ids.Contains(x.RecurrencePatternId))
            .GroupBy(x => x.RecurrencePatternId)
            .Select(g => new { Id = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Id, g => g.Count, cancellationToken);
        var refs = await LocationRefs.LoadAsync(
            context, page.Items.Select(p => p.LocationId), claims ?? LocationClaims.None, cancellationToken);

        var items = new List<ExistingRecurrencePattern>(page.Items.Count);
        foreach (var pattern in page.Items)
        {
            var dto = ToDtoShape(pattern);
            dto.GeneratedEventCount = rowCounts.GetValueOrDefault(pattern.RecurrencePatternId);
            dto.ExclusionCount = exclusionCounts.GetValueOrDefault(pattern.RecurrencePatternId);
            dto.LocationRef = refs.For(dto.LocationId);
            items.Add(dto);
        }

        await CalendarLinkProjection.FillCountsAsync(context, items, cancellationToken);

        return new PagedResult<ExistingRecurrencePattern>
        {
            Items = items,
            Offset = page.Offset,
            Limit = page.Limit,
            TotalCount = page.TotalCount,
        };
    }
}

using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Core.Pagination;
using Odyssey.Dtos;
using Odyssey.Dtos.Finance;
using ContextPropertyEventType = Odyssey.Context.PropertyEventType;
using DtoPropertyEventType = Odyssey.Dtos.Finance.PropertyEventType;
using ContextEventSource = Odyssey.Context.ContractEventSource;
using DtoEventSource = Odyssey.Dtos.Finance.ContractEventSource;

namespace Odyssey.Core.Finance;

/// <summary>
/// One property event plus the raw id of whoever recorded it. The id travels beside the DTO rather than
/// on it, so the type that reaches the serializer has nowhere to put it — the
/// <see cref="ContractEventRead"/> shape.
/// </summary>
public sealed record PropertyEventRead(ExistingPropertyEvent Event, string? AuthorId);

/// <summary>A page of property events plus the raw author id behind each, keyed by event id.</summary>
public sealed record PropertyEventPage(
    PagedResult<ExistingPropertyEvent> Page,
    IReadOnlyDictionary<Guid, string?> AuthorIds);

/// <summary>
/// The property event log (issue #209): a user-maintained, chronological record of what has happened to
/// one property. Owns validation, the paged projection and the writes; the controller owns claim
/// authorization and turning an author id into a display label.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every read and write is scoped by owner.</b> The rows share the <c>Events</c> table with contract
/// events; <c>context.PropertyEvents</c> is already filtered to the property branch by discriminator, and
/// every query here additionally names the route's property id. A contract event id, or another
/// property's, is therefore indistinguishable from an unknown one: <c>null</c> → <c>404</c>.
/// </para>
/// <para>
/// <b>Not authoritative over anything.</b> A hand-written <see cref="ContextPropertyEventType.Disposed"/>
/// does not set <c>DisposedDate</c>; the system rows are a consequence of the property's fields, recorded
/// by <see cref="PropertyEventRecorder"/> inside <c>PropertyService</c>'s own save, never by this service.
/// </para>
/// <para>
/// The title, blank-field and future-date rules are <see cref="EventFieldRules"/>, shared with the
/// contract log rather than copied.
/// </para>
/// </remarks>
public class PropertyEventService
{
    private readonly OdysseyContext context;
    private readonly TimeProvider timeProvider;

    public PropertyEventService(OdysseyContext context, TimeProvider timeProvider)
    {
        this.context = context;
        this.timeProvider = timeProvider;
    }

    private DateTime UtcNow => timeProvider.GetUtcNow().UtcDateTime;

    /// <summary>
    /// One property's events, paged, searchable and sortable. <see langword="null"/> when the property
    /// does not exist.
    /// </summary>
    public async Task<PropertyEventPage?> ListAsync(
        Guid propertyId,
        PropertyEventsQueryParams query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (!await context.Properties.AnyAsync(p => p.PropertyId == propertyId, cancellationToken))
        {
            return null;
        }

        var q = context.PropertyEvents
            .AsNoTracking()
            .Where(e => e.PropertyId == propertyId);

        var typeFilter = (query.Types ?? [])
            .Select(t => (ContextPropertyEventType)t)
            .Distinct()
            .ToList();
        if (typeFilter.Count > 0)
        {
            q = q.Where(e => typeFilter.Contains(e.Type));
        }

        if (query.Source is { } source)
        {
            var sourceFilter = (ContextEventSource)source;
            q = q.Where(e => e.Source == sourceFilter);
        }

        if (query.From is { } from)
        {
            var fromUtc = EventFieldRules.NormalizeToUtc(from);
            q = q.Where(e => e.OccurredAt >= fromUtc);
        }

        if (query.To is { } to)
        {
            var toUtc = EventFieldRules.NormalizeToUtc(to);
            q = q.Where(e => e.OccurredAt <= toUtc);
        }

        var term = ListQuery.NormalizeSearch(query.Search);
        if (term is not null)
        {
            var pattern = ListQuery.ContainsPattern(term);
            q = q.Where(e =>
                EF.Functions.Like(e.Title, pattern) ||
                (e.Description != null && EF.Functions.Like(e.Description, pattern)) ||
                (e.Notes != null && EF.Functions.Like(e.Notes, pattern)));
        }

        // Dates read newest-first by default (this is a log); the label-ish keys read A-Z. EventId is the
        // tiebreaker, so a page boundary is stable across requests.
        var ascending = ListQuery.Ascending(
            query.SortDir,
            naturalDefaultAscending: query.SortBy is PropertyEventSortBy.Title or PropertyEventSortBy.Type);

        q = query.SortBy switch
        {
            PropertyEventSortBy.Title => ascending
                ? q.OrderBy(e => e.Title).ThenBy(e => e.EventId)
                : q.OrderByDescending(e => e.Title).ThenBy(e => e.EventId),
            PropertyEventSortBy.Type => ascending
                ? q.OrderBy(e => e.Type).ThenBy(e => e.EventId)
                : q.OrderByDescending(e => e.Type).ThenBy(e => e.EventId),
            PropertyEventSortBy.CreatedAtUtc => ascending
                ? q.OrderBy(e => e.CreatedAtUtc).ThenBy(e => e.EventId)
                : q.OrderByDescending(e => e.CreatedAtUtc).ThenBy(e => e.EventId),
            _ => ascending
                ? q.OrderBy(e => e.OccurredAt).ThenBy(e => e.EventId)
                : q.OrderByDescending(e => e.OccurredAt).ThenBy(e => e.EventId),
        };

        // Hand-materialised for the ContractEventService reason: the author id comes out of the same
        // window read as the DTOs, never a second query.
        var totalCount = await q.CountAsync(cancellationToken);
        var (safeOffset, safeLimit) = ListQuery.ResolveWindow(query.Offset, query.Limit);

        var rows = await q
            .Skip(safeOffset)
            .Take(safeLimit)
            .ToListAsync(cancellationToken);

        var page = new PagedResult<ExistingPropertyEvent>
        {
            Items = rows.Select(ToDto).ToList(),
            Offset = safeOffset,
            Limit = safeLimit,
            TotalCount = totalCount,
        };

        return new PropertyEventPage(page, rows.ToDictionary(row => row.EventId, row => row.CreatedByUserId));
    }

    /// <summary>
    /// Adds one hand-written event. <see langword="null"/> when the property does not exist.
    /// </summary>
    /// <param name="userId">The caller, stamped onto <c>CreatedByUserId</c>. Never read from the body.</param>
    /// <exception cref="DomainValidationException">A blank title.</exception>
    /// <exception cref="DomainUnprocessableException">
    /// A future <c>occurredAt</c>, or a type illegal for the property's type or system-only.
    /// </exception>
    public async Task<PropertyEventRead?> CreateAsync(
        Guid propertyId,
        NewPropertyEvent request,
        string? userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var propertyType = await PropertyTypeOf(propertyId, cancellationToken);
        if (propertyType is null)
        {
            return null;
        }

        EnsureWritableType(propertyType.Value, request.Type, keeping: null);

        var entity = new PropertyEvent
        {
            PropertyId = propertyId,
            Type = (ContextPropertyEventType)request.Type,
            // Always User on this path, by construction: NewPropertyEvent carries no source field.
            Source = ContextEventSource.User,
            Title = EventFieldRules.RequireTitle(request.Title),
            Description = EventFieldRules.Blank(request.Description),
            Notes = EventFieldRules.Blank(request.Notes),
            OccurredAt = EventFieldRules.ValidateOccurredAt(request.OccurredAt, UtcNow, asUnprocessable: true),
            CreatedByUserId = string.IsNullOrWhiteSpace(userId) ? null : userId,
            CreatedAtUtc = UtcNow,
        };

        context.PropertyEvents.Add(entity);
        await context.SaveChangesAsync(cancellationToken);

        return new PropertyEventRead(ToDto(entity), entity.CreatedByUserId);
    }

    /// <summary>
    /// Replaces one event <b>in full</b>. <see langword="null"/> when the property or the event does not
    /// exist, or the event is not on that property (another property's, or a contract's).
    /// </summary>
    /// <remarks>
    /// <c>Source</c>, <c>CreatedByUserId</c> and <c>CreatedAtUtc</c> are left alone: an edit is not a
    /// re-authoring, and a <c>System</c> row stays <c>System</c>. A system-only type may be kept on a row
    /// that already carries it — a typo fix on an automatic marker — but never introduced.
    /// </remarks>
    public async Task<PropertyEventRead?> UpdateAsync(
        Guid propertyId,
        Guid eventId,
        UpdatePropertyEvent request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var propertyType = await PropertyTypeOf(propertyId, cancellationToken);
        if (propertyType is null)
        {
            return null;
        }

        var entity = await context.PropertyEvents
            .FirstOrDefaultAsync(e => e.EventId == eventId && e.PropertyId == propertyId, cancellationToken);
        if (entity is null)
        {
            return null;
        }

        EnsureWritableType(propertyType.Value, request.Type, keeping: (DtoPropertyEventType)entity.Type);

        entity.Type = (ContextPropertyEventType)request.Type;
        entity.Title = EventFieldRules.RequireTitle(request.Title);
        entity.Description = EventFieldRules.Blank(request.Description);
        entity.Notes = EventFieldRules.Blank(request.Notes);
        entity.OccurredAt = EventFieldRules.ValidateOccurredAt(request.OccurredAt, UtcNow, asUnprocessable: true);

        await context.SaveChangesAsync(cancellationToken);

        return new PropertyEventRead(ToDto(entity), entity.CreatedByUserId);
    }

    /// <summary>
    /// Removes one event. <see langword="false"/> under the same rules as <see cref="UpdateAsync"/>.
    /// System rows are deletable, as on contracts.
    /// </summary>
    public async Task<bool> DeleteAsync(Guid propertyId, Guid eventId, CancellationToken cancellationToken = default)
    {
        // Tracked Remove rather than ExecuteDeleteAsync, which throws on the EF InMemory provider.
        var entity = await context.PropertyEvents
            .FirstOrDefaultAsync(e => e.EventId == eventId && e.PropertyId == propertyId, cancellationToken);
        if (entity is null)
        {
            return false;
        }

        context.PropertyEvents.Remove(entity);
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task<PropertyType?> PropertyTypeOf(Guid propertyId, CancellationToken cancellationToken) =>
        await context.Properties
            .AsNoTracking()
            .Where(p => p.PropertyId == propertyId)
            .Select(p => (PropertyType?)p.Type)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// The matrix and system-only rules (§8.4). Both are <c>422</c>s keyed <c>type</c>: the body is
    /// well-formed and names a real member — what fails is the combination with the stored property.
    /// Messages carry enum labels only, never a title or a property detail.
    /// </summary>
    private static void EnsureWritableType(
        PropertyType propertyType, DtoPropertyEventType requested, DtoPropertyEventType? keeping)
    {
        if (!PropertyEventTypeMatrix.IsLegal(propertyType, requested))
        {
            throw new DomainUnprocessableException(
                $"Event type '{requested}' is not legal on a {propertyType} property.", EventFieldRules.TypeField);
        }

        if (PropertyEventTypeMatrix.IsSystemOnly(requested) && requested != keeping)
        {
            throw new DomainUnprocessableException(
                $"Event type '{requested}' is recorded by the server and cannot be written by hand.",
                EventFieldRules.TypeField);
        }
    }

    private static ExistingPropertyEvent ToDto(PropertyEvent e) => new()
    {
        PropertyEventId = e.EventId,
        PropertyId = e.PropertyId,
        Type = (DtoPropertyEventType)e.Type,
        Source = (DtoEventSource)e.Source,
        Title = e.Title,
        Description = e.Description,
        Notes = e.Notes,
        OccurredAt = e.OccurredAt,
        CreatedAtUtc = e.CreatedAtUtc,
    };
}

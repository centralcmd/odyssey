using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Core.Pagination;
using Odyssey.Dtos;
using Odyssey.Dtos.Finance;
using ContextContractEventType = Odyssey.Context.ContractEventType;
using ContextContractEventSource = Odyssey.Context.ContractEventSource;
using DtoContractEventType = Odyssey.Dtos.Finance.ContractEventType;
using DtoContractEventSource = Odyssey.Dtos.Finance.ContractEventSource;

namespace Odyssey.Core.Finance;

/// <summary>
/// One event plus the raw id of whoever recorded it. <see cref="ExistingContractEvent"/> deliberately
/// carries a display <i>label</i> and never the id, so the id travels beside the DTO rather than on
/// it — which is what makes leaking it a compile-time impossibility rather than a matter of care: the
/// only type that reaches the serializer has nowhere to put it.
/// </summary>
public sealed record ContractEventRead(ExistingContractEvent Event, string? AuthorId);

/// <summary>
/// A page of events plus the raw author id behind each, keyed by event id. Same reasoning as
/// <see cref="ContractEventRead"/>; both come out of the one read, so the attribution costs no extra
/// round trip.
/// </summary>
public sealed record ContractEventPage(
    PagedResult<ExistingContractEvent> Page,
    IReadOnlyDictionary<Guid, string?> AuthorIds);

/// <summary>
/// The contract event log (issue #138): a user-maintained, chronological record of what has happened
/// to one contract. Owns validation, the paged projection and the writes; the controller owns claim
/// authorization and turning an author id into a display label.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not an audit log, and not authoritative over anything.</b> Since issue #154 the server does record
/// some events itself — but through <c>ContractEventRecorder</c>, staged onto the mutating service's own
/// change tracker, never through this service, which stays the HTTP write path and the reader. Every row
/// remains editable and deletable by any <c>contracts.update</c> holder, and an event never affects the contract's derived
/// <c>ContractStatus</c> — a <see cref="ContextContractEventType.Terminated"/> event leaves an
/// otherwise-active contract <c>Active</c> (issue #138 §4.2).
/// </para>
/// <para>
/// <b>Every read is contract-scoped and paged in SQL</b>, served by the
/// <c>(ContractId, OccurredAt)</c> index. There are no related-entity projections to resolve, so this
/// path carries no <c>N+1</c> risk.
/// </para>
/// <para>
/// <b>Attribution is stamped, never accepted.</b> <c>CreatedByUserId</c> and <c>CreatedAtUtc</c> come
/// from the caller and the server clock on insert, and an update rewrites neither — editing an event
/// must never rewrite who recorded it.
/// </para>
/// </remarks>
public class ContractEventService
{
    /// <summary>
    /// How far ahead of the server clock an <c>occurredAt</c> may sit before it is rejected. An
    /// ordinary "now" from a client whose clock runs slightly fast must not be spuriously refused, and
    /// a minute is far short of anything a user would mean by a future-dated event (issue #138 §8.3).
    /// </summary>
    public static readonly TimeSpan FutureTolerance = TimeSpan.FromSeconds(60);

    private const string OccurredAtField = "occurredAt";
    private const string TitleField = "title";

    private readonly OdysseyContext context;
    private readonly TimeProvider timeProvider;

    public ContractEventService(OdysseyContext context, TimeProvider timeProvider)
    {
        this.context = context;
        this.timeProvider = timeProvider;
    }

    private DateTime UtcNow => timeProvider.GetUtcNow().UtcDateTime;

    /// <summary>
    /// One contract's events, paged, searchable and sortable. Returns <see langword="null"/> when the
    /// contract does not exist, which the controller turns into a <c>404</c>.
    /// </summary>
    /// <remarks>
    /// <c>Search</c> spans <c>Title</c>, <c>Description</c> <b>and</b> <c>Notes</c>. All three are
    /// searched because all three sit behind one claim and are returned to the same callers — omitting
    /// <c>Notes</c> here would make the field harder to find without making it any less readable
    /// (issue #138 §4.1).
    /// </remarks>
    public async Task<ContractEventPage?> ListAsync(
        Guid contractId,
        ContractEventsQueryParams query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (!await context.Contracts.AnyAsync(c => c.ContractId == contractId, cancellationToken))
        {
            return null;
        }

        var q = context.ContractEvents
            .AsNoTracking()
            .Where(e => e.ContractId == contractId);

        var typeFilter = (query.Types ?? [])
            .Select(t => (ContextContractEventType)t)
            .ToList();
        if (typeFilter.Count > 0)
        {
            q = q.Where(e => typeFilter.Contains(e.Type));
        }

        if (query.Source is { } source)
        {
            var sourceFilter = (ContextContractEventSource)source;
            q = q.Where(e => e.Source == sourceFilter);
        }

        if (query.From is { } from)
        {
            var fromUtc = NormalizeToUtc(from);
            q = q.Where(e => e.OccurredAt >= fromUtc);
        }

        if (query.To is { } to)
        {
            var toUtc = NormalizeToUtc(to);
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

        // The two date keys read newest-first by default (this is a log); the two label-ish keys read
        // A-Z. ContractEventId is the tiebreaker, so a page boundary is stable across requests — two
        // events recorded at the same instant would otherwise be free to swap between pages.
        var ascending = ListQuery.Ascending(
            query.SortDir,
            naturalDefaultAscending: query.SortBy is ContractEventSortBy.Title or ContractEventSortBy.Type);

        q = query.SortBy switch
        {
            ContractEventSortBy.Title => ascending
                ? q.OrderBy(e => e.Title).ThenBy(e => e.ContractEventId)
                : q.OrderByDescending(e => e.Title).ThenBy(e => e.ContractEventId),
            ContractEventSortBy.Type => ascending
                ? q.OrderBy(e => e.Type).ThenBy(e => e.ContractEventId)
                : q.OrderByDescending(e => e.Type).ThenBy(e => e.ContractEventId),
            ContractEventSortBy.CreatedAtUtc => ascending
                ? q.OrderBy(e => e.CreatedAtUtc).ThenBy(e => e.ContractEventId)
                : q.OrderByDescending(e => e.CreatedAtUtc).ThenBy(e => e.ContractEventId),
            _ => ascending
                ? q.OrderBy(e => e.OccurredAt).ThenBy(e => e.ContractEventId)
                : q.OrderByDescending(e => e.OccurredAt).ThenBy(e => e.ContractEventId),
        };

        // Hand-materialised rather than ListQuery.ToPagedResultAsync, for one reason: the author id has
        // to come out of the SAME window read as the DTOs — the shared helper's map would either drop
        // it or have to smuggle it out by side effect, and a second query for it would be a per-page
        // round trip the read does not need. The count and window are otherwise exactly the helper's.
        var totalCount = await q.CountAsync(cancellationToken);
        var (safeOffset, safeLimit) = ListQuery.ResolveWindow(query.Offset, query.Limit);

        var rows = await q
            .Skip(safeOffset)
            .Take(safeLimit)
            .Select(e => new EventRow
            {
                ContractEventId = e.ContractEventId,
                ContractId = e.ContractId,
                Type = e.Type,
                Source = e.Source,
                Title = e.Title,
                Description = e.Description,
                Notes = e.Notes,
                OccurredAt = e.OccurredAt,
                CreatedByUserId = e.CreatedByUserId,
                CreatedAtUtc = e.CreatedAtUtc,
            })
            .ToListAsync(cancellationToken);

        var page = new PagedResult<ExistingContractEvent>
        {
            Items = rows.Select(ToDto).ToList(),
            Offset = safeOffset,
            Limit = safeLimit,
            TotalCount = totalCount,
        };

        return new ContractEventPage(
            page,
            rows.ToDictionary(row => row.ContractEventId, row => row.CreatedByUserId));
    }

    /// <summary>
    /// Adds one event to the contract. Returns <see langword="null"/> when the contract does not exist.
    /// </summary>
    /// <param name="userId">
    /// The caller, stamped onto <c>CreatedByUserId</c>. Never read from the request body.
    /// </param>
    public async Task<ContractEventRead?> CreateAsync(
        Guid contractId,
        NewContractEvent request,
        string? userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!await context.Contracts.AnyAsync(c => c.ContractId == contractId, cancellationToken))
        {
            return null;
        }

        var entity = new ContractEvent
        {
            ContractId = contractId,
            Type = (ContextContractEventType)request.Type,
            // Always User on this path, by CONSTRUCTION rather than by a check: NewContractEvent carries
            // no source field, so there is no request body that produces a System row (issue #154 §7.4).
            Source = ContextContractEventSource.User,
            Title = RequireTitle(request.Title),
            Description = Blank(request.Description),
            Notes = Blank(request.Notes),
            OccurredAt = ValidateOccurredAt(request.OccurredAt),
            CreatedByUserId = string.IsNullOrWhiteSpace(userId) ? null : userId,
            CreatedAtUtc = UtcNow,
        };

        context.ContractEvents.Add(entity);
        await context.SaveChangesAsync(cancellationToken);

        return new ContractEventRead(ToDto(entity), entity.CreatedByUserId);
    }

    /// <summary>
    /// Replaces one event <b>in full</b>: an omitted <c>description</c> or <c>notes</c> clears it and an
    /// omitted <c>type</c> resets it to <see cref="ContextContractEventType.Other"/>. Returns
    /// <see langword="null"/> when the contract or the event does not exist, or the event is not on
    /// that contract.
    /// </summary>
    /// <remarks>
    /// <c>CreatedByUserId</c> and <c>CreatedAtUtc</c> are deliberately left alone. They are what the row
    /// records about its own provenance, and an edit is not a re-authoring.
    /// </remarks>
    public async Task<ContractEventRead?> UpdateAsync(
        Guid contractId,
        Guid eventId,
        UpdateContractEvent request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var entity = await context.ContractEvents
            .FirstOrDefaultAsync(e => e.ContractEventId == eventId && e.ContractId == contractId, cancellationToken);
        if (entity is null)
        {
            return null;
        }

        // Source is deliberately NOT part of the replacement (issue #154 §5.3). It records how the row
        // came to exist, which an edit does not change — flipping an edited System row to User would
        // make the log's one provenance signal depend on whether anyone had since fixed a typo, and
        // would erase the fact that the transition really did occur.
        entity.Type = (ContextContractEventType)request.Type;
        entity.Title = RequireTitle(request.Title);
        entity.Description = Blank(request.Description);
        entity.Notes = Blank(request.Notes);
        entity.OccurredAt = ValidateOccurredAt(request.OccurredAt);

        await context.SaveChangesAsync(cancellationToken);

        return new ContractEventRead(ToDto(entity), entity.CreatedByUserId);
    }

    /// <summary>
    /// Removes one event from the contract. <see langword="false"/> when the contract or the event does
    /// not exist, or the event is not on that contract.
    /// </summary>
    public async Task<bool> DeleteAsync(
        Guid contractId, Guid eventId, CancellationToken cancellationToken = default)
    {
        // Tracked Remove rather than ExecuteDeleteAsync: the latter lives in the relational package and
        // throws on the EF InMemory provider the fast test tiers run on.
        var entity = await context.ContractEvents
            .FirstOrDefaultAsync(e => e.ContractEventId == eventId && e.ContractId == contractId, cancellationToken);
        if (entity is null)
        {
            return false;
        }

        context.ContractEvents.Remove(entity);
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static ExistingContractEvent ToDto(ContractEvent e) => new()
    {
        ContractEventId = e.ContractEventId,
        ContractId = e.ContractId,
        Type = (DtoContractEventType)e.Type,
        Source = (DtoContractEventSource)e.Source,
        Title = e.Title,
        Description = e.Description,
        Notes = e.Notes,
        OccurredAt = e.OccurredAt,
        CreatedAtUtc = e.CreatedAtUtc,
    };

    private static ExistingContractEvent ToDto(EventRow row) => new()
    {
        ContractEventId = row.ContractEventId,
        ContractId = row.ContractId,
        Type = (DtoContractEventType)row.Type,
        Source = (DtoContractEventSource)row.Source,
        Title = row.Title,
        Description = row.Description,
        Notes = row.Notes,
        OccurredAt = row.OccurredAt,
        CreatedAtUtc = row.CreatedAtUtc,
    };

    /// <summary>
    /// The "not in the future" bound. It is the server clock — runtime state, not a compile-time
    /// constant — so it belongs here and not in a <c>[Range]</c> attribute.
    /// </summary>
    private DateTime ValidateOccurredAt(DateTime occurredAt)
    {
        var utc = NormalizeToUtc(occurredAt);
        if (utc > UtcNow + FutureTolerance)
        {
            throw new DomainValidationException(
                "An event cannot have occurred in the future.", code: null, field: OccurredAtField);
        }

        return utc;
    }

    /// <summary>
    /// <c>[StringLength(MinimumLength = 1)]</c> accepts a string of spaces, so the whitespace-only case
    /// is rejected here — as empty, which is what it is.
    /// </summary>
    private static string RequireTitle(string? title)
    {
        var trimmed = title?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            throw new DomainValidationException("Title is required.", code: null, field: TitleField);
        }

        return trimmed;
    }

    /// <summary>A blank optional field is stored as absent, not as a string of spaces.</summary>
    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DateTime NormalizeToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    /// <summary>
    /// The SQL-side row shape: the DTO's fields plus the author id the DTO does not carry. Private, so
    /// nothing outside this service can hand it to a serializer.
    /// </summary>
    private sealed class EventRow
    {
        public required Guid ContractEventId { get; init; }
        public required Guid ContractId { get; init; }
        public required ContextContractEventType Type { get; init; }
        public required ContextContractEventSource Source { get; init; }
        public required string Title { get; init; }
        public string? Description { get; init; }
        public string? Notes { get; init; }
        public required DateTime OccurredAt { get; init; }
        public string? CreatedByUserId { get; init; }
        public required DateTime CreatedAtUtc { get; init; }
    }
}

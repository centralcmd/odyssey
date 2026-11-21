using Odyssey.Core;
using Odyssey.Core.Finance;
using System.Data;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Odyssey.Context;
using Odyssey.Dtos.Journal;
using Odyssey.Core.Journal.Interop;
using Odyssey.Core.Locations;
using Odyssey.Core.Pagination;
using Odyssey.Dtos;
using Mapster;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Odyssey.Core.Journal;

public partial class ContactService
{
    private static readonly Regex MultiWhitespaceRegex = new("\\s+", RegexOptions.Compiled);
    private static readonly Regex CountryCodeRegex = new("^[A-Za-z]{2}$", RegexOptions.Compiled);

    // The resolved display value as an EF-translatable sort key (DisplayName override, else the
    // type-appropriate fallback). Kept in sync with ContactNaming.Resolve.
    private static readonly System.Linq.Expressions.Expression<Func<Contact, string>> ResolvedNameKey =
        c => c.DisplayName != null && c.DisplayName != ""
            ? c.DisplayName
            : c.Type == ContactType.Person
                ? (c.PersonDetails!.FirstName + " " + c.PersonDetails.LastName)
                : c.OrganizationDetails!.LegalName;

    private readonly OdysseyContext context;
    private readonly IContactReferenceGuard referenceGuard;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<ContactService>? logger;

    private readonly Odyssey.Core.FileStorage.IObjectStore? objectStore;

    public ContactService(
        OdysseyContext context,
        IContactReferenceGuard referenceGuard,
        TimeProvider? timeProvider = null,
        ILogger<ContactService>? logger = null,
        Odyssey.Core.FileStorage.IObjectStore? objectStore = null)
    {
        this.context = context;
        this.referenceGuard = referenceGuard;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.logger = logger;
        this.objectStore = objectStore;
    }

    // The base read query, eagerly loading the 1:1 detail sub-records and the three contact
    // collections so the list rows and the GET /{id} detail render without extra round-trips
    // (§7). AsSplitQuery avoids a cartesian blow-up across the three collections.
    private IQueryable<Contact> ReadQuery() => context.Contacts
        .AsNoTracking()
        .Include(c => c.PersonDetails)
        .Include(c => c.OrganizationDetails)
        .Include(c => c.Aliases)
        .Include(c => c.Addresses).ThenInclude(a => a.Location)
        .Include(c => c.EmailAddresses)
        .Include(c => c.PhoneNumbers)
        .AsSplitQuery();

    /// <summary>Server-side paged list (issue #277): search + type/status filters + allowlisted sort.</summary>
    public async Task<PagedResult<ExistingContact>> ListAsync(
        ContactsQueryParams query,
        CancellationToken cancellationToken = default,
        LocationClaims? claims = null)
    {
        var q = ApplyFilters(ReadQuery(), query);

        var ascending = ListQuery.Ascending(query.SortDir, naturalDefaultAscending: true);
        var sorted = query.SortBy switch
        {
            ContactSortBy.Type => ascending ? q.OrderBy(c => c.Type) : q.OrderByDescending(c => c.Type),
            ContactSortBy.Status => ascending ? q.OrderBy(c => c.Archived != null) : q.OrderByDescending(c => c.Archived != null),
            ContactSortBy.NormalizedName => ascending ? q.OrderBy(c => c.NormalizedName) : q.OrderByDescending(c => c.NormalizedName),
            // Name (and the natural default) sort by the resolved display value, not the uppercased key.
            _ => ascending ? q.OrderBy(ResolvedNameKey) : q.OrderByDescending(ResolvedNameKey),
        };
        q = sorted.ThenBy(c => c.ContactId);

        var page = await q.ToPagedResultAsync(query.Offset, query.Limit, MapWithOrderedContacts, cancellationToken);
        await FillOtherUsesAsync(page.Items.SelectMany(c => c.Addresses).ToList(), claims, cancellationToken);
        return page;
    }

    /// <summary>
    /// Unpaginated read matching the same search/type/status filters as <see cref="ListAsync"/>. Throws
    /// <see cref="DomainValidationException"/> if the matched set exceeds <paramref name="maxRows"/> before any
    /// row is materialized. It once backed the vCard collection export, which now streams
    /// (<c>ContactVCardService.ExportManyStreamingAsync</c>); tests keep it as a lookup helper, hence internal.
    /// </summary>
    internal async Task<IReadOnlyList<ExistingContact>> ListAllMatching(
        ContactsQueryParams query, int maxRows, CancellationToken cancellationToken = default)
    {
        var q = ApplyFilters(ReadQuery(), query);

        var count = await q.CountAsync(cancellationToken);
        if (count > maxRows)
        {
            throw new DomainValidationException(
                $"{count} contacts match the current filters, which exceeds the maximum of {maxRows} exportable at once. Narrow your filters and try again.");
        }

        var rows = await q.OrderBy(c => c.ContactId).ToListAsync(cancellationToken);
        return rows.Select(MapWithOrderedContacts).ToList();
    }

    /// <summary>
    /// The coordinates (and time zone, when set) of the places the given address rows link to, keyed by
    /// address id — only places that carry both coordinates. Core-internal: read only by the vCard export,
    /// which writes them beside the address under <c>contacts.read</c>.
    /// </summary>
    internal async Task<IReadOnlyDictionary<Guid, AddressGeocode>> GetAddressGeocodesAsync(
        IReadOnlyCollection<Guid> addressIds, CancellationToken cancellationToken = default)
    {
        if (addressIds.Count == 0)
        {
            return new Dictionary<Guid, AddressGeocode>();
        }

        var rows = await context.Addresses
            .AsNoTracking()
            .Where(a => addressIds.Contains(a.Id) && a.Location.Latitude != null && a.Location.Longitude != null)
            .Select(a => new { a.Id, a.Location.Latitude, a.Location.Longitude, a.Location.TimeZoneId })
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(r => r.Id, r => new AddressGeocode(r.Latitude!.Value, r.Longitude!.Value, r.TimeZoneId));
    }

    /// <summary>
    /// Row count matching the same filters as <see cref="ListAsync"/> — the Goal 8 pre-fetch cap check
    /// and the <c>X-Odyssey-Export-Rows</c> completeness-header count (issue #343 §5/§11), computed
    /// before any row is fetched.
    /// </summary>
    public async Task<int> CountMatchingAsync(ContactsQueryParams query, CancellationToken cancellationToken = default) =>
        await ApplyFilters(ReadQuery(), query).CountAsync(cancellationToken);

    /// <summary>
    /// Streams the matched set in <c>ContactId</c>-ordered chunks of at most <paramref name="chunkSize"/>
    /// rows each (issue #343 §5 Goal 8) — peak memory is proportional to <paramref name="chunkSize"/>,
    /// not the matched row count, because each chunk is mapped, yielded, and released before the next is
    /// fetched. Callers are expected to have already cap-checked via <see cref="CountMatchingAsync"/> —
    /// this method itself never throws on row count.
    /// </summary>
    /// <remarks>
    /// A PR #403 review fix replaced this method's original design — the whole chunked fetch inside one
    /// explicit <see cref="IsolationLevel.RepeatableRead"/> transaction — because <c>OdysseyContext</c>
    /// enables <c>EnableRetryOnFailure()</c> in production, which forbids a bare
    /// <c>Database.BeginTransactionAsync</c> unless the ENTIRE unit of work (begin, every query, commit)
    /// runs inside one <c>CreateExecutionStrategy().ExecuteAsync</c> call — verified against real MariaDB,
    /// not just from the docs. That doesn't compose with a chunked read that yields output as it goes (a
    /// retry would re-emit chunks already streamed to the client), so this no longer opens a transaction
    /// at all: the ordered <c>ContactId</c> set is captured in one cheap up-front read (itself a plain,
    /// safely-retryable query), and each chunk is then fetched independently by a fixed id batch — see
    /// <see cref="ExportChunking.ReorderToSnapshot{T}"/> for the consistency trade-off this makes.
    /// </remarks>
    public async IAsyncEnumerable<IReadOnlyList<ExistingContact>> StreamMatchingChunksAsync(
        ContactsQueryParams query, int chunkSize, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var baseQuery = ApplyFilters(ReadQuery(), query);

        var orderedIds = await baseQuery
            .OrderBy(c => c.ContactId)
            .Select(c => c.ContactId)
            .ToListAsync(cancellationToken);

        foreach (var idBatch in orderedIds.Chunk(chunkSize))
        {
            var rows = await baseQuery.Where(c => idBatch.Contains(c.ContactId)).ToListAsync(cancellationToken);
            var ordered = ExportChunking.ReorderToSnapshot(idBatch, rows, c => c.ContactId);
            if (ordered.Count == 0)
            {
                continue; // every id in this batch was deleted between the snapshot and this fetch
            }

            yield return ordered.Select(MapWithOrderedContacts).ToList();
        }
    }

    private static IQueryable<Contact> ApplyFilters(IQueryable<Contact> q, ContactsQueryParams query)
    {
        var term = ListQuery.NormalizeSearch(query.Search);
        if (term is not null)
        {
            var pattern = ListQuery.ContainsPattern(term);
            var normalizedPattern = ListQuery.ContainsPattern(ContactNaming.Normalize(term));
            // The alias and middle-name arms use the RAW pattern — un-uppercased, but still escaped by
            // ListQuery.ContainsPattern, so there is no LIKE-wildcard injection. Case-insensitivity
            // comes from the column's _ci collation, which means both arms are case-SENSITIVE on the
            // EF InMemory provider: the coverage for them lives in Odyssey.IntegrationTests (issue #48
            // §5). The alias LABEL is deliberately not searched (Goal 3), so the placeholder promises
            // only name, alias and notes.
            q = q.Where(c =>
                EF.Functions.Like(c.NormalizedName, normalizedPattern) ||
                (c.DisplayName != null && EF.Functions.Like(c.DisplayName, pattern)) ||
                (c.Notes != null && EF.Functions.Like(c.Notes, pattern)) ||
                c.Aliases.Any(a => EF.Functions.Like(a.Value, pattern)) ||
                (c.PersonDetails != null && c.PersonDetails.MiddleName != null
                    && EF.Functions.Like(c.PersonDetails.MiddleName, pattern)));
        }

        if (query.Types is { Length: > 0 } types)
        {
            q = q.Where(c => types.Contains(c.Type));
        }

        return query.Status switch
        {
            ArchivalStatus.Archived => q.Where(c => c.Archived != null),
            ArchivalStatus.Active => q.Where(c => c.Archived == null),
            _ => q,
        };
    }

    public async Task<ExistingContact?> Get(
        Guid contactId, CancellationToken cancellationToken = default, LocationClaims? claims = null)
    {
        var contact = await ReadQuery()
            .FirstOrDefaultAsync(value => value.ContactId == contactId, cancellationToken);
        if (contact is null)
        {
            return null;
        }

        var dto = MapWithOrderedContacts(contact);
        await FillOtherUsesAsync(dto.Addresses, claims, cancellationToken);
        return dto;
    }

    // EF can't order an Include, so the inline contact collections are sorted after materialisation to
    // match the primary-first order the dedicated GET .../addresses|emails|phones endpoints return.
    private static ExistingContact MapWithOrderedContacts(Contact contact)
    {
        var dto = contact.Adapt<ExistingContact>();
        // Aliases have no primary, so they sort by value then id — the same order the dedicated
        // GET .../aliases returns, modulo collation: a collation-ordered and an OrdinalIgnoreCase-
        // ordered list can differ on accented input, which is accepted (issue #48 §6).
        dto.Aliases = [.. dto.Aliases.OrderBy(a => a.Value, StringComparer.OrdinalIgnoreCase).ThenBy(a => a.Id)];
        dto.Addresses = [.. dto.Addresses.OrderByDescending(a => a.IsPrimary).ThenBy(a => a.Id)];
        dto.EmailAddresses = [.. dto.EmailAddresses.OrderByDescending(e => e.IsPrimary).ThenBy(e => e.Id)];
        dto.PhoneNumbers = [.. dto.PhoneNumbers.OrderByDescending(p => p.IsPrimary).ThenBy(p => p.Id)];
        return dto;
    }

}

/// <summary>A completed conversion: the converted contact and the type it was converted from.</summary>
public sealed record ContactConversionResult(ExistingContact Contact, ContactType PreviousType);

/// <summary>A linked place's coordinates and time zone, for the vCard export. Redacted: it is where someone lives.</summary>
internal sealed record AddressGeocode(decimal Latitude, decimal Longitude, string? TimeZoneId)
{
    public override string ToString() => nameof(AddressGeocode) + " { (redacted) }";
}

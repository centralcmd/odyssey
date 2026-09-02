using Odyssey.Core;
using Odyssey.Core.Finance;
using System.Data;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Odyssey.Context;
using Odyssey.Dtos.Journal;
// Aliased rather than a plain using: Odyssey.Dtos.Finance also declares ArchivalStatus.
using DetachedInsuranceLinks = Odyssey.Dtos.Finance.DetachedInsuranceLinks;
using Odyssey.Core.Journal.Interop;
using Odyssey.Core.Pagination;
using Odyssey.Dtos;
using Mapster;
using Microsoft.EntityFrameworkCore;

namespace Odyssey.Core.Journal;

public class ContactService
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

    public ContactService(
        OdysseyContext context,
        IContactReferenceGuard referenceGuard,
        TimeProvider? timeProvider = null)
    {
        this.context = context;
        this.referenceGuard = referenceGuard;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    // The base read query, eagerly loading the 1:1 detail sub-records and the three contact
    // collections so the list rows and the GET /{id} detail render without extra round-trips
    // (§7). AsSplitQuery avoids a cartesian blow-up across the three collections.
    private IQueryable<Contact> ReadQuery() => context.Contacts
        .AsNoTracking()
        .Include(c => c.PersonDetails)
        .Include(c => c.OrganizationDetails)
        .Include(c => c.Addresses)
        .Include(c => c.EmailAddresses)
        .Include(c => c.PhoneNumbers)
        .AsSplitQuery();

    /// <summary>Server-side paged list (issue #277): search + type/status filters + allowlisted sort.</summary>
    public async Task<PagedResult<ExistingContact>> ListAsync(
        ContactsQueryParams query,
        CancellationToken cancellationToken = default)
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

        return await q.ToPagedResultAsync(query.Offset, query.Limit, MapWithOrderedContacts, cancellationToken);
    }

    /// <summary>
    /// Unpaginated read matching the same search/type/status filters as <see cref="ListAsync"/> (issue
    /// #338 §5, §7) — backs vCard export's "all"/"filtered" collection endpoint, which ignores paging.
    /// Throws <see cref="DomainValidationException"/> (→ 400) if the matched set exceeds
    /// <paramref name="maxRows"/> before any row is materialized.
    /// </summary>
    public async Task<IReadOnlyList<ExistingContact>> ListAllMatching(
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
            q = q.Where(c =>
                EF.Functions.Like(c.NormalizedName, normalizedPattern) ||
                (c.DisplayName != null && EF.Functions.Like(c.DisplayName, pattern)) ||
                (c.Notes != null && EF.Functions.Like(c.Notes, pattern)));
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

    public async Task<ExistingContact?> Get(Guid contactId, CancellationToken cancellationToken = default)
    {
        var contact = await ReadQuery()
            .FirstOrDefaultAsync(value => value.ContactId == contactId, cancellationToken);

        return contact is null ? null : MapWithOrderedContacts(contact);
    }

    // EF can't order an Include, so the inline contact collections are sorted after materialisation to
    // match the primary-first order the dedicated GET .../addresses|emails|phones endpoints return.
    private static ExistingContact MapWithOrderedContacts(Contact contact)
    {
        var dto = contact.Adapt<ExistingContact>();
        dto.Addresses = [.. dto.Addresses.OrderByDescending(a => a.IsPrimary).ThenBy(a => a.Id)];
        dto.EmailAddresses = [.. dto.EmailAddresses.OrderByDescending(e => e.IsPrimary).ThenBy(e => e.Id)];
        dto.PhoneNumbers = [.. dto.PhoneNumbers.OrderByDescending(p => p.IsPrimary).ThenBy(p => p.Id)];
        return dto;
    }

    public async Task<ExistingContact> Create(NewContact newContact, CancellationToken cancellationToken = default)
    {
        ValidateShape(newContact);

        var externalUid = await ResolveExternalUid(newContact.ExternalUid, excludingContactId: null, cancellationToken);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var contact = new Contact
        {
            NormalizedName = string.Empty,
            Type = newContact.Type,
            ExternalUid = externalUid,
            CreatedAt = now,
            UpdatedAt = now,
            Archived = newContact.Archived ? now : null,
        };

        ApplyBaseAndDetails(contact, newContact);

        context.Contacts.Add(contact);
        await context.SaveChangesAsync(cancellationToken);

        return (await Get(contact.ContactId, cancellationToken))!;
    }

    public async Task<ExistingContact?> Update(Guid id, NewContact putContact, CancellationToken cancellationToken = default)
    {
        var contact = await context.Contacts
            .Include(c => c.PersonDetails)
            .Include(c => c.OrganizationDetails)
            .FirstOrDefaultAsync(value => value.ContactId == id, cancellationToken);

        if (contact is null)
        {
            return null;
        }

        ValidateShape(putContact);

        if (putContact.ExternalUid is not null)
        {
            contact.ExternalUid = await ResolveExternalUid(putContact.ExternalUid, excludingContactId: id, cancellationToken);
        }

        contact.Type = putContact.Type;
        ApplyBaseAndDetails(contact, putContact);
        ApplyArchiveTransition(contact, putContact.Archived);
        contact.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;

        await context.SaveChangesAsync(cancellationToken);

        return await Get(id, cancellationToken);
    }

    /// <summary>
    /// Looks up a contact by its <see cref="Contact.ExternalUid"/> (issue #338 §9) — backs
    /// vCard import's create-vs-update resolution (a vCard <c>UID</c> matching an existing row's
    /// <c>ExternalUid</c> means "update this row", otherwise "create new").
    /// </summary>
    public async Task<Guid?> FindIdByExternalUid(string externalUid, CancellationToken cancellationToken = default) =>
        await context.Contacts.AsNoTracking()
            .Where(c => c.ExternalUid == externalUid)
            .Select(c => (Guid?)c.ContactId)
            .FirstOrDefaultAsync(cancellationToken);

    // Generates a fresh urn:uuid ExternalUid when the caller didn't supply one; otherwise validates the
    // supplied value isn't already claimed by a *different* contact (issue #338 §6) — a 400 on the
    // direct API create/update path, a per-entry import skip when the caller is ContactVCardService.
    private async Task<string> ResolveExternalUid(string? requested, Guid? excludingContactId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            return $"urn:uuid:{Guid.NewGuid()}";
        }

        var trimmed = requested.Trim();
        if (trimmed.Length > 255)
        {
            throw new DomainValidationException("External ID cannot exceed 255 characters.");
        }

        if (trimmed.Any(char.IsControl))
        {
            throw new DomainValidationException("External ID cannot contain control characters.");
        }

        var owner = await context.Contacts.AsNoTracking()
            .Where(c => c.ExternalUid == trimmed)
            .Select(c => (Guid?)c.ContactId)
            .FirstOrDefaultAsync(cancellationToken);

        if (owner is { } existingOwnerId && existingOwnerId != excludingContactId)
        {
            throw new DomainValidationException("External ID is already in use by another contact.");
        }

        return trimmed;
    }

    /// <summary>
    /// Deletes a contact, applying the Finance-side on-delete behaviours first.
    ///
    /// <para>
    /// With <paramref name="detachInsuranceLinks"/> the contact's insurer, insured-contact and
    /// beneficiary link rows are removed <b>in the same transaction</b> instead of blocking the delete
    /// — the supported release valve for an erasure request (issue #27 §7 #6, §10 #5). Without it a
    /// contact named on any policy is refused, which is the deliberate default: a beneficiary
    /// designation vanishing silently on contact deletion would lose it without trace.
    /// </para>
    /// </summary>
    /// <returns>What was detached, or null when nothing was (including when the contact did not exist).</returns>
    public async Task<DetachedInsuranceLinks?> Delete(
        Guid id, bool detachInsuranceLinks = false, CancellationToken cancellationToken = default)
    {
        var contact = await context.Contacts
            .FirstOrDefaultAsync(value => value.ContactId == id, cancellationToken);
        if (contact is null)
        {
            return null;
        }

        // The finance references to a contact are real FKs again now that both halves share one context,
        // so the database would apply the SET NULL / CASCADE / RESTRICT behaviours on its own. The
        // application-level guard below is kept in front of them for two reasons: it turns the insurance
        // RESTRICT into a 409 with an explanation rather than a raw FK violation surfacing as a 500, and
        // it is the only implementation of those behaviours under the EF InMemory provider the fast test
        // tiers run on.
        //
        // There is no advisory lock any more (issue #27 §5). The three link tables carry real Restrict
        // FKs, so the DATABASE arbitrates the check-and-write race the lock was written for, and the
        // residual loser surfaces as a 409 rather than a 500. The lock's own counterparty — the
        // insurance write path — no longer takes it, which left it a mutex with nothing to contend with,
        // holding a pinned connection and a 10-second timeout per delete.
        DetachedInsuranceLinks? detached = null;

        await ExecuteAtomicallyAsync(async () =>
        {
            if (detachInsuranceLinks)
            {
                // Staged onto this context, not saved: the detach and the delete must commit together,
                // or an interruption leaves the links gone and the contact still present. Tracked
                // RemoveRange, never ExecuteDelete — that throws on the InMemory provider.
                detached = await referenceGuard.StageInsuranceLinkDetachAsync(id, cancellationToken);
            }
            else if (await referenceGuard.IsReferencedByInsuranceAsync(id, cancellationToken))
            {
                // Restrict: a contact named as an insurer, an insured contact or a beneficiary blocks
                // the delete. The controller re-checks first and shapes a claim-conditional payload;
                // this stays as defence-in-depth for direct (non-HTTP) callers.
                throw new DomainConflictException(
                    "This contact is named on one or more insurance policies and cannot be deleted. "
                    + "Detach its insurance links, or remove it from those policies first.");
            }

            // Clear/cascade the Finance-side references (SetNull + contract-party Cascade), then delete
            // the contact (its Person/Org/address/email/phone children and the journal/photo link rows
            // cascade in-context). All of it inside the transaction: widening the guard from one probe
            // to three widened the window this used to leave open, and the six ExecuteUpdate/
            // ExecuteDelete statements in the cleanup were the genuinely non-atomic part all along.
            await referenceGuard.ClearAndCascadeReferencesAsync(id, cancellationToken);

            context.Contacts.Remove(contact);
            await context.SaveChangesAsync(cancellationToken);
        });

        return detached;
    }

    /// <summary>
    /// Runs <paramref name="work"/> in one database transaction. Wrapped in the context's execution
    /// strategy because <c>AddDatabases</c> enables retry-on-failure, and a retrying strategy refuses an
    /// ambient transaction it did not open itself (a bare <c>BeginTransactionAsync</c> throws).
    /// Follows <c>UserAdministrationService.ExecuteAtomicallyAsync</c>.
    ///
    /// <para>
    /// The EF InMemory provider honours neither transactions nor the execution strategy, so it runs the
    /// work directly — which is correct rather than a compromise: there is nothing there to be atomic
    /// against, and <c>BeginTransactionAsync</c> would warn. The real coverage lives in
    /// <c>Odyssey.IntegrationTests</c>.
    /// </para>
    /// </summary>
    private async Task ExecuteAtomicallyAsync(Func<Task> work)
    {
        if (!context.Database.IsRelational())
        {
            await work();
            return;
        }

        var strategy = context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await context.Database.BeginTransactionAsync();
            await work();
            await transaction.CommitAsync();
        });
    }

    // ── Base + details mapping ────────────────────────────────────────────────

    // Cross-field shape check (§9), mirroring NewContact.Validate for direct (non-HTTP) callers
    // that bypass [ApiController] model validation.
    private static void ValidateShape(NewContact source)
    {
        var personExpected = source.Type == ContactType.Person;
        if (personExpected && (source.PersonDetails is null || source.OrganizationDetails is not null))
            throw new DomainValidationException("A Person contact requires person details and no organization details.");
        if (!personExpected && (source.OrganizationDetails is null || source.PersonDetails is not null))
            throw new DomainValidationException("An Organization contact requires organization details and no person details.");
    }

    private void ApplyBaseAndDetails(Contact contact, NewContact source)
    {
        contact.DisplayName = CleanOptional(source.DisplayName, 128, nameof(source.DisplayName));
        contact.Notes = CleanOptional(source.Notes, 1024, nameof(source.Notes));

        if (source.Type == ContactType.Person)
        {
            // Drop an organization sub-record left over from a type change.
            if (contact.OrganizationDetails is not null)
            {
                context.Remove(contact.OrganizationDetails);
                contact.OrganizationDetails = null;
            }
            // Clear the deprecated base column so it can't retain a stale value after Org -> Person.
            contact.OrganizationNumber = null;

            var details = source.PersonDetails!;
            var dob = ValidateDateOfBirth(details.DateOfBirth);
            contact.PersonDetails ??= new PersonDetails { FirstName = string.Empty, LastName = string.Empty };
            contact.PersonDetails.FirstName = CleanRequired(details.FirstName, 128, "First name");
            contact.PersonDetails.LastName = CleanRequired(details.LastName, 128, "Last name");
            contact.PersonDetails.DateOfBirth = dob;
            contact.PersonDetails.RelationshipType = details.RelationshipType;
            contact.PersonDetails.Sex = details.Sex;
            contact.PersonDetails.Title = CleanOptional(details.Title, 128, "Title");
            contact.PersonDetails.Company = CleanOptional(details.Company, 256, "Company");
        }
        else
        {
            if (contact.PersonDetails is not null)
            {
                context.Remove(contact.PersonDetails);
                contact.PersonDetails = null;
            }

            var details = source.OrganizationDetails!;
            contact.OrganizationDetails ??= new OrganizationDetails { LegalName = string.Empty };
            contact.OrganizationDetails.LegalName = CleanRequired(details.LegalName, 256, "Legal name");
            contact.OrganizationDetails.OrganizationNumber = CleanOptional(details.OrganizationNumber, 64, "Organization number");
            contact.OrganizationDetails.Website = ValidateWebsite(details.Website);
            // Keep the deprecated base column in sync while it is retained (§15).
            contact.OrganizationNumber = contact.OrganizationDetails.OrganizationNumber;
        }

        contact.NormalizedName = ContactNaming.Normalize(ContactNaming.Resolve(contact));
    }

    private DateOnly? ValidateDateOfBirth(DateTime? dateOfBirth)
    {
        if (dateOfBirth is null)
            return null;

        var value = DateOnly.FromDateTime(dateOfBirth.Value);
        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        if (value > today)
            throw new DomainValidationException("Date of birth cannot be in the future.");

        return value;
    }

    private static string? ValidateWebsite(string? website)
    {
        var cleaned = CleanOptional(website, 2048, "Website");
        if (cleaned is null)
            return null;

        if (!Uri.TryCreate(cleaned, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new DomainValidationException("Website must be an absolute http:// or https:// URL.");

        return cleaned;
    }

    private static string CleanRequired(string value, int maxLength, string label)
    {
        var cleaned = MultiWhitespaceRegex.Replace((value ?? string.Empty).Trim(), " ");
        if (cleaned.Length is < 1)
            throw new DomainValidationException($"{label} is required.");
        if (cleaned.Length > maxLength)
            throw new DomainValidationException($"{label} cannot exceed {maxLength} characters.");
        if (cleaned.Any(char.IsControl))
            throw new DomainValidationException($"{label} cannot contain control characters.");
        return cleaned;
    }

    private static string? CleanOptional(string? value, int maxLength, string label)
    {
        if (value is null)
            return null;
        var cleaned = value.Trim();
        if (cleaned.Length == 0)
            return null;
        if (cleaned.Length > maxLength)
            throw new DomainValidationException($"{label} cannot exceed {maxLength} characters.");
        if (cleaned.Any(char.IsControl))
            throw new DomainValidationException($"{label} cannot contain control characters.");
        return cleaned;
    }

    private void ApplyArchiveTransition(Contact contact, bool requestedArchived)
    {
        var currentArchived = contact.Archived is not null;
        if (!currentArchived && requestedArchived)
            contact.Archived = timeProvider.GetUtcNow().UtcDateTime;
        else if (currentArchived && !requestedArchived)
            contact.Archived = null;
    }

    // ── Address sub-resource ──────────────────────────────────────────────────

    public async Task<IReadOnlyList<ExistingAddress>?> GetAddresses(Guid contactId, CancellationToken cancellationToken = default)
    {
        if (!await context.Contacts.AnyAsync(c => c.ContactId == contactId, cancellationToken))
            return null;

        var rows = await context.Addresses.AsNoTracking()
            .Where(a => a.ContactId == contactId)
            .OrderByDescending(a => a.IsPrimary).ThenBy(a => a.Id)
            .ToListAsync(cancellationToken);
        return rows.Adapt<List<ExistingAddress>>();
    }

    public async Task<ExistingAddress?> CreateAddress(Guid contactId, NewAddress request, CancellationToken cancellationToken = default)
    {
        var contact = await LoadForChildMutation(contactId, cancellationToken);
        if (contact is null)
            return null;

        var siblings = await context.Addresses.Where(a => a.ContactId == contactId).ToListAsync(cancellationToken);
        var address = new Address
        {
            ContactId = contactId,
            Label = request.Label,
            Line1 = CleanRequired(request.Line1, 256, "Line 1"),
            Line2 = CleanOptional(request.Line2, 256, "Line 2"),
            City = CleanRequired(request.City, 128, "City"),
            PostalCode = CleanOptional(request.PostalCode, 32, "Postal code"),
            Region = CleanOptional(request.Region, 128, "Region"),
            CountryCode = NormalizeCountryCode(request.CountryCode),
        };
        context.Addresses.Add(address);

        ArbitratePrimaryOnCreate(siblings, address, request.IsPrimary, a => a.IsPrimary, (a, v) => a.IsPrimary = v);
        Touch(contact);
        await context.SaveChangesAsync(cancellationToken);
        return address.Adapt<ExistingAddress>();
    }

    public async Task<bool> UpdateAddress(Guid contactId, Guid addressId, NewAddress request, CancellationToken cancellationToken = default)
    {
        var contact = await LoadForChildMutation(contactId, cancellationToken);
        if (contact is null)
            return false;

        var address = await context.Addresses.FirstOrDefaultAsync(a => a.Id == addressId && a.ContactId == contactId, cancellationToken);
        if (address is null)
            return false;

        address.Label = request.Label;
        address.Line1 = CleanRequired(request.Line1, 256, "Line 1");
        address.Line2 = CleanOptional(request.Line2, 256, "Line 2");
        address.City = CleanRequired(request.City, 128, "City");
        address.PostalCode = CleanOptional(request.PostalCode, 32, "Postal code");
        address.Region = CleanOptional(request.Region, 128, "Region");
        address.CountryCode = NormalizeCountryCode(request.CountryCode);

        var siblings = await context.Addresses.Where(a => a.ContactId == contactId).ToListAsync(cancellationToken);
        ArbitratePrimaryOnUpdate(siblings, address, request.IsPrimary, a => a.Id, a => a.IsPrimary, (a, v) => a.IsPrimary = v);
        Touch(contact);
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> DeleteAddress(Guid contactId, Guid addressId, CancellationToken cancellationToken = default)
    {
        var contact = await LoadForChildMutation(contactId, cancellationToken);
        if (contact is null)
            return false;

        var address = await context.Addresses.FirstOrDefaultAsync(a => a.Id == addressId && a.ContactId == contactId, cancellationToken);
        if (address is null)
            return false;

        context.Addresses.Remove(address);
        var remaining = await context.Addresses.Where(a => a.ContactId == contactId && a.Id != addressId).ToListAsync(cancellationToken);
        PromoteFirstIfNonePrimary(remaining, address.IsPrimary, a => a.Id, a => a.IsPrimary, (a, v) => a.IsPrimary = v);
        Touch(contact);
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    // ── Email sub-resource ────────────────────────────────────────────────────

    public async Task<IReadOnlyList<ExistingEmailAddress>?> GetEmails(Guid contactId, CancellationToken cancellationToken = default)
    {
        if (!await context.Contacts.AnyAsync(c => c.ContactId == contactId, cancellationToken))
            return null;

        var rows = await context.EmailAddresses.AsNoTracking()
            .Where(e => e.ContactId == contactId)
            .OrderByDescending(e => e.IsPrimary).ThenBy(e => e.Id)
            .ToListAsync(cancellationToken);
        return rows.Adapt<List<ExistingEmailAddress>>();
    }

    public async Task<ExistingEmailAddress?> CreateEmail(Guid contactId, NewEmailAddress request, CancellationToken cancellationToken = default)
    {
        var contact = await LoadForChildMutation(contactId, cancellationToken);
        if (contact is null)
            return null;

        var siblings = await context.EmailAddresses.Where(e => e.ContactId == contactId).ToListAsync(cancellationToken);
        var email = new EmailAddress
        {
            ContactId = contactId,
            Label = request.Label,
            Value = CleanRequired(request.Value, 256, "Email address"),
        };
        context.EmailAddresses.Add(email);

        ArbitratePrimaryOnCreate(siblings, email, request.IsPrimary, e => e.IsPrimary, (e, v) => e.IsPrimary = v);
        Touch(contact);
        await context.SaveChangesAsync(cancellationToken);
        return email.Adapt<ExistingEmailAddress>();
    }

    public async Task<bool> UpdateEmail(Guid contactId, Guid emailId, NewEmailAddress request, CancellationToken cancellationToken = default)
    {
        var contact = await LoadForChildMutation(contactId, cancellationToken);
        if (contact is null)
            return false;

        var email = await context.EmailAddresses.FirstOrDefaultAsync(e => e.Id == emailId && e.ContactId == contactId, cancellationToken);
        if (email is null)
            return false;

        email.Label = request.Label;
        email.Value = CleanRequired(request.Value, 256, "Email address");

        var siblings = await context.EmailAddresses.Where(e => e.ContactId == contactId).ToListAsync(cancellationToken);
        ArbitratePrimaryOnUpdate(siblings, email, request.IsPrimary, e => e.Id, e => e.IsPrimary, (e, v) => e.IsPrimary = v);
        Touch(contact);
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> DeleteEmail(Guid contactId, Guid emailId, CancellationToken cancellationToken = default)
    {
        var contact = await LoadForChildMutation(contactId, cancellationToken);
        if (contact is null)
            return false;

        var email = await context.EmailAddresses.FirstOrDefaultAsync(e => e.Id == emailId && e.ContactId == contactId, cancellationToken);
        if (email is null)
            return false;

        context.EmailAddresses.Remove(email);
        var remaining = await context.EmailAddresses.Where(e => e.ContactId == contactId && e.Id != emailId).ToListAsync(cancellationToken);
        PromoteFirstIfNonePrimary(remaining, email.IsPrimary, e => e.Id, e => e.IsPrimary, (e, v) => e.IsPrimary = v);
        Touch(contact);
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    // ── Phone sub-resource ────────────────────────────────────────────────────

    public async Task<IReadOnlyList<ExistingPhoneNumber>?> GetPhones(Guid contactId, CancellationToken cancellationToken = default)
    {
        if (!await context.Contacts.AnyAsync(c => c.ContactId == contactId, cancellationToken))
            return null;

        var rows = await context.PhoneNumbers.AsNoTracking()
            .Where(p => p.ContactId == contactId)
            .OrderByDescending(p => p.IsPrimary).ThenBy(p => p.Id)
            .ToListAsync(cancellationToken);
        return rows.Adapt<List<ExistingPhoneNumber>>();
    }

    public async Task<ExistingPhoneNumber?> CreatePhone(Guid contactId, NewPhoneNumber request, CancellationToken cancellationToken = default)
    {
        var contact = await LoadForChildMutation(contactId, cancellationToken);
        if (contact is null)
            return null;

        var siblings = await context.PhoneNumbers.Where(p => p.ContactId == contactId).ToListAsync(cancellationToken);
        var phone = new PhoneNumber
        {
            ContactId = contactId,
            Label = request.Label,
            Value = CleanRequired(request.Value, 32, "Phone number"),
        };
        context.PhoneNumbers.Add(phone);

        ArbitratePrimaryOnCreate(siblings, phone, request.IsPrimary, p => p.IsPrimary, (p, v) => p.IsPrimary = v);
        Touch(contact);
        await context.SaveChangesAsync(cancellationToken);
        return phone.Adapt<ExistingPhoneNumber>();
    }

    public async Task<bool> UpdatePhone(Guid contactId, Guid phoneId, NewPhoneNumber request, CancellationToken cancellationToken = default)
    {
        var contact = await LoadForChildMutation(contactId, cancellationToken);
        if (contact is null)
            return false;

        var phone = await context.PhoneNumbers.FirstOrDefaultAsync(p => p.Id == phoneId && p.ContactId == contactId, cancellationToken);
        if (phone is null)
            return false;

        phone.Label = request.Label;
        phone.Value = CleanRequired(request.Value, 32, "Phone number");

        var siblings = await context.PhoneNumbers.Where(p => p.ContactId == contactId).ToListAsync(cancellationToken);
        ArbitratePrimaryOnUpdate(siblings, phone, request.IsPrimary, p => p.Id, p => p.IsPrimary, (p, v) => p.IsPrimary = v);
        Touch(contact);
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> DeletePhone(Guid contactId, Guid phoneId, CancellationToken cancellationToken = default)
    {
        var contact = await LoadForChildMutation(contactId, cancellationToken);
        if (contact is null)
            return false;

        var phone = await context.PhoneNumbers.FirstOrDefaultAsync(p => p.Id == phoneId && p.ContactId == contactId, cancellationToken);
        if (phone is null)
            return false;

        context.PhoneNumbers.Remove(phone);
        var remaining = await context.PhoneNumbers.Where(p => p.ContactId == contactId && p.Id != phoneId).ToListAsync(cancellationToken);
        PromoteFirstIfNonePrimary(remaining, phone.IsPrimary, p => p.Id, p => p.IsPrimary, (p, v) => p.IsPrimary = v);
        Touch(contact);
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    // ── Shared child helpers ──────────────────────────────────────────────────

    // Loads the parent tracked so its UpdatedAt bump (§9, F4) persists in the same transaction.
    private async Task<Contact?> LoadForChildMutation(Guid contactId, CancellationToken cancellationToken) =>
        await context.Contacts.FirstOrDefaultAsync(c => c.ContactId == contactId, cancellationToken);

    private void Touch(Contact contact) => contact.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;

    private static string NormalizeCountryCode(string value)
    {
        var cleaned = (value ?? string.Empty).Trim();
        if (!CountryCodeRegex.IsMatch(cleaned))
            throw new DomainValidationException("Country code must be two letters.");
        return cleaned.ToUpperInvariant();
    }

    // On create: if requested primary — or this is the first record in the collection — make it the
    // sole primary; otherwise leave the existing primary untouched (§9).
    private static void ArbitratePrimaryOnCreate<T>(
        IReadOnlyList<T> siblings, T created, bool requestedPrimary,
        Func<T, bool> getPrimary, Action<T, bool> setPrimary)
    {
        var makePrimary = requestedPrimary || siblings.Count == 0;
        setPrimary(created, makePrimary);
        if (makePrimary)
        {
            foreach (var sibling in siblings)
                setPrimary(sibling, false);
        }
    }

    // On update: setting primary clears the others; clearing it re-promotes the first sibling if the
    // collection would otherwise have no primary (§9).
    private static void ArbitratePrimaryOnUpdate<T>(
        IReadOnlyList<T> collection, T updated, bool requestedPrimary,
        Func<T, Guid> getId, Func<T, bool> getPrimary, Action<T, bool> setPrimary)
    {
        if (requestedPrimary)
        {
            foreach (var item in collection)
                setPrimary(item, getId(item) == getId(updated));
            return;
        }

        setPrimary(updated, false);
        if (!collection.Any(getPrimary))
        {
            var first = collection.OrderBy(getId).FirstOrDefault();
            if (first is not null)
                setPrimary(first, true);
        }
    }

    private static void PromoteFirstIfNonePrimary<T>(
        IReadOnlyList<T> remaining, bool removedWasPrimary,
        Func<T, Guid> getId, Func<T, bool> getPrimary, Action<T, bool> setPrimary)
    {
        if (!removedWasPrimary || remaining.Count == 0 || remaining.Any(getPrimary))
            return;
        setPrimary(remaining.OrderBy(getId).First(), true);
    }
}

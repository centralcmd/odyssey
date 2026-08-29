using Odyssey.Dtos;
using Mapster;
using Microsoft.EntityFrameworkCore;
using Odyssey.Core.Finance;
using Odyssey.Dtos.Journal;
using Odyssey.Context;

namespace Odyssey.Core.Journal;

/// <summary>
/// The <see cref="IContactLookup"/> implementation. Contact was moved into <see cref="OdysseyContext"/>
/// (issue #325 follow-up); this lives in the Journal module and injects <see cref="OdysseyContext"/>.
/// The interface stays in <c>Odyssey.Core.Finance</c> so Finance can depend on it without referencing Journal
/// (DI wires this implementation) — Journal already references Finance.
/// </summary>
public sealed class ContactLookup(OdysseyContext context) : IContactLookup
{
    public async Task<IReadOnlySet<Guid>> ExistingIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default)
    {
        if (ids.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var found = await context.Contacts
            .Where(c => ids.Contains(c.ContactId))
            .Select(c => c.ContactId)
            .ToListAsync(cancellationToken);

        return found.ToHashSet();
    }

    public async Task<IReadOnlySet<Guid>> ExistingPersonIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default)
    {
        if (ids.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var found = await context.Contacts
            .Where(c => ids.Contains(c.ContactId) && c.Type == ContactType.Person)
            .Select(c => c.ContactId)
            .ToListAsync(cancellationToken);

        return found.ToHashSet();
    }

    public async Task<IReadOnlyDictionary<Guid, ContactRef>> ResolveRefsAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default)
    {
        if (ids.Count == 0)
        {
            return new Dictionary<Guid, ContactRef>();
        }

        var distinct = ids.Distinct().ToList();
        var rows = await context.Contacts
            .AsNoTracking()
            .Where(c => distinct.Contains(c.ContactId))
            .Include(c => c.PersonDetails)
            .Include(c => c.OrganizationDetails)
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(
            c => c.ContactId,
            c => new ContactRef(
                c.ContactId,
                ContactNaming.Resolve(c),
                c.NormalizedName,
                c.Type,
                c.OrganizationDetails?.OrganizationNumber ?? c.OrganizationNumber,
                c.Archived));
    }

    public async Task<IReadOnlyList<ContactRef>> ListActiveContactRefsAsync(CancellationToken cancellationToken = default)
    {
        var rows = await context.Contacts
            .AsNoTracking()
            .Where(c => c.Archived == null)
            .Include(c => c.PersonDetails)
            .Include(c => c.OrganizationDetails)
            .ToListAsync(cancellationToken);

        return rows
            .Select(c => new ContactRef(
                c.ContactId,
                ContactNaming.Resolve(c),
                c.NormalizedName,
                c.Type,
                c.OrganizationDetails?.OrganizationNumber ?? c.OrganizationNumber,
                c.Archived))
            .ToList();
    }

    public async Task<IReadOnlyDictionary<Guid, ExistingContact>> ResolveContactsAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default)
    {
        if (ids.Count == 0)
        {
            return new Dictionary<Guid, ExistingContact>();
        }

        var distinct = ids.Distinct().ToList();
        var rows = await context.Contacts
            .AsNoTracking()
            .Where(c => distinct.Contains(c.ContactId))
            .Include(c => c.PersonDetails)
            .Include(c => c.OrganizationDetails)
            .Include(c => c.Addresses)
            .Include(c => c.EmailAddresses)
            .Include(c => c.PhoneNumbers)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);

        // Mapster maps Contact → ExistingContact via ContactMapsterConfig (registered in this assembly).
        return rows.ToDictionary(c => c.ContactId, c => c.Adapt<ExistingContact>());
    }

    public async Task<IReadOnlyList<Guid>> SearchIdsByNameAsync(string term, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return [];
        }

        var normalized = ContactNaming.Normalize(term);
        return await context.Contacts
            .Where(c => c.NormalizedName.Contains(normalized))
            .Select(c => c.ContactId)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<Guid, string>> ResolveExternalUidsAsync(
        IReadOnlyCollection<Guid> contactIds, CancellationToken cancellationToken = default)
    {
        if (contactIds.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        var ids = contactIds.Distinct().ToList();
        var rows = await context.Contacts
            .Where(c => ids.Contains(c.ContactId))
            .Select(c => new { c.ContactId, c.ExternalUid })
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(r => r.ContactId, r => r.ExternalUid);
    }

    public async Task<IReadOnlyDictionary<string, Guid>> ResolveIdsByExternalUidAsync(
        IReadOnlyCollection<string> externalUids, CancellationToken cancellationToken = default)
    {
        var map = new Dictionary<string, Guid>(StringComparer.Ordinal);
        if (externalUids.Count == 0)
        {
            return map;
        }

        var uids = externalUids.Distinct(StringComparer.Ordinal).ToList();
        var rows = await context.Contacts
            .Where(c => uids.Contains(c.ExternalUid))
            .Select(c => new { c.ExternalUid, c.ContactId })
            .ToListAsync(cancellationToken);

        // ExternalUid is unique (#338): at most one id per value. Ordinal dictionary matches the column's
        // binary collation, so a case-variant reference never resolves to a differently-cased id.
        foreach (var row in rows)
        {
            map[row.ExternalUid] = row.ContactId;
        }

        return map;
    }
}

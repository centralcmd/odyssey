using Odyssey.Dtos;
using Odyssey.Dtos.Journal;

namespace Odyssey.Core.Finance;

/// <summary>
/// A slim, resolved projection of a contact used by Finance read paths (custodian, insurer,
/// subscription/contract references, matched-contact names) to build their data-minimised DTOs without a
/// an <c>Include</c> — which is what keeps a finance entity free of a <c>Contact</c> navigation even
/// though the reference is a real foreign key again.
/// </summary>
public sealed record ContactRef(
    Guid ContactId,
    string Name,
    string NormalizedName,
    ContactType Type,
    string? OrganizationNumber,
    DateTime? Archived);

public interface IContactLookup
{
    Task<IReadOnlySet<Guid>> ExistingIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves each existing contact id to a <see cref="ContactRef"/> (display name resolved from the
    /// Person/Organization details). Batched — replaces the cross-context navigation projections Finance
    /// read DTOs used to build via <c>.Include(...)</c>. Ids with no matching contact are absent.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, ContactRef>> ResolveRefsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves each existing contact id to a full <see cref="ExistingContact"/> DTO (with Person/Org
    /// details and address/email/phone children). Used by read paths that embed the whole contact — e.g.
    /// <c>ExistingTransaction.Contact</c> — which previously came from an EF navigation include now that
    /// Contact lives in OdysseyContext. Ids with no matching contact are absent.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, ExistingContact>> ResolveContactsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default);

    /// <summary>
    /// The ids of contacts whose resolved display name contains <paramref name="term"/> (case-insensitive).
    /// Used by Finance list endpoints that filter/sort by a linked contact's name (e.g. insurance by
    /// insurer) — the caller pre-resolves ids here, then filters its own table by <c>Contains(id)</c>.
    /// A JOIN would also work now that finance and journal share one context; see the call sites for why
    /// swapping to one is a separate change.
    /// </summary>
    Task<IReadOnlyList<Guid>> SearchIdsByNameAsync(string term, CancellationToken cancellationToken = default);

    /// <summary>
    /// All non-archived contacts as <see cref="ContactRef"/>s. Used by the file-analysis matcher to build
    /// the merchant→contact name vocabulary it sends to the model.
    /// </summary>
    Task<IReadOnlyList<ContactRef>> ListActiveContactRefsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The subset of <paramref name="ids"/> that exist and are contacts of type
    /// <c>Person</c> (issue #321 — photos link only people). Used both to enforce the
    /// "people must be Person" write rule and to drop unresolved person links at read time.
    /// </summary>
    Task<IReadOnlySet<Guid>> ExistingPersonIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default);

    /// <summary>
    /// Maps each existing contact id to its <c>ExternalUid</c> (issue #339 §5). Batched — used by the
    /// journal-entry VJOURNAL export to project <c>X-ODYSSEY-CONTACT</c> without an N+1. Ids with no
    /// matching contact are simply absent from the result.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, string>> ResolveExternalUidsAsync(IReadOnlyCollection<Guid> contactIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Maps each <c>ExternalUid</c> to the contact id that owns it (issue #339 §5). Batched — used by
    /// the journal-entry VJOURNAL import to resolve <c>X-ODYSSEY-CONTACT</c> references. Because
    /// <c>ExternalUid</c> is unique (#338), each value maps to at most one id; matching is ordinal/
    /// case-sensitive, agreeing with the column's binary collation. Unmatched values are absent.
    /// </summary>
    Task<IReadOnlyDictionary<string, Guid>> ResolveIdsByExternalUidAsync(IReadOnlyCollection<string> externalUids, CancellationToken cancellationToken = default);
}

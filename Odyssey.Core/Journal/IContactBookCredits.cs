using Odyssey.Dtos.Books;

namespace Odyssey.Core.Journal;

/// <summary>
/// Whether a contact is credited on any book, as main author or contributor (issue #527 §7.10). Declared here, in the
/// consuming module, and implemented in <c>Odyssey.Core.Books</c>, so Journal reaches Books only through this seam —
/// the <c>IContactLookup</c> shape. The two <c>RESTRICT</c> keys are the backstop; this turns them into an explanatory
/// <c>409</c> and is the only implementation the FK-free EF InMemory tier sees.
/// </summary>
public interface IContactBookCredits
{
    /// <summary>Two indexed probes; the authoritative check, run inside the delete's own transaction.</summary>
    Task<bool> HasCreditsAsync(Guid contactId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The counts and every crediting book, for the controller to shape a claim-conditional <c>409</c> body. Returns
    /// <see langword="null"/> when nothing credits the contact. The controller strips <c>Books</c> without
    /// <c>books.read</c>; this method never decides that.
    /// </summary>
    Task<ContactBookCreditBlockers?> GetCreditsAsync(Guid contactId, CancellationToken cancellationToken = default);
}

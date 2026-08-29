namespace Odyssey.Core.Finance;

/// <summary>
/// Applies, in application code, the same referential-integrity behaviours the database enforces via the
/// cross-module FKs from Finance entities to <c>Contact</c>. Implemented against <c>OdysseyContext</c>
/// and called by <c>ContactService.Delete</c> (in Odyssey.Core.Journal) before the contact row is removed.
/// </summary>
/// <remarks>
/// Not redundant with those FKs. It is what turns the insurer <c>RESTRICT</c> into a 409 that explains
/// itself rather than a raw FK violation surfacing as a 500, and the EF InMemory provider enforces no
/// foreign keys at all — so this is the only implementation the fast test tiers ever exercise. The
/// database is the backstop for any write path that forgets to call it.
/// </remarks>
public interface IContactReferenceGuard
{
    /// <summary>
    /// True if any insurance policy still references <paramref name="contactId"/> as its insurer.
    /// Runs in front of the <c>ON DELETE RESTRICT</c> FK on <c>InsurancePolicy.InsurerId</c>: the caller
    /// blocks the delete with a 409 when this returns true, so the constraint never has to fire.
    /// </summary>
    Task<bool> IsReferencedAsInsurerAsync(Guid contactId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies the FK on-delete behaviours for a contact being deleted, in one OdysseyContext
    /// unit of work: null out the <c>SetNull</c> links (transactions, subscriptions, account custodians,
    /// account-file issuers, file-analysis matched contacts) and delete the <c>Cascade</c> contract-party
    /// rows. Call <see cref="IsReferencedAsInsurerAsync"/> first — this does not touch insurers.
    /// </summary>
    Task ClearAndCascadeReferencesAsync(Guid contactId, CancellationToken cancellationToken = default);
}

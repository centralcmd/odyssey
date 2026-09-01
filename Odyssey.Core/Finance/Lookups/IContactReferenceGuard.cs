using Odyssey.Dtos.Finance;

namespace Odyssey.Core.Finance;

/// <summary>
/// Applies, in application code, the same referential-integrity behaviours the database enforces via the
/// cross-module FKs from Finance entities to <c>Contact</c>. Implemented against <c>OdysseyContext</c>
/// and called by <c>ContactService.Delete</c> (in Odyssey.Core.Journal) before the contact row is removed.
/// </summary>
/// <remarks>
/// Not redundant with those FKs. It is what turns the insurance <c>RESTRICT</c> into a 409 that explains
/// itself rather than a raw FK violation surfacing as a 500, and the EF InMemory provider enforces no
/// foreign keys at all — so this is the only implementation the fast test tiers ever exercise. The
/// database is the backstop for any write path that forgets to call it.
/// </remarks>
public interface IContactReferenceGuard
{
    /// <summary>
    /// Which insurance link kinds still name <paramref name="contactId"/>, with per-kind row counts and
    /// the policies involved — the structured form of the blocked delete (issue #27 §7 #5). Returns an
    /// empty result when nothing blocks.
    /// </summary>
    /// <remarks>
    /// Returns <b>structured</b> data rather than a message because the 409 payload is
    /// <b>claim-conditional</b> and only the controller can evaluate that: <c>DomainConflictException</c>
    /// carries a message and nothing else, and the domain service has no <c>ClaimsPrincipal</c>. So the
    /// controller calls this, decides from <c>User</c> whether to include the policy identifiers, and
    /// the service keeps its own unconditional check as defence-in-depth for non-HTTP callers. Same
    /// split the file-attach endpoints already use: the controller owns authorization, the service owns
    /// the invariant.
    /// </remarks>
    Task<InsuranceLinkBlockers> GetInsuranceLinkBlockersAsync(Guid contactId, CancellationToken cancellationToken = default);

    /// <summary>
    /// True if any insurance policy still names <paramref name="contactId"/> as an insurer, an insured
    /// contact or a beneficiary. Runs in front of the three <c>ON DELETE RESTRICT</c> FKs on the link
    /// tables: the caller blocks the delete with a 409 when this returns true, so the constraints never
    /// have to fire.
    /// </summary>
    Task<bool> IsReferencedByInsuranceAsync(Guid contactId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies the FK on-delete behaviours for a contact being deleted, in one OdysseyContext
    /// unit of work: null out the <c>SetNull</c> links (transactions, subscriptions, account custodians,
    /// account-file issuers, file-analysis matched contacts) and delete the <c>Cascade</c> contract-party
    /// rows. Call <see cref="IsReferencedByInsuranceAsync"/> first — this does not touch insurance links.
    /// </summary>
    Task ClearAndCascadeReferencesAsync(Guid contactId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes every insurance link row naming <paramref name="contactId"/> — insurer, insured contact
    /// and beneficiary — and reports what was removed (issue #27 §7 #6).
    /// </summary>
    /// <remarks>
    /// <b>Stages the removal onto the caller's context; it does not save.</b> The detach and the contact
    /// delete have to commit together, so the caller owns the transaction and the
    /// <c>SaveChangesAsync</c>. Uses tracked <c>RemoveRange</c>, never <c>ExecuteDelete</c>: the latter
    /// lives in <c>EntityFrameworkCore.Relational</c> and throws on the InMemory provider, which is
    /// precisely the tier the application-code cascade exists to serve.
    /// </remarks>
    Task<DetachedInsuranceLinks> StageInsuranceLinkDetachAsync(Guid contactId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Which insurance link kinds name a contact, and where. The domain-side shape behind the
/// claim-conditional 409 payload; the controller narrows it to
/// <see cref="ContactInsuranceLinkBlockers"/> according to the caller's claims.
/// </summary>
public sealed record InsuranceLinkBlockers(
    IReadOnlyList<InsuranceLinkKindCount> Kinds,
    IReadOnlyList<BlockingInsurancePolicy> Policies)
{
    public static readonly InsuranceLinkBlockers None = new([], []);

    /// <summary>Total link ROWS across all three kinds — never a count of resolved names.</summary>
    public int TotalLinks => Kinds.Sum(k => k.Count);

    public bool Any => Kinds.Count > 0;
}

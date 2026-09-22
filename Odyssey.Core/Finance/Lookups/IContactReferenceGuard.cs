using Odyssey.Context;
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
///
/// <para>
/// <b>The contract-<c>Beneficiary</c> rule has no FK backstop at all</b> (issue #157 §4.8, §5.5). The
/// <c>ContractParty → Contact</c> FK stays <c>CASCADE</c>, because the fourteen other roles should keep
/// cascading and converting the key to <c>RESTRICT</c> would block them too. So for that one role this
/// guard is the <em>only</em> enforcement rather than a friendlier face on a constraint — which is why
/// <see cref="IsReferencedByRestrictedLinkAsync"/> has to know about it and not only the blocker query.
/// </para>
/// </remarks>
public interface IContactReferenceGuard
{
    /// <summary>
    /// What still names <paramref name="contactId"/> and therefore blocks its deletion: the insurance
    /// link kinds with per-kind row counts and the policies involved (issue #27 §7 #5), plus the
    /// contracts naming it as a <c>Beneficiary</c> (issue #157 §5.4). Returns an empty result when
    /// nothing blocks.
    /// </summary>
    /// <remarks>
    /// Returns <b>structured</b> data rather than a message because the 409 payload is
    /// <b>claim-conditional</b> and only the controller can evaluate that: <c>DomainConflictException</c>
    /// carries a message and nothing else, and the domain service has no <c>ClaimsPrincipal</c>. So the
    /// controller calls this, decides from <c>User</c> whether to include the policy and contract
    /// identifiers, and the service keeps its own unconditional check as defence-in-depth for non-HTTP
    /// callers. Same split the file-attach endpoints already use: the controller owns authorization,
    /// the service owns the invariant.
    /// </remarks>
    Task<ContactDeleteBlockers> GetDeleteBlockersAsync(Guid contactId, CancellationToken cancellationToken = default);

    /// <summary>
    /// True if anything still names <paramref name="contactId"/> in a way that must block its deletion:
    /// an insurance policy naming it as insurer, insured contact or beneficiary, or a contract naming
    /// it as a <c>Beneficiary</c> party.
    /// </summary>
    /// <remarks>
    /// The insurance half runs in front of three <c>ON DELETE RESTRICT</c> FKs, so the constraints never
    /// have to fire. <b>The contract half has no constraint behind it</b>, so this is the whole rule:
    /// a direct, non-HTTP caller that skipped the controller's pre-check is refused here or not at all
    /// (issue #157 §5.5).
    /// </remarks>
    Task<bool> IsReferencedByRestrictedLinkAsync(Guid contactId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies the FK on-delete behaviours for a contact being deleted, in one OdysseyContext
    /// unit of work: null out the <c>SetNull</c> links (transactions, account custodians,
    /// account-file issuers, file-analysis matched contacts) and delete the <c>Cascade</c> contract-party
    /// rows. Call <see cref="IsReferencedByRestrictedLinkAsync"/> first — this does not touch the
    /// restricted links.
    /// </summary>
    /// <remarks>
    /// <b>The contract-party delete deliberately EXCLUDES the <c>Beneficiary</c> role</b> (issue #157
    /// §5.5). Those rows are a blocker, not a cascade: without the exclusion they would be deleted here
    /// <em>and</em> by <see cref="StageLinkDetach"/> inside the same <c>SaveChangesAsync</c>, and EF
    /// Core raises <c>DbUpdateConcurrencyException</c> — breaking the ordinary detach success path, not
    /// merely some race.
    /// </remarks>
    Task ClearAndCascadeReferencesAsync(Guid contactId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads — and only reads — every link row that blocks deletion of <paramref name="contactId"/>,
    /// as the <b>one snapshot</b> that then drives both the caller's per-class claim check and the
    /// removal (issue #157 §7.3). Materialises tracked entities; hand the result to
    /// <see cref="StageLinkDetach"/>.
    /// </summary>
    /// <remarks>
    /// Split from the staging step precisely so there is no second query between the two. The claim the
    /// detach valve demands is derived from <em>which classes are present</em>, so if the determination
    /// and the destruction could observe different states, a row inserted between them would be
    /// destroyed by a caller never asked to prove the claim for it (CWE-367).
    /// </remarks>
    Task<ContactLinkDetachPlan> ReadLinkDetachPlanAsync(Guid contactId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages removal of exactly the rows <see cref="ReadLinkDetachPlanAsync"/> read, and reports what
    /// was removed (issue #27 §7 #6, widened by issue #157 §5.4).
    /// </summary>
    /// <remarks>
    /// <b>Stages onto the caller's context; it does not save.</b> The detach and the contact delete have
    /// to commit together, so the caller owns the transaction and the <c>SaveChangesAsync</c>. Uses
    /// tracked <c>RemoveRange</c>, never <c>ExecuteDelete</c>: the latter lives in
    /// <c>EntityFrameworkCore.Relational</c> and throws on the InMemory provider, which is precisely the
    /// tier the application-code cascade exists to serve.
    /// </remarks>
    DetachedInsuranceLinks StageLinkDetach(ContactLinkDetachPlan plan);
}

/// <summary>
/// What names a contact, and where — the domain-side shape behind the claim-conditional 409 payload.
/// The controller narrows it to <see cref="ContactInsuranceLinkBlockers"/> and
/// <see cref="ContactContractBeneficiaryBlockers"/> according to the caller's claims.
/// </summary>
public sealed record ContactDeleteBlockers(
    IReadOnlyList<InsuranceLinkKindCount> InsuranceKinds,
    IReadOnlyList<BlockingInsurancePolicy> Policies,
    IReadOnlyList<BlockingContractBeneficiary> Contracts,
    int ContractBeneficiaryLinks)
{
    public static readonly ContactDeleteBlockers None = new([], [], [], 0);

    /// <summary>Total insurance link ROWS across all three kinds — never a count of resolved names.</summary>
    public int TotalInsuranceLinks => InsuranceKinds.Sum(k => k.Count);

    public bool AnyInsurance => InsuranceKinds.Count > 0;

    public bool AnyContractBeneficiary => ContractBeneficiaryLinks > 0;

    public bool Any => AnyInsurance || AnyContractBeneficiary;

    /// <summary>
    /// The blocker classes actually present — what the detach valve derives its required claims from
    /// (issue #157 §7.3). A class with no rows is never demanded.
    /// </summary>
    public IReadOnlySet<ContactDeleteBlockerClass> Classes
    {
        get
        {
            var classes = new HashSet<ContactDeleteBlockerClass>();
            if (AnyInsurance) classes.Add(ContactDeleteBlockerClass.InsuranceLink);
            if (AnyContractBeneficiary) classes.Add(ContactDeleteBlockerClass.ContractBeneficiary);
            return classes;
        }
    }
}

/// <summary>
/// The materialised rows a detach would destroy — read once, inside the delete's transaction, and then
/// used for both the claim check and the removal (issue #157 §7.3).
/// </summary>
/// <remarks>
/// Carries entity instances rather than ids on purpose: re-loading them at removal time would be the
/// second snapshot this type exists to prevent.
/// </remarks>
public sealed record ContactLinkDetachPlan
{
    public required IReadOnlyList<InsurancePolicyInsurer> Insurers { get; init; }

    public required IReadOnlyList<InsurancePolicyInsuredContact> InsuredContacts { get; init; }

    public required IReadOnlyList<InsurancePolicyBeneficiary> Beneficiaries { get; init; }

    /// <summary>Contract parties in the <c>Beneficiary</c> role, the one contract role that blocks a delete.</summary>
    public required IReadOnlyList<ContractParty> ContractBeneficiaries { get; init; }

    public int TotalInsuranceLinks => Insurers.Count + InsuredContacts.Count + Beneficiaries.Count;

    public bool AnyInsurance => TotalInsuranceLinks > 0;

    public bool AnyContractBeneficiary => ContractBeneficiaries.Count > 0;

    /// <summary>The blocker classes this snapshot contains. The claim check reads this, never a re-query.</summary>
    public IReadOnlySet<ContactDeleteBlockerClass> Classes
    {
        get
        {
            var classes = new HashSet<ContactDeleteBlockerClass>();
            if (AnyInsurance) classes.Add(ContactDeleteBlockerClass.InsuranceLink);
            if (AnyContractBeneficiary) classes.Add(ContactDeleteBlockerClass.ContractBeneficiary);
            return classes;
        }
    }
}

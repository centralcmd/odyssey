namespace Odyssey.Dtos.Finance;

/// <summary>
/// The contract half of a refused <c>DELETE /api/contacts/{id}</c>: the contracts naming the contact as
/// a <see cref="ContractPartyRole.Beneficiary"/>, which block the delete: a designation vanishing
/// silently on contact deletion would lose it without trace (issue #157 §5.4, §7.4).
/// </summary>
/// <remarks>
/// <b>The names are claim-gated and the counts are not.</b> <see cref="Contracts"/> is populated only
/// for a caller that also holds <c>contracts.read</c>. The boundary is applied in
/// <c>ContactController</c> rather than the service: <c>DomainConflictException</c> carries a message
/// and nothing else, and the domain service has no <c>ClaimsPrincipal</c>.
///
/// <para>
/// The boundary costs nothing today — every shipped role holding <c>contacts.delete</c> also holds
/// <c>contracts.read</c> — and exists for a future narrower role. The count alone still makes the
/// <c>409</c> actionable: it says how many links must go, and the detach valve does not require the
/// caller to name them.
/// </para>
/// </remarks>
public sealed record ContactContractBeneficiaryBlockers
{
    /// <summary>
    /// Total beneficiary party ROWS naming the contact, never a count of resolved names — the rule
    /// every count in this codebase follows.
    /// </summary>
    public int TotalLinks { get; set; }

    /// <summary>How many distinct contracts are involved. Safe without <c>contracts.read</c>: a count is not an identifier.</summary>
    public int ContractCount { get; set; }

    /// <summary>
    /// The blocking contracts — <b>empty unless the caller holds <c>contracts.read</c></b>. Never
    /// partially populated: an empty list beside a non-zero <see cref="ContractCount"/> is exactly the
    /// "you may not see which" case.
    /// </summary>
    public List<BlockingContractBeneficiary> Contracts { get; set; } = new();
}

/// <summary>One contract naming the contact as a beneficiary.</summary>
public sealed record BlockingContractBeneficiary
{
    public required Guid ContractId { get; set; }

    public required string ContractName { get; set; }
}

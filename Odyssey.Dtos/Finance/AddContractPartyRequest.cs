using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

/// <summary>
/// Adds a party to a contract (issue #174 §7). Exactly one of the three scalar ids must be set — the
/// one-of-three (XOR) invariant. Deliberately carries scalar ids only (no nested account/contact/
/// policy object) so a party link can never over-post or mutate the target entity (§10 #3).
/// </summary>
public sealed record AddContractPartyRequest
{
    public Guid? AccountId { get; set; }

    public Guid? ContactId { get; set; }

    public Guid? InsurancePolicyId { get; set; }
}

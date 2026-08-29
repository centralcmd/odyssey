namespace Odyssey.Dtos.Finance;

/// <summary>
/// A contract party as returned on the read path (issue #174 §7). Exactly one of the three reference
/// projections is populated, matching <see cref="Kind"/>; the others are null. Each reference is a
/// minimal, data-minimised projection (see <see cref="ContractAccountReference"/> et al.).
/// </summary>
public sealed record ExistingContractParty
{
    public required Guid ContractPartyId { get; set; }

    public required Guid ContractId { get; set; }

    public ContractPartyKind Kind { get; set; }

    public ContractAccountReference? Account { get; set; }

    public ContractContactReference? Institution { get; set; }

    public ContractPolicyReference? InsurancePolicy { get; set; }
}

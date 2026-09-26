namespace Odyssey.Dtos.Finance;

/// <summary>
/// A contract party as returned on the read path (issue #174 §7). At most one of the three reference
/// projections is populated, matching <see cref="Kind"/>; the others are null. The matching one is also
/// null when the target no longer resolves — <see cref="Kind"/> still says which it was. Each reference is a
/// minimal, data-minimised projection (see <see cref="ContractAccountReference"/> et al.).
/// </summary>
public sealed record ExistingContractParty
{
    public required Guid ContractPartyId { get; set; }

    public required Guid ContractId { get; set; }

    public ContractPartyKind Kind { get; set; }

    public ContractAccountReference? Account { get; set; }

    public ContractContactReference? Institution { get; set; }

    /// <summary>The linked property (issue #208), when <see cref="Kind"/> is <see cref="ContractPartyKind.Property"/>.</summary>
    public ContractPropertyReference? Property { get; set; }

    /// <summary>
    /// What this record does in the agreement (issue #121). A top-level field on the party, so it does
    /// not depend on the target reference resolving.
    /// </summary>
    public ContractPartyRole Role { get; set; }

    /// <summary>When the party entered the role; null is the contract's own extent (the default term).</summary>
    public DateTime? FromDate { get; set; }

    /// <summary>When it left; null means it is still in the role.</summary>
    public DateTime? ToDate { get; set; }
}

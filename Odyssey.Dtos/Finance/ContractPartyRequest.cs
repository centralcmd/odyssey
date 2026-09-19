using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

/// <summary>
/// Writes ONE party onto a contract — the body of both the add (<c>POST …/parties</c>) and the edit
/// (<c>PUT …/parties/{partyId}</c>), which take identical fields (issue #121 §5). Exactly one of the
/// two scalar ids must be set — the one-of-two (XOR) invariant. Deliberately carries scalar ids only
/// (no nested account/contact object) so a party link can never over-post or mutate the target entity
/// (issue #174 §10 #3); the edit inherits that invariant unchanged.
/// </summary>
/// <remarks>
/// <b><c>null</c> means the default term here, not "unchanged".</b> This is the one place the contract
/// API departs from <see cref="UpdateContract"/>. A <c>PUT</c> on a party is a <b>full replacement</b>
/// of that link — role, target and both dates — exactly as <c>PUT …/parties/{role}/{targetId}</c> is
/// for an insurance party. A client that omits <see cref="ToDate"/> on an edit <b>clears</b> it, and
/// one that omits <see cref="Role"/> <b>resets it to</b> <see cref="ContractPartyRole.Unspecified"/>.
/// That second case is why every party write is logged (issue #121 §7.7).
///
/// <para>
/// Renamed from <c>AddContractPartyRequest</c> with issue #121: the add and the edit take identical
/// fields, and two records with identical fields drift. The JSON shape is unchanged apart from the
/// three added properties.
/// </para>
/// </remarks>
public sealed record ContractPartyRequest
{
    public Guid? AccountId { get; set; }

    public Guid? ContactId { get; set; }

    /// <summary>
    /// What the linked record does in the agreement. Non-nullable, so an omitted <c>role</c> binds to
    /// <see cref="ContractPartyRole.Unspecified"/> — the same shape <see cref="NewContract.Type"/>
    /// uses. Deliberately <b>not</b> <c>[Required]</c>: that would make the role mandatory on the wire
    /// and break the "a role-less write is valid" rule.
    /// </summary>
    [EnumDataType(typeof(ContractPartyRole))]
    public ContractPartyRole Role { get; set; } = ContractPartyRole.Unspecified;

    /// <summary>When the party enters the role. Null means the contract's own extent.</summary>
    public DateTime? FromDate { get; set; }

    /// <summary>When the party leaves it. Null means it is still in the role.</summary>
    public DateTime? ToDate { get; set; }
}

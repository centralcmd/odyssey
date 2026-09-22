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
/// of that link — role, target and both dates. A client that omits <see cref="ToDate"/> on an edit
/// <b>clears</b> it. Every
/// party write is logged (issue #121 §7.7), which is what makes an accidental role change visible.
///
/// <para>
/// <see cref="Role"/> is <b>required on every write</b> since issue #157 §8.1, and which roles are
/// legal depends on the <em>contract's</em> type — a rule model validation cannot see, because the
/// body does not carry the type. That check therefore lives in <c>ContractService</c> and answers with
/// a <c>422</c>; see <see cref="ContractPartyRoleMatrix"/>.
/// </para>
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
    /// What the linked record does in the agreement. <b>Required</b> (issue #157 §8.1): with
    /// <c>Unspecified</c> retired there is no longer a value meaning "nobody has said", so a role-less
    /// write has nothing to resolve to.
    /// </summary>
    /// <remarks>
    /// <b>Nullable is load-bearing, not cosmetic.</b> Left non-nullable, an omitted <c>role</c> would
    /// bind to <c>0</c> — now an undefined member — and <c>[EnumDataType]</c> would reject it with a
    /// message about an invalid enum value, which misdescribes what the caller did wrong. Nullable
    /// plus <c>[Required]</c> produces "The Role field is required."
    /// </remarks>
    [Required]
    [EnumDataType(typeof(ContractPartyRole))]
    public ContractPartyRole? Role { get; set; }

    /// <summary>When the party enters the role. Null means the contract's own extent.</summary>
    public DateTime? FromDate { get; set; }

    /// <summary>When the party leaves it. Null means it is still in the role.</summary>
    public DateTime? ToDate { get; set; }
}

namespace Odyssey.Dtos.Finance;

/// <summary>
/// What a contract party <em>does</em> in the agreement (issue #121 §4). Orthogonal to
/// <see cref="ContractPartyKind"/>, which says which of the two nullable target columns is set: nothing
/// constrains one by the other, so an account party may carry any role.
/// </summary>
/// <remarks>
/// Ordinals are a wire AND persistence contract — they are serialized in request and response bodies and
/// stored as <c>int</c> — and are aligned member-for-member
/// with <c>Odyssey.Context.ContractPartyRole</c>, which is the persisted half. Later members
/// append; no member is renumbered or removed.
///
/// <para>
/// <see cref="Unspecified"/> and <see cref="Other"/> are deliberately both present and mean different
/// things — "nobody has said" versus "somebody looked and none of these fit". A surface that conflates
/// them loses the distinction that makes a future role-completeness prompt possible.
/// </para>
/// </remarks>
public enum ContractPartyRole
{
    /// <summary>No role stated. The migration's backfill value, and what a role-less write resolves to.</summary>
    Unspecified = 0,
    Employee = 1,
    Employer = 2,
    Buyer = 3,
    Seller = 4,
    ServiceProvider = 5,

    /// <summary>A real, deliberate role that is none of the above — distinct from <see cref="Unspecified"/>.</summary>
    Other = 6,
}

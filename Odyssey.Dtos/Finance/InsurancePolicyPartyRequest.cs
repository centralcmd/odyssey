using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

/// <summary>
/// Writes ONE party onto a policy — the body of both the add (<c>POST …/parties</c>) and the edit
/// (<c>PUT …/parties/{role}/{targetId}</c>). Carries a scalar id and the role it belongs to, never a
/// nested Contact or Account: the mass-assignment invariant (issue #27 §10 #4) holds here exactly as
/// it does on the policy write, so adding a party can never create or rename the linked record.
/// </summary>
/// <remarks>
/// On the edit, <see cref="Role"/> and <see cref="TargetId"/> are the <i>desired</i> values and the
/// route carries the old ones: a party moved between roles stays one party rather than becoming two.
/// Both dates are optional, and <c>null</c> is not "unset" but the default term — the policy's own
/// extent — which is why a party added with the defaults follows the policy for its whole lifetime.
/// </remarks>
public sealed record InsurancePolicyPartyRequest
{
    [EnumDataType(typeof(InsurancePartyRole))]
    public InsurancePartyRole Role { get; set; } = InsurancePartyRole.Insurer;

    /// <summary>The contact or account being linked — which one is decided by <see cref="Role"/>.</summary>
    [Required]
    public required Guid TargetId { get; set; }

    /// <summary>When the party enters the role. Null means the policy's own extent.</summary>
    public DateTime? FromDate { get; set; }

    /// <summary>When the party leaves it. Null means it is still in the role.</summary>
    public DateTime? ToDate { get; set; }
}

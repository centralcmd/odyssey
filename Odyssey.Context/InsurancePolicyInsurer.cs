using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Odyssey.Context;

/// <summary>
/// Join row naming one <see cref="Contact"/> as an insurer on one <see cref="InsurancePolicy"/>
/// (issue #27 §6). The policy link cascades — the row dies with the policy; the contact link
/// <b>restricts</b>, so a contact still named as an insurer cannot be deleted.
/// </summary>
/// <remarks>
/// Four narrow tables rather than one polymorphic table with a kind discriminator: the target types
/// are fixed per collection and it is the <i>relationship</i> that differs, so a shared table would
/// force one cap and one index across four collections whose futures diverge —
/// <see cref="InsurancePolicyBeneficiary"/>'s attribution columns being the first instance.
/// </remarks>
[Index(nameof(InsurancePolicyId), nameof(ContactId), IsUnique = true)]
[Index(nameof(ContactId))]
public class InsurancePolicyInsurer
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }

    public Guid InsurancePolicyId { get; set; }

    public Guid ContactId { get; set; }

    /// <summary>
    /// When this party entered the role, or <c>null</c> for the policy's own extent — the default. A
    /// party's term is its <em>own</em> fact, independent of the policy's renewal periods, so a
    /// renewal never re-dates it (design system, <c>AddPolicyPartyModal</c>).
    /// </summary>
    public DateTime? FromDate { get; set; }

    /// <summary>When this party left the role, or <c>null</c> while it is still in it.</summary>
    public DateTime? ToDate { get; set; }

    public InsurancePolicy? InsurancePolicy { get; set; }
}

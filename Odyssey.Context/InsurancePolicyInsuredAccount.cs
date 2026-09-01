using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Odyssey.Context;

/// <summary>
/// Join row naming one <see cref="Account"/> as an insured asset on one <see cref="InsurancePolicy"/>
/// (issue #27 §6) — a house, an outbuilding, a vehicle.
/// </summary>
/// <remarks>
/// Both FKs <b>cascade</b>. On the account side that preserves today's observable behaviour: the
/// former <c>InsurancePolicy.InsuredAccountId</c> was <c>SET NULL</c>, so deleting the account left the
/// policy standing with no insured account — which on a link table is expressed by removing the row.
/// <c>SET NULL</c> has no meaning here: nulling the only target would leave a row pointing at nothing.
/// </remarks>
[Index(nameof(InsurancePolicyId), nameof(AccountId), IsUnique = true)]
[Index(nameof(AccountId))]
public class InsurancePolicyInsuredAccount
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }

    public Guid InsurancePolicyId { get; set; }

    public Guid AccountId { get; set; }

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

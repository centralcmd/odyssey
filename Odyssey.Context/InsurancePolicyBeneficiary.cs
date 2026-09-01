using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Odyssey.Context;

/// <summary>
/// Join row naming one <see cref="Contact"/> as a beneficiary on one <see cref="InsurancePolicy"/>
/// (issue #27 §6). Cascades with the policy, restricts on the contact.
/// </summary>
/// <remarks>
/// The one link that carries user attribution, and deliberately the only one: a beneficiary
/// designation is the highest-consequence link this feature adds, and "who named this person, and
/// when" is the question a beneficiary dispute actually asks. Two columns in the migration that
/// creates the table cost nothing; retrofitting them later against live rows does not.
/// <see cref="CreatedByUserId"/> is declared through <c>DeclareUserAttribution</c>, so it is
/// <c>SET NULL</c> like every other attribution column — the row is shared data that must outlive its
/// author's departure.
/// </remarks>
[Index(nameof(InsurancePolicyId), nameof(ContactId), IsUnique = true)]
[Index(nameof(ContactId))]
public class InsurancePolicyBeneficiary
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

    /// <summary>Who named this beneficiary. Written once, at insert — a later save by a different user
    /// that leaves the designation in place never rewrites it.</summary>
    public string? CreatedByUserId { get; set; }

    [Required]
    public required DateTime CreatedAtUtc { get; set; }

    public InsurancePolicy? InsurancePolicy { get; set; }
}

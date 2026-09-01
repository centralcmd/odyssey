using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Odyssey.Context;

/// <summary>
/// Join row naming one <see cref="Contact"/> as insured under one <see cref="InsurancePolicy"/>
/// (issue #27 §6) — the policyholder, a spouse, a named driver. Cascades with the policy, restricts
/// on the contact: the same posture the insurer link has always had, widened to this kind.
/// </summary>
[Index(nameof(InsurancePolicyId), nameof(ContactId), IsUnique = true)]
[Index(nameof(ContactId))]
public class InsurancePolicyInsuredContact
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }

    public Guid InsurancePolicyId { get; set; }

    public Guid ContactId { get; set; }

    public InsurancePolicy? InsurancePolicy { get; set; }
}

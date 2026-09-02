using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Odyssey.Context;

[Index(nameof(Type), nameof(Archived))]
[Index(nameof(Archived))]
public class InsurancePolicy
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid InsurancePolicyId { get; set; }

    [StringLength(128)]
    [Required]
    public required string Name { get; set; }

    [StringLength(128)]
    public string? PolicyNumber { get; set; }

    [Required]
    public InsurancePolicyType Type { get; set; } = InsurancePolicyType.Other;

    [StringLength(1024)]
    public string? Notes { get; set; }

    public DateTime? Archived { get; set; }

    [Required]
    public required DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public ICollection<PolicyRenewal> Renewals { get; set; } = new List<PolicyRenewal>();

    // The four link collections (issue #27). Each is OPTIONAL and may hold many members — zero is a
    // valid, healthy state for all four, insurers included: a policy drafted before the insurer is
    // known is a real record. They replace the former scalar InsurerId / InsuredAccountId outright
    // rather than sitting alongside them; the collections are the single representation.
    //
    // Each link carries a real FK to its target with the on-delete behaviour the application code
    // would otherwise be imitating: RESTRICT for the three contact kinds (a contact named on a policy
    // cannot be deleted — the guard turns that into a 409 that explains itself, and the detach path is
    // the supported release valve), CASCADE for the account kind (deleting the account removes the
    // link and leaves the policy standing, which is what SET NULL used to mean on the scalar).
    public ICollection<InsurancePolicyInsurer> Insurers { get; set; } = new List<InsurancePolicyInsurer>();

    public ICollection<InsurancePolicyInsuredAccount> InsuredAccounts { get; set; } = new List<InsurancePolicyInsuredAccount>();

    public ICollection<InsurancePolicyInsuredContact> InsuredContacts { get; set; } = new List<InsurancePolicyInsuredContact>();

    public ICollection<InsurancePolicyBeneficiary> Beneficiaries { get; set; } = new List<InsurancePolicyBeneficiary>();
}

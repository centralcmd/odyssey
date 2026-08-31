using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Odyssey.Context;

[Index(nameof(Type), nameof(Archived))]
[Index(nameof(InsurerId))]
[Index(nameof(InsuredAccountId))]
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

    // The insurer Contact. A real FK with ON DELETE RESTRICT, declared in OdysseyContext, so a contact
    // named on a policy cannot be deleted. InsuranceService still validates existence via IContactLookup
    // (a 400 beats an FK violation), and IContactReferenceGuard still turns the restriction into a 409.
    [Required]
    public required Guid InsurerId { get; set; }

    public Guid? InsuredAccountId { get; set; }

    [ForeignKey(nameof(InsuredAccountId))]
    [DeleteBehavior(DeleteBehavior.SetNull)]
    public Account? InsuredAccount { get; set; }

    [StringLength(1024)]
    public string? Notes { get; set; }

    public DateTime? Archived { get; set; }

    [Required]
    public required DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public ICollection<PolicyRenewal> Renewals { get; set; } = new List<PolicyRenewal>();
}

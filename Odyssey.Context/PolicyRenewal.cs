using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Odyssey.Context;

[Index(nameof(InsurancePolicyId), nameof(ToDate))]
public class PolicyRenewal
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid PolicyRenewalId { get; set; }

    [Required]
    public required Guid InsurancePolicyId { get; set; }

    [ForeignKey(nameof(InsurancePolicyId))]
    [DeleteBehavior(DeleteBehavior.Cascade)]
    public InsurancePolicy? InsurancePolicy { get; set; }

    [Required]
    public required DateTime FromDate { get; set; }

    [Required]
    public required DateTime ToDate { get; set; }

    [Precision(18, 6)]
    [Required]
    public required decimal Premium { get; set; }

    [StringLength(3)]
    [Required]
    public string PremiumCurrencyCode { get; set; } = "USD";

    [Precision(18, 6)]
    [Required]
    public required decimal CoverageAmount { get; set; }

    [StringLength(3)]
    [Required]
    public string CoverageCurrencyCode { get; set; } = "USD";

    [StringLength(512)]
    public string? Notes { get; set; }

    [Required]
    public required DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public ICollection<PolicyRenewalFile> Files { get; set; } = new List<PolicyRenewalFile>();
}

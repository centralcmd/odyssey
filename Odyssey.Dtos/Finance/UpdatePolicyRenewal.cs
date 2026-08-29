using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

public sealed record UpdatePolicyRenewal
{
    [Required]
    public required DateTime FromDate { get; set; }

    [Required]
    public required DateTime ToDate { get; set; }

    [Range(0, double.MaxValue)]
    public required decimal Premium { get; set; }

    [Required]
    [StringLength(3, MinimumLength = 3)]
    public string PremiumCurrencyCode { get; set; } = "USD";

    [Range(0, double.MaxValue)]
    public required decimal CoverageAmount { get; set; }

    [Required]
    [StringLength(3, MinimumLength = 3)]
    public string CoverageCurrencyCode { get; set; } = "USD";

    [StringLength(512)]
    public string? Notes { get; set; }
}

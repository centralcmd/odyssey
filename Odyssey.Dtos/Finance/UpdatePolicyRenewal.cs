using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

public sealed record UpdatePolicyRenewal
{
    [Required]
    public required DateTime FromDate { get; set; }

    [Required]
    public required DateTime ToDate { get; set; }

    // Deliberately unbounded below: a refund or a correction to a period already recorded is a real
    // premium figure, so the money editor offers a sign and the server accepts one.
    public required decimal Premium { get; set; }

    [Required]
    [StringLength(3, MinimumLength = 3)]
    public string PremiumCurrencyCode { get; set; } = "USD";

    // Unbounded below for the same reason as Premium — a correcting term can reduce cover already
    // recorded, and the two currencies stay independent of each other.
    public required decimal CoverageAmount { get; set; }

    [Required]
    [StringLength(3, MinimumLength = 3)]
    public string CoverageCurrencyCode { get; set; } = "USD";

    [StringLength(512)]
    public string? Notes { get; set; }
}

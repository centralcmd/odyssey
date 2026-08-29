namespace Odyssey.Dtos.Finance;

public sealed record ExistingPolicyRenewal
{
    public required Guid PolicyRenewalId { get; set; }

    public required Guid InsurancePolicyId { get; set; }

    public required DateTime FromDate { get; set; }

    public required DateTime ToDate { get; set; }

    public decimal Premium { get; set; }

    public string PremiumCurrencyCode { get; set; } = "USD";

    public decimal CoverageAmount { get; set; }

    public string CoverageCurrencyCode { get; set; } = "USD";

    public string? Notes { get; set; }

    public required DateTime CreatedAtUtc { get; set; }

    public List<ExistingPolicyRenewalFile> Files { get; set; } = new();
}

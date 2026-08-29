namespace Odyssey.Dtos.Finance;

/// <summary>
/// Lean list projection (issue #175 §7): per-row scalars and counts only — no full renewals[]/files[]
/// arrays — so the list endpoint stays a single batched query with no N+1.
/// </summary>
public sealed record InsurancePolicyListItem
{
    public required Guid InsurancePolicyId { get; set; }

    public required string Name { get; set; }

    public InsurancePolicyType Type { get; set; }

    public required InsurerReference Insurer { get; set; }

    public CoverageStatus CoverageStatus { get; set; }

    public DateTime? CurrentRenewalEndDate { get; set; }

    public decimal? CurrentPremium { get; set; }

    public string? CurrentPremiumCurrencyCode { get; set; }

    public decimal? CurrentCoverage { get; set; }

    public string? CurrentCoverageCurrencyCode { get; set; }

    public int RenewalCount { get; set; }

    public int FileCount { get; set; }

    public DateTime? Archived { get; set; }
}

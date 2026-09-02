namespace Odyssey.Dtos.Finance;

/// <summary>
/// Lean list projection (issue #175 §7): per-row scalars and counts only — no full renewals[]/files[]
/// arrays — so the list endpoint stays a single batched query with no N+1.
/// </summary>
public sealed record InsurancePolicyListItem
{
    public required Guid InsurancePolicyId { get; set; }

    public required string Name { get; set; }

    /// <summary>Included because it is shown on the row's meta line — the same reason
    /// <c>SubscriptionListItem.ExternalId</c> is. A plain scalar off the same row, so no extra query.</summary>
    public string? PolicyNumber { get; set; }

    public InsurancePolicyType Type { get; set; }

    /// <summary>
    /// The policy's insurers, as data-minimised references — the one collection the list row names
    /// rather than counts, because it is what the row's meta line shows. Possibly empty.
    /// </summary>
    public List<PolicyContactReference> Insurers { get; set; } = new();

    public CoverageStatus CoverageStatus { get; set; }

    public DateTime? CurrentRenewalEndDate { get; set; }

    /// <summary>
    /// The end date of the latest renewal period on record, or null when the policy has none. This is
    /// what a LAPSED or ARCHIVED row headlines on: cover that has run out still has a date worth
    /// showing, and "expired" is a different fact from "never covered". Null here is exactly the
    /// <see cref="CoverageStatus.NoCoverage"/> case — no period ever existed.
    /// </summary>
    public DateTime? LatestRenewalEndDate { get; set; }

    /// <summary>
    /// The start date of the earliest renewal period, or null when the policy has none. What an
    /// UPCOMING row headlines on — in that state every period is still in the future, so the earliest
    /// one is when cover begins.
    /// </summary>
    public DateTime? EarliestRenewalStartDate { get; set; }

    public decimal? CurrentPremium { get; set; }

    public string? CurrentPremiumCurrencyCode { get; set; }

    public decimal? CurrentCoverage { get; set; }

    public string? CurrentCoverageCurrencyCode { get; set; }

    public int RenewalCount { get; set; }

    /// <summary>
    /// Documents on this policy — the sum across its periods. A document belongs to a period and
    /// nowhere else (issue #26), so this counts <c>renewals[].files[]</c>; before that change it
    /// counted only the policy-level attachments, which no longer exist.
    /// </summary>
    public int FileCount { get; set; }

    /// <summary>Link ROWS, never resolved names — see <see cref="RenewalCount"/> for the pattern.
    /// Counting resolved names instead would make a contact whose links are all unnamed look erasable
    /// when it is not (issue #27 §9).</summary>
    public int InsuredAccountCount { get; set; }

    /// <inheritdoc cref="InsuredAccountCount" />
    public int InsuredContactCount { get; set; }

    /// <inheritdoc cref="InsuredAccountCount" />
    public int BeneficiaryCount { get; set; }

    public DateTime? Archived { get; set; }
}

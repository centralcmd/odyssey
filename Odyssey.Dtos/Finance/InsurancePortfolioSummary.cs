namespace Odyssey.Dtos.Finance;

/// <summary>A currency-tagged money subtotal used by the portfolio summary rollups.</summary>
public sealed record CurrencyAmount
{
    public required string CurrencyCode { get; set; }

    public decimal Amount { get; set; }
}

/// <summary>Policy counts bucketed by derived coverage status (ExpiringSoon counted separately from Active).</summary>
public sealed record CoverageStatusCounts
{
    public int Active { get; set; }

    public int ExpiringSoon { get; set; }

    public int Lapsed { get; set; }

    public int Upcoming { get; set; }

    public int NoCoverage { get; set; }

    /// <summary>Archived policies — counted here (spanning every policy) so the status filter and
    /// "By status" breakdown can surface them, mirroring the Contracts summary.</summary>
    public int Archived { get; set; }
}

/// <summary>A typed count used by the portfolio summary rollup (mirrors <c>ContractTypeCount</c>).</summary>
public sealed record InsuranceTypeCount
{
    public InsurancePolicyType Type { get; set; }

    public int Count { get; set; }
}

/// <summary>
/// Portfolio rollup (issue #175 §7): counts by status plus per-currency premium/coverage subtotals
/// (current renewals only). When <see cref="BaseCurrency"/> is requested, converted grand totals are
/// included, excluding any currency that lacks a direct rate (listed in <see cref="UnconvertedCurrencies"/>).
/// </summary>
public sealed record InsurancePortfolioSummary
{
    public int TotalPolicies { get; set; }

    public required CoverageStatusCounts CountsByStatus { get; set; }

    /// <summary>Per-type counts over the live (non-archived) set — the "By type" breakdown tile.</summary>
    public List<InsuranceTypeCount> CountsByType { get; set; } = new();

    public List<CurrencyAmount> PremiumByCurrency { get; set; } = new();

    public List<CurrencyAmount> CoverageByCurrency { get; set; } = new();

    /// <summary>Echoes the requested base currency; null when no conversion was requested.</summary>
    public string? BaseCurrency { get; set; }

    public decimal? ConvertedTotalPremium { get; set; }

    public decimal? ConvertedTotalCoverage { get; set; }

    /// <summary>Currencies excluded from the converted totals because no direct rate exists.</summary>
    public List<string> UnconvertedCurrencies { get; set; } = new();
}

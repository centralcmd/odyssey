namespace Odyssey.Dtos.Finance;

public sealed record TaxStatementReport
{
    public required Guid TaxStatementId { get; set; }
    public required int FiscalYear { get; set; }
    public required string BaseCurrencyCode { get; set; }
    public TaxStatementStatus Status { get; set; }
    public DateTime? FiledAtUtc { get; set; }
    public DateTime? TaxOfficeApprovedAtUtc { get; set; }

    public TaxStatementDeclaredFigures Declared { get; set; } = new();
    public TaxStatementDerivedFigures Derived { get; set; } = new();
    public TaxStatementReconciliation Reconciliation { get; set; } = new();

    public int ExcludedTransactionCount { get; set; }
    public Dictionary<string, int> ExcludedCurrencies { get; set; } = new();
}

public sealed record TaxStatementDeclaredFigures
{
    public decimal? TotalAssets { get; set; }
    public decimal? TotalLiabilities { get; set; }
    public decimal? NetWorth { get; set; }
    public decimal? TotalIncome { get; set; }
    public decimal? AssessedTax { get; set; }
    public decimal? SettlementAmount { get; set; }
    public DateTime? SettledAtUtc { get; set; }
}

public sealed record TaxStatementDerivedFigures
{
    /// <summary>
    /// False when per-account balances cannot be computed; the net-worth fields are then null.
    /// Advance tax paid and actual income are derived from tagged transactions and remain available.
    /// </summary>
    public bool Available { get; set; }
    public decimal? TotalAssets { get; set; }
    public decimal? TotalLiabilities { get; set; }
    public decimal? NetWorth { get; set; }

    // Advance/within-year tax only — not the post-assessment settlement.
    public decimal PaidTax { get; set; }
    public decimal ActualIncome { get; set; }

    // Settlement-role tags summed over the statement's settlement range (additional tax paid
    // and refunds received after the assessment), so it compares with the declared settlement.
    public decimal SettlementPaid { get; set; }
}

public sealed record TaxStatementReconciliation
{
    public decimal? OutstandingTax { get; set; }
    public decimal? IncomeVariance { get; set; }
    public decimal? NetWorthVariance { get; set; }
    public decimal? SettlementVariance { get; set; }

    // (assessed tax − declared settlement) − derived advance tax paid.
    public decimal? AdvancePaidVariance { get; set; }

    // Declared settlement − derived settlement recorded.
    public decimal? SettlementRecordedVariance { get; set; }
}

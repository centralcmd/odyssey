namespace Odyssey.Dtos.Finance;

/// <summary>Transaction counts bucketed by status (issue #372).</summary>
public sealed record TransactionStatusCounts
{
    public int New { get; set; }

    public int Approved { get; set; }

    public int Flagged { get; set; }
}

/// <summary>
/// Summary rollup for the transactions page header (issue #372): the whole-ledger count, the
/// by-status and by-direction breakdowns, and the money in / out totals. Computed over every
/// transaction — the page's filters narrow the grid, never the header.
/// </summary>
/// <remarks>
/// <see cref="TotalIn"/> and <see cref="TotalOut"/> are naive cross-currency sums (the app applies no
/// FX on this path), matching what the page previously computed in the browser after downloading the
/// entire table. <see cref="TotalOut"/> is reported as a positive magnitude.
/// </remarks>
public sealed record TransactionSummary
{
    public int TotalTransactions { get; set; }

    public required TransactionStatusCounts CountsByStatus { get; set; }

    /// <summary>Transactions with a non-negative amount ("money in").</summary>
    public int IncomeCount { get; set; }

    /// <summary>Transactions with a negative amount ("money out").</summary>
    public int ExpenseCount { get; set; }

    public decimal TotalIn { get; set; }

    /// <summary>The absolute total of the negative amounts (a positive figure).</summary>
    public decimal TotalOut { get; set; }
}

namespace Odyssey.Dtos.Finance;

/// <summary>
/// Summary rollup for the budgets page header (issue #372): how many budgets are live and what they
/// plan for, in aggregate. Replaces the page's former whole-table fetch.
/// </summary>
/// <remarks>
/// <see cref="PlannedBalance"/> is planned income minus planned expenses across the non-archived
/// budgets — a naive cross-currency sum (each budget carries its own base currency and no FX is
/// applied on this path), which is what the page previously computed in the browser.
/// </remarks>
public sealed record BudgetSummary
{
    public int TotalBudgets { get; set; }

    /// <summary>Non-archived budgets.</summary>
    public int ActiveCount { get; set; }

    public int ArchivedCount { get; set; }

    public decimal PlannedBalance { get; set; }
}

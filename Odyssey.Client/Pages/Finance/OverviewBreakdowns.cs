using Odyssey.Client.Components;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance;

/// <summary>
/// The Overview breakdown rows the Accounts, Tax statements and Budgets headers render, built from
/// the server rollups. A static here rather than private members on each page, so the bucket mapping
/// is testable without rendering a page — a key read from the wrong count would otherwise only be
/// visible by eye.
/// </summary>
/// <remarks>
/// Every key is typed, never a string: a mistyped string key fell through to a default bucket and
/// silently mis-counted, where an enum key either compiles or does not.
/// </remarks>
public static class OverviewBreakdowns
{
    /// <summary>
    /// Accounts "By type", over the live set. <see cref="AccountType.Unknown"/> gets its own
    /// "Unclassified" row — only when there is one — so the rows partition what the tile counts and
    /// its total agrees with the live account count.
    /// </summary>
    public static IReadOnlyList<OdsBreakdownRow> AccountTypeRows(AccountSummary? summary) =>
        OdsBreakdown.CountedTypeRows(
            (summary?.CountsByType ?? []).ToDictionary(c => c.Type, c => c.Count),
            [.. AccountTypeVisuals.Selectable, AccountType.Unknown],
            t => (AccountTypeVisuals.MaterialIcon(t), AccountTypeVisuals.FgColor(t),
                  t == AccountType.Unknown ? AccountTypeVisuals.UnclassifiedLabel : AccountTypeVisuals.Label(t)));

    /// <summary>
    /// Tax statements "By status", over every statement on file. The labels, tones and glyphs are
    /// <see cref="TaxStatementStatusVisuals"/>'s; archiving outranks the review status server-side, so
    /// the four rows partition the file.
    /// </summary>
    public static IReadOnlyList<OdsBreakdownRow> TaxStatementStatusRows(TaxStatementSummary? summary) =>
        OdsBreakdown.CountedStatusRows<TaxStatementStatusFilter>(
            key => summary?.CountsByStatus is not { } c ? 0 : key switch
            {
                TaxStatementStatusFilter.New => c.New,
                TaxStatementStatusFilter.Approved => c.Approved,
                TaxStatementStatusFilter.Flagged => c.Flagged,
                TaxStatementStatusFilter.Archived => c.Archived,
                _ => throw new ArgumentOutOfRangeException(nameof(key), key, null),
            },
            new OdsBreakdownDef<TaxStatementStatusFilter>(TaxStatementStatusFilter.New, "New", "info", "fiber_new"),
            new OdsBreakdownDef<TaxStatementStatusFilter>(TaxStatementStatusFilter.Approved, "Approved", "income", "check_circle"),
            new OdsBreakdownDef<TaxStatementStatusFilter>(TaxStatementStatusFilter.Flagged, "Flagged", "expense", "flag"),
            new OdsBreakdownDef<TaxStatementStatusFilter>(TaxStatementStatusFilter.Archived, "Archived", "outline", "inventory_2"));

    /// <summary>
    /// Budgets "By status": Active and Archived partition the whole file, so the total is the budget
    /// count. Keyed on whether the bucket is the archived one.
    /// </summary>
    public static IReadOnlyList<OdsBreakdownRow> BudgetStatusRows(BudgetSummary? summary) =>
        OdsBreakdown.CountedStatusRows<bool>(
            archived => archived ? summary?.ArchivedCount ?? 0 : summary?.ActiveCount ?? 0,
            new OdsBreakdownDef<bool>(false, "Active", "income", "task_alt"),
            new OdsBreakdownDef<bool>(true, "Archived", "outline", "inventory_2"));
}

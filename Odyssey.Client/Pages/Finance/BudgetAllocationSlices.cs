using Odyssey.Client.Components;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance;

/// <summary>
/// The slices behind a budget's Allocation donuts: one planned and one actual donut per direction.
/// </summary>
public static class BudgetAllocationSlices
{
    /// <summary>
    /// Planned amounts by TAG NAME, largest first. The item has no name of its own, so the label comes
    /// from the embedded tag rather than a second lookup. Zero plans are left out: a slice of nothing
    /// draws nothing.
    /// </summary>
    public static List<OdsDonutSlice> Planned(ExistingBudget budget, BudgetCategoryType category) =>
        budget.BudgetItems
            .Where(i => i.CategoryType == category && i.PlannedAmount > 0)
            .GroupBy(i => i.Tag.Name)
            .Select(g => new OdsDonutSlice { Label = g.Key, Value = g.Sum(i => i.PlannedAmount) })
            .OrderByDescending(s => s.Value)
            .ToList();

    /// <summary>
    /// Actual amounts by tag, ordered by and coloured from <paramref name="planned"/> so a tag keeps
    /// its colour across the planned/actual pair. The colour is set explicitly because the donut
    /// assigns palette stops by index after dropping zero slices — a tag with no matched transactions
    /// would otherwise shift every colour after it. A tag planned at zero follows the planned ones.
    /// Income is the signed sum, an expense its magnitude, the same reading the Actual balance takes.
    /// </summary>
    public static List<OdsDonutSlice> Actual(
        ExistingBudget budget, BudgetCategoryType category, IReadOnlyDictionary<Guid, decimal> actualByTag,
        IReadOnlyList<OdsDonutSlice> planned)
    {
        var rank = planned.Select((s, i) => (s.Label, i)).ToDictionary(x => x.Label, x => x.i);
        return budget.BudgetItems
            .Where(i => i.CategoryType == category)
            .GroupBy(i => i.Tag.Name)
            .Select(g => (Name: g.Key, Actual: g.Sum(i => Signed(actualByTag.GetValueOrDefault(i.TransactionTagId)))))
            .Select((t, i) => (t.Name, t.Actual, Rank: rank.TryGetValue(t.Name, out var r) ? r : planned.Count + i))
            .OrderBy(t => t.Rank)
            .Select(t => new OdsDonutSlice
            {
                Label = t.Name,
                Value = t.Actual,
                Color = OdsDonutPalette.Default[t.Rank % OdsDonutPalette.Default.Count],
            })
            .ToList();

        decimal Signed(decimal sum) => category == BudgetCategoryType.Expense ? Math.Abs(sum) : sum;
    }
}

using Odyssey.Context;
using Odyssey.TestData.Catalog;
using static Odyssey.TestData.DemoDataDefaults;

namespace Odyssey.TestData.Generators;

/// <summary>
/// Builds one budget per year (spec §3.9), each with the same canonical line items
/// linked to tags. Base (2016) amounts escalate deterministically per year.
/// </summary>
/// <remarks>
/// An item is defined by its TAG, not by a name of its own (issue #75): the tag names the line
/// wherever it is displayed. Three template rows used to carry a name differing from their tag —
/// "Investment Income" (Dividends), "Savings Contributions" (Savings) and "Investment Contributions"
/// (Investments) — and now read as the tag. The deterministic seed is keyed by tag name too, so demo
/// budget item GUIDs changed with this switch.
/// </remarks>
public static class BudgetGenerator
{
    private sealed record TemplateItem(string TagName, BudgetCategoryType Category, decimal BaseAmount);

    // Canonical annual template. Amounts are the year-2016 base; later years escalate.
    private static readonly TemplateItem[] Template =
    [
        new(Tags.Salary, BudgetCategoryType.Income, 60_000m),
        new(Tags.Bonus, BudgetCategoryType.Income, 5_000m),
        new(Tags.Dividends, BudgetCategoryType.Income, 1_200m),
        new(Tags.Housing, BudgetCategoryType.Expense, 18_000m),
        new(Tags.Groceries, BudgetCategoryType.Expense, 7_200m),
        new(Tags.Savings, BudgetCategoryType.Expense, 6_000m),
        new(Tags.Investments, BudgetCategoryType.Expense, 6_000m),
        new(Tags.Utilities, BudgetCategoryType.Expense, 3_600m),
        new(Tags.Travel, BudgetCategoryType.Expense, 3_000m),
        new(Tags.DiningOut, BudgetCategoryType.Expense, 3_000m),
        new(Tags.Transportation, BudgetCategoryType.Expense, 2_400m),
        new(Tags.Healthcare, BudgetCategoryType.Expense, 2_400m),
        new(Tags.Insurance, BudgetCategoryType.Expense, 2_000m),
        new(Tags.Fuel, BudgetCategoryType.Expense, 1_800m),
        new(Tags.Entertainment, BudgetCategoryType.Expense, 1_800m),
        new(Tags.Clothing, BudgetCategoryType.Expense, 1_200m),
        new(Tags.Subscriptions, BudgetCategoryType.Expense, 600m),
    ];

    public static (List<Budget> Budgets, List<BudgetItem> Items) Build()
    {
        var budgets = new List<Budget>();
        var items = new List<BudgetItem>();

        for (var year = FirstYear; year <= LastYear; year++)
        {
            var budgetId = DeterministicGuid.From($"budget::{year}");
            budgets.Add(new Budget
            {
                BudgetId = budgetId,
                Name = $"Household Budget {year}",
                Description = $"Annual household budget for {year}",
                StartDate = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                EndDate = new DateTime(year, 12, 31, 0, 0, 0, DateTimeKind.Utc),
                BaseCurrencyCode = Currencies.Usd,
                Archived = null,
            });

            foreach (var template in Template)
            {
                var isIncome = template.Category == BudgetCategoryType.Income;
                items.Add(new BudgetItem
                {
                    BudgetItemId = DeterministicGuid.From($"budgetitem::{year}::{template.TagName}"),
                    BudgetId = budgetId,
                    CategoryType = template.Category,
                    PlannedAmount = Escalate(template.BaseAmount, year, isIncome),
                    TransactionTagId = Tags.IdFor(template.TagName),
                });
            }
        }

        return (budgets, items);
    }
}

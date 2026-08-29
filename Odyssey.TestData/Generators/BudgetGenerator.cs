using Odyssey.Context;
using Odyssey.TestData.Catalog;
using static Odyssey.TestData.DemoDataDefaults;

namespace Odyssey.TestData.Generators;

/// <summary>
/// Builds one budget per year (spec §3.9), each with the same canonical line items
/// linked to tags. Base (2016) amounts escalate deterministically per year.
/// </summary>
public static class BudgetGenerator
{
    private sealed record TemplateItem(string Name, string TagName, BudgetCategoryType Category, decimal BaseAmount);

    // Canonical annual template. Amounts are the year-2016 base; later years escalate.
    private static readonly TemplateItem[] Template =
    [
        new("Salary", Tags.Salary, BudgetCategoryType.Income, 60_000m),
        new("Bonus", Tags.Bonus, BudgetCategoryType.Income, 5_000m),
        new("Investment Income", Tags.Dividends, BudgetCategoryType.Income, 1_200m),
        new("Housing", Tags.Housing, BudgetCategoryType.Expense, 18_000m),
        new("Groceries", Tags.Groceries, BudgetCategoryType.Expense, 7_200m),
        new("Savings Contributions", Tags.Savings, BudgetCategoryType.Expense, 6_000m),
        new("Investment Contributions", Tags.Investments, BudgetCategoryType.Expense, 6_000m),
        new("Utilities", Tags.Utilities, BudgetCategoryType.Expense, 3_600m),
        new("Travel", Tags.Travel, BudgetCategoryType.Expense, 3_000m),
        new("Dining Out", Tags.DiningOut, BudgetCategoryType.Expense, 3_000m),
        new("Transportation", Tags.Transportation, BudgetCategoryType.Expense, 2_400m),
        new("Healthcare", Tags.Healthcare, BudgetCategoryType.Expense, 2_400m),
        new("Insurance", Tags.Insurance, BudgetCategoryType.Expense, 2_000m),
        new("Fuel", Tags.Fuel, BudgetCategoryType.Expense, 1_800m),
        new("Entertainment", Tags.Entertainment, BudgetCategoryType.Expense, 1_800m),
        new("Clothing", Tags.Clothing, BudgetCategoryType.Expense, 1_200m),
        new("Subscriptions", Tags.Subscriptions, BudgetCategoryType.Expense, 600m),
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
                    BudgetItemId = DeterministicGuid.From($"budgetitem::{year}::{template.Name}"),
                    BudgetId = budgetId,
                    Name = template.Name,
                    Description = null,
                    CategoryType = template.Category,
                    PlannedAmount = Escalate(template.BaseAmount, year, isIncome),
                    TransactionTagId = Tags.IdFor(template.TagName),
                });
            }
        }

        return (budgets, items);
    }
}

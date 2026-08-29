using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

public sealed record ExistingBudget
{
    public required Guid BudgetId { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public required DateTime StartDate { get; set; }
    public required DateTime EndDate { get; set; }
    public required DateTime? Archived { get; set; }

    [StringLength(3)]
    public string BaseCurrencyCode { get; set; } = "USD";
    public List<ExistingBudgetItem> BudgetItems { get; set; } = new();

    /// <summary>
    /// Number of transactions matching this budget's item tags, base currency
    /// and date range. Computed by the service, not mapped from the entity.
    /// </summary>
    public int TransactionCount { get; set; }
    
    public decimal ExpectedIncomes => BudgetItems
        .Where(i => i.CategoryType == BudgetCategoryType.Income)
        .Sum(i => i.PlannedAmount);
    public decimal ExpectedExpenses => BudgetItems
        .Where(i => i.CategoryType == BudgetCategoryType.Expense)
        .Sum(i => i.PlannedAmount);
    public decimal ExpectedDifference => ExpectedIncomes - ExpectedExpenses;
}

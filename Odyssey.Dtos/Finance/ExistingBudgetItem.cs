namespace Odyssey.Dtos.Finance;

public sealed record ExistingBudgetItem
{
    public required Guid BudgetItemId { get; set; }
    public required Guid BudgetId { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public required BudgetCategoryType CategoryType { get; set; }
    public required decimal PlannedAmount { get; set; }
    public Guid? TransactionTagId { get; set; }
}

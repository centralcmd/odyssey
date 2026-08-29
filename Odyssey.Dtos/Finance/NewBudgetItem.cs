using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

public sealed record NewBudgetItem
{
    public required Guid BudgetId { get; set; }
    [StringLength(64)]
    public required string Name { get; set; }
    [StringLength(256)]
    public string? Description { get; set; }
    [EnumDataType(typeof(BudgetCategoryType))]
    public required BudgetCategoryType CategoryType { get; set; }
    public required decimal PlannedAmount { get; set; }
    public Guid? TransactionTagId { get; set; }
}

namespace Odyssey.Dtos.Finance;

/// <summary>
/// A budget item as read back: a planned amount for a tag, in a category, within a budget.
/// </summary>
/// <remarks>
/// The item has no name and no description of its own (issue #75) — <see cref="Tag"/> carries them,
/// and it is always present, because <c>TransactionTagId</c> is a required foreign key and every read
/// path <c>Include</c>s the navigation. <see cref="TransactionTagId"/> is retained as the round-trip
/// key mirroring <c>NewBudgetItem</c>, and always equals <c>Tag.TransactionTagId</c>.
/// </remarks>
public sealed record ExistingBudgetItem
{
    public required Guid BudgetItemId { get; set; }
    public required Guid BudgetId { get; set; }
    public required BudgetCategoryType CategoryType { get; set; }
    public required decimal PlannedAmount { get; set; }
    public required Guid TransactionTagId { get; set; }

    /// <summary>
    /// The tag this line plans for — the item's identity. Populated by the explicit Mapster
    /// registration in <c>MapsterConfig</c>; never null on a read path.
    /// </summary>
    public required ExistingTransactionTag Tag { get; set; }
}

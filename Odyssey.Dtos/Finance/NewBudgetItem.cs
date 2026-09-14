using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

/// <summary>
/// The budget item write model. A budget item is a planned amount for a tag, so the tag is required
/// and there is no name or description to send (issue #75).
/// </summary>
/// <remarks>
/// Deliberately carries a <b>scalar</b> tag id and no nested tag: the nesting on
/// <see cref="ExistingBudgetItem"/> is read-only and one-directional, and
/// <c>BudgetItemService</c> assigns field-by-field with no DTO-to-entity <c>Adapt</c>, which makes
/// that structural rather than a matter of vigilance.
/// </remarks>
public sealed record NewBudgetItem
{
    public required Guid BudgetId { get; set; }
    [EnumDataType(typeof(BudgetCategoryType))]
    public required BudgetCategoryType CategoryType { get; set; }
    public required decimal PlannedAmount { get; set; }

    /// <summary>
    /// The tag this line plans for. <c>[Required]</c> does not reject <see cref="Guid.Empty"/> on a
    /// non-nullable <see cref="Guid"/>, so the service rejects that separately with a 400.
    /// </summary>
    [Required]
    public required Guid TransactionTagId { get; set; }
}

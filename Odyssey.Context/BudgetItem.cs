using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Odyssey.Context;

/// <summary>
/// A planned amount for a transaction tag, in a category, within a budget (issue #75).
/// </summary>
/// <remarks>
/// The item carries no name and no description of its own: the linked <see cref="TransactionTag"/>
/// <b>is</b> its identity — the tag's name is the row's label, the tag's description its secondary
/// line, and the tag is what the actuals are summed from. That is why
/// <see cref="TransactionTagId"/> is required and why the unique index is on
/// <c>(BudgetId, TransactionTagId)</c>: one item per tag per budget.
/// </remarks>
[Index(nameof(BudgetId), nameof(TransactionTagId), IsUnique = true)]
public class BudgetItem
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid BudgetItemId { get; set; }

    [Required]
    public required Guid BudgetId { get; set; }

    [ForeignKey(nameof(BudgetId))]
    [DeleteBehavior(DeleteBehavior.Cascade)]
    public Budget? Budget { get; set; }

    /// <summary>Income or expense. A tag carries no income/expense sense, so the item still does.</summary>
    [Required]
    public BudgetCategoryType CategoryType { get; set; }

    [Required]
    [Precision(18, 6)]
    public required decimal PlannedAmount { get; set; }

    [Required]
    public required Guid TransactionTagId { get; set; }

    /// <summary>
    /// The tag this line plans for. <see cref="DeleteBehavior.Restrict"/> is load-bearing and must
    /// stay: EF's default for a <i>required</i> reference is <see cref="DeleteBehavior.Cascade"/>, so
    /// left to the default, deleting a tag would silently delete every budget item planning for it.
    /// </summary>
    [ForeignKey(nameof(TransactionTagId))]
    [DeleteBehavior(DeleteBehavior.Restrict)]
    public TransactionTag? TransactionTag { get; set; }
}

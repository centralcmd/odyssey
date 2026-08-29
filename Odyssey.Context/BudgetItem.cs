using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Odyssey.Context;

[Index(nameof(BudgetId), nameof(Name), IsUnique = true)]
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

    [StringLength(64)]
    [Required]
    public required string Name { get; set; }

    [StringLength(256)]
    public string? Description { get; set; }

    [Required]
    public BudgetCategoryType CategoryType { get; set; }

    [Required]
    [Precision(18, 6)]
    public required decimal PlannedAmount { get; set; }

    public Guid? TransactionTagId { get; set; }

    [ForeignKey(nameof(TransactionTagId))]
    [DeleteBehavior(DeleteBehavior.Restrict)]
    public TransactionTag? TransactionTag { get; set; }
}

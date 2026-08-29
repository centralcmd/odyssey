using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Odyssey.Context;

[Index(nameof(Archived))]
public class TransactionTag
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid TransactionTagId { get; set; }

    [StringLength(64)]
    [Required]
    public required string Name { get; set; }

    [StringLength(256)]
    public string? Description { get; set; }

    public DateTime? Archived { get; set; }

    public ICollection<Transaction> Transactions { get; set; } = new List<Transaction>();

    public ICollection<TransactionTagLink> TransactionTagLinks { get; set; } = new List<TransactionTagLink>();

    public ICollection<BudgetItem> BudgetItems { get; set; } = new List<BudgetItem>();

    public ICollection<AccountSmartTag> AccountSmartTags { get; set; } = new List<AccountSmartTag>();
}

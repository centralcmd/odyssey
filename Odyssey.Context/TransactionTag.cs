using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Odyssey.Context;

[Index(nameof(Archived))]
// A tag name is an identity, not a label: it names every budget item planning for the tag (issue
// #75), so two same-named tags would render as two indistinguishable budget rows. The index covers
// ARCHIVED rows too — MariaDB has no filtered indexes — so reusing a retired name means unarchiving
// or renaming the archived tag. TransactionTagService keeps an OrdinalIgnoreCase guard alongside it,
// which is the only implementation the FK/index-free EF InMemory tiers run.
[Index(nameof(Name), IsUnique = true)]
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

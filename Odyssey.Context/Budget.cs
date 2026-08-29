using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Odyssey.Context;

[Index(nameof(Archived))]
public class Budget
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid BudgetId { get; set; }

    [StringLength(64)]
    [Required]
    public required string Name { get; set; }

    [StringLength(256)]
    public string? Description { get; set; }

    [Required]
    public required DateTime StartDate { get; set; }

    [Required]
    public required DateTime EndDate { get; set; }

    public DateTime? Archived { get; set; }

    [StringLength(3)]
    [Required]
    public string BaseCurrencyCode { get; set; } = "USD";

    public ICollection<BudgetItem> BudgetItems { get; set; } = new List<BudgetItem>();
}

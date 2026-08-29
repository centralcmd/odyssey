using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;

namespace Odyssey.Context;

[Index(nameof(Archived))]
public class Currency
{
    [Key]
    [StringLength(3)]
    public required string CurrencyCode { get; set; }

    [StringLength(64)]
    [Required]
    public required string Name { get; set; }

    [Range(0, 12)]
    [Required]
    public required int MinorUnits { get; set; }

    [StringLength(8)]
    public string? Symbol { get; set; }

    public DateTime? Archived { get; set; }
}

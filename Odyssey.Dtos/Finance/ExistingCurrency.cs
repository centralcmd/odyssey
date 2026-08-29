using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

public sealed record ExistingCurrency
{
    [StringLength(3)]
    public required string CurrencyCode { get; set; }

    [StringLength(64)]
    public required string Name { get; set; }

    [Range(0, 12)]
    public int MinorUnits { get; set; }

    [StringLength(8)]
    public required string Symbol { get; set; }

    public DateTime? Archived { get; set; }
}

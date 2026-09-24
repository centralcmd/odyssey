using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

/// <summary>The estimate in force on a property as of a date (issue #167) — the shape of <see cref="CurrentAccountEstimate"/>.</summary>
public sealed record CurrentPropertyEstimate
{
    public decimal Value { get; set; }

    [StringLength(3)]
    public string? CurrencyCode { get; set; }

    public DateTime EffectiveFrom { get; set; }
}

using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

/// <summary>
/// The currently-effective estimated value for an account — the entry with the greatest
/// <c>EffectiveFrom</c> on or before the resolution date.
/// </summary>
public sealed record CurrentAccountEstimate
{
    public decimal Value { get; set; }

    [StringLength(3)]
    public string? CurrencyCode { get; set; }

    public DateTime EffectiveFrom { get; set; }
}

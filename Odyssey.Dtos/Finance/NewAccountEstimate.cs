using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

public sealed record NewAccountEstimate
{
    // Must be >= 0 (an estimated value cannot be negative); the service enforces it.
    [Required]
    public decimal Value { get; set; }

    // Must equal the account currency; defaults to it when omitted. A supplied value that differs is
    // rejected. The service normalizes and validates it.
    [StringLength(3)]
    public string? CurrencyCode { get; set; }

    [Required]
    public DateTime EffectiveFrom { get; set; }

    [StringLength(512)]
    public string? Note { get; set; }
}

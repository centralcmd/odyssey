using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

/// <summary>
/// Create/replace payload for a property estimate (issue #167) — the shape of
/// <see cref="NewAccountEstimate"/>. <b>Carries no owner id</b>: the property comes from the route and
/// is written by the service, so an estimate can be neither forged onto nor moved between owners.
/// </summary>
public sealed record NewPropertyEstimate
{
    // Must be >= 0 (an estimated value cannot be negative); the service enforces it.
    [Required]
    public decimal Value { get; set; }

    // Must equal the property currency; defaults to it when omitted. A supplied value that differs is
    // rejected. The service normalizes and validates it.
    [StringLength(3)]
    public string? CurrencyCode { get; set; }

    [Required]
    public DateTime EffectiveFrom { get; set; }

    [StringLength(512)]
    public string? Note { get; set; }
}

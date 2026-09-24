using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

/// <summary>One entry of a property's estimated-value history (issue #167), gated on <c>properties.estimates.read</c>.</summary>
public sealed record ExistingPropertyEstimate
{
    public required Guid PropertyEstimateId { get; set; }
    public required Guid PropertyId { get; set; }
    public decimal Value { get; set; }

    [StringLength(3)]
    public string? CurrencyCode { get; set; }

    public DateTime EffectiveFrom { get; set; }

    [StringLength(512)]
    public string? Note { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}

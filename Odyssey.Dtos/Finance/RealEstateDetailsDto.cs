using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

/// <summary>
/// Real-estate fields embedded in <see cref="NewProperty"/>/<see cref="ExistingProperty"/> (issue #167).
/// Carries no <c>PropertyId</c>: the detail row is an owned 1:1 child keyed by its parent, so there is
/// nothing to over-post. The service additionally uppercases <see cref="CountryCode"/> and rejects a
/// <see cref="BuildYear"/> in the future — a clock-dependent bound no <c>[Range]</c> can carry.
/// </summary>
public sealed record RealEstateDetailsDto
{
    [Required]
    [EnumDataType(typeof(RealEstateKind))]
    public RealEstateKind Kind { get; set; }

    [StringLength(256)]
    public string? AddressLine { get; set; }

    [StringLength(32)]
    public string? PostalCode { get; set; }

    [StringLength(128)]
    public string? City { get; set; }

    /// <summary>ISO 3166-1 alpha-2; stored uppercased.</summary>
    [StringLength(2, MinimumLength = 2)]
    [RegularExpression("^[A-Za-z]{2}$")]
    public string? CountryCode { get; set; }

    /// <summary>The land-registry identifier (e.g. Norwegian <i>gårds- og bruksnummer</i>) — free text, never parsed.</summary>
    [StringLength(64)]
    public string? CadastralNumber { get; set; }

    [Range(0, 1_000_000)]
    public decimal? LivingAreaSqm { get; set; }

    [Range(0, 1_000_000)]
    public decimal? PlotAreaSqm { get; set; }

    [Range(1000, 2200)]
    public int? BuildYear { get; set; }
}

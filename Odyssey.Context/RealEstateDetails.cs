using Microsoft.EntityFrameworkCore;
using Odyssey.Dtos.Finance;
using System.ComponentModel.DataAnnotations;

namespace Odyssey.Context;

/// <summary>
/// Real-estate sub-record of a <see cref="Property"/> whose <c>Type</c> is
/// <see cref="PropertyType.RealEstate"/> (issue #167). 1:1 with the parent, sharing its primary key —
/// the <see cref="PersonDetails"/> shape — and cascade-deleted with it.
///
/// <para>
/// The address is personal data (it identifies a natural person); it is reachable only through
/// <c>properties.read</c> and is never echoed in an error body or a log line.
/// </para>
/// </summary>
public class RealEstateDetails
{
    [Key]
    public Guid PropertyId { get; set; }

    public Property Property { get; set; } = null!;

    [Required]
    public RealEstateKind Kind { get; set; }

    [StringLength(256)]
    public string? AddressLine { get; set; }

    [StringLength(32)]
    public string? PostalCode { get; set; }

    [StringLength(128)]
    public string? City { get; set; }

    /// <summary>ISO 3166-1 alpha-2, uppercased by the service.</summary>
    [StringLength(2)]
    public string? CountryCode { get; set; }

    /// <summary>The land-registry identifier — free text, deliberately not parsed.</summary>
    [StringLength(64)]
    public string? CadastralNumber { get; set; }

    [Precision(18, 2)]
    public decimal? LivingAreaSqm { get; set; }

    [Precision(18, 2)]
    public decimal? PlotAreaSqm { get; set; }

    public int? BuildYear { get; set; }
}

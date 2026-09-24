using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

/// <summary>
/// Read projection of a property (issue #167), gated on <c>properties.read</c> alone and reachable
/// through no other claim. Exactly one of <see cref="RealEstateDetails"/>/<see cref="VehicleDetails"/>
/// is populated, matching <see cref="Type"/>.
///
/// <para>
/// The address, registration number and VIN are personal data or close to it (§7.9): they appear in no
/// cross-claim projection, embed or lookup, and never in an error body or a log line. Do not add this
/// record to another module's read path.
/// </para>
/// </summary>
public sealed record ExistingProperty
{
    public required Guid PropertyId { get; set; }

    [StringLength(256)]
    public required string Name { get; set; }

    [StringLength(256)]
    public required string Description { get; set; }

    public PropertyType Type { get; set; }

    [StringLength(3)]
    public required string CurrencyCode { get; set; }

    public DateTime? AcquiredDate { get; set; }

    public DateTime? DisposedDate { get; set; }

    [StringLength(1024)]
    public string? Notes { get; set; }

    public DateTime? Archived { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    /// <summary>Derived from <see cref="Archived"/>/<see cref="DisposedDate"/>, never stored.</summary>
    public PropertyStatus Status { get; set; }

    public RealEstateDetailsDto? RealEstateDetails { get; set; }

    public VehicleDetailsDto? VehicleDetails { get; set; }
}

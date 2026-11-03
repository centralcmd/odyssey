using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Odyssey.Dtos.Journal;

namespace Odyssey.Context;

/// <summary>
/// One of the household's important places — home, the cabin, school (issue #350). Shared by the whole
/// household like a contact: no owner column, access by claim. Archived, or deleted explicitly once nothing
/// references it (issue #380 §5.7).
///
/// <para>
/// <b>A location is personal data.</b> A home address with ≈ 1 m coordinates identifies where people live,
/// and <see cref="Description"/> is free text that may name people. An update overwrites in place and keeps
/// no previous value; the erasure posture is in <c>docs/deployment.md</c>.
/// </para>
///
/// <para>
/// <b>The one store for every place and address in the app (issue #380).</b> Calendar events, recurring
/// series, journal entries, contact addresses and real-estate properties reference a location by
/// <c>LocationId</c> (each key <c>RESTRICT</c>) instead of keeping their own copy. The six structured
/// address fields are optional; <see cref="AddressKey"/> is computed from them by the application
/// (<c>LocationAddressKey</c>) and is unique, so one address is one row. Coordinates and the time zone
/// are optional too, but all-or-none (<c>CK_Locations_CoordinatesAllOrNone</c>): a converted or
/// address-derived place has none until someone geocodes it.
/// </para>
///
/// <para>
/// The name is unique under the column's <c>_ci</c> collation — case- and accent-insensitive, archived rows
/// included. <c>LocationService</c> pre-checks with the same folding and maps the index's 1062 to the same
/// <c>409</c> on a race.
/// </para>
/// </summary>
[Index(nameof(Name), IsUnique = true)]
[Index(nameof(Archived), nameof(Name))]
[Index(nameof(AddressKey), IsUnique = true)]
public class Location
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid LocationId { get; set; }

    [Required]
    [StringLength(LocationRules.NameMaxLength)]
    public required string Name { get; set; }

    [StringLength(LocationRules.DescriptionMaxLength)]
    public string? Description { get; set; }

    [StringLength(LocationRules.AddressTextMaxLength)]
    public string? AddressText { get; set; }

    [StringLength(LocationRules.AddressLineMaxLength)]
    public string? Line1 { get; set; }

    [StringLength(LocationRules.AddressLineMaxLength)]
    public string? Line2 { get; set; }

    [StringLength(LocationRules.PostalCodeMaxLength)]
    public string? PostalCode { get; set; }

    [StringLength(LocationRules.CityMaxLength)]
    public string? City { get; set; }

    /// <summary>State / province / county.</summary>
    [StringLength(LocationRules.RegionMaxLength)]
    public string? Region { get; set; }

    /// <summary>Uppercase ISO 3166-1 alpha-2, <c>char(2)</c>.</summary>
    [StringLength(LocationRules.CountryCodeLength)]
    public string? CountryCode { get; set; }

    /// <summary>
    /// Lowercase hex SHA-256 of the normalized structured address (<c>LocationAddressKey</c>), computed by the
    /// application on every write of the six fields — never by the database. <c>char(64) ascii_bin</c>, unique.
    /// Null when <see cref="Line1"/> and <see cref="City"/> are both empty.
    /// </summary>
    [StringLength(LocationAddressKey.Length)]
    public string? AddressKey { get; set; }

    /// <summary>
    /// Set on a location merged away into another (issue #380 §5.6): the forwarder is archived and frozen, and
    /// a later find-or-create of its address follows this to the survivor. Self-FK, <c>RESTRICT</c>; chains are
    /// at most one hop.
    /// </summary>
    public Guid? MergedIntoLocationId { get; set; }

    /// <summary>
    /// The weather point this place reads its weather from (issue #387), or null. Many places may share one
    /// point. <c>SET NULL</c>: deleting a point unlinks its places and never blocks. Written only by
    /// <c>PUT /api/locations/{id}/weather-point</c> — no other location, contact or property write takes it.
    /// </summary>
    public Guid? WeatherPointId { get; set; }

    /// <summary>WGS 84, rounded to 5 decimals on write. Null together with <see cref="Longitude"/> and
    /// <see cref="TimeZoneId"/>, never alone.</summary>
    [Precision(7, 5)]
    public decimal? Latitude { get; set; }

    [Precision(8, 5)]
    public decimal? Longitude { get; set; }

    public int? AltitudeMeters { get; set; }

    [StringLength(64)]
    public string? TimeZoneId { get; set; }

    /// <summary>Advisory provenance, asserted by the client and trusted for nothing.</summary>
    public LocationSource Source { get; set; }

    [StringLength(LocationRules.SourceReferenceMaxLength)]
    public string? SourceReference { get; set; }

    public DateTime? Archived { get; set; }

    public string? CreatedByUserId { get; set; }

    public string? UpdatedByUserId { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}

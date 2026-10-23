using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Odyssey.Dtos.Journal;

namespace Odyssey.Context;

/// <summary>
/// One of the household's important places — home, the cabin, school (issue #350). Shared by the whole
/// household like a contact: no owner column, access by claim. Archived, never deleted.
///
/// <para>
/// <b>A location is personal data.</b> A home address with ≈ 1 m coordinates identifies where people live,
/// and <see cref="Description"/> is free text that may name people. An update overwrites in place and keeps
/// no previous value; the erasure posture is in <c>docs/deployment.md</c>.
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

    /// <summary>WGS 84, rounded to 5 decimals on write.</summary>
    [Precision(7, 5)]
    public decimal Latitude { get; set; }

    [Precision(8, 5)]
    public decimal Longitude { get; set; }

    public int? AltitudeMeters { get; set; }

    [Required]
    [StringLength(64)]
    public required string TimeZoneId { get; set; }

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

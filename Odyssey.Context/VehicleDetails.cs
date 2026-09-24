using Odyssey.Dtos.Finance;
using System.ComponentModel.DataAnnotations;

namespace Odyssey.Context;

/// <summary>
/// Vehicle sub-record of a <see cref="Property"/> whose <c>Type</c> is <see cref="PropertyType.Vehicle"/>
/// (issue #167). 1:1 with the parent, sharing its primary key — the <see cref="OrganizationDetails"/>
/// shape — and cascade-deleted with it.
///
/// <para>
/// The registration number and VIN resolve to a registered keeper in a public motor register, so both
/// are indirectly identifying personal data. Neither is unique: a plate is reissued, and two records may
/// legitimately describe one vehicle across a sale.
/// </para>
/// </summary>
public class VehicleDetails
{
    [Key]
    public Guid PropertyId { get; set; }

    public Property Property { get; set; } = null!;

    [Required]
    public VehicleKind Kind { get; set; }

    /// <summary>Uppercased and whitespace-stripped by the service.</summary>
    [StringLength(16)]
    public string? RegistrationNumber { get; set; }

    /// <summary>Uppercased and whitespace-stripped by the service; no check-digit validation.</summary>
    [StringLength(32)]
    public string? Vin { get; set; }

    [StringLength(64)]
    public string? Make { get; set; }

    [StringLength(64)]
    public string? Model { get; set; }

    public int? ModelYear { get; set; }

    public DateTime? FirstRegisteredDate { get; set; }
}

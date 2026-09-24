using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

/// <summary>
/// Vehicle fields embedded in <see cref="NewProperty"/>/<see cref="ExistingProperty"/> (issue #167).
/// The service uppercases and whitespace-strips <see cref="RegistrationNumber"/> and <see cref="Vin"/>,
/// and rejects a <see cref="ModelYear"/> more than one year in the future. There is deliberately no VIN
/// check-digit validation: the standard is not universal outside North America, and a false rejection
/// is worse than an unvalidated string. Neither identifier is unique — a plate is reissued, and two
/// records may describe one vehicle across a sale.
/// </summary>
public sealed record VehicleDetailsDto
{
    [Required]
    [EnumDataType(typeof(VehicleKind))]
    public VehicleKind Kind { get; set; }

    [StringLength(16)]
    public string? RegistrationNumber { get; set; }

    [StringLength(32)]
    public string? Vin { get; set; }

    [StringLength(64)]
    public string? Make { get; set; }

    [StringLength(64)]
    public string? Model { get; set; }

    [Range(1900, 2200)]
    public int? ModelYear { get; set; }

    public DateTime? FirstRegisteredDate { get; set; }
}

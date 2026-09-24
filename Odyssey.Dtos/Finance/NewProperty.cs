using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

/// <summary>
/// Create/replace payload for a property (issue #167). Carries the base fields plus exactly one of
/// <see cref="RealEstateDetails"/>/<see cref="VehicleDetails"/>, matching <see cref="Type"/> — the
/// <c>NewContact</c> shape.
///
/// <para>
/// On <c>PUT</c> the <see cref="Type"/> must still be present and must equal the stored type: it is
/// never silently ignored, so a caller that believed it was changing the type is told it did not
/// (a <c>422</c> from the service, since the bound depends on the persisted row).
/// </para>
/// </summary>
public sealed record NewProperty : IValidatableObject
{
    [Required]
    [StringLength(256)]
    public required string Name { get; set; }

    [Required]
    [StringLength(256)]
    public required string Description { get; set; }

    // Nullable so an omitted type is a [Required] failure rather than silently binding to RealEstate (0).
    [Required]
    [EnumDataType(typeof(PropertyType))]
    public PropertyType? Type { get; set; }

    [Required]
    [StringLength(3, MinimumLength = 3)]
    public required string CurrencyCode { get; set; }

    public DateTime? AcquiredDate { get; set; }

    public DateTime? DisposedDate { get; set; }

    [StringLength(1024)]
    public string? Notes { get; set; }

    public bool Archived { get; set; }

    public RealEstateDetailsDto? RealEstateDetails { get; set; }

    public VehicleDetailsDto? VehicleDetails { get; set; }

    /// <summary>
    /// Cross-field rules (§8): exactly one detail sub-object, matching <see cref="Type"/>, and a
    /// disposal that does not precede the acquisition. Runs under <c>[ApiController]</c> model
    /// validation; the service re-runs it for direct (non-HTTP) callers.
    /// </summary>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Type == PropertyType.RealEstate)
        {
            if (RealEstateDetails is null)
            {
                yield return new ValidationResult(
                    "Real-estate details are required for a RealEstate property.", [nameof(RealEstateDetails)]);
            }

            if (VehicleDetails is not null)
            {
                yield return new ValidationResult(
                    "Vehicle details must not be supplied for a RealEstate property.", [nameof(VehicleDetails)]);
            }
        }
        else if (Type == PropertyType.Vehicle)
        {
            if (VehicleDetails is null)
            {
                yield return new ValidationResult(
                    "Vehicle details are required for a Vehicle property.", [nameof(VehicleDetails)]);
            }

            if (RealEstateDetails is not null)
            {
                yield return new ValidationResult(
                    "Real-estate details must not be supplied for a Vehicle property.", [nameof(RealEstateDetails)]);
            }
        }

        if (AcquiredDate is { } acquired && DisposedDate is { } disposed && disposed < acquired)
        {
            yield return new ValidationResult(
                "The disposed date must not precede the acquired date.", [nameof(DisposedDate)]);
        }
    }
}

using Odyssey.Dtos;
using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Journal;

/// <summary>
/// Create/replace payload for a contact (issue #325). Carries the base fields plus exactly one
/// of <see cref="PersonDetails"/>/<see cref="OrganizationDetails"/>, matching <see cref="Type"/>. The
/// contact collections (addresses/emails/phones) are managed through their own sub-resource endpoints,
/// not this DTO.
/// </summary>
public sealed record NewContact : IValidatableObject
{
    [Required]
    [EnumDataType(typeof(ContactType))]
    public ContactType Type { get; set; }

    /// <summary>Optional display-name override; null means "use the computed fallback".</summary>
    [StringLength(128)]
    public string? DisplayName { get; set; }

    [StringLength(1024)]
    public string? Notes { get; set; }

    public required bool Archived { get; set; }

    /// <summary>
    /// Optional external identity anchor (issue #338 §6) — a caller-supplied vCard <c>UID</c>-shaped
    /// value. <c>null</c>/omitted means "auto-generate one" (<c>urn:uuid:{Guid.NewGuid()}</c>); a value
    /// already used by a different contact is a validation failure, not silently ignored.
    /// </summary>
    [StringLength(255)]
    public string? ExternalUid { get; set; }

    public PersonDetailsDto? PersonDetails { get; set; }

    public OrganizationDetailsDto? OrganizationDetails { get; set; }

    /// <summary>
    /// Cross-field rule (§9): exactly one details sub-object must be populated, matching
    /// <see cref="Type"/>. Runs identically on the POST and PUT-as-upsert-create paths via
    /// <c>[ApiController]</c> model validation (architect finding #6); the service re-checks it for
    /// direct (non-HTTP) callers.
    /// </summary>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Type == ContactType.Person)
        {
            if (PersonDetails is null)
            {
                yield return new ValidationResult(
                    "Person details are required for a Person contact.", [nameof(PersonDetails)]);
            }

            if (OrganizationDetails is not null)
            {
                yield return new ValidationResult(
                    "Organization details must not be supplied for a Person contact.", [nameof(OrganizationDetails)]);
            }
        }
        else if (Type == ContactType.Organization)
        {
            if (OrganizationDetails is null)
            {
                yield return new ValidationResult(
                    "Organization details are required for an Organization contact.", [nameof(OrganizationDetails)]);
            }

            if (PersonDetails is not null)
            {
                yield return new ValidationResult(
                    "Person details must not be supplied for an Organization contact.", [nameof(PersonDetails)]);
            }
        }
    }
}

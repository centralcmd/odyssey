using Odyssey.Dtos;
using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Journal;

// Converting a contact between Person and Organization in place (issue #335). Declared here, in the
// zero-reference Dtos project, so the API, the typed client and the WASM dialog name one vocabulary.

/// <summary>
/// The camelCase field names the conversion preview reports in <see cref="ContactConversionPreview.LostFields"/>
/// and <see cref="ContactConvertedField"/> (issue #335 §3.1). One declaration, so the client keys its
/// labels on the same symbol the server emits.
/// </summary>
public static class ContactConversionFields
{
    public const string FirstName = "firstName";
    public const string MiddleName = "middleName";
    public const string LastName = "lastName";
    public const string DateOfBirth = "dateOfBirth";
    public const string DateOfDeath = "dateOfDeath";
    public const string Sex = "sex";
    public const string Title = "title";
    public const string Company = "company";
    public const string LegalName = "legalName";
    public const string OrganizationNumber = "organizationNumber";
    public const string Website = "website";
    public const string EstablishedDate = "establishedDate";
    public const string DissolvedDate = "dissolvedDate";
}

/// <summary>Stable discriminators surfaced as the problem-details <c>code</c> and the preview's notes.</summary>
public static class ContactConversionCodes
{
    /// <summary>The <c>409</c> refusing a type change while a type-restricted link exists.</summary>
    public const string Blocked = "contact-type-change-blocked";

    /// <summary>The <c>409</c> refusing a conversion whose <c>expectedUpdatedAt</c> no longer matches.</summary>
    public const string Stale = "contact-conversion-stale";

    /// <summary>A preview note: an Organization → Person split acted on a legal name of more than two words.</summary>
    public const string NameSplitLowConfidence = "nameSplitLowConfidence";
}

/// <summary>Caps on the blocker id lists a preview or a <c>409</c> carries (issue #335 §7.2).</summary>
public static class ContactConversionLimits
{
    /// <summary>The most photo ids / properties listed per blocker class; the count stays exact.</summary>
    public const int MaxBlockerItems = 50;
}

/// <summary>
/// <c>POST /api/contacts/{id}/conversion</c>. Carries the target, the precondition and the name
/// overrides only: every other target field is derived server-side from the stored row (§7.3).
/// </summary>
public sealed record ContactConversionRequest : IValidatableObject
{
    /// <summary>Nullable so an omitted value is a "required" <c>400</c>, never a bind to <c>0</c>.</summary>
    [Required]
    [EnumDataType(typeof(ContactType))]
    public ContactType? TargetType { get; set; }

    /// <summary>
    /// The <c>UpdatedAt</c> the caller decided from, echoed verbatim from the preview or the contact.
    /// Compared exactly; a mismatch is a <c>409</c> with code <see cref="ContactConversionCodes.Stale"/>.
    /// </summary>
    [Required]
    public DateTime? ExpectedUpdatedAt { get; set; }

    /// <summary>Carry birth ↔ established and death ↔ dissolved over; <c>false</c> drops them.</summary>
    public bool CarryLifecycleDates { get; set; } = true;

    /// <summary>Replaces the derived first name. Organization → Person only.</summary>
    [StringLength(128)]
    public string? FirstName { get; set; }

    /// <summary>Replaces the derived last name. Organization → Person only.</summary>
    [StringLength(128)]
    public string? LastName { get; set; }

    /// <summary>Replaces the derived legal name. Person → Organization only.</summary>
    [StringLength(256)]
    public string? LegalName { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (TargetType == ContactType.Person && LegalName is not null)
        {
            yield return new ValidationResult(
                "A legal name override applies only to a conversion to an organization.", [nameof(LegalName)]);
        }

        if (TargetType == ContactType.Organization)
        {
            if (FirstName is not null)
            {
                yield return new ValidationResult(
                    "A first name override applies only to a conversion to a person.", [nameof(FirstName)]);
            }

            if (LastName is not null)
            {
                yield return new ValidationResult(
                    "A last name override applies only to a conversion to a person.", [nameof(LastName)]);
            }
        }
    }
}

/// <summary>
/// The query of <c>GET /api/contacts/{id}/conversion-preview</c>. A <c>sealed class</c> because it is a
/// query-string binding model, the <c>QueryParams</c> convention.
/// </summary>
public sealed class ContactConversionPreviewQuery
{
    [Required]
    [EnumDataType(typeof(ContactType))]
    public ContactType? TargetType { get; set; }

    public bool CarryLifecycleDates { get; set; } = true;
}

/// <summary>The read-only dry run of a conversion (issue #335 §5.1).</summary>
public sealed record ContactConversionPreview
{
    public ContactType CurrentType { get; set; }

    public ContactType TargetType { get; set; }

    /// <summary>The value to echo as <see cref="ContactConversionRequest.ExpectedUpdatedAt"/>.</summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>True only with no blocker and no unresolved derivation issue.</summary>
    public bool CanConvert { get; set; }

    /// <summary>The derived target details; exactly one of the two is non-null.</summary>
    public PersonDetailsDto? PersonDetails { get; set; }

    public OrganizationDetailsDto? OrganizationDetails { get; set; }

    /// <summary><see cref="ContactConversionFields"/> names of source fields holding a value that will be deleted.</summary>
    public List<string> LostFields { get; set; } = [];

    public List<ContactConvertedField> ConvertedFields { get; set; } = [];

    /// <summary>Fields the caller must override before the conversion can succeed.</summary>
    public List<ContactConversionIssue> DerivationIssues { get; set; } = [];

    public List<ContactConversionNote> Notes { get; set; } = [];

    public ContactRemappedLabels RemappedLabels { get; set; } = new();

    public ContactTypeChangeBlockers Blockers { get; set; } = new();
}

/// <summary>A set value that moves to a differently-named field.</summary>
public sealed record ContactConvertedField
{
    public required string From { get; set; }

    public required string To { get; set; }
}

public sealed record ContactConversionIssue
{
    public required string Field { get; set; }

    public required string Message { get; set; }
}

public sealed record ContactConversionNote
{
    public required string Code { get; set; }

    public required string Field { get; set; }
}

/// <summary>How many contact-method labels clamp to <c>Other</c> on the target type.</summary>
public sealed record ContactRemappedLabels
{
    public int Addresses { get; set; }

    public int Emails { get; set; }

    public int Phones { get; set; }
}

/// <summary>
/// The two type-restricted links that refuse a type change (issue #335 §8.3). Counts are always exact;
/// the id lists are capped at <see cref="ContactConversionLimits.MaxBlockerItems"/> and are emptied by
/// the API for a caller without the matching read claim.
/// </summary>
public sealed record ContactTypeChangeBlockers
{
    /// <summary>Person → Organization: the contact is tagged as a person on these photos.</summary>
    public ContactPhotoTagBlocker PhotoPersonTags { get; set; } = new();

    /// <summary>Organization → Person: the contact is the homeowner association of these properties.</summary>
    public ContactHomeownerAssociationBlocker HomeownerAssociations { get; set; } = new();

    public bool HasAny() => PhotoPersonTags.Count > 0 || HomeownerAssociations.Count > 0;
}

public sealed record ContactPhotoTagBlocker
{
    public int Count { get; set; }

    /// <summary>Filled only for a <c>photos.read</c> holder.</summary>
    public List<Guid> PhotoIds { get; set; } = [];

    public bool Truncated { get; set; }
}

public sealed record ContactHomeownerAssociationBlocker
{
    public int Count { get; set; }

    /// <summary>Id and name only — never an address. Filled only for a <c>properties.read</c> holder.</summary>
    public List<ContactBlockingProperty> Properties { get; set; } = [];

    public bool Truncated { get; set; }
}

public sealed record ContactBlockingProperty
{
    public Guid Id { get; set; }

    public required string Name { get; set; }
}

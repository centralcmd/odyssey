using Odyssey.Dtos;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Odyssey.Context;

[Index(nameof(NormalizedName))]
[Index(nameof(Type), nameof(Archived))]
[Index(nameof(ExternalUid), IsUnique = true)]
public class Contact
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid ContactId { get; set; }

    /// <summary>
    /// Stable external identity anchor for vCard import/export (issue #338 §6) — decouples the vCard
    /// <c>UID</c> from <see cref="ContactId"/>. Always populated: auto-generated
    /// (<c>urn:uuid:{Guid.NewGuid()}</c>) on create unless the caller supplies one, and written verbatim
    /// from an imported vCard's <c>UID</c>. Unlike the similar <c>CalendarEvent</c>/
    /// <c>RecurrencePattern.ExternalUid</c>, this is required (not nullable) and unique (Contact has
    /// no "same UID, multiple rows" case).
    /// </summary>
    [Required]
    [StringLength(255)]
    public required string ExternalUid { get; set; }

    /// <summary>
    /// User-editable override for the contact's display name (issue #325). <c>null</c> means
    /// "use the computed fallback" (<c>FirstName + LastName</c> for a Person, <c>LegalName</c> for an
    /// Organization).
    /// </summary>
    [StringLength(128)]
    public string? DisplayName { get; set; }

    /// <summary>
    /// Search/sort key derived from the resolved display value (truncated to 256 chars before
    /// normalizing), recomputed on every save. Widened from 128 to 256 (issue #325) since the
    /// resolved value can reach ~257 chars (FirstName 128 + space + LastName 128).
    /// </summary>
    [StringLength(256)]
    [Required]
    public required string NormalizedName { get; set; }

    [Required]
    public ContactType Type { get; set; }

    /// <summary>
    /// Deprecated — retained temporarily through v1 (issue #325 §15), superseded by
    /// <see cref="OrganizationDetails.OrganizationNumber"/>. Dropped in the follow-up cleanup migration.
    /// </summary>
    [StringLength(64)]
    public string? OrganizationNumber { get; set; }

    /// <summary>Free-text notes (renamed from <c>Description</c> in issue #325 — no semantic change).</summary>
    [StringLength(1024)]
    public string? Notes { get; set; }

    public DateTime? Archived { get; set; }

    /// <summary>Creation timestamp (UTC), new in issue #325.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Last-modification timestamp (UTC), new in issue #325 — bumped on the contact's own save
    /// and on any Address/EmailAddress/PhoneNumber child mutation (§9).
    /// </summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// Transitional snapshot of the pre-migration six-value <c>Type</c> ordinal (issue #325 §15) —
    /// a rollback/audit column dropped alongside <see cref="OrganizationNumber"/> in the follow-up
    /// cleanup migration.
    /// </summary>
    public int? LegacyType { get; set; }

    public PersonDetails? PersonDetails { get; set; }
    public OrganizationDetails? OrganizationDetails { get; set; }
    public ICollection<Address> Addresses { get; set; } = new List<Address>();
    public ICollection<EmailAddress> EmailAddresses { get; set; } = new List<EmailAddress>();
    public ICollection<PhoneNumber> PhoneNumbers { get; set; } = new List<PhoneNumber>();
}

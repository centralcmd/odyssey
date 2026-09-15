using Odyssey.Dtos;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Odyssey.Context;

[Index(nameof(NormalizedName))]
[Index(nameof(Type), nameof(Archived))]
[Index(nameof(ExternalUid), IsUnique = true)]
[Index(nameof(AvatarFileId), IsUnique = true)]
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

    /// <summary>Free-text notes (renamed from <c>Description</c> in issue #325 — no semantic change).</summary>
    [StringLength(1024)]
    public string? Notes { get; set; }

    /// <summary>
    /// The contact's one image — a profile picture for a <c>Person</c>, a company logo for an
    /// <c>Organization</c> (issue #86). A reference into the existing Files store, never a blob column:
    /// one file store, one lifecycle, one set of limits.
    ///
    /// <para>
    /// The FK is declared with an explicit <c>DeleteBehavior.SetNull</c> in <c>OdysseyContext</c> —
    /// EF's default for an optional relationship is <c>ClientSetNull</c>, which emits <c>RESTRICT</c>,
    /// and under that a <c>DELETE /api/files/{id}</c> naming an avatar would surface as a 500 instead
    /// of the graceful detach the contact wants (it falls back to its type glyph).
    /// </para>
    ///
    /// <para>
    /// The index is <b>unique</b> (nullable, so MariaDB permits many <c>NULL</c>s): a file is the
    /// avatar of at most one contact, which is what makes "deleting the contact deletes its avatar
    /// file" safe. A concurrent double-POST that violates it is a 409, never a 500.
    /// </para>
    ///
    /// <para>
    /// The reverse direction — contact deleted, avatar file deleted — is <b>not</b> expressible as a
    /// foreign key and lives in the contact-delete transaction, applying the shared release rule.
    /// </para>
    /// </summary>
    public Guid? AvatarFileId { get; set; }

    public DateTime? Archived { get; set; }

    /// <summary>Creation timestamp (UTC), new in issue #325.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Last-modification timestamp (UTC), new in issue #325 — bumped on the contact's own save
    /// and on any Address/EmailAddress/PhoneNumber/ContactAlias child mutation (§9; issue #48).
    /// </summary>
    public DateTime UpdatedAt { get; set; }

    public PersonDetails? PersonDetails { get; set; }
    public OrganizationDetails? OrganizationDetails { get; set; }
    /// <summary>
    /// The contact's alternative names (issue #48). A first-class child collection mirroring the
    /// three below; it is a name, not a contact method, so it is surfaced in its own section.
    /// </summary>
    public ICollection<ContactAlias> Aliases { get; set; } = new List<ContactAlias>();

    public ICollection<Address> Addresses { get; set; } = new List<Address>();
    public ICollection<EmailAddress> EmailAddresses { get; set; } = new List<EmailAddress>();
    public ICollection<PhoneNumber> PhoneNumbers { get; set; } = new List<PhoneNumber>();
}

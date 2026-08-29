using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Odyssey.Dtos.Application;

namespace Odyssey.Context;

/// <summary>
/// A user's self-owned profile (issue #316), 1:1 with <see cref="ApplicationUser"/> in the same context
/// and database. Modelled with its own <see cref="UserProfileId"/> <c>Guid</c> PK plus a separate
/// <see cref="UserId"/> FK carrying a unique index (matching the <c>Contact.ContactId</c>
/// pattern) — the unique index enforces the 1:1 and the FK's cascade delete removes the profile with the
/// user. Every field is nullable at the schema level: a row is only written on a completed <c>PUT</c>,
/// so it is only ever absent or complete ("required" is the completeness rule in §9, not a NOT NULL
/// constraint, keeping the migration additive and reversible).
/// </summary>
[Index(nameof(UserId), IsUnique = true)]
public class UserProfile
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid UserProfileId { get; set; }

    /// <summary>FK to <see cref="ApplicationUser.Id"/>; unique (1:1) with cascade delete.</summary>
    [Required]
    public string UserId { get; set; } = string.Empty;

    [MaxLength(128)]
    public string? FirstName { get; set; }

    [MaxLength(128)]
    public string? MiddleName { get; set; }

    [MaxLength(128)]
    public string? LastName { get; set; }

    /// <summary>User-editable override; <c>null</c> ⇒ the resolver falls back to <see cref="FirstName"/>.</summary>
    [MaxLength(256)]
    public string? DisplayName { get; set; }

    /// <summary>Free text (e.g. "Dr.", "CFO"); not used in attribution.</summary>
    [MaxLength(128)]
    public string? Title { get; set; }

    /// <summary>Date of birth (no time component); required on a completed profile.</summary>
    public DateOnly? BirthDate { get; set; }

    /// <summary>Required on a completed profile; nullable at the schema level.</summary>
    public Sex? Sex { get; set; }
}

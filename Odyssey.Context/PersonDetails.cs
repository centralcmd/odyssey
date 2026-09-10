using Odyssey.Dtos;
using System.ComponentModel.DataAnnotations;

namespace Odyssey.Context;

/// <summary>
/// Person-specific sub-record of a <see cref="Contact"/> whose <c>Type</c> is
/// <see cref="ContactType.Person"/> (issue #325). 1:1 with the parent, sharing its primary key.
/// </summary>
public class PersonDetails
{
    [Key]
    public Guid ContactId { get; set; }

    public Contact Contact { get; set; } = null!;

    [Required]
    [StringLength(128)]
    public required string FirstName { get; set; }

    [Required]
    [StringLength(128)]
    public required string LastName { get; set; }

    /// <summary>
    /// Optional middle name (issue #48) — stored, rendered and searchable, but deliberately outside
    /// the <c>First Last</c> fallback feeding <see cref="Contact.NormalizedName"/>, so adding one
    /// shifts no existing contact's search key, sort position or rendered name.
    /// </summary>
    [StringLength(128)]
    public string? MiddleName { get; set; }

    /// <summary>Optional birth date — a pure date (no time component). Must not be in the future.</summary>
    public DateOnly? DateOfBirth { get; set; }

    /// <summary>
    /// Optional date of death (issue #48). Recording it archives nothing and removes no capability:
    /// finance rows legitimately reference a deceased counterparty and an estate is administered for
    /// years, so the record stays live and selectable everywhere.
    /// </summary>
    public DateOnly? DateOfDeath { get; set; }

    /// <summary>Optional sex (issue #325 v5). <c>null</c> means unspecified.</summary>
    public Sex? Sex { get; set; }

    /// <summary>Optional free-text job title (issue #325 v5), e.g. "Senior Engineer" — not an honorific.</summary>
    [StringLength(128)]
    public string? Title { get; set; }

    /// <summary>
    /// Optional free-text employer name (issue #325 v5) — deliberately NOT a foreign key to another
    /// contact; an informal note, not a modeled relationship.
    /// </summary>
    [StringLength(256)]
    public string? Company { get; set; }
}

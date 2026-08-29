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

    /// <summary>Optional birth date — a pure date (no time component). Must not be in the future.</summary>
    public DateOnly? DateOfBirth { get; set; }

    public RelationshipType? RelationshipType { get; set; }

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

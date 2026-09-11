using Odyssey.Dtos;
using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Journal;

/// <summary>
/// Person-specific fields embedded in <see cref="NewContact"/>/<see cref="ExistingContact"/>
/// (issue #325). <c>DateOfBirth</c> is exposed as <see cref="DateTime"/>? (midnight, time ignored)
/// because <c>MudDatePicker</c> binds to it; it is stored as a pure <c>DateOnly</c> server-side.
/// </summary>
public sealed record PersonDetailsDto
{
    [Required]
    [StringLength(128)]
    public required string FirstName { get; set; }

    [Required]
    [StringLength(128)]
    public required string LastName { get; set; }

    /// <summary>
    /// Optional middle name as a passport, bank record or imported vCard carries it (issue #48).
    /// Stored, rendered and <b>searchable</b>, but deliberately NOT part of the <c>First Last</c>
    /// display-name fallback — adding one shifts no contact's resolved name, search key or sort
    /// position (§2 Non-Goal 4).
    /// </summary>
    [StringLength(128)]
    public string? MiddleName { get; set; }

    public DateTime? DateOfBirth { get; set; }

    /// <summary>
    /// Optional date of death (issue #48). Recording it changes no state and removes no capability:
    /// the contact is not archived, keeps its links and stays selectable as a counterparty,
    /// custodian, insurance party and journal participant (§2 Non-Goal 3). Must not be in the future,
    /// nor before <see cref="DateOfBirth"/> when both are present.
    /// </summary>
    public DateTime? DateOfDeath { get; set; }

    [EnumDataType(typeof(RelationshipType))]
    public RelationshipType? RelationshipType { get; set; }

    [EnumDataType(typeof(Sex))]
    public Sex? Sex { get; set; }

    [StringLength(128)]
    public string? Title { get; set; }

    [StringLength(256)]
    public string? Company { get; set; }
}

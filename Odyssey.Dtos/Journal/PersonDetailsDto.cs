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

    public DateTime? DateOfBirth { get; set; }

    [EnumDataType(typeof(RelationshipType))]
    public RelationshipType? RelationshipType { get; set; }

    [EnumDataType(typeof(Sex))]
    public Sex? Sex { get; set; }

    [StringLength(128)]
    public string? Title { get; set; }

    [StringLength(256)]
    public string? Company { get; set; }
}

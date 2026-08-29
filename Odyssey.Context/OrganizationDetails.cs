using Odyssey.Dtos;
using System.ComponentModel.DataAnnotations;

namespace Odyssey.Context;

/// <summary>
/// Organization-specific sub-record of a <see cref="Contact"/> whose <c>Type</c> is
/// <see cref="ContactType.Organization"/> (issue #325). 1:1 with the parent, sharing its
/// primary key.
/// </summary>
public class OrganizationDetails
{
    [Key]
    public Guid ContactId { get; set; }

    public Contact Contact { get; set; } = null!;

    [Required]
    [StringLength(256)]
    public required string LegalName { get; set; }

    [StringLength(64)]
    public string? OrganizationNumber { get; set; }

    /// <summary>Optional website — validated as a well-formed http/https URL (§9, security finding F3).</summary>
    [StringLength(2048)]
    public string? Website { get; set; }
}

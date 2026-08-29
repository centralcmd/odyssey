using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Journal;

/// <summary>
/// Organization-specific fields embedded in <see cref="NewContact"/>/
/// <see cref="ExistingContact"/> (issue #325). <c>Website</c> is additionally restricted to
/// http/https schemes by the service (§9, security finding F3).
/// </summary>
public sealed record OrganizationDetailsDto
{
    [Required]
    [StringLength(256)]
    public required string LegalName { get; set; }

    [StringLength(64)]
    public string? OrganizationNumber { get; set; }

    [StringLength(2048)]
    [Url]
    public string? Website { get; set; }
}

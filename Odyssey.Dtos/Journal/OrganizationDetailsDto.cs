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

    /// <summary>
    /// Optional date the organization came into existence (issue #48). May stand without
    /// <see cref="DissolvedDate"/> — the founding date of an old institution is frequently unknown,
    /// and so is the other way round. Must not be in the future.
    /// </summary>
    public DateTime? EstablishedDate { get; set; }

    /// <summary>
    /// Optional date the organization was dissolved (issue #48) — the term a business register
    /// publishes (Norwegian <i>oppløst</i>), not "closed". Reads as historical; nothing is
    /// archived and every link stays intact. Must not be in the future, nor precede
    /// <see cref="EstablishedDate"/> when both are present.
    /// </summary>
    public DateTime? DissolvedDate { get; set; }
}

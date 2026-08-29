using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Odyssey.Dtos.Application;

namespace Odyssey.Context;

/// <summary>
/// A published Terms of Service version (issue #354 §6). Insert-only: publishing never mutates or
/// deletes a prior version, so the table is the full authoring history. Starts empty — there is no
/// seeded version, and "no ToS published yet" is a supported state throughout the feature.
/// </summary>
public class TermsOfServiceVersion
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    /// <summary>Plain text (no Markdown in v1). Stored as <c>longtext</c>; capped at 50,000 chars in validation.</summary>
    [Required]
    [StringLength(LegalLimits.MaxTermsOfServiceContentLength, MinimumLength = 1)]
    public string Content { get; set; } = string.Empty;

    /// <summary>UTC. The "current" version is the highest <see cref="PublishedAt"/>, ties broken by highest <see cref="Id"/>.</summary>
    public DateTime PublishedAt { get; set; }

    /// <summary>Nullable FK to <c>AspNetUsers</c> with <c>SetNull</c>: a deleted admin leaves the version intact.</summary>
    [StringLength(255)]
    public string? PublishedByUserId { get; set; }
}

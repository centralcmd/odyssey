using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Odyssey.Context;

/// <summary>
/// One user's accept/decline response to a specific <see cref="TermsOfServiceVersion"/> (issue #354 §6).
/// Insert-only in normal operation, with the same transactional pseudonymization exception as
/// <see cref="LicenseAcceptance"/>.
/// </summary>
[Index(nameof(UserId), nameof(TermsOfServiceVersionId), nameof(RespondedAt))]
public class TermsOfServiceAcceptance
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    /// <summary>Matches <c>AspNetUsers.Id</c>'s column width; not an enforced FK (see <see cref="LicenseAcceptance"/>).</summary>
    [Required]
    [StringLength(255)]
    public string UserId { get; set; } = string.Empty;

    /// <summary>FK to <see cref="TermsOfServiceVersion"/> with <c>Restrict</c> — no endpoint ever deletes a version.</summary>
    public int TermsOfServiceVersionId { get; set; }

    public bool Accepted { get; set; }

    public DateTime RespondedAt { get; set; }
}

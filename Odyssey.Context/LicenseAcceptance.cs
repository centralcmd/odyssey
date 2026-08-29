using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Odyssey.Context;

/// <summary>
/// One user's accept/decline response to the repository <c>LICENSE</c> as it read at the moment they
/// responded (issue #354 §6). Insert-only in normal operation — the sole exception is the
/// <see cref="UserId"/> pseudonymization update performed, transactionally, when the referenced account
/// is deleted.
/// </summary>
/// <remarks>
/// <see cref="UserId"/> deliberately carries <em>no</em> foreign key to <c>AspNetUsers</c>: this is a
/// compliance record that must outlive the account it references. Deletion overwrites the value with a
/// keyed digest of the user's email rather than leaving it pointing at a dead (and potentially reusable)
/// id — see <c>LegalPseudonymizer</c>.
/// </remarks>
[Index(nameof(UserId), nameof(LicenseHash), nameof(RespondedAt))]
public class LicenseAcceptance
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    /// <summary>Matches <c>AspNetUsers.Id</c>'s column width; not an enforced FK (see the type remarks).</summary>
    [Required]
    [StringLength(255)]
    public string UserId { get; set; } = string.Empty;

    /// <summary>SHA-256 hex digest of the <c>LICENSE</c> content this response was given against.</summary>
    [Required]
    [StringLength(64)]
    public string LicenseHash { get; set; } = string.Empty;

    public bool Accepted { get; set; }

    public DateTime RespondedAt { get; set; }
}

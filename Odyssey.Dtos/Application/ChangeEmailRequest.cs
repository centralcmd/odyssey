using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Application;

/// <summary>
/// Body of <c>POST /api/account/email</c> (issue #246) — the first-party email-change request, which
/// replaced the unguarded <c>newEmail</c> half of Identity's <c>POST /manage/info</c>.
/// </summary>
/// <remarks>
/// <c>NewEmail</c> is bounded at 256 to match the Identity <c>Email</c>/<c>UserName</c> columns, and the
/// password carries the same 256 ceiling as <see cref="ChangePasswordRequest"/> so an oversized string
/// never reaches the hasher.
/// </remarks>
public sealed record ChangeEmailRequest
{
    [Required]
    [EmailAddress]
    [StringLength(256)]
    public required string NewEmail { get; set; }

    [Required]
    [StringLength(256, MinimumLength = 1)]
    public required string CurrentPassword { get; set; }
}

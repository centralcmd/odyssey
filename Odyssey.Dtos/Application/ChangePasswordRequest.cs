using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Application;

/// <summary>
/// Body of <c>POST /api/account/password</c> (issue #406 §5.7) — the first-party change-password
/// endpoint, and the one write a password-gated session can reach.
/// </summary>
/// <remarks>
/// The upper bound is the point. <c>IdentityOptions.Password</c> sets only a <em>minimum</em> (16),
/// <c>PasswordOptions</c> has no maximum, and Identity's PBKDF2 hasher has no inherent input-size cap,
/// so without <see cref="StringLengthAttribute"/> a multi-megabyte string would reach the hasher.
/// <c>MinimumLength = 16</c> mirrors <c>RequiredLength</c> for a cheap pre-hash rejection; Identity
/// remains the authoritative policy gate for the character-class rules.
/// </remarks>
public sealed record ChangePasswordRequest
{
    [Required]
    [StringLength(256, MinimumLength = 1)]
    public required string CurrentPassword { get; set; }

    [Required]
    [StringLength(256, MinimumLength = 16)]
    public required string NewPassword { get; set; }
}

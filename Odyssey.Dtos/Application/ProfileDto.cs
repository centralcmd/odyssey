using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Application;

/// <summary>
/// The authenticated caller's own profile (issue #316). Bound by <c>PUT /api/profile</c> (writable
/// scalars only — no email/role/flags, so no over-posting) and returned by both endpoints.
/// <see cref="IsComplete"/> is response-only / server-computed and ignored on input.
/// </summary>
public sealed record ProfileDto
{
    [StringLength(128)]
    public string? FirstName { get; set; }

    [StringLength(128)]
    public string? MiddleName { get; set; }

    [StringLength(128)]
    public string? LastName { get; set; }

    [StringLength(256)]
    public string? DisplayName { get; set; }

    [StringLength(128)]
    public string? Title { get; set; }

    public DateOnly? BirthDate { get; set; }

    [EnumDataType(typeof(Sex))]
    public Sex? Sex { get; set; }

    /// <summary>Response-only: all four required fields (First/Last name, birth date, sex) present &amp; valid.</summary>
    public bool IsComplete { get; set; }

    /// <summary>
    /// Response-only (issue #406): an administrator triggered a password reset, so the caller must set a
    /// new password before the application will serve them. Read from the identity row on every fetch and
    /// ignored on input, exactly like <see cref="IsComplete"/> — a client that posts it cannot set or
    /// clear it. The authoritative enforcement is <c>PasswordChangeRequiredMiddleware</c>; this field only
    /// lets the client show the gate form instead of a screen of failed requests.
    /// </summary>
    public bool MustChangePassword { get; set; }
}

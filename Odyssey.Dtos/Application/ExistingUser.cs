using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Application;

public sealed record ExistingUser
{
    public string Id { get; init; } = string.Empty;

    public string? UserName { get; init; }

    /// <summary>
    /// The resolver's resolved label for this user (issue #316) — <c>DisplayName ?? FirstName ?? email</c>
    /// under the admin caller's claims. This is the resolver output, never a bare column read.
    /// </summary>
    [StringLength(256)]
    public string? DisplayName { get; init; }

    public string? Email { get; init; }

    public bool EmailConfirmed { get; init; }

    public bool Enabled { get; init; }

    public DateTimeOffset? LockoutEnd { get; init; }

    public string Role { get; init; } = string.Empty;

    public DateTimeOffset? CreatedAtUtc { get; init; }

    /// <summary>
    /// An admin-initiated password reset is outstanding for this user (issue #406) — they are blocked from
    /// the application until they set a new password. Read-only, under the existing <c>users.read</c> gate.
    /// </summary>
    public bool MustChangePassword { get; init; }

    // The profile's structured name/birth attributes. This whole endpoint is already gated on
    // users.read, so surfacing them here is an admin-only view, never mixed into the resolver's
    // cross-cutting DisplayName above (that one is read under the target's own claims elsewhere).
    [StringLength(128)]
    public string? FirstName { get; init; }

    [StringLength(128)]
    public string? MiddleName { get; init; }

    [StringLength(128)]
    public string? LastName { get; init; }

    public DateOnly? BirthDate { get; init; }

    [EnumDataType(typeof(Sex))]
    public Sex? Sex { get; init; }
}

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

    /// <summary>
    /// This user's profile-picture version token (issue #94 §6), per row on the <c>users.read</c>-gated
    /// admin list — so <c>/users</c> renders 50 rows with <b>zero</b> speculative requests.
    ///
    /// <para>
    /// <b>The token means "renderable by this caller", not "a row exists".</b> It is <c>null</c>
    /// whenever the read path would refuse — today, an <b>administratively disabled</b> subject — and
    /// the nulling belongs at <c>UserAdministrationService.MapUserAsync</c>, the single point every
    /// <see cref="ExistingUser"/> is projected through: the list, the detail panel's
    /// <c>GET /api/users/{id}</c>, <i>and</i> the row returned by the admin update that performs the
    /// disabling, which is the first render after an account is disabled and so the likeliest moment
    /// for a stale non-null token. Any future condition that makes the read refuse must null the token
    /// in the same commit.
    /// </para>
    ///
    /// <para>
    /// <b>Do not derive this from <see cref="Enabled"/>.</b> The two are produced from different
    /// predicates: <see cref="Enabled"/> uses sign-in eligibility, which a <i>transient</i> lockout
    /// makes false, while this uses the administrative-disable sentinel. So a row can legitimately show
    /// <c>Enabled == false</c> beside a rendering picture — someone who mistyped their password five
    /// times is locked out, not disabled. The tempting "consistency fix" of deriving one from the other
    /// reintroduces the <c>200 → 404 → 200</c> password-spray oracle, quietly.
    /// </para>
    /// </summary>
    public Guid? ProfileImageVersion { get; init; }
}

using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Application;

/// <summary>
/// What the server knows about one stored secret, with <strong>no field derived from the plaintext</strong>
/// — no length, no hash, no prefix, no last-four (issue #444 §7).
///
/// <para>
/// Members start at 1, matching <see cref="SettingFaultKind"/>'s convention: a defaulted or zero
/// <c>int</c> is never a valid value in this codebase, so a miss-path <c>TryGetValue</c> cannot yield
/// a meaningful state by accident.
/// </para>
/// </summary>
public enum SecretSettingState
{
    /// <summary>No row exists. <strong>Healthy</strong>, not degraded — the consumer behaves exactly as
    /// it does today with the credential unconfigured.</summary>
    NotSet = 1,

    /// <summary>A row exists and unprotects cleanly.</summary>
    Set = 2,

    /// <summary>
    /// A row exists but this server cannot decrypt it — the Data Protection key ring was replaced or
    /// lost, or the row carries an unrecognised <c>ProtectionScheme</c>. <strong>Degraded</strong>, and
    /// the one state that must never be collapsed into <see cref="NotSet"/>.
    /// </summary>
    Unreadable = 3,
}

/// <summary>
/// One entry of <c>GET /api/system-settings/secrets</c>. Every field here is safe under
/// <c>system-settings.read</c>: the key is a compile-time constant already visible in the client
/// bundle, the state carries no value content, and the attribution triple is the same one
/// <see cref="SystemSettingsDto"/> already exposes under the same claim through the same
/// claim-aware display-name resolver.
/// </summary>
public sealed record SecretSettingStatusDto
{
    [StringLength(100)]
    public required string Key { get; set; }

    [EnumDataType(typeof(SecretSettingState))]
    public SecretSettingState State { get; set; }

    public DateTime? UpdatedAt { get; set; }

    [StringLength(255)]
    public string? UpdatedBy { get; set; }

    public string? UpdatedByDisplayName { get; set; }
}

/// <summary>
/// The body of <c>PUT /api/system-settings/secrets/{key}</c> — exactly one property, deliberately.
///
/// <para>
/// It carries no key, no id, no timestamp and no actor: the key comes from the route and is matched
/// against the registry, and <c>UpdatedAt</c>/<c>UpdatedBy</c> are set server-side. There is no nested
/// object anywhere in the contract and therefore no relationship a caller could over-post.
/// </para>
///
/// <para>
/// <strong>It suppresses its own <see cref="ToString"/></strong> (§10). A record prints every member,
/// and a record's <c>ToString()</c> is exactly what surfaces in a logged exception context — so the
/// default implementation would put the submitted credential in the application log the first time an
/// unrelated exception carried this object. <c>[JsonIgnore]</c> is not the mechanism: it would break
/// model binding outright.
/// </para>
/// </summary>
public sealed record SecretSettingUpdate
{
    /// <summary>
    /// The plaintext credential. Bounded here rather than in a validator because the bound is a
    /// compile-time constant, so <c>[ApiController]</c> model validation rejects an over-length value
    /// before the service runs (CLAUDE.md: "a cap whose bound is a compile-time constant belongs in
    /// the attribute").
    /// </summary>
    [Required]
    [StringLength(SecretSettingKeys.MaxPlaintextLength)]
    public string Value { get; set; } = string.Empty;

    /// <summary>
    /// Redacts the whole record. An explicit override rather than <c>PrintMembers</c> because on a
    /// <c>sealed record</c> the generated <c>PrintMembers</c> is <c>private</c>, not <c>protected</c>,
    /// so the shape the spec describes cannot be spelled here — and an explicit <c>ToString</c> says
    /// what it does at the one place a reader looks.
    /// </summary>
    public override string ToString() => nameof(SecretSettingUpdate) + " { Value = <redacted> }";
}

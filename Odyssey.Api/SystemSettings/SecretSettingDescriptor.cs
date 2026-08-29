using Odyssey.Dtos.Application;

namespace Odyssey.Api.SystemSettings;

/// <summary>
/// How recoverable a secret is if it is lost (issue #444 §5 item 3). Carried as a declared field
/// rather than as prose, so every follow-up has to classify its own key instead of relying on a
/// paragraph being re-read a year later — and so the destructive-action copy and the backup guidance
/// can be driven from the declaration.
/// </summary>
public enum SecretKind
{
    /// <summary>
    /// A credential that can simply be re-issued at the provider and re-pasted — an SMTP relay
    /// password, an API key. Losing it is an outage, not a data loss.
    /// </summary>
    RotatableCredential = 1,

    /// <summary>
    /// A key that other data was <em>derived</em> from — an HMAC/pseudonymization key. It cannot be
    /// reconstructed, and its loss silently voids every value derived from it: the prior data becomes
    /// permanently un-re-derivable. This is the classification the Clear confirmation is load-bearing
    /// for (§11).
    /// </summary>
    DerivationKey = 2,
}

/// <summary>
/// One secret-valued setting, declared once (issue #444 §5).
///
/// <para>
/// <strong>A parallel type, deliberately not a subclass of <see cref="SystemSettingDescriptor"/></strong>
/// (§5 option B1). That type has three members a secret must never reach: <c>Format</c>, whose output
/// the audit loop writes verbatim; <c>Project</c>, which writes onto the response DTO; and
/// <c>AuditChanges</c>, which is <em>derived</em> from the claim — so a
/// <c>SecretSetting : SystemSettingDescriptor</c> carrying the security claim would log the credential
/// in plaintext at <c>Information</c> on its very first write. A separate type means no existing loop
/// can be handed one.
/// </para>
///
/// <para>
/// The scope of that guarantee is worth stating exactly: the separate type prevents a secret being
/// handed to the <em>existing</em> <c>{OldValue} -&gt; {NewValue}</c> loop. It does not prevent
/// somebody authoring a new leak inside <see cref="SecretSettingsService"/> itself — that residual is
/// covered by a log-capturing test over a sentinel value, which is a test, not a type guarantee.
/// </para>
///
/// <para>
/// There is deliberately <strong>no cache key</strong>: secrets are never cached (§5 option C1), so
/// there is nothing to evict on a write.
/// </para>
/// </summary>
public sealed class SecretSettingDescriptor
{
    /// <summary>The <c>SecretSettingKeys</c> constant this descriptor owns.</summary>
    public required string Key { get; init; }

    /// <summary>
    /// The claim a write requires. Retained and re-checked in the service as defence in depth for
    /// non-HTTP callers even though the controller enforces it at the action — and asserted equal to
    /// that action's policy by a guard test, since two declarations of one authorization fact can
    /// drift (§10).
    /// </summary>
    public required string RequiredClaim { get; init; }

    /// <summary>Recoverability. <c>required</c> so a new key cannot be added without classifying it.</summary>
    public required SecretKind Kind { get; init; }

    /// <summary>
    /// Maximum plaintext length. Mirrors <c>SecretSettingUpdate</c>'s <c>[StringLength]</c>, which
    /// model validation applies first; this is the defence-in-depth copy for direct callers.
    /// </summary>
    public int MaxLength { get; init; } = SecretSettingKeys.MaxPlaintextLength;

    /// <summary>
    /// Optional semantic check, run last on an already-trimmed, non-empty, printable-ASCII,
    /// length-checked value. Returns an error message, or <see langword="null"/> when acceptable.
    /// <strong>The message must never contain the submitted value</strong> (§9 rule 3).
    /// </summary>
    public Func<string, string?>? Validator { get; init; }

    /// <summary>
    /// Filtered out of the registry when <c>IHostEnvironment.IsProduction()</c>. Set on the test-only
    /// key alone — a guard test asserts exactly one descriptor carries it and names which, because a
    /// real credential descriptor carrying the flag would be silently invisible in Production and
    /// present only as a <c>404</c> "key not registered".
    /// </summary>
    public bool NonProduction { get; init; }
}

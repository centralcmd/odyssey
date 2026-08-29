using Odyssey.Dtos.Application;
using Odyssey.Dtos.Authorization;

namespace Odyssey.Api.SystemSettings;

/// <summary>
/// The single declaration of every encrypted secret setting (issue #444 §5), mirroring
/// <see cref="SystemSettingsRegistry"/>'s single-declaration discipline while deliberately sharing
/// none of its type hierarchy.
///
/// <para>
/// Unlike its plaintext sibling this is an <em>instance</em>, not a static: the descriptor set depends
/// on <see cref="IHostEnvironment"/>, because the test-only key is filtered out of Production. The
/// unfiltered collection stays available as <see cref="AllUnfiltered"/> so the structural guard tests
/// assert over what is <em>declared</em> rather than over what a Production host would show them —
/// under a Production environment the filtered list is empty, and those guards would pass vacuously.
/// </para>
/// </summary>
public sealed class SecretSettingsRegistry
{
    /// <summary>
    /// Every declared descriptor, before the environment filter. The guard tests read this.
    ///
    /// <para>
    /// It shipped with exactly one entry — the non-Production test key, so that #444's guards could
    /// not pass vacuously at merge. Issue #445 added the five real credentials beside it; the test key
    /// stays, because it is still the only key a deployment can write to without consequences.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<SecretSettingDescriptor> AllUnfiltered =
    [
        new SecretSettingDescriptor
        {
            Key = SecretSettingKeys.DiagnosticsSelfTest,
            RequiredClaim = PermissionClaims.SystemSettingsSecurityUpdate,
            // Nothing derives from it, so losing it costs nothing — and it is the classification a
            // reader should see on the one key that is not a real credential.
            Kind = SecretKind.RotatableCredential,
            NonProduction = true,
        },

        // ── Issue #445: the five real credentials, in the order they shipped ────────────────────
        //
        // Every one of them is gated by the SAME claim as the test key, so the surface's
        // authorization is unchanged and no administrator has to sign out and back in. What differs
        // per descriptor is Kind, which is what the Clear confirmation and the backup guidance read.

        // Wave 1. Rotatable: re-issued at the provider and re-pasted, and a failure is a recorded job
        // failure rather than a lockout. Attached per REQUEST by FileAnalysisApiKeyHandler — a
        // DefaultRequestHeaders entry is fixed at client construction and could not follow a rotation.
        new SecretSettingDescriptor
        {
            Key = SecretSettingKeys.FileAnalysisApiKey,
            RequiredClaim = PermissionClaims.SystemSettingsSecurityUpdate,
            Kind = SecretKind.RotatableCredential,
        },

        // Wave 2. The relay pair. Two descriptors rather than one composite value: they are written,
        // cleared and audited independently like every other row, and the CONSUMER — not the store —
        // is where the pair rule lives (SmtpEmailSender skips the send unless both resolve).
        new SecretSettingDescriptor
        {
            Key = SecretSettingKeys.EmailUsername,
            RequiredClaim = PermissionClaims.SystemSettingsSecurityUpdate,
            Kind = SecretKind.RotatableCredential,
        },
        new SecretSettingDescriptor
        {
            Key = SecretSettingKeys.EmailPassword,
            RequiredClaim = PermissionClaims.SystemSettingsSecurityUpdate,
            Kind = SecretKind.RotatableCredential,
            // NO relaxation of the shared printable-ASCII rule (issue #445 AC 9). This is the one key
            // where a real credential could legitimately fall outside 0x20-0x7E, and the relaxation was
            // available — #444 sized the ciphertext column from the byte worst case independently of
            // the character rule. It was declined because the rule is also what keeps CR/LF out of an
            // SMTP handshake, and because the alternative to a relaxation is not a bare 400: the entry
            // field names the constraint as the value is typed, which is an answer an administrator can
            // act on. If a deployment ever meets a relay password it genuinely cannot store, the
            // relaxation goes HERE, on this descriptor, and says so.
        },

        // Wave 3. A derivation key, but the benign one: rotating it breaks nothing already recorded —
        // previously written digests simply stop correlating with new ones.
        new SecretSettingDescriptor
        {
            Key = SecretSettingKeys.EmailRecipientHashKey,
            RequiredClaim = PermissionClaims.SystemSettingsSecurityUpdate,
            Kind = SecretKind.DerivationKey,
        },

        // Wave 4. The key whose dominant risk is LOSS, not disclosure: nothing re-issues it, and every
        // consent row already pseudonymised with it becomes permanently un-re-derivable. Moving it in
        // makes its durability depend on the database backup AND the Data Protection keys volume where
        // it used to depend on one environment variable — an accepted trade, paid for by the export
        // instruction in the Clear confirmation and by the keys-volume backup guidance in
        // docs/deployment.md.
        new SecretSettingDescriptor
        {
            Key = SecretSettingKeys.LegalPseudonymizationSecret,
            RequiredClaim = PermissionClaims.SystemSettingsSecurityUpdate,
            Kind = SecretKind.DerivationKey,
        },
    ];

    private readonly IReadOnlyList<SecretSettingDescriptor> descriptors;

    public SecretSettingsRegistry(IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        descriptors = environment.IsProduction()
            ? AllUnfiltered.Where(descriptor => !descriptor.NonProduction).ToList()
            : AllUnfiltered;
    }

    /// <summary>The descriptors this host serves, in declaration order.</summary>
    public IReadOnlyList<SecretSettingDescriptor> All => descriptors;

    /// <summary>
    /// Resolves a route key with an ordinal comparison, or <see langword="null"/> when unknown. Keys
    /// are never used to build a file path, a SQL fragment or a protector purpose without first
    /// passing through here.
    /// </summary>
    public SecretSettingDescriptor? Find(string? key) =>
        key is null ? null : descriptors.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.Ordinal));
}

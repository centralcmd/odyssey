namespace Odyssey.Dtos.Application;

/// <summary>
/// The vocabulary of encrypted secret-setting keys (issue #444). Deliberately a <em>separate</em>
/// catalogue from <c>SystemSettingsKeys</c>, not an extension of it: a secret has no compiled default
/// and no <c>HasData</c> seed row, so folding these into <c>SystemSettingsKeys.AllKeys</c> would break
/// <c>Registry_keys_match_the_key_catalogue_exactly</c> and
/// <c>Every_descriptor_default_parses_onto_the_read_dto</c> — guard tests this repo has declined to
/// weaken before (§5 option A2).
///
/// <para>
/// It lives in <c>Odyssey.Dtos.Application</c> rather than <c>Odyssey.Context</c> because
/// the Blazor client's secret catalogue names the same constants — the status endpoint carries no
/// title, description or icon, so those are authored client-side and joined to the server's keys.
/// </para>
///
/// <para>
/// <strong>No colon in any key.</strong> Keys must survive a trip through <c>IConfiguration</c>'s
/// section separator if one ever reaches the adoption table, and every <c>SystemSettingsKeys</c> value
/// already follows the same colon-free convention.
/// </para>
/// </summary>
public static class SecretSettingKeys
{
    /// <summary>
    /// The registered <strong>test-only</strong> key (§6). Its sole purpose is to give this issue's
    /// acceptance criteria something to write to: without a registered key the guards would pass
    /// vacuously at merge and first bite inside the first follow-up, which is precisely the
    /// "the guard existed but never fired" shape the parallel-descriptor decision exists to prevent.
    ///
    /// <para>
    /// Its descriptor is marked non-Production, so it is filtered out of the registry — and therefore
    /// out of the status endpoint, the write path and the reader — whenever
    /// <c>IHostEnvironment.IsProduction()</c>. That mirrors <c>DemoDataSeeder</c>'s Production refusal
    /// (test scaffolding), not a feature toggle, so it does not contradict CLAUDE.md's
    /// "no runtime feature toggles" rule.
    /// </para>
    /// </summary>
    public const string DiagnosticsSelfTest = "DiagnosticsSelfTest";

    /// <summary>
    /// The Anthropic API key sent as <c>x-api-key</c> on every file-analysis request (issue #445
    /// Wave 1). Rotatable: it is re-issued at the provider and re-pasted, and a failure is a recorded
    /// job failure rather than a lockout.
    ///
    /// <para>
    /// Its <em>destination</em> deliberately does not move with it — <c>FileAnalysis:BaseUrl</c> is a
    /// system setting whose write path is claim-gated and https-only, and the key is attached
    /// per-request by a handler that never follows a redirect. An admin-editable destination plus a
    /// stored key would otherwise be one-request credential exfiltration.
    /// </para>
    /// </summary>
    public const string FileAnalysisApiKey = "FileAnalysisApiKey";

    /// <summary>
    /// The SMTP relay username (issue #445 Wave 2). Consumed as a <strong>pair</strong> with
    /// <see cref="EmailPassword"/>: a username from the store beside a password that is absent or
    /// unreadable is a half-configured credential, and the send is skipped rather than attempted
    /// unauthenticated.
    /// </summary>
    public const string EmailUsername = "EmailUsername";

    /// <summary>
    /// The SMTP relay password (issue #445 Wave 2). A human-chosen password at a third party, so it is
    /// the one key where the store's printable-ASCII rule can plausibly reject a real credential — the
    /// relaxation was <strong>not</strong> taken, and the constraint is named at the point of entry
    /// instead of arriving as a bare <c>400</c>.
    /// </summary>
    public const string EmailPassword = "EmailPassword";

    /// <summary>
    /// The HMAC key behind the recipient digests the per-recipient send throttle writes to the log
    /// (issue #445 Wave 3). A <em>derivation</em> key: rotating it breaks nothing already recorded, but
    /// digests written before the change stop correlating with the ones after it.
    ///
    /// <para>
    /// An absent row is healthy here in the fullest sense — the throttle already generates a
    /// per-process key when none is configured, which is a supported configuration rather than a
    /// degraded one.
    /// </para>
    /// </summary>
    public const string EmailRecipientHashKey = "EmailRecipientHashKey";

    /// <summary>
    /// The HMAC key that pseudonymises a deleted account's consent rows (issue #445 Wave 4). The one
    /// key in this catalogue whose dominant risk is <strong>loss</strong> rather than disclosure: there
    /// is no provider to re-issue it from, and every row already pseudonymised with it becomes
    /// permanently un-re-derivable — the property GDPR Art. 7(1) attribution depends on.
    ///
    /// <para>
    /// That is why its durability now depends on two artefacts staying in sync (the database backup and
    /// the Data Protection keys volume) where it used to depend on one, and why the Clear confirmation
    /// tells an administrator to export the value first.
    /// </para>
    /// </summary>
    public const string LegalPseudonymizationSecret = "LegalPseudonymizationSecret";

    /// <summary>Every declared secret key. The client catalogue is asserted against this.</summary>
    public static readonly IReadOnlyList<string> AllKeys =
    [
        DiagnosticsSelfTest,
        FileAnalysisApiKey,
        EmailUsername,
        EmailPassword,
        EmailRecipientHashKey,
        LegalPseudonymizationSecret,
    ];

    /// <summary>
    /// The maximum plaintext length, in characters (§6). A compile-time bound, so per CLAUDE.md it
    /// belongs on the request DTO's <c>[StringLength]</c> rather than in a validator; the service
    /// re-checks it as defence in depth for non-HTTP callers.
    /// </summary>
    public const int MaxPlaintextLength = 1024;
}

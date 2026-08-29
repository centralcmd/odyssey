namespace Odyssey.Context.Secrets;

/// <summary>
/// The outcome of reading one stored secret (issue #444 §9 rule 6) — three distinct states, never a
/// <c>string?</c>.
///
/// <para>
/// <strong>This is the single most load-bearing decision in the feature.</strong> A <c>string?</c>
/// return would let a consumer write <c>?? configuredFallback</c> and thereby treat an
/// <em>unreadable</em> rotated credential as an <em>unset</em> one — silently sending with the old
/// configured value the administrator believed they had replaced. That is why the reader ships with
/// this infrastructure rather than with the first consumer.
/// </para>
///
/// <para>
/// <strong>It suppresses its own <c>ToString()</c>.</strong> This — not the request DTO — is the type
/// consumers actually hold: in a <c>catch</c> block's exception context, in a
/// <c>LogDebug("reader returned {Result}", result)</c> during a follow-up's debugging, inside
/// <c>SmtpEmailSender</c> and <c>ClaudeFileAnalysisProvider</c>. Written positionally it would print
/// the plaintext.
/// </para>
/// </summary>
public sealed record SecretResult
{
    private readonly string? value;

    private SecretResult(SecretReadState state, string? value)
    {
        State = state;
        this.value = value;
    }

    /// <summary>Which of the three states this is.</summary>
    public SecretReadState State { get; }

    /// <summary>No row exists for the key. <strong>Healthy</strong> — the credential is unconfigured.</summary>
    public static readonly SecretResult NotSet = new(SecretReadState.NotSet, null);

    /// <summary>
    /// A row exists but could not be decrypted, or carries an unrecognised protection scheme.
    /// <strong>Degraded.</strong> Whether the consumer then fails open or closed is that consumer's
    /// own decision, made in its own follow-up issue — this contract guarantees only that the two
    /// conditions arrive distinguishable.
    /// </summary>
    public static readonly SecretResult Unreadable = new(SecretReadState.Unreadable, null);

    /// <summary>A row exists and decrypted to <paramref name="value"/>.</summary>
    public static SecretResult Found(string value) =>
        new(SecretReadState.Found, value ?? throw new ArgumentNullException(nameof(value)));

    /// <summary>
    /// The plaintext, if this is a <see cref="SecretReadState.Found"/>. The only way to reach the
    /// value, so a consumer cannot get at it without having branched on the state first.
    /// </summary>
    public bool TryGetValue(out string plaintext)
    {
        plaintext = value ?? string.Empty;
        return State == SecretReadState.Found;
    }

    /// <summary>
    /// Redacted. An explicit override rather than <c>PrintMembers</c>: on a <c>sealed record</c> the
    /// generated <c>PrintMembers</c> is <c>private</c>, so the <c>protected override</c> shape cannot
    /// be spelled here — and this states the intent at the one place a reader looks.
    /// </summary>
    public override string ToString() => $"{nameof(SecretResult)} {{ State = {State} }}";
}

/// <summary>The three read states. Members start at 1 — a defaulted <c>int</c> is never valid here.</summary>
public enum SecretReadState
{
    /// <summary>No row for this key.</summary>
    NotSet = 1,

    /// <summary>A row that decrypted cleanly.</summary>
    Found = 2,

    /// <summary>A row that could not be decrypted, or one written under an unknown scheme.</summary>
    Unreadable = 3,
}

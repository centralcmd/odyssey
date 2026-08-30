namespace Odyssey.Dtos;

/// <summary>
/// The shape rule for the admin-editable file-analysis base URL (issue #439) — validation,
/// canonicalisation, and the host projection every echo of the value goes through.
///
/// <para>
/// <strong>Why it lives here.</strong> The rule was once applied by two halves of the stack — the
/// API's <c>PUT</c> path and the migrations job's configuration adoption — each with its own copy of
/// the predicate, on the one setting where a duplicate is least affordable: it decides which host
/// receives the document and the configured API key. Moving it to <c>Odyssey.Dtos</c>, which has zero
/// project references and is reachable from both halves, made drift impossible rather than merely
/// detected. Adoption has since been removed and the API is the only caller, but the rule stays here:
/// it is the same split <c>CLAUDE.md</c> documents for <see cref="SystemSettingsDefaults"/>, and the
/// client catalogue reaches it from the other side.
/// </para>
///
/// <para>
/// <strong>Why the path rule is strict.</strong> <c>ClaudeFileAnalysisProvider</c> resolves a
/// <em>root-absolute</em> <c>/v1/messages</c> against the base URI, which replaces any path the base
/// carries: <c>https://gateway.internal/proxy</c> would post to
/// <c>https://gateway.internal/v1/messages</c> and discard <c>/proxy</c> silently. Accepting a path
/// would give a <c>200</c> on save, an advisory naming a host that looks right and a job stamp
/// recording that same host — while requests carrying the API key went somewhere nobody configured.
/// Rejecting it makes the value that is accepted and the value that is used the same value.
/// </para>
///
/// <para>
/// <strong>Applied at both ends</strong>, exactly like the privacy-notice URL: write-time validation
/// protects only values that arrived through the API, and says nothing about one planted by a database
/// restore, a hand edit or an older build. The read path resolves an unusable stored value to
/// <see langword="null"/> rather than to the compiled default, because substituting
/// <c>api.anthropic.com</c> for a gateway the administrator deliberately configured would transfer a
/// document to a processor neither they nor the consenting user chose.
/// </para>
///
/// <para>
/// Private, loopback and link-local hosts are <strong>allowed</strong>. An internal corporate gateway
/// is the main reason this setting is editable at all, so blocking them would defeat the feature; the
/// residual SSRF exposure is accepted under the trusted-admin model and bounded by the rule that a
/// provider response body never reaches a user-facing message.
/// </para>
/// </summary>
public static class FileAnalysisBaseUrlRule
{
    /// <summary>Returns an error message, or null when <paramref name="value"/> is acceptable.</summary>
    public static string? Validate(string value) =>
        TryCanonicalize(value, out _)
            ? null
            : "must be an absolute https:// address with no path, query, fragment or credentials. "
              + "Enter the host only — the provider appends /v1/messages itself.";

    /// <summary>
    /// The canonical stored form (scheme + authority, no trailing slash), or null when the value is
    /// not usable. Canonicalising means <c>https://host</c> and <c>https://host/</c> do not read as a
    /// change and so produce no spurious audit line.
    /// </summary>
    public static string? Canonicalize(string value) =>
        TryCanonicalize(value, out var canonical) ? canonical : null;

    /// <summary>
    /// Reduces a stored value to its <see cref="Uri.Host"/> for anything that echoes it — the audit
    /// line, the advisories, the job stamp. Never the path, query or <c>userinfo</c>.
    ///
    /// <para>
    /// This is applied to the <em>old</em> value in an audit line as well as the new one, and that is
    /// the whole point: the write validator constrains only what an administrator can submit, so the
    /// value being replaced may carry <c>https://key:secret@host</c> planted by a restore. Without the
    /// projection, the first administrator to correct such a row through the UI would write that
    /// credential into the application log.
    /// </para>
    /// </summary>
    public static string Host(string value) =>
        Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host)
            ? uri.Host
            : Unparseable;

    /// <summary>What <see cref="Host"/> logs in place of a value it cannot parse. Never the value itself.</summary>
    public const string Unparseable = "(unparseable)";

    private static bool TryCanonicalize(string value, out string canonical)
    {
        canonical = string.Empty;

        if (value is null || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        // https only. The document and the API key both travel over this, so plaintext is not a
        // preference here; and Uri.TryCreate accepts file:, ftp: and javascript: just as happily.
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            return false;
        }

        if (string.IsNullOrEmpty(uri.Host))
        {
            return false;
        }

        // userinfo is a credential; a query or fragment can carry a token. Both are echoed nowhere and
        // used by nothing, so accepting them would only create somewhere for a secret to hide.
        if (!string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        // The root-absolute request builder discards any path, so a path here would be accepted and
        // then ignored — see the remarks on this class.
        if (uri.AbsolutePath is not ("" or "/"))
        {
            return false;
        }

        // Scheme + authority, no trailing slash: GetLeftPart(Authority) already omits it.
        canonical = uri.GetLeftPart(UriPartial.Authority);
        return true;
    }
}

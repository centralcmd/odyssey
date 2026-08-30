namespace Odyssey.Dtos;

/// <summary>
/// The shape rule for the admin-editable public client origin (issue #8) — the value every
/// confirmation and password-reset link is composed against.
///
/// <para>
/// <strong>This is the weakest point in the feature, and the rule is what narrows it.</strong> Unlike
/// the SMTP host, this value needs no credential to be dangerous: whoever can change it receives a
/// password-reset token for any address they know, including another administrator's. Clearing the
/// stored credential — the structural control that closes the host threat — does nothing here,
/// because no credential is involved. So the controls are the security claim, the audit line with a
/// host-only projection, this rule, and the client-side origin-mismatch hint. Issue #8 §10.2 states
/// the residual and records that the requester accepted it.
/// </para>
///
/// <para>
/// <strong>The loopback <c>http</c> exemption is deliberate and is a deviation from
/// <see cref="FileAnalysisBaseUrlRule"/></strong>, which has no scheme exemption at all. The dev and
/// Aspire stacks serve the client over <c>http://localhost:5199</c>, and this rule lives in
/// <c>Odyssey.Dtos</c>, which cannot reach <c>IHostEnvironment</c> — so the exemption cannot be gated
/// to Development and is unconditional. What it does <em>not</em> widen: a loopback address in a link
/// resolves on the RECIPIENT'S OWN MACHINE, not on the server and not on any host an attacker
/// controls, so setting one is a denial-of-reset rather than an interception. It does not extend the
/// takeover path above.
/// </para>
///
/// <para>
/// <strong>A path is permitted</strong> — a deployment may be hosted under a subpath — but is
/// normalised without its trailing slash, because links are composed as
/// <c>{base}/{clientPath}{query}</c>. A query, a fragment and <c>userinfo</c> are all refused: none is
/// used by the composition, so accepting one would only create somewhere for a token to hide in a
/// value that is echoed into audit lines.
/// </para>
///
/// <para>
/// <strong>Applied at both ends.</strong> Write-time validation protects only values that arrived
/// through the API. The send path re-validates (issue #8 §5.9, §11.1) and REFUSES rather than
/// substituting the compiled default — an <c>http://</c> public host planted by a restore must not
/// silently compose a reset link, and must not silently be replaced by a value the administrator
/// never chose. <c>StringSetting.ReadValidator</c> runs the same predicate on the <c>GET</c> path so
/// the administrator can see the row is faulted rather than discovering it when a reset fails.
/// </para>
/// </summary>
public static class EmailClientBaseUrlRule
{
    /// <summary>The maximum stored length, mirroring the DTO's <c>[StringLength]</c>.</summary>
    public const int MaxLength = 256;

    /// <summary>Returns an error message, or null when <paramref name="value"/> is acceptable.</summary>
    public static string? Validate(string value) =>
        TryCanonicalize(value, out _)
            ? null
            : "must be an absolute https:// address with no query, fragment or credentials. "
              + "http:// is accepted only for loopback addresses.";

    /// <summary>
    /// The canonical stored form — scheme, authority and any path, without a trailing slash — or null
    /// when the value is not usable. Canonicalising means <c>https://host</c> and <c>https://host/</c>
    /// do not read as a change and so produce no spurious audit line.
    /// </summary>
    public static string? Canonicalize(string value) =>
        TryCanonicalize(value, out var canonical) ? canonical : null;

    /// <summary>
    /// Reduces a stored value to its <see cref="Uri.Host"/> for anything that echoes it. Applied to
    /// the OLD value in an audit line as well as the new one — the write validator never saw the value
    /// being replaced, so it may carry <c>https://token@host</c> planted by a restore.
    /// </summary>
    public static string Host(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return EmailSmtpHostRule.NotConfigured;
        }

        return Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host)
            ? uri.Host
            : FileAnalysisBaseUrlRule.Unparseable;
    }

    /// <summary>
    /// The ORIGIN — scheme, host and non-default port — of a stored value, for the client's
    /// origin-mismatch hint. Null when the value cannot be parsed or is empty.
    ///
    /// <para>
    /// The comparison the hint makes is between origins, not hosts: an administrator browsing
    /// <c>https://admin.internal</c> while the saved value is <c>https://odyssey.example.net</c> is
    /// exactly the case worth flagging, and so is a scheme or port that differs on the same host.
    /// </para>
    /// </summary>
    public static string? Origin(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
        && !string.IsNullOrEmpty(uri.Host)
            ? uri.GetLeftPart(UriPartial.Authority)
            : null;

    private static bool TryCanonicalize(string value, out string canonical)
    {
        canonical = string.Empty;

        if (value is null || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (string.IsNullOrEmpty(uri.Host))
        {
            return false;
        }

        var https = string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal);
        var http = string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal);

        if (!https && !http)
        {
            // Uri.TryCreate accepts file:, ftp: and javascript: just as happily as it does https.
            return false;
        }

        // Uri.IsLoopback matches the LITERAL host — 127.0.0.0/8, ::1 and "localhost" — never a
        // resolved address, so it is not DNS-rebindable: a name that resolves to 127.0.0.1 is not
        // loopback by this test and gets no exemption.
        if (http && !uri.IsLoopback)
        {
            return false;
        }

        // userinfo is a credential; a query or fragment can carry a token. The link composition uses
        // none of them, so accepting one would only put a secret somewhere that is later echoed.
        if (!string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        // A path IS allowed — a deployment may live under a subpath — but normalised without its
        // trailing slash, since links are composed as "{base}/{clientPath}{query}".
        var path = uri.AbsolutePath.TrimEnd('/');
        canonical = uri.GetLeftPart(UriPartial.Authority) + path;

        return canonical.Length <= MaxLength;
    }
}

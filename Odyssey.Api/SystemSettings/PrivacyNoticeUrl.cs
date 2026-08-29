namespace Odyssey.Api.SystemSettings;

/// <summary>
/// Validation and canonicalisation for the admin-editable privacy-notice URL (issue #421 Wave 1).
///
/// <para>
/// <strong>This is the sharpest new risk the wave introduces.</strong> The value is rendered into an
/// <c>href</c> in the analyze-file consent panel, Blazor does <em>not</em> sanitise <c>href</c>, and
/// <see cref="Uri.TryCreate(string?, UriKind, out Uri?)"/> happily accepts <c>javascript:</c> and
/// <c>data:</c> as absolute URIs — so an https-only scheme allow-list is the only thing standing
/// between a settings write and stored XSS in a GDPR consent gate.
/// </para>
///
/// <para>
/// Applied at <strong>both ends</strong>, deliberately: <see cref="Validate"/> on write, and
/// <see cref="Project"/> on the read path. Write-time validation alone protects only values that
/// arrived through the API — it does nothing for one planted by a database restore, a hand edit, or an
/// older build with weaker rules.
/// </para>
/// </summary>
internal static class PrivacyNoticeUrl
{
    /// <summary>Returns an error message, or null when <paramref name="value"/> is acceptable.</summary>
    internal static string? Validate(string value) =>
        TryCanonicalize(value, out _) ? null : "must be an absolute https:// URL.";

    /// <summary>
    /// The form served to clients: canonicalised when the stored value is valid, and the compiled
    /// default when it is not. Never returns an unvalidated stored value, which is what makes a
    /// database-planted <c>javascript:</c> value unreachable from the rendered page.
    /// </summary>
    internal static string Project(string storedValue, string fallback) =>
        TryCanonicalize(storedValue, out var canonical) ? canonical : fallback;

    private static bool TryCanonicalize(string value, out string canonical)
    {
        canonical = string.Empty;

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return false;
        }

        // The allow-list, not TryCreate, is the barrier: "javascript:alert(1)" and
        // "data:text/html,<script>" are both perfectly well-formed absolute URIs.
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            return false;
        }

        // Credentials in a URL rendered as a link are a phishing affordance and have no legitimate
        // use in a published privacy notice.
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        canonical = uri.AbsoluteUri;
        return true;
    }
}

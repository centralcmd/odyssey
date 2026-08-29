namespace Odyssey.Client.Auth;

/// <summary>
/// Validates a <c>returnUrl</c> query parameter before anything navigates to it.
/// </summary>
/// <remarks>
/// <para>
/// A gate that sends the user somewhere after it completes is an open-redirect primitive: the link is
/// same-origin and the destination is attacker-chosen, which is exactly the shape a phishing pretext
/// wants. Only app-relative paths are accepted, and the checks below are the ones that actually matter
/// against a browser's URL parser rather than against intuition:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>Backslashes are rejected outright.</b> The WHATWG URL parser treats <c>\</c> as <c>/</c> in the
/// authority position, so <c>/\evil.com</c> and <c>\/evil.com</c> resolve to
/// <c>https://evil.com/</c> — a different origin — despite passing a naive "starts with a single
/// slash" test. This is the bypass that a <c>//</c>-prefix check alone misses (PR #407 security review,
/// CWE-601).
/// </description></item>
/// <item><description>
/// <b>Control characters are rejected.</b> Browsers strip tab, CR and LF while parsing, so
/// <c>/&#92;t/evil.com</c> can smuggle a slash-run past a naive check.
/// </description></item>
/// <item><description>
/// <b>A leading <c>//</c> is rejected</b> — protocol-relative, so also a different origin.
/// </description></item>
/// </list>
/// <para>
/// This is a client-side control over a client-side navigation; it is not a server trust boundary. The
/// server-side gate does not consult <c>returnUrl</c> at all.
/// </para>
/// </remarks>
public static class LocalReturnUrl
{
    /// <summary>
    /// Reads the <c>returnUrl</c> parameter out of <paramref name="uri"/>'s query and validates it with
    /// <see cref="Parse"/>, returning <see langword="null"/> when it is absent or unsafe.
    /// </summary>
    /// <param name="uri">The current absolute URI, whose query is searched for <c>returnUrl</c>.</param>
    /// <param name="rejectedPathPrefix">
    /// A path the caller must never return to — the gate's own route, so a completed gate cannot
    /// redirect to itself and spin. Passed through to <see cref="Parse"/>.
    /// </param>
    /// <remarks>
    /// Every gate page needs exactly this — read the parameter, then refuse anything that isn't an
    /// app-relative path that is not the gate's own route — so it lives here rather than being
    /// re-implemented per page. Each hand-rolled copy is a place the validation can be forgotten, which
    /// is the whole failure mode <see cref="Parse"/> exists to prevent.
    /// <para>
    /// A repeated <c>returnUrl</c> is resolved by taking the first occurrence that <em>validates</em>,
    /// not the first that appears. That cannot smuggle anything past <see cref="Parse"/> — every
    /// candidate is checked independently — and it keeps the gate pages consistent with each other.
    /// </para>
    /// </remarks>
    public static string? FromQuery(string uri, string? rejectedPathPrefix = null)
    {
        var query = new Uri(uri).Query;
        if (string.IsNullOrEmpty(query))
        {
            return null;
        }

        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length != 2 || parts[0] != "returnUrl")
            {
                continue;
            }

            if (Parse(Uri.UnescapeDataString(parts[1]), rejectedPathPrefix) is { } safe)
            {
                return safe;
            }
        }

        return null;
    }

    /// <summary>
    /// Returns <paramref name="candidate"/> when it is a safe app-relative target, otherwise
    /// <see langword="null"/> so the caller falls back to its own default destination.
    /// </summary>
    /// <param name="candidate">The raw, already URL-decoded parameter value.</param>
    /// <param name="rejectedPathPrefix">
    /// A path the caller must never return to — the gate's own route, so a completed gate cannot
    /// redirect to itself and spin.
    /// </param>
    public static string? Parse(string? candidate, string? rejectedPathPrefix = null)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        if (candidate.Contains('\\', StringComparison.Ordinal) || candidate.Any(char.IsControl))
        {
            return null;
        }

        if (!candidate.StartsWith('/') || candidate.StartsWith("//", StringComparison.Ordinal))
        {
            return null;
        }

        if (rejectedPathPrefix is not null
            && PathOf(candidate).StartsWith(rejectedPathPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return candidate;
    }

    private static string PathOf(string url) => url.Split('?', '#')[0].TrimEnd('/');
}

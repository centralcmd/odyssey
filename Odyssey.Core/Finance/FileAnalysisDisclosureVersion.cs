using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Odyssey.Core.Finance;

/// <summary>
/// The integrity token that binds an affirmed consent to the disclosure the user was actually shown
/// (issue #439 §5.3c).
///
/// <para>
/// <c>GET /api/file-analysis/disclosure</c> returns it, <c>AnalyzeFileRequest</c> echoes it, and
/// <c>FileAnalysisService</c> recomputes it from the <em>same per-run snapshot the transfer uses</em>
/// and compares. That last part is what makes the check sound: the comparison cannot be defeated by
/// the values shifting between the check and the send.
/// </para>
///
/// <para>
/// <strong>Not a secret.</strong> Every input except the host is already carried in the response it
/// accompanies, and the host is one-way through the hash — which is precisely why the whole base URL
/// is not an input. The version changes when the destination changes without the hash input ever
/// carrying a path, a query or <c>userinfo</c>.
/// </para>
///
/// <para>
/// <strong><c>enabled</c> is deliberately excluded.</strong> It is not a disclosure fact, and
/// including it would invalidate every open consent gate on an unrelated toggle.
/// </para>
/// </summary>
public static class FileAnalysisDisclosureVersion
{
    /// <summary>
    /// The delimiter between fields: a unit separator (U+001F) rather than a printable character, so
    /// no field value can contain it and shift a boundary — <c>"ab" + "c"</c> and <c>"a" + "bc"</c>
    /// must not hash the same.
    /// </summary>
    private const char Separator = '\u001f';

    /// <summary>Base64url characters kept from the digest. 16 is ~96 bits — ample for a change detector.</summary>
    private const int Length = 16;

    /// <summary>
    /// Computes the version for one snapshot. Returns <see cref="string.Empty"/> when the snapshot
    /// carries no usable model or base URL, which is unreachable in practice: both callers refuse with
    /// <see cref="FileAnalysisUnavailableException"/> (analyze) or <c>503</c> (the disclosure endpoint)
    /// before they get here, and an empty version can never match an echoed one.
    /// </summary>
    public static string Compute(FileAnalysisSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.Model is not { } model || settings.BaseUrl is not { } baseUrl)
        {
            return string.Empty;
        }

        // The HOST, never the whole URL — see the remarks. An unparseable value cannot reach here from
        // the read path (it resolves to null and refuses), so the fallback is defensive only.
        var host = Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host)
            ? uri.Host
            : baseUrl;

        var input = string.Join(Separator,
            settings.Processor,
            settings.ProcessorRegion,
            settings.LawfulBasis,
            settings.PrivacyNoticeUrl,
            model,
            host);

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Base64Url.EncodeToString(digest)[..Length];
    }

    /// <summary>
    /// Whether an echoed version matches the one in force. A null, empty or whitespace echo is a
    /// <strong>mismatch</strong>, not a skip: a client that sends none has not shown the user a
    /// disclosure this server can vouch for, and treating absence as agreement would make the check
    /// opt-out by omission.
    /// </summary>
    public static bool Matches(FileAnalysisSettings settings, string? echoed) =>
        !string.IsNullOrWhiteSpace(echoed)
        && string.Equals(Compute(settings), echoed, StringComparison.Ordinal);
}

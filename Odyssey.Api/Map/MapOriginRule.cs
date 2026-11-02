using System.Globalization;
using System.Text.RegularExpressions;

namespace Odyssey.Api.Map;

/// <summary>
/// The one .NET implementation of the map's origin and path rules (issue #370 §8). The client container's
/// <c>15-validate-map-origins.sh</c> implements the <c>AllowedOrigins</c> half in POSIX <c>sh</c>;
/// <c>TestShared/MapOriginVectors.json</c> is run against both so the two cannot drift.
/// </summary>
/// <remarks>
/// Every method returns the <b>rule</b> that failed, or <see langword="null"/>. None ever quotes the value:
/// a mistakenly pasted keyed URL must not reach a log.
/// </remarks>
public static partial class MapOriginRule
{
    public const int MaxOrigins = 8;

    /// <summary>The per-entry shape. Lowercase only, no empty or hyphen-edged label, no path.</summary>
    [GeneratedRegex(
        @"^https://[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)*(:[0-9]{1,5})?\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex OriginPattern();

    /// <summary>
    /// A root-relative path. The character after the leading <c>/</c> comes from a set holding neither
    /// <c>/</c> nor <c>\</c>, so <c>//host/x</c> and <c>/\host/x</c> fail without a lookahead. The pattern
    /// alone admits <c>/../s.json</c>; <see cref="HasDotDotSegment"/> is the separate check for that.
    /// </summary>
    [GeneratedRegex(@"^(/|/[A-Za-z0-9._~-][A-Za-z0-9._~/-]*)\z", RegexOptions.CultureInvariant)]
    private static partial Regex RootRelativePattern();

    /// <summary>Any character below <c>0x20</c>, or <c>0x7F</c>, checked on the raw string.</summary>
    public static bool HasControlCharacter(string value) => value.Any(c => c < 0x20 || c == 0x7F);

    /// <summary>An unrendered template placeholder.</summary>
    public static bool HasPlaceholder(string value) =>
        value.Contains("{{", StringComparison.Ordinal) || value.Contains("}}", StringComparison.Ordinal);

    /// <summary>
    /// Parses <c>AllowedOrigins</c>, or names the rule it breaks. Empty is valid and yields no origins.
    /// </summary>
    public static string? TryParseAllowedOrigins(string value, out IReadOnlyList<Uri> origins)
    {
        origins = [];

        if (HasControlCharacter(value))
            return "must not contain control characters (tab, carriage return, newline included)";
        if (value.Length == 0)
            return null;
        if (value.StartsWith(' ') || value.EndsWith(' '))
            return "must not start or end with a space";
        if (value.Contains("  ", StringComparison.Ordinal))
            return "must separate origins with single spaces";

        var entries = value.Split(' ');
        if (entries.Length > MaxOrigins)
            return $"must list at most {MaxOrigins} origins";

        var parsed = new List<Uri>(entries.Length);
        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];
            if (!OriginPattern().IsMatch(entry))
                return $"entry {i + 1} must be a lowercase https origin with no path, wildcard or trailing slash";

            var portSeparator = entry.LastIndexOf(':');
            if (portSeparator > "https:".Length)
            {
                var port = int.Parse(entry[(portSeparator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture);
                if (port is < 1 or > 65535)
                    return $"entry {i + 1} must have a port between 1 and 65535";
            }

            if (!Uri.TryCreate(entry, UriKind.Absolute, out var uri))
                return $"entry {i + 1} must be a lowercase https origin with no path, wildcard or trailing slash";

            parsed.Add(uri);
        }

        origins = parsed;
        return null;
    }

    /// <summary>Whether <paramref name="value"/> is a root-relative path this rule accepts.</summary>
    public static bool IsRootRelative(string value) => value.StartsWith('/');

    /// <summary>
    /// Checks a style URL: an absolute https URL with no userinfo, query or fragment whose origin is listed,
    /// or a root-relative path with no <c>..</c> segment.
    /// </summary>
    public static string? CheckStyleUrl(string value, IReadOnlyList<Uri> allowedOrigins)
    {
        if (IsRootRelative(value))
        {
            if (!RootRelativePattern().IsMatch(value))
                return "must be a root-relative path of the form /dir/style.json (no //, no backslash)";
            if (HasDotDotSegment(value))
                return "must not contain a '..' path segment";
            return null;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return "must be an absolute https URL or a root-relative path";
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
            return "must use the https scheme";
        if (uri.UserInfo.Length > 0 || AuthorityHasUserInfo(value))
            return "must not carry userinfo";
        if (uri.Query.Length > 0 || value.Contains('?'))
            return "must not carry a query string";
        if (uri.Fragment.Length > 0 || value.Contains('#'))
            return "must not carry a fragment";
        if (!allowedOrigins.Any(origin => SameOrigin(origin, uri)))
            return "must be served from an origin listed in Map:AllowedOrigins";

        return null;
    }

    /// <summary>The attribution link: absolute https, no userinfo, no fragment. A query string is allowed.</summary>
    public static string? CheckAttributionUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return "must be an absolute https URL";
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
            return "must use the https scheme";
        if (uri.UserInfo.Length > 0 || AuthorityHasUserInfo(value))
            return "must not carry userinfo";
        if (uri.Fragment.Length > 0 || value.Contains('#'))
            return "must not carry a fragment";

        return null;
    }

    /// <summary>Plain text: non-blank, with no <c>&lt;</c>, <c>&gt;</c> or <c>&amp;</c>.</summary>
    public static string? CheckAttribution(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "must not be blank";
        if (value.IndexOfAny(['<', '>', '&']) >= 0)
            return "must be plain text without '<', '>' or '&'";

        return null;
    }

    /// <summary>Scheme, lowercased IDN host and EFFECTIVE port — never a string comparison.</summary>
    public static bool SameOrigin(Uri left, Uri right) =>
        string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.IdnHost.ToLowerInvariant(), right.IdnHost.ToLowerInvariant(), StringComparison.Ordinal)
        && left.Port == right.Port;

    // Uri.UserInfo is empty for a bare "https://@host/", which still smuggles an authority separator.
    private static bool AuthorityHasUserInfo(string value)
    {
        var start = value.IndexOf("://", StringComparison.Ordinal);
        if (start < 0)
            return false;

        var authority = value[(start + 3)..];
        var end = authority.IndexOfAny(['/', '?', '#']);
        return (end < 0 ? authority : authority[..end]).Contains('@');
    }

    private static bool HasDotDotSegment(string path) =>
        path.Split('/').Any(segment => segment == "..");
}

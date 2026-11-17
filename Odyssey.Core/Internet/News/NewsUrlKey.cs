using System.Globalization;
using System.Text;
using Odyssey.Core.Internet.News.Content;

namespace Odyssey.Core.Internet.News;

/// <summary>
/// The URL keys (issue #470 §8.2), server-only. <see cref="UrlKey"/> identifies a feed (stored as <c>UrlKeyHash</c>);
/// <see cref="LinkKey"/> identifies an article link (stored as <c>LinkKeyHash</c>, and the <c>l:</c> identity key).
/// Both hosts compute them with this one implementation, over ICU's IDN mapping, so the API and the worker agree.
/// </summary>
public static class NewsUrlKey
{
    private static readonly IdnMapping Idn = new() { AllowUnassigned = false, UseStd3AsciiRules = false };

    /// <summary>
    /// Trimmed; an absolute <c>http</c>/<c>https</c> URL with no user info; scheme and host lower-cased; an IDN host as
    /// punycode; the default port and the fragment dropped; the tracking parameters removed, the rest kept in order.
    /// <c>null</c> when <paramref name="url"/> is not such a URL.
    /// </summary>
    public static string? UrlKey(string? url)
    {
        if (!TryParts(url, out var scheme, out var authority, out var pathAndQuery))
        {
            return null;
        }

        return $"{scheme}://{authority}{pathAndQuery}";
    }

    /// <summary><see cref="UrlKey"/>, then the scheme and a leading <c>www.</c> removed, so an http → https move keeps it.</summary>
    public static string? LinkKey(string? url)
    {
        if (!TryParts(url, out _, out var authority, out var pathAndQuery))
        {
            return null;
        }

        if (authority.StartsWith("www.", StringComparison.Ordinal) && authority.Length > 4)
        {
            authority = authority[4..];
        }

        return authority + pathAndQuery;
    }

    /// <summary>The SHA-256 of a key's UTF-8 bytes, the stored <c>BINARY(32)</c> form.</summary>
    public static byte[] Hash(string key) => NewsHashing.Sha256(key);

    /// <summary>The canonical host of a web URL: lower-case, IDN as punycode, an IPv6 literal unbracketed.</summary>
    public static string? CanonicalHost(Uri uri)
    {
        if (!NewsUrl.IsWebUrl(uri))
        {
            return null;
        }

        if (uri.HostNameType is UriHostNameType.IPv6)
        {
            return uri.Host.Trim('[', ']').ToLowerInvariant();
        }

        if (uri.HostNameType is UriHostNameType.IPv4)
        {
            return uri.Host;
        }

        try
        {
            return Idn.GetAscii(uri.Host.TrimEnd('.')).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static bool TryParts(string? url, out string scheme, out string authority, out string pathAndQuery)
    {
        scheme = authority = pathAndQuery = string.Empty;
        var trimmed = url?.Trim();
        if (string.IsNullOrEmpty(trimmed) || !Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || !NewsUrl.IsWebUrl(uri))
        {
            return false;
        }

        var host = CanonicalHost(uri);
        if (host is null)
        {
            return false;
        }

        scheme = uri.Scheme.ToLowerInvariant();
        var hostPart = uri.HostNameType is UriHostNameType.IPv6 ? $"[{host}]" : host;
        authority = uri.IsDefaultPort ? hostPart : $"{hostPart}:{uri.Port.ToString(CultureInfo.InvariantCulture)}";
        var path = uri.GetComponents(UriComponents.Path | UriComponents.KeepDelimiter, UriFormat.UriEscaped);
        if (path.Length == 0)
        {
            path = "/";
        }

        pathAndQuery = path + FilterQuery(uri.GetComponents(UriComponents.Query, UriFormat.UriEscaped));
        return true;
    }

    private static string FilterQuery(string query)
    {
        if (query.Length == 0)
        {
            return string.Empty;
        }

        var kept = new StringBuilder();
        foreach (var parameter in query.Split('&'))
        {
            if (parameter.Length == 0 || IsTrackingParameter(parameter))
            {
                continue;
            }

            kept.Append(kept.Length == 0 ? '?' : '&').Append(parameter);
        }

        return kept.ToString();
    }

    private static bool IsTrackingParameter(string parameter)
    {
        var equals = parameter.IndexOf('=', StringComparison.Ordinal);
        var name = equals < 0 ? parameter : parameter[..equals];
        return name.StartsWith("utm_", StringComparison.OrdinalIgnoreCase)
            || name.Equals("fbclid", StringComparison.OrdinalIgnoreCase)
            || name.Equals("gclid", StringComparison.OrdinalIgnoreCase)
            || name.Equals("mc_cid", StringComparison.OrdinalIgnoreCase)
            || name.Equals("mc_eid", StringComparison.OrdinalIgnoreCase)
            || name.Equals("_hsenc", StringComparison.OrdinalIgnoreCase);
    }
}

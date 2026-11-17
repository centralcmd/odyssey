using Odyssey.Dtos;

namespace Odyssey.Core.Internet.News.Content;

/// <summary>
/// The URL sanitiser (issue #470 §8.7): only an absolute <c>http</c>/<c>https</c> URL with no user info and at most
/// <see cref="NewsLimits.MaxUrlLength"/> characters is kept. Everything else — <c>javascript:</c>, <c>data:</c>,
/// <c>mailto:</c>, a relative reference that cannot be resolved, credentials — becomes <c>null</c> (an image reference
/// that sanitises to <c>null</c> is <c>Rejected</c> by the caller).
/// </summary>
public static class NewsUrl
{
    /// <summary>
    /// Sanitises <paramref name="value"/>, resolving a relative reference against <paramref name="baseUrl"/> first when
    /// one is given. The result is the URL's canonical absolute spelling.
    /// </summary>
    public static string? Sanitize(string? value, Uri? baseUrl = null) =>
        TryResolve(value, baseUrl, out var uri) ? uri.AbsoluteUri : null;

    /// <summary>As <see cref="Sanitize"/>, returning the parsed <see cref="Uri"/>.</summary>
    public static bool TryResolve(string? value, Uri? baseUrl, out Uri uri)
    {
        uri = null!;
        var trimmed = NewsText.Clean(value).Trim();
        if (trimmed.Length == 0 || trimmed.Length > NewsLimits.MaxUrlLength)
        {
            return false;
        }

        // Whitespace or a control inside a URL is never legitimate, and Uri would quietly drop or escape it.
        if (trimmed.Any(char.IsWhiteSpace))
        {
            return false;
        }

        Uri? candidate;
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absolute) && !IsImplicitFileUri(trimmed, absolute))
        {
            candidate = absolute;
        }
        else if (baseUrl is not null && IsWebUrl(baseUrl) && Uri.TryCreate(baseUrl, trimmed, out var resolved))
        {
            candidate = resolved;
        }
        else
        {
            return false;
        }

        if (!IsWebUrl(candidate) || candidate.AbsoluteUri.Length > NewsLimits.MaxUrlLength)
        {
            return false;
        }

        uri = candidate;
        return true;
    }

    /// <summary>An absolute <c>http</c>/<c>https</c> URL with a host and no user info.</summary>
    public static bool IsWebUrl(Uri uri) =>
        uri.IsAbsoluteUri
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && uri.UserInfo.Length == 0
        && uri.Host.Length > 0;

    // On Linux "/path" parses as an absolute file:// URI; for a feed it is a relative reference.
    private static bool IsImplicitFileUri(string text, Uri uri) =>
        uri.IsFile && !text.StartsWith("file:", StringComparison.OrdinalIgnoreCase);
}

using Odyssey.Dtos;

namespace Odyssey.Client.Pages.Internet;

/// <summary>
/// The feed dialog's client-side checks (issue #473 §8.1), mirroring the DTO annotations through
/// <see cref="NewsLimits"/>. The server stays the enforcer and its 400/422 always wins; private-address and port
/// rules are never pre-checked here.
/// </summary>
public static class NewsFeedForm
{
    public const string UrlMessage = "Enter the feed’s full address, starting with http:// or https://.";

    public static readonly string TitleMessage = $"Keep it under {NewsLimits.MaxCustomTitleLength} characters.";

    public static readonly string IntervalMessage =
        $"Use {NewsLimits.MinFetchIntervalMinutes} to {NewsLimits.MaxFetchIntervalMinutes} minutes, or leave empty.";

    public static readonly string UrlHelp =
        "Required. An RSS, Atom or JSON Feed address starting with http:// or https://. Odyssey checks it on the first fetch.";

    public const string TitleHelp = "Optional. Leave empty to use the feed’s own title.";

    public static readonly string IntervalHelp =
        $"Optional. {NewsLimits.MinFetchIntervalMinutes}–{NewsLimits.MaxFetchIntervalMinutes} minutes; empty uses the default.";

    /// <summary>Required; ≤ <see cref="NewsLimits.MaxUrlLength"/>; an absolute http/https URI with a host and no user info.</summary>
    public static string? ValidateUrl(string? url)
    {
        var value = url?.Trim();
        if (string.IsNullOrEmpty(value) || value.Length > NewsLimits.MaxUrlLength)
        {
            return UrlMessage;
        }

        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
               && !string.IsNullOrEmpty(uri.Host)
               && string.IsNullOrEmpty(uri.UserInfo)
            ? null
            : UrlMessage;
    }

    public static string? ValidateTitle(string? title) =>
        title is { } t && t.Trim().Length > NewsLimits.MaxCustomTitleLength ? TitleMessage : null;

    public static string? ValidateInterval(decimal? minutes) =>
        minutes is not { } m ? null
        : m != decimal.Truncate(m) || m < NewsLimits.MinFetchIntervalMinutes || m > NewsLimits.MaxFetchIntervalMinutes ? IntervalMessage
        : null;

    /// <summary>
    /// The address with its query masked: <c>https://example.com/feed.xml?…</c> (§3.3). A private feed's token often
    /// rides in the query, so it is hidden until <b>Show full address</b>. Returns the value unchanged when there is
    /// no query, and <see langword="null"/> mask when it is not an absolute URI.
    /// </summary>
    public static (string Shown, bool Masked) Mask(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Query.Length > 1)
        {
            return ($"{uri.GetLeftPart(UriPartial.Path)}?…", true);
        }

        var cut = url.IndexOf('?', StringComparison.Ordinal);
        return cut >= 0 && cut < url.Length - 1 ? (url[..cut] + "?…", true) : (url, false);
    }
}

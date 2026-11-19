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

    public const string UrlHelp = "RSS, Atom or JSON Feed address. Odyssey checks it on the first fetch.";

    public const string TitleHelp = "Leave empty to use the feed’s own title.";

    public static readonly string IntervalHelp =
        $"{NewsLimits.MinFetchIntervalMinutes}–{NewsLimits.MaxFetchIntervalMinutes} minutes. Empty uses the default.";

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
}

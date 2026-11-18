using Odyssey.Core.Internet.News.Content;
using Odyssey.Core.Internet.News.Fetching;
using Odyssey.Dtos;

namespace Odyssey.Core.Internet.News.Reading;

/// <summary>A feed address that passed <see cref="NewsFeedUrlRules"/>: the stored text, its host and its key hash.</summary>
public sealed record NewsFeedAddress(string Url, string Host, byte[] UrlKeyHash);

/// <summary>
/// The feed URL rules (issue #470 §5.1 item 5, §8.1): an absolute <c>http</c>/<c>https</c> URL of at most
/// <see cref="NewsLimits.MaxUrlLength"/> characters with no user info, a port of 80 or 443 unless the allow-list grants
/// the host another, and — for an IP-literal host — an address the egress policy would reach. Every create and every URL
/// change runs them, accepting a suggestion included. The API makes no request, so a named host is checked for its
/// address only when the worker connects (§7.9). Messages name the rule, never the submitted value.
/// </summary>
public static class NewsFeedUrlRules
{
    public static NewsFeedAddress Validate(string? url, IReadOnlyList<NewsPrivateHostGrant> grants) =>
        TryValidate(url, grants, out var address, out var message) ? address! : throw NewsErrors.UrlRejected(message!);

    /// <summary>
    /// The same rules without an exception (issue #508 §3, A9), for the OPML import, which checks up to
    /// <c>NewsOpmlMaxImportEntries</c> addresses and reports each refusal as a row. <paramref name="message"/> is the
    /// rule's curated message, which never echoes the address.
    /// </summary>
    public static bool TryValidate(string? url, IReadOnlyList<NewsPrivateHostGrant> grants, out NewsFeedAddress? address, out string? message)
    {
        ArgumentNullException.ThrowIfNull(grants);
        address = null;
        message = Check(url?.Trim() ?? string.Empty, grants, out var host, out var key);
        if (message is not null)
        {
            return false;
        }

        address = new NewsFeedAddress(url!.Trim(), host!, NewsUrlKey.Hash(key!));
        return true;
    }

    private static string? Check(string trimmed, IReadOnlyList<NewsPrivateHostGrant> grants, out string? host, out string? key)
    {
        host = key = null;
        if (trimmed.Length == 0)
        {
            return "Enter the feed's address.";
        }

        if (trimmed.Length > NewsLimits.MaxUrlLength)
        {
            return $"A feed address is at most {NewsLimits.MaxUrlLength} characters.";
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return "A feed address must start with http:// or https://.";
        }

        if (uri.UserInfo.Length > 0)
        {
            return "A feed address cannot carry a user name or password.";
        }

        if (!NewsUrl.IsWebUrl(uri) || NewsUrlKey.CanonicalHost(uri) is not { } canonical || NewsUrlKey.UrlKey(trimmed) is not { } urlKey)
        {
            return "This is not a valid feed address.";
        }

        var context = new NewsEgressContext(canonical, NewsEgressPurpose.Feed, grants);
        switch (NewsEgressPolicy.CheckUrl(uri, context))
        {
            case NewsEgressRefusal.None:
                host = canonical;
                key = urlKey;
                return null;
            case NewsEgressRefusal.Port:
                return "A feed address may use port 80 or 443 unless an administrator has granted its host another.";
            case NewsEgressRefusal.PrivateAddress:
            case NewsEgressRefusal.HardDeniedAddress:
                return "This address cannot be fetched.";
            default:
                return "This is not a valid feed address.";
        }
    }
}

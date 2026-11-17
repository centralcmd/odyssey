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
    public static NewsFeedAddress Validate(string? url, IReadOnlyList<NewsPrivateHostGrant> grants)
    {
        ArgumentNullException.ThrowIfNull(grants);
        var trimmed = url?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            throw NewsErrors.UrlRejected("Enter the feed's address.");
        }

        if (trimmed.Length > NewsLimits.MaxUrlLength)
        {
            throw NewsErrors.UrlRejected($"A feed address is at most {NewsLimits.MaxUrlLength} characters.");
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw NewsErrors.UrlRejected("A feed address must start with http:// or https://.");
        }

        if (uri.UserInfo.Length > 0)
        {
            throw NewsErrors.UrlRejected("A feed address cannot carry a user name or password.");
        }

        if (!NewsUrl.IsWebUrl(uri) || NewsUrlKey.CanonicalHost(uri) is not { } host || NewsUrlKey.UrlKey(trimmed) is not { } key)
        {
            throw NewsErrors.UrlRejected("This is not a valid feed address.");
        }

        var context = new NewsEgressContext(host, NewsEgressPurpose.Feed, grants);
        switch (NewsEgressPolicy.CheckUrl(uri, context))
        {
            case NewsEgressRefusal.None:
                break;
            case NewsEgressRefusal.Port:
                throw NewsErrors.UrlRejected("A feed address may use port 80 or 443 unless an administrator has granted its host another.");
            case NewsEgressRefusal.PrivateAddress:
            case NewsEgressRefusal.HardDeniedAddress:
                throw NewsErrors.UrlRejected("This address cannot be fetched.");
            default:
                throw NewsErrors.UrlRejected("This is not a valid feed address.");
        }

        return new NewsFeedAddress(trimmed, host, NewsUrlKey.Hash(key));
    }
}

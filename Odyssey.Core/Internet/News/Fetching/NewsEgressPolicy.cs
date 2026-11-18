using System.Net;
using Odyssey.Core.Internet.News.Content;
using Odyssey.Dtos;

namespace Odyssey.Core.Internet.News.Fetching;

/// <summary>What a news request fetches. An allow-list grant reaches an image only on the feed's own host (§6.2).</summary>
public enum NewsEgressPurpose
{
    Feed = 0,
    Image = 1,
}

/// <summary>Why the egress policy refused a request. Every refusal is <c>BlockedAddress</c> to the feed (§7.9).</summary>
public enum NewsEgressRefusal
{
    None = 0,

    /// <summary>Not an absolute http(s) URL, or one with user info.</summary>
    NotAWebUrl = 1,

    /// <summary>A port other than 80 and 443 that no grant names.</summary>
    Port = 2,

    /// <summary>A default-blocked address with no grant that applies.</summary>
    PrivateAddress = 3,

    /// <summary>A hard-denied address: never allowed, allow-listed or not.</summary>
    HardDeniedAddress = 4,

    /// <summary>The host resolved to no address.</summary>
    NoAddress = 5,
}

/// <summary>
/// The per-request egress context (§7.9), carried on the request (<see cref="NewsEgress.ContextKey"/>) so the
/// connect-time check applies the same policy to the exact request it opens a socket for.
/// </summary>
/// <param name="FeedHost">The canonical host of the FEED's own URL (<see cref="NewsUrlKey.CanonicalHost"/>).</param>
/// <param name="Purpose">A feed request (its redirect hops included) or an image request.</param>
/// <param name="Grants">The allow-list, read uncached for this fetch; a degraded read is empty (§7.9).</param>
/// <param name="ChainOnFeedHost">
/// Every hop so far stayed on the feed's host. Once a redirect leaves it, no grant applies for the rest of the chain,
/// so a public host cannot redirect into an allow-listed private one (AC 21).
/// </param>
public sealed record NewsEgressContext(string FeedHost, NewsEgressPurpose Purpose, IReadOnlyList<NewsPrivateHostGrant> Grants, bool ChainOnFeedHost = true)
{
    /// <summary>The grant for <paramref name="host"/>, when one applies to this request.</summary>
    public NewsPrivateHostGrant? GrantFor(string host) =>
        ChainOnFeedHost && string.Equals(host, FeedHost, StringComparison.Ordinal)
            ? Grants.FirstOrDefault(g => string.Equals(g.Host, host, StringComparison.Ordinal))
            : null;
}

/// <summary>
/// The egress policy (issue #470 §7.9), in one place for the URL check, the connect-time check and the redirect
/// check: http(s) without user info; ports 80 and 443 unless the host's grant names another; global unicast only,
/// unless a grant for the feed's own host opens a default-blocked address; hard-denied addresses never.
/// </summary>
public static class NewsEgressPolicy
{
    /// <summary>The URL-level check, before any DNS: scheme, user info, port, and an IP-literal host's class.</summary>
    public static NewsEgressRefusal CheckUrl(Uri url, NewsEgressContext context)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(context);
        if (!NewsUrl.IsWebUrl(url) || NewsUrlKey.CanonicalHost(url) is not { } host)
        {
            return NewsEgressRefusal.NotAWebUrl;
        }

        var portRefusal = CheckPort(host, url.Port, context);
        if (portRefusal != NewsEgressRefusal.None)
        {
            return portRefusal;
        }

        return IPAddress.TryParse(host, out var literal) ? CheckAddress(literal, host, context) : NewsEgressRefusal.None;
    }

    /// <summary>The port rule: 80 and 443, plus the one port the host's applicable grant names.</summary>
    public static NewsEgressRefusal CheckPort(string host, int port, NewsEgressContext context) =>
        port is 80 or 443 || context.GrantFor(host)?.Port == port ? NewsEgressRefusal.None : NewsEgressRefusal.Port;

    /// <summary>The address rule, applied to every address a host resolves to (and to an IP literal).</summary>
    public static NewsEgressRefusal CheckAddress(IPAddress address, string host, NewsEgressContext context) =>
        NewsAddressClassifier.Classify(address) switch
        {
            NewsAddressClass.Public => NewsEgressRefusal.None,
            NewsAddressClass.Private => context.GrantFor(host) is not null ? NewsEgressRefusal.None : NewsEgressRefusal.PrivateAddress,
            _ => NewsEgressRefusal.HardDeniedAddress,
        };
}

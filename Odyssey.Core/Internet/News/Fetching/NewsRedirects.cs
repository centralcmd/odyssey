using System.Net;
using Odyssey.Core.Internet.News.Content;

namespace Odyssey.Core.Internet.News.Fetching;

/// <summary>What to do with a response's redirect (§7.9, §9.3).</summary>
public enum NewsRedirectOutcome
{
    /// <summary>Not a redirect: handle the response.</summary>
    NotARedirect = 0,

    /// <summary>Follow <see cref="NewsRedirectStep.Next"/> (after taking its host's throttle slot, §3.6).</summary>
    Follow = 1,

    /// <summary>A 6th redirect: <c>TooManyRedirects</c>.</summary>
    TooManyRedirects = 2,

    /// <summary>No usable <c>Location</c>, or not an http(s) URL without user info: <c>HttpStatus</c>.</summary>
    InvalidLocation = 3,

    /// <summary>An https → http downgrade for an image (allowed for feeds only): <c>BlockedAddress</c>.</summary>
    DowngradeRefused = 4,
}

/// <summary>One redirect decision.</summary>
/// <param name="Next">The URL to request next, when following.</param>
/// <param name="Context">The egress context for the next hop (grants stop applying once the chain leaves the feed's host).</param>
/// <param name="State">The chain state to pass to the next decision.</param>
public sealed record NewsRedirectStep(NewsRedirectOutcome Outcome, Uri? Next, NewsEgressContext Context, NewsRedirectState State);

/// <summary>
/// The redirect chain so far: how many hops were actually requested, and the <c>SuggestedUrl</c> a same-host
/// permanent redirect recorded (§3.3, §9.3).
/// </summary>
/// <param name="Hops">Redirects followed so far.</param>
/// <param name="SuggestedUrl">The target of the chain's same-host 301/308 prefix, if any.</param>
/// <param name="PermanentSameHost">Every hop so far was a 301/308 to the feed's own host.</param>
public sealed record NewsRedirectState(int Hops, string? SuggestedUrl, bool PermanentSameHost)
{
    public static NewsRedirectState Start { get; } = new(0, null, true);
}

/// <summary>
/// Manual redirects (issue #470 §7.9): at most <see cref="MaxRedirects"/> hops actually requested, every hop re-checked
/// by the policy (the caller re-runs <see cref="NewsEgressPolicy.CheckUrl"/> through <see cref="NewsEgress.CreateRequest"/>,
/// and the connect-time check runs again for the new connection). A same-host 301/308 records a suggested URL; a
/// cross-host one — a subdomain included — does not, and never lets a grant follow it.
/// </summary>
public static class NewsRedirects
{
    public const int MaxRedirects = 5;

    public static NewsRedirectStep Next(Uri current, HttpStatusCode status, Uri? location, NewsEgressContext context, NewsRedirectState state)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(state);

        var code = (int)status;
        if (code is not (301 or 302 or 303 or 307 or 308))
        {
            return new NewsRedirectStep(NewsRedirectOutcome.NotARedirect, null, context, state);
        }

        if (state.Hops >= MaxRedirects)
        {
            return new NewsRedirectStep(NewsRedirectOutcome.TooManyRedirects, null, context, state);
        }

        if (location is null || !NewsUrl.TryResolve(location.OriginalString, current, out var next) || NewsUrlKey.CanonicalHost(next) is not { } nextHost)
        {
            return new NewsRedirectStep(NewsRedirectOutcome.InvalidLocation, null, context, state);
        }

        if (context.Purpose == NewsEgressPurpose.Image && current.Scheme == Uri.UriSchemeHttps && next.Scheme == Uri.UriSchemeHttp)
        {
            return new NewsRedirectStep(NewsRedirectOutcome.DowngradeRefused, null, context, state);
        }

        var sameHost = string.Equals(nextHost, context.FeedHost, StringComparison.Ordinal);
        var permanent = code is 301 or 308;
        var stillPermanentSameHost = state.PermanentSameHost && permanent && sameHost;
        var suggestion = stillPermanentSameHost && context.Purpose == NewsEgressPurpose.Feed ? next.AbsoluteUri : state.SuggestedUrl;

        var nextContext = context with { ChainOnFeedHost = context.ChainOnFeedHost && sameHost };
        return new NewsRedirectStep(
            NewsRedirectOutcome.Follow,
            next,
            nextContext,
            new NewsRedirectState(state.Hops + 1, suggestion, stillPermanentSameHost));
    }
}

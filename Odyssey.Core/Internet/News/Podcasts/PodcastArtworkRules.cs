using Microsoft.EntityFrameworkCore;
using Odyssey.Context;

namespace Odyssey.Core.Internet.News.Podcasts;

/// <summary>The per-feed artwork incident rule (issue #556 §3.4 artwork step 3).</summary>
public static class PodcastArtworkRules
{
    /// <summary>
    /// Records an incident against the feed and rejects its remaining <c>Pending</c> / <c>Deferred</c> artwork, staged
    /// on the caller's context: a hostile feed costs at most one worker exit, not one per image.
    /// </summary>
    public static async Task BlockFeedAsync(OdysseyContext context, Guid feedId, DateTime now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var details = await context.PodcastFeedDetails.FirstOrDefaultAsync(d => d.NewsFeedId == feedId, cancellationToken);
        if (details is not null)
        {
            details.ArtworkIncidentCount++;
            details.ArtworkIncidentAtUtc = now;
        }

        var waiting = await context.PodcastArtwork
            .Where(a => a.NewsFeedId == feedId && (a.State == PodcastArtworkState.Pending || a.State == PodcastArtworkState.Deferred))
            .ToListAsync(cancellationToken);
        foreach (var artwork in waiting)
        {
            artwork.State = PodcastArtworkState.Rejected;
            artwork.RejectedReason = PodcastArtworkRejectedReason.FeedIncident;
            artwork.DeferredReason = null;
            artwork.NextAttemptAtUtc = null;
            artwork.LeaseOwner = null;
            artwork.LeaseExpiresAtUtc = null;
        }
    }

    /// <summary>
    /// Clears a feed's incident count and returns its feed-rejected artwork to <c>Pending</c> once (30 days after the
    /// last incident, or on a URL change). Artwork rejected for its own render incident stays rejected.
    /// </summary>
    public static async Task ResetFeedIncidentsAsync(OdysseyContext context, PodcastFeedDetails details, DateTime now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(details);
        details.ArtworkIncidentCount = 0;
        details.ArtworkIncidentAtUtc = null;
        var rejected = await context.PodcastArtwork
            .Where(a => a.NewsFeedId == details.NewsFeedId && a.State == PodcastArtworkState.Rejected
                && a.RejectedReason == PodcastArtworkRejectedReason.FeedIncident)
            .ToListAsync(cancellationToken);
        foreach (var artwork in rejected)
        {
            artwork.State = PodcastArtworkState.Pending;
            artwork.RejectedReason = null;
            artwork.Attempts = 0;
            artwork.NextAttemptAtUtc = now;
        }
    }
}

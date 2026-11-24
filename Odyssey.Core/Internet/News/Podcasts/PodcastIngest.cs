using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Odyssey.Context;
using Odyssey.Core.Internet.News.Jobs;
using Odyssey.Dtos;
using Odyssey.Dtos.Internet;

namespace Odyssey.Core.Internet.News.Podcasts;

/// <summary>
/// What one podcast fetch knows before its items are ingested (issue #556 §3.4 ingest step 6): the auto-download
/// conditions that do not depend on the item, read once, and the counts it keeps as it queues.
/// </summary>
public sealed class PodcastIngestState
{
    public required bool BaselineValid { get; init; }

    public required int QueuedToday { get; set; }

    public required int Unfetched { get; set; }

    public required long QuotaUsedBytes { get; init; }

    public required DateOnly Today { get; init; }

    /// <summary>The feed's known artwork by URL key (hex), with its id: every new one adds here.</summary>
    public required Dictionary<string, Guid> Artwork { get; init; }

    public required bool ArtworkBlocked { get; init; }

    /// <summary>Episode hashes already stored, by article id: an unchanged one is never rewritten.</summary>
    public required Dictionary<Guid, byte[]> EpisodeHashes { get; init; }

    public int AutoQueued { get; set; }

    public bool ArtworkCapLogged { get; set; }
}

/// <summary>
/// The episode step of ingest (issue #556 §3.4 ingest steps 3–7). It stages rows on the caller's context and never
/// saves, so a new or changed item's episode commits with its article in that article's own fenced save. Pure database
/// work: no file is written here (artwork and chapter files are fetched by their own phases).
/// </summary>
public sealed class PodcastIngest(ILogger<PodcastIngest> logger)
{
    /// <summary>Reads the fetch's starting state and upserts the channel fields (§3.4 step 5, §4.2), in one save.</summary>
    public async Task<PodcastIngestState> BeginAsync(
        OdysseyContext context, NewsClaim claim, Parsing.NewsParsedFeed feed, NewsSettings settings, DateTime now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(settings);
        var details = await context.PodcastFeedDetails.FirstOrDefaultAsync(d => d.NewsFeedId == claim.FeedId, cancellationToken);
        if (details is null)
        {
            details = new PodcastFeedDetails { NewsFeedId = claim.FeedId };
            context.PodcastFeedDetails.Add(details);
        }

        var today = DateOnly.FromDateTime(now);
        if (details.AutoDownloadDate != today)
        {
            details.AutoDownloadDate = today;
            details.AutoDownloadsToday = 0;
        }

        // §3.4 artwork step 3: a feed whose artwork caused an incident 30+ days ago gets its feed-rejected artwork back.
        if (details.ArtworkIncidentCount > 0 && details.ArtworkIncidentAtUtc is { } incident && now - incident >= PodcastTiming.ArtworkIncidentReset)
        {
            await PodcastArtworkRules.ResetFeedIncidentsAsync(context, details, now, cancellationToken);
        }

        var artwork = await context.PodcastArtwork.AsNoTracking()
            .Where(a => a.NewsFeedId == claim.FeedId)
            .Select(a => new { a.PodcastArtworkId, a.SourceUrlKeyHash })
            .ToListAsync(cancellationToken);
        var known = artwork.ToDictionary(a => Convert.ToHexString(a.SourceUrlKeyHash), a => a.PodcastArtworkId, StringComparer.Ordinal);
        var state = new PodcastIngestState
        {
            BaselineValid = details.BaselineAtUtc is { } baseline && (claim.KindChangedAtUtc is not { } changed || baseline > changed),
            QueuedToday = details.AutoDownloadsToday,
            Unfetched = await UnfetchedAutoAsync(context, claim.FeedId, cancellationToken),
            QuotaUsedBytes = await QuotaUsedAsync(context, cancellationToken),
            Today = today,
            Artwork = known,
            ArtworkBlocked = details.ArtworkIncidentCount > 0,
            EpisodeHashes = (await context.PodcastEpisodes.AsNoTracking()
                    .Where(e => e.NewsFeedId == claim.FeedId)
                    .Select(e => new { e.NewsArticleId, e.EpisodeHash })
                    .ToListAsync(cancellationToken))
                .ToDictionary(e => e.NewsArticleId, e => e.EpisodeHash),
        };

        if (PodcastFields.NormalizeFeed(feed.Podcast) is { } channel)
        {
            details.Author = channel.Author;
            details.Explicit = channel.Explicit;
            details.PodcastType = channel.PodcastType;
            details.ArtworkId = channel.ImageUrl is { } image ? Artwork(context, claim.FeedId, image, state, now) : null;
        }

        await context.SaveChangesAsync(cancellationToken);
        return state;
    }

    /// <summary>Whether an existing article's episode needs a write this fetch (its row is missing or its hash differs).</summary>
    public static bool NeedsWrite(PodcastIngestState state, Guid articleId, PodcastNormalizedEpisode episode) =>
        !state.EpisodeHashes.TryGetValue(articleId, out var stored) || !stored.AsSpan().SequenceEqual(episode.EpisodeHash);

    /// <summary>
    /// Stages the episode, its audio row, its feed chapters and its artwork link on <paramref name="context"/>, unsaved
    /// (§3.4 steps 3–6). <paramref name="articleIsNew"/> is true only for an article this fetch inserted — the one case
    /// auto-download may queue.
    /// </summary>
    public async Task StageAsync(
        OdysseyContext context, NewsClaim claim, Guid articleId, DateTime articleSortAtUtc, PodcastNormalizedEpisode episode,
        bool articleIsNew, PodcastIngestState state, NewsSettings settings, DateTime now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(episode);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(settings);

        var row = articleIsNew ? null : await context.PodcastEpisodes.FirstOrDefaultAsync(e => e.NewsArticleId == articleId, cancellationToken);
        var hostHash = HostHash(episode.EnclosureUrl);
        if (row is null)
        {
            row = new PodcastEpisode { NewsArticleId = articleId, NewsFeedId = claim.FeedId, SortAtUtc = articleSortAtUtc };
            context.PodcastEpisodes.Add(row);
            context.PodcastEpisodeAudio.Add(new PodcastEpisodeAudio
            {
                NewsArticleId = articleId,
                NewsFeedId = claim.FeedId,
                EnclosureHostKeyHash = hostHash,
                State = PodcastAudioState.None,
                AudioFence = Guid.NewGuid(),
            });
        }
        else
        {
            // §4.4: the per-host rule follows the enclosure's host, except while a download holds its guard.
            var audio = await context.PodcastEpisodeAudio.FirstOrDefaultAsync(a => a.NewsArticleId == articleId, cancellationToken);
            if (audio is not null && audio.State != PodcastAudioState.Downloading && !audio.EnclosureHostKeyHash.AsSpan().SequenceEqual(hostHash))
            {
                audio.EnclosureHostKeyHash = hostHash;
            }
        }

        var chaptersChanged = !Equals(row.ChaptersUrl, episode.ChaptersUrl);
        row.EnclosureUrl = episode.EnclosureUrl;
        row.EnclosureDeclaredType = episode.EnclosureDeclaredType;
        row.EnclosureDeclaredLength = episode.EnclosureDeclaredLength;
        row.Description = episode.Description;
        row.DurationSeconds = episode.DurationSeconds;
        row.SeasonNumber = episode.SeasonNumber;
        row.EpisodeNumber = episode.EpisodeNumber;
        row.EpisodeType = episode.EpisodeType;
        row.Explicit = episode.Explicit;
        row.ArtworkId = episode.ImageUrl is { } image ? Artwork(context, claim.FeedId, image, state, now) : null;
        row.EpisodeHash = episode.EpisodeHash;
        if (chaptersChanged)
        {
            row.ChaptersUrl = episode.ChaptersUrl;
            row.ChaptersUrlKeyHash = episode.ChaptersUrl is { } url ? PodcastFields.UrlKeyHash(url) : null;
            row.ChaptersFetchState = episode.ChaptersUrl is null ? PodcastChaptersFetchState.None : PodcastChaptersFetchState.Pending;
            row.ChaptersAttempts = 0;
            row.ChaptersNextAttemptAtUtc = episode.ChaptersUrl is null ? null : now;
        }

        await PodcastChapterRows.ReplaceInPlaceAsync(context, articleId, PodcastChapterSource.Feed, episode.FeedChapters, cancellationToken);
        state.EpisodeHashes[articleId] = episode.EpisodeHash;

        if (articleIsNew && ShouldAutoDownload(claim, state, settings))
        {
            var audio = context.PodcastEpisodeAudio.Local.First(a => a.NewsArticleId == articleId);
            audio.State = PodcastAudioState.Queued;
            audio.RequestedBy = PodcastAudioRequestedBy.Auto;
            audio.RequestedAtUtc = now;
            audio.NextAttemptAtUtc = now;
            state.QueuedToday++;
            state.Unfetched++;
            state.AutoQueued++;
            var details = await context.PodcastFeedDetails.FirstAsync(d => d.NewsFeedId == claim.FeedId, cancellationToken);
            details.AutoDownloadDate = state.Today;
            details.AutoDownloadsToday = state.QueuedToday;
        }
    }

    /// <summary>
    /// Stamps the baseline after a fetch that completed as a full parse (§3.4 step 7): success with no transient skip,
    /// so a fetch cut short by a cap or the ingest budget never makes the rest of the back catalogue look new.
    /// </summary>
    public static async Task StampBaselineAsync(OdysseyContext context, Guid feedId, DateTime now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var details = await context.PodcastFeedDetails.FirstOrDefaultAsync(d => d.NewsFeedId == feedId, cancellationToken);
        if (details is not null && details.BaselineAtUtc is null)
        {
            details.BaselineAtUtc = now;
        }
    }

    /// <summary>The §3.4 step 6 conditions that depend on the feed and the fetch, not on the item.</summary>
    private static bool ShouldAutoDownload(NewsClaim claim, PodcastIngestState state, NewsSettings settings) =>
        claim.AutoDownload
        && state.BaselineValid
        && state.QueuedToday < settings.PodcastAutoDownloadsPerFeedPerDay
        && state.Unfetched < settings.PodcastAutoDownloadMaxUnfetchedPerFeed
        && state.QuotaUsedBytes < (long)(settings.PodcastAudioQuotaBytes * PodcastLimits.AutoDownloadQuotaShare);

    /// <summary>
    /// The feed's artwork id for <paramref name="url"/>, inserting a <c>Pending</c> row when new (§3.4 step 5). At the
    /// per-feed cap, or while the feed's artwork is blocked by an incident, no row is inserted and the link is null.
    /// </summary>
    private Guid? Artwork(OdysseyContext context, Guid feedId, string url, PodcastIngestState state, DateTime now)
    {
        var hash = PodcastFields.UrlKeyHash(url);
        var key = Convert.ToHexString(hash);
        if (state.Artwork.TryGetValue(key, out var id))
        {
            return id;
        }

        if (state.ArtworkBlocked)
        {
            return null;
        }

        if (state.Artwork.Count >= PodcastLimits.MaxArtworkPerFeed)
        {
            if (!state.ArtworkCapLogged)
            {
                state.ArtworkCapLogged = true;
                logger.LogWarning("Podcast feed {FeedId} reached its artwork cap; new images fall back to the show artwork.", feedId);
            }

            return null;
        }

        var artwork = new PodcastArtwork
        {
            PodcastArtworkId = Guid.NewGuid(),
            NewsFeedId = feedId,
            SourceUrl = url,
            SourceUrlKeyHash = hash,
            State = PodcastArtworkState.Pending,
            NextAttemptAtUtc = now,
            CreatedAtUtc = now,
        };
        context.PodcastArtwork.Add(artwork);
        state.Artwork[key] = artwork.PodcastArtworkId;
        return artwork.PodcastArtworkId;
    }

    /// <summary>SHA-256 of the lower-case enclosure host, for the one-download-per-host guard.</summary>
    public static byte[] HostHash(string url) =>
        NewsUrlKey.Hash(Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.IdnHost.ToLowerInvariant() : url);

    /// <summary>Auto-downloaded episodes of the feed on, or on their way to, the server that nobody has consumed yet: no device download and no finished listen since it was stored (§3.4, #593).</summary>
    private static Task<int> UnfetchedAutoAsync(OdysseyContext context, Guid feedId, CancellationToken cancellationToken) =>
        (from row in PodcastConsumption.Rows(context.PodcastEpisodeAudio.AsNoTracking(), context.PodcastEpisodes.AsNoTracking())
         where row.Audio.NewsFeedId == feedId
             && row.Audio.RequestedBy == PodcastAudioRequestedBy.Auto
             && (row.Audio.State == PodcastAudioState.Stored || row.Audio.State == PodcastAudioState.Queued
                 || row.Audio.State == PodcastAudioState.Downloading || row.Audio.State == PodcastAudioState.Deferred)
             && row.ConsumedAtUtc == null
         select row.Audio).CountAsync(cancellationToken);

    /// <summary>The audio quota's usage: stored, awaiting deletion and reserved (§6.4).</summary>
    private static async Task<long> QuotaUsedAsync(OdysseyContext context, CancellationToken cancellationToken)
    {
        var totals = await context.NewsStorageTotals.AsNoTracking().FirstOrDefaultAsync(t => t.Id == NewsStorageTotal.SingletonId, cancellationToken);
        return totals is null ? 0 : totals.AudioBytes + totals.AudioPendingReleaseBytes + totals.AudioReservedBytes;
    }
}

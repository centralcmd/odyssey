using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Odyssey.Context;
using Odyssey.Core.FileStorage;
using Odyssey.Core.Internet.News.Podcasts.Files;
using Odyssey.Core.Internet.News.Reading;
using Odyssey.Core.Pagination;
using Odyssey.Dtos;
using Odyssey.Dtos.Internet;

namespace Odyssey.Core.Internet.News.Podcasts;

/// <summary>A stored file ready to serve: never its path or stored-object id outside this process.</summary>
public sealed record PodcastFileDescriptor(Guid StoredObjectId, string FeatureKey, string ContentType, string Sha256Hex, long SizeBytes, string? DownloadName);

/// <summary>What <c>POST …/audio</c> did (§5.3 #9): queued now or already in flight (<c>202</c>), or already stored (<c>200</c>).</summary>
public sealed record PodcastAudioRequestOutcome(bool AlreadyStored, PodcastEpisodeAudioInfo Audio);

/// <summary>
/// The podcast episode endpoints (issue #556 §5.2–§5.4): the list with its signed keyset cursor, the detail with its
/// show notes and chapters, the audio content with last-played stamping, the user's download and remove, and the
/// artwork content. An episode exists for the API only while its feed is a <c>Podcast</c> and not pending deletion; an
/// archived feed's episodes are still read and served, but a new download is refused.
/// </summary>
public sealed class PodcastEpisodeService(
    OdysseyContext context,
    NewsArticleService articles,
    INewsArticleSearch search,
    PodcastEpisodeCursor cursors,
    IPodcastAudioRequests requests,
    INewsCounterWriter counters,
    INewsSettingsLookup settings,
    IObjectStore store,
    FileStorageVolumeRegistry volumes,
    TimeProvider time,
    ILogger<PodcastEpisodeService> logger)
{
    private DateTime Now => time.GetUtcNow().UtcDateTime;

    // ── The list ──────────────────────────────────────────────────────────────────────────────────────

    public Task<PodcastEpisodePage> ListAsync(PodcastEpisodesQueryParams query, CancellationToken cancellationToken) =>
        NewsQueryGuard.RunAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(query);
            var filter = new PodcastEpisodeFilter(
                [.. NewsFilterIds.Normalize(query.FeedIds).Order()],
                [.. (query.AudioState ?? []).Distinct().Order()],
                ListQuery.NormalizeSearch(query.Search),
                query.IncludeArchivedFeeds,
                query.SortBy ?? PodcastEpisodeSortBy.Newest);
            var terms = filter.Search is null ? [] : NewsSearchTerms.Parse(filter.Search);
            if (filter.Search is not null && terms.Count == 0)
            {
                throw NewsErrors.SearchUnusable();
            }

            var limit = Math.Clamp(query.Limit, 1, NewsLimits.MaxPageSize);
            NewsCursorPosition? position = query.Cursor is { } raw ? cursors.Read(raw, filter) : null;
            var asOf = position?.AsOfUtc ?? Now;
            await EnsureFeedsAsync(filter.FeedIds, cancellationToken);

            var matching = await MatchingAsync(filter, terms, asOf, cancellationToken);
            var newest = filter.SortBy == PodcastEpisodeSortBy.Newest;
            var keyed = matching;
            if (position is { } p)
            {
                var (s, id) = (p.SortAtUtc, p.Id);
                keyed = newest
                    ? keyed.Where(e => e.SortAtUtc < s || (e.SortAtUtc == s && e.Id.CompareTo(id) < 0))
                    : keyed.Where(e => e.SortAtUtc > s || (e.SortAtUtc == s && e.Id.CompareTo(id) > 0));
            }

            var ordered = newest
                ? keyed.OrderByDescending(e => e.SortAtUtc).ThenByDescending(e => e.Id)
                : keyed.OrderBy(e => e.SortAtUtc).ThenBy(e => e.Id);
            var keys = await ordered.Skip(position is null ? query.Offset : 0).Take(limit + 1).News().ToListAsync(cancellationToken);

            var page = new PodcastEpisodePage { AsOfUtc = asOf };
            var hasMore = keys.Count > limit;
            if (hasMore)
            {
                keys = keys[..limit];
            }

            if (position is null)
            {
                var count = await matching.Take(NewsLimits.MaxCountedArticles + 1).News().CountAsync(cancellationToken);
                page.TotalCount = Math.Min(count, NewsLimits.MaxCountedArticles);
                page.TotalCountCapped = count > NewsLimits.MaxCountedArticles;
            }

            if (hasMore && keys.Count > 0)
            {
                page.NextCursor = cursors.Issue(filter, new NewsCursorPosition(keys[^1].SortAtUtc, keys[^1].Id, 0, asOf));
            }

            page.Items = [.. await SummariesAsync([.. keys.Select(k => k.Id)], cancellationToken)];
            return page;
        });

    /// <summary>Every named feed must be a live podcast feed (§5.2 #6): otherwise a <c>422</c> keyed <c>feedIds</c>.</summary>
    private async Task EnsureFeedsAsync(IReadOnlyList<Guid> feedIds, CancellationToken cancellationToken)
    {
        if (feedIds.Count == 0)
        {
            return;
        }

        var known = await context.NewsFeeds.AsNoTracking().News()
            .CountAsync(f => feedIds.Contains(f.NewsFeedId) && f.DeletionRequestedAtUtc == null && f.Kind == NewsFeedKind.Podcast, cancellationToken);
        if (known != feedIds.Count)
        {
            throw NewsErrors.FilterUnknown("feedIds");
        }
    }

    /// <summary>
    /// The matching episodes as sort keys (§5.2 #6 query plan). With named feeds the keys are the episode's own copy of the
    /// sort key, read on <c>IX_PodcastEpisodes_Feed_Sort</c>; with none they are the article's, read on the kind-aware
    /// <c>IX_NewsArticles_Kind_Sort</c> through <c>FeedKind = Podcast</c> (plus any feed whose copy is still being
    /// rewritten). The two are the same value — both are written once, at insert — and the join to the feed row stays the
    /// authoritative filter either way.
    /// </summary>
    private async Task<IQueryable<NewsArticleKey>> MatchingAsync(
        PodcastEpisodeFilter filter, IReadOnlyList<string> terms, DateTime asOf, CancellationToken cancellationToken)
    {
        var feedIds = filter.FeedIds;
        var feeds = context.NewsFeeds.AsNoTracking().Where(f => f.Kind == NewsFeedKind.Podcast && f.DeletionRequestedAtUtc == null
            && (filter.IncludeArchivedFeeds || f.ArchivedAtUtc == null || feedIds.Contains(f.NewsFeedId)));
        var stream = context.NewsArticles.AsNoTracking().Where(a => a.FirstSeenAtUtc <= asOf);
        if (feedIds.Count > 0)
        {
            feeds = feeds.Where(f => feedIds.Contains(f.NewsFeedId));
        }
        else
        {
            var pending = await NewsFeedKindRewrite.PendingFeeds(context).News().ToListAsync(cancellationToken);
            stream = NewsFeedKindRewrite.OfKind(stream, NewsFeedKind.Podcast, pending);
        }

        if (terms.Count > 0)
        {
            stream = search.Apply(stream, terms);
        }

        var episodes = context.PodcastEpisodes.AsNoTracking();
        if (filter.AudioStates.Count > 0)
        {
            var states = filter.AudioStates;
            episodes = episodes.Where(e => context.PodcastEpisodeAudio.Any(a => a.NewsArticleId == e.NewsArticleId && states.Contains(a.State)));
        }

        return feedIds.Count > 0
            ? from episode in episodes
              join feed in feeds on episode.NewsFeedId equals feed.NewsFeedId
              join article in stream on episode.NewsArticleId equals article.NewsArticleId
              select new NewsArticleKey { Id = episode.NewsArticleId, SortAtUtc = episode.SortAtUtc }
            : from article in stream
              join episode in episodes on article.NewsArticleId equals episode.NewsArticleId
              join feed in feeds on article.NewsFeedId equals feed.NewsFeedId
              select new NewsArticleKey { Id = article.NewsArticleId, SortAtUtc = article.SortAtUtc };
    }

    // ── Summaries and the detail ──────────────────────────────────────────────────────────────────────

    private async Task<List<PodcastEpisodeSummary>> SummariesAsync(IReadOnlyList<Guid> ids, CancellationToken cancellationToken) =>
        [.. (await RowsAsync(ids, cancellationToken)).Select(Summary)];

    private sealed record Row(
        PodcastEpisode Episode, PodcastEpisodeAudio? Audio, string Title, string Excerpt, DateTime? PublishedAtUtc,
        Guid FeedId, string? CustomTitle, string? FetchedTitle, string? SiteUrl, bool FeedArchived,
        bool AutoDeleteEnabled, int AutoDeleteAfterDays, DateTime? AutoDeleteChangedAtUtc, Guid? ArtworkId, bool HasChapters, List<Guid> TagIds);

    private async Task<List<Row>> RowsAsync(IReadOnlyList<Guid> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        var rows = await (from episode in context.PodcastEpisodes.AsNoTracking()
                          join article in context.NewsArticles.AsNoTracking() on episode.NewsArticleId equals article.NewsArticleId
                          join feed in context.NewsFeeds.AsNoTracking() on episode.NewsFeedId equals feed.NewsFeedId
                          join state in context.NewsFeedFetchStates.AsNoTracking() on feed.NewsFeedId equals state.NewsFeedId into states
                          from state in states.DefaultIfEmpty()
                          join audio in context.PodcastEpisodeAudio.AsNoTracking() on episode.NewsArticleId equals audio.NewsArticleId into audios
                          from audio in audios.DefaultIfEmpty()
                          join details in context.PodcastFeedDetails.AsNoTracking() on feed.NewsFeedId equals details.NewsFeedId into allDetails
                          from details in allDetails.DefaultIfEmpty()
                          where ids.Contains(episode.NewsArticleId)
                          select new
                          {
                              Episode = episode,
                              Audio = audio,
                              article.Title,
                              article.Excerpt,
                              article.PublishedAtUtc,
                              feed.NewsFeedId,
                              feed.CustomTitle,
                              FetchedTitle = state == null ? null : state.Title,
                              SiteUrl = state == null ? null : state.SiteUrl,
                              Archived = feed.ArchivedAtUtc != null,
                              feed.AutoDeleteEnabled,
                              feed.AutoDeleteAfterDays,
                              feed.AutoDeleteChangedAtUtc,
                              ShowArtworkId = details == null ? null : details.ArtworkId,
                          })
            .News().ToListAsync(cancellationToken);

        var artworkIds = rows.SelectMany(r => new[] { r.Episode.ArtworkId, r.ShowArtworkId }).OfType<Guid>().Distinct().ToList();
        var storedArtwork = (await context.PodcastArtwork.AsNoTracking().News()
            .Where(a => artworkIds.Contains(a.PodcastArtworkId) && a.State == PodcastArtworkState.Stored)
            .Select(a => a.PodcastArtworkId).ToListAsync(cancellationToken)).ToHashSet();
        var withChapters = (await context.PodcastChapters.AsNoTracking().News()
            .Where(c => ids.Contains(c.NewsArticleId)).Select(c => c.NewsArticleId).Distinct().ToListAsync(cancellationToken)).ToHashSet();
        var tags = (await context.NewsArticleTags.AsNoTracking().News().Where(l => ids.Contains(l.NewsArticleId))
                .Select(l => new { l.NewsArticleId, l.NewsTagId }).ToListAsync(cancellationToken))
            .ToLookup(l => l.NewsArticleId, l => l.NewsTagId);

        var byId = rows.ToDictionary(r => r.Episode.NewsArticleId);
        var result = new List<Row>(ids.Count);
        foreach (var id in ids)
        {
            if (!byId.TryGetValue(id, out var r))
            {
                continue;
            }

            Guid? artwork = r.Episode.ArtworkId is { } own && storedArtwork.Contains(own) ? own
                : r.ShowArtworkId is { } show && storedArtwork.Contains(show) ? show : null;
            result.Add(new Row(
                r.Episode, r.Audio, r.Title, r.Excerpt, r.PublishedAtUtc, r.NewsFeedId, r.CustomTitle, r.FetchedTitle, r.SiteUrl, r.Archived,
                r.AutoDeleteEnabled, r.AutoDeleteAfterDays, r.AutoDeleteChangedAtUtc, artwork, withChapters.Contains(id), [.. tags[id].Order()]));
        }

        return result;
    }

    private PodcastEpisodeSummary Summary(Row row) => Fill(new PodcastEpisodeSummary(), row);

    private T Fill<T>(T summary, Row row) where T : PodcastEpisodeSummary
    {
        var e = row.Episode;
        summary.Id = e.NewsArticleId;
        summary.FeedId = row.FeedId;
        summary.FeedTitle = NewsFeedProjection.DisplayTitle(row.CustomTitle, row.FetchedTitle, row.SiteUrl);
        summary.Title = row.Title;
        summary.PublishedAtUtc = row.PublishedAtUtc;
        summary.SortAtUtc = e.SortAtUtc;
        summary.Excerpt = row.Excerpt;
        summary.DurationSeconds = e.DurationSeconds;
        summary.SeasonNumber = e.SeasonNumber;
        summary.EpisodeNumber = e.EpisodeNumber;
        summary.EpisodeType = e.EpisodeType;
        summary.Explicit = e.Explicit;
        summary.ArtworkId = row.ArtworkId;
        summary.HasChapters = row.HasChapters;
        summary.EnclosureDeclaredLength = e.EnclosureDeclaredLength;
        summary.TagIds = row.TagIds;
        summary.Audio = AudioInfo(row.Audio);
        summary.LastPlayedAtUtc = e.LastPlayedAtUtc;
        summary.PlaybackPositionSeconds = e.PlaybackPositionSeconds;
        summary.AutoDeleteAtUtc = AutoDeleteAt(row);
        return summary;
    }

    /// <summary>The §3.7 rule, as a date: only while it applies to this row.</summary>
    private static DateTime? AutoDeleteAt(Row row)
    {
        if (row.Audio is not { State: PodcastAudioState.Stored, StoredAtUtc: { } storedAt } || !row.AutoDeleteEnabled || row.FeedArchived
            || row.Episode.LastPlayedAtUtc is not { } played || played < storedAt)
        {
            return null;
        }

        var from = row.AutoDeleteChangedAtUtc is { } changed && changed > played ? changed : played;
        return from.AddDays(row.AutoDeleteAfterDays);
    }

    private PodcastEpisodeAudioInfo AudioInfo(PodcastEpisodeAudio? audio)
    {
        if (audio is null)
        {
            return new PodcastEpisodeAudioInfo { State = PodcastAudioState.None };
        }

        var available = audio.LastUserRequestAtUtc is { } last ? last + PodcastLimits.UserRequeueCooldown : (DateTime?)null;
        return new PodcastEpisodeAudioInfo
        {
            State = audio.State,
            FailureKind = audio.State == PodcastAudioState.Failed || audio.State == PodcastAudioState.Queued ? audio.FailureKind : null,
            DeferredReason = audio.State == PodcastAudioState.Deferred ? audio.DeferredReason : null,
            SizeBytes = audio.State == PodcastAudioState.Stored ? audio.SizeBytes : null,
            ContentType = audio.State == PodcastAudioState.Stored ? audio.ContentType : null,
            StoredAtUtc = audio.State == PodcastAudioState.Stored ? audio.StoredAtUtc : null,
            RequestedBy = audio.RequestedBy,
            RequestAvailableAtUtc = available > Now ? available : null,
        };
    }

    /// <summary>The detail (§5.2 #7); <c>null</c> for an article with no episode or a feed that is not a podcast.</summary>
    public async Task<PodcastEpisodeDetail?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!await VisibleAsync(id, cancellationToken))
        {
            return null;
        }

        var row = (await NewsQueryGuard.RunAsync(() => RowsAsync([id], cancellationToken))).SingleOrDefault();
        if (row is null || await articles.GetAsync(id, cancellationToken) is not { } article)
        {
            return null;
        }

        var detail = Fill(new PodcastEpisodeDetail(), row);
        detail.Description = row.Episode.Description;
        detail.ShowNotes = article.Body;
        detail.ShowNotesUnavailable = article.BodyUnavailable;
        detail.ShowNotesOmittedReason = article.BodyOmittedReason;
        detail.Images = article.Images;
        var chapters = await NewsQueryGuard.RunAsync(() => context.PodcastChapters.AsNoTracking().News()
            .Where(c => c.NewsArticleId == id).ToListAsync(cancellationToken));
        if (chapters.Count > 0)
        {
            // Read precedence (§4.5): Feed > Json > AudioFile; the others are kept, not shown.
            var source = chapters.Min(c => c.Source);
            detail.ChaptersSource = source;
            detail.Chapters = [.. chapters.Where(c => c.Source == source).OrderBy(c => c.Position)
                .Select(c => new PodcastEpisodeChapterItem { StartMs = c.StartMs, EndMs = c.EndMs, Title = c.Title, Url = c.Url })];
        }

        return detail;
    }

    private Task<bool> VisibleAsync(Guid id, CancellationToken cancellationToken) =>
        NewsQueryGuard.RunAsync(() => (from episode in context.PodcastEpisodes.AsNoTracking()
                                       join feed in context.NewsFeeds.AsNoTracking() on episode.NewsFeedId equals feed.NewsFeedId
                                       where episode.NewsArticleId == id && feed.Kind == NewsFeedKind.Podcast && feed.DeletionRequestedAtUtc == null
                                       select episode.NewsArticleId).News().AnyAsync(cancellationToken));

    // ── Audio content ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The audio to serve (§5.3 #8, steps 1–2): <c>null</c> unless it is <c>Stored</c>, the feed is a podcast and not
    /// pending deletion (an archived feed's audio is still served). Reads the database only.
    /// </summary>
    public Task<PodcastFileDescriptor?> AudioAsync(Guid id, CancellationToken cancellationToken) =>
        NewsQueryGuard.RunAsync(async () =>
        {
            var row = await (from audio in context.PodcastEpisodeAudio.AsNoTracking()
                             join episode in context.PodcastEpisodes.AsNoTracking() on audio.NewsArticleId equals episode.NewsArticleId
                             join article in context.NewsArticles.AsNoTracking() on audio.NewsArticleId equals article.NewsArticleId
                             join feed in context.NewsFeeds.AsNoTracking() on audio.NewsFeedId equals feed.NewsFeedId
                             join state in context.NewsFeedFetchStates.AsNoTracking() on feed.NewsFeedId equals state.NewsFeedId into states
                             from state in states.DefaultIfEmpty()
                             where audio.NewsArticleId == id && audio.State == PodcastAudioState.Stored && audio.StoredObjectId != null
                                 && feed.Kind == NewsFeedKind.Podcast && feed.DeletionRequestedAtUtc == null
                             select new
                             {
                                 audio.StoredObjectId,
                                 audio.ContentType,
                                 audio.Sha256,
                                 audio.SizeBytes,
                                 article.Title,
                                 feed.CustomTitle,
                                 FetchedTitle = state == null ? null : state.Title,
                                 SiteUrl = state == null ? null : state.SiteUrl,
                             })
                .News().FirstOrDefaultAsync(cancellationToken);
            if (row is not { StoredObjectId: { } objectId, ContentType: { } type, Sha256: { } hash })
            {
                return null;
            }

            var extension = ContentTypeExtensions.For(type) ?? ".bin";
            return new PodcastFileDescriptor(
                objectId, PodcastAudioStorage.FeatureKey, type, Convert.ToHexStringLower(hash), row.SizeBytes ?? 0,
                DownloadName(NewsFeedProjection.DisplayTitle(row.CustomTitle, row.FetchedTitle, row.SiteUrl), row.Title, extension));
        });

    /// <summary>
    /// <c>&lt;feed title&gt; - &lt;episode title&gt;.&lt;ext&gt;</c>, each part through the store's strictest sanitizer
    /// (CR, LF, quotes, <c>;</c> and control characters cannot survive), at most 200 characters, the extension from the
    /// stored type.
    /// </summary>
    public static string DownloadName(string feedTitle, string episodeTitle, string extension)
    {
        // The sanitizer drops a trailing ".ext", so each part carries a throwaway one.
        var feed = StoredFileNames.SanitizeStem(feedTitle + ".x", null, extension);
        var episode = StoredFileNames.SanitizeStem(episodeTitle + ".x", null, extension);
        var stem = $"{feed} - {episode}";
        const int MaxLength = 200;
        if (stem.Length + extension.Length > MaxLength)
        {
            stem = stem[..(MaxLength - extension.Length)].TrimEnd(' ', '-', '.');
        }

        return stem + extension;
    }

    public Task<Stream?> OpenAsync(Guid id, PodcastFileDescriptor descriptor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return OpenFileAsync(descriptor, $"podcast file of {id}", cancellationToken);
    }

    private async Task<Stream?> OpenFileAsync(PodcastFileDescriptor descriptor, string what, CancellationToken cancellationToken)
    {
        EnsureStorage(descriptor.FeatureKey);
        try
        {
            return (await store.OpenReadAsync(descriptor.FeatureKey, descriptor.StoredObjectId, cancellationToken)).Content;
        }
        catch (FileStorageUnavailableException)
        {
            throw NewsErrors.StorageUnavailable();
        }
        catch (Exception ex) when (ex is FileStorageIntegrityException or DomainNotFoundException)
        {
            logger.LogError("The {What} could not be served: its file is missing or failed its check.", what);
            return null;
        }
    }

    /// <summary>
    /// The last-played stamp (§5.3 #8): one conditional single-column update, made by the controller only after one
    /// response delivered the whole representation to a <c>news.update</c> holder.
    /// </summary>
    public async Task StampPlayedAsync(Guid id, CancellationToken cancellationToken)
    {
        var now = Now;
        if (context.Database.IsRelational())
        {
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE `PodcastEpisodes` SET `LastPlayedAtUtc` = {now} WHERE `NewsArticleId` = {id}", cancellationToken);
            return;
        }

        if (await context.PodcastEpisodes.FirstOrDefaultAsync(e => e.NewsArticleId == id, cancellationToken) is { } episode)
        {
            episode.LastPlayedAtUtc = now;
            await context.SaveChangesAsync(cancellationToken);
        }
    }

    // ── Download and remove ───────────────────────────────────────────────────────────────────────────

    /// <summary><c>POST …/audio</c> (§5.3 #9). <c>null</c> for an unknown episode.</summary>
    public async Task<PodcastAudioRequestOutcome?> RequestAsync(Guid id, string userId, CancellationToken cancellationToken)
    {
        var row = await NewsQueryGuard.RunAsync(() => (from audio in context.PodcastEpisodeAudio.AsNoTracking()
                                                       join feed in context.NewsFeeds.AsNoTracking() on audio.NewsFeedId equals feed.NewsFeedId
                                                       where audio.NewsArticleId == id && feed.Kind == NewsFeedKind.Podcast && feed.DeletionRequestedAtUtc == null
                                                       select new { Audio = audio, feed.NewsFeedId, feed.ArchivedAtUtc }).News().FirstOrDefaultAsync(cancellationToken));
        if (row is null)
        {
            return null;
        }

        switch (row.Audio.State)
        {
            case PodcastAudioState.Stored:
                return new PodcastAudioRequestOutcome(true, AudioInfo(row.Audio));
            case PodcastAudioState.Queued or PodcastAudioState.Downloading or PodcastAudioState.Deferred:
                return new PodcastAudioRequestOutcome(false, AudioInfo(row.Audio));
        }

        if (row.ArchivedAtUtc is not null)
        {
            throw NewsErrors.FeedInactive(row.NewsFeedId);
        }

        var now = Now;
        if (row.Audio.LastUserRequestAtUtc is { } last && last + PodcastLimits.UserRequeueCooldown > now)
        {
            var wait = (int)Math.Ceiling((last + PodcastLimits.UserRequeueCooldown - now).TotalSeconds);
            throw NewsErrors.PodcastAudioCooldown(id, Math.Max(1, wait));
        }

        EnsureStorage(PodcastAudioStorage.FeatureKey);
        var quota = (await settings.GetAsync(cancellationToken)).PodcastAudioQuotaBytes;
        var totals = await NewsQueryGuard.RunAsync(() => context.NewsStorageTotals.AsNoTracking().News()
            .Where(t => t.Id == NewsStorageTotal.SingletonId)
            .Select(t => new { t.AudioBytes, t.AudioPendingReleaseBytes }).FirstOrDefaultAsync(cancellationToken));
        if (totals is not null && totals.AudioBytes + totals.AudioPendingReleaseBytes >= quota)
        {
            throw NewsErrors.PodcastAudioQuotaReached();
        }

        if (await NewsQueryGuard.RunAsync(() => requests.RequestAsync(context, id, now, cancellationToken)) == 1)
        {
            logger.LogInformation("Podcast audio {EpisodeId} was requested by user {UserId}.", id, userId);
        }

        context.ChangeTracker.Clear();
        var current = await NewsQueryGuard.RunAsync(() => context.PodcastEpisodeAudio.AsNoTracking().News().FirstAsync(a => a.NewsArticleId == id, cancellationToken));
        return current.State == PodcastAudioState.None || current.State == PodcastAudioState.Failed
            ? throw NewsErrors.PodcastAudioCooldown(id, (int)PodcastLimits.UserRequeueCooldown.TotalSeconds)
            : new PodcastAudioRequestOutcome(current.State == PodcastAudioState.Stored, AudioInfo(current));
    }

    /// <summary>
    /// <c>DELETE …/audio</c> (§5.3 #10): stored audio released with the grace, or a pending request cancelled; the
    /// cooldown stamped. <c>false</c> for an unknown episode; <c>409</c> while it downloads.
    /// </summary>
    public async Task<bool> RemoveAsync(Guid id, string userId, CancellationToken cancellationToken)
    {
        var visible = await VisibleAsync(id, cancellationToken);
        var audio = visible
            ? await NewsQueryGuard.RunAsync(() => context.PodcastEpisodeAudio.News().FirstOrDefaultAsync(a => a.NewsArticleId == id, cancellationToken))
            : null;
        if (audio is null)
        {
            return visible;
        }

        var now = Now;
        switch (audio.State)
        {
            case PodcastAudioState.None:
                return true;
            case PodcastAudioState.Downloading:
                throw NewsErrors.PodcastAudioBusy(id);
            case PodcastAudioState.Stored:
                EnsureStorage(PodcastAudioStorage.FeatureKey);
                try
                {
                    await NewsQueryGuard.RunAsync(() => NewsTransactions.RunAsync(context, async () =>
                    {
                        var released = await PodcastAudioRelease.StageAsync(store, audio, cancellationToken);
                        audio.LastUserRequestAtUtc = now;
                        await context.SaveChangesAsync(cancellationToken);
                        await counters.AddToPodcastStorageAsync(context, PodcastStorageDelta.AudioReleased(released), cancellationToken);
                    }, cancellationToken));
                }
                catch (DbUpdateConcurrencyException)
                {
                    // A download claimed it a moment ago.
                    throw NewsErrors.PodcastAudioBusy(id);
                }

                logger.LogInformation("Podcast audio {EpisodeId} was removed by user {UserId}; its file is removed after the deletion grace.", id, userId);
                return true;
            default:
                await NewsQueryGuard.RunAsync(() => requests.CancelAsync(context, id, now, cancellationToken));
                logger.LogInformation("The podcast audio request for {EpisodeId} was cancelled by user {UserId}.", id, userId);
                return true;
        }
    }

    // ── Artwork ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The artwork rendition to serve (§5.4 #11): <c>null</c> unless stored and its feed a live podcast feed.</summary>
    public Task<PodcastFileDescriptor?> ArtworkAsync(Guid artworkId, PodcastArtworkSize size, CancellationToken cancellationToken) =>
        NewsQueryGuard.RunAsync(async () =>
        {
            var kind = size switch
            {
                PodcastArtworkSize.Small => PodcastArtworkFileKind.Small,
                PodcastArtworkSize.Original => PodcastArtworkFileKind.Original,
                _ => PodcastArtworkFileKind.Medium,
            };
            var row = await (from file in context.PodcastArtworkFiles.AsNoTracking()
                             join artwork in context.PodcastArtwork.AsNoTracking() on file.PodcastArtworkId equals artwork.PodcastArtworkId
                             join feed in context.NewsFeeds.AsNoTracking() on artwork.NewsFeedId equals feed.NewsFeedId
                             where artwork.PodcastArtworkId == artworkId && file.Kind == kind && artwork.State == PodcastArtworkState.Stored
                                 && feed.Kind == NewsFeedKind.Podcast && feed.DeletionRequestedAtUtc == null
                             select new { file.StoredObjectId, file.ContentType, file.Sha256, file.SizeBytes })
                .News().FirstOrDefaultAsync(cancellationToken);
            return row is null
                ? null
                : new PodcastFileDescriptor(row.StoredObjectId, PodcastArtworkStorage.FeatureKey, row.ContentType, Convert.ToHexStringLower(row.Sha256), row.SizeBytes, null);
        });

    public Task<Stream?> OpenArtworkAsync(Guid artworkId, PodcastFileDescriptor descriptor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return OpenFileAsync(descriptor, $"podcast artwork {artworkId}", cancellationToken);
    }

    /// <summary>No binding, an inactive store or a degraded bound volume: a file endpoint is <c>503</c>.</summary>
    private void EnsureStorage(string featureKey)
    {
        if (!volumes.IsActive
            || !volumes.Bindings.TryGetValue(featureKey, out var name)
            || volumes.Find(name) is not { } volume
            || volumes.RecheckIfDue(volume))
        {
            throw NewsErrors.StorageUnavailable();
        }
    }
}

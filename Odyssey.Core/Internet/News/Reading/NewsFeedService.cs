using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using Odyssey.Context;
using Odyssey.Core.Pagination;
using Odyssey.Dtos;
using Odyssey.Dtos.Internet;

namespace Odyssey.Core.Internet.News.Reading;

/// <summary>
/// The feed endpoints (issue #470 §5.1). The API writes only the columns §3.3 gives it: the <c>NewsFeeds</c> settings
/// under <see cref="NewsFeed.SettingsVersion"/>, and the four cross-boundary fetch-state columns through
/// <see cref="INewsFetchStateFlags"/>, which never checks the worker's fence — so no API write can fail a running fetch,
/// and none fails because of one. Reads never write. A feed pending deletion is a 404 everywhere.
/// </summary>
public sealed class NewsFeedService(
    OdysseyContext context,
    INewsSettingsLookup settings,
    INewsFetchStateFlags flags,
    Podcasts.IPodcastAudioRequests audioRequests,
    TimeProvider time,
    ILogger<NewsFeedService> logger)
{
    /// <summary>The escape character of <see cref="ListQuery.EscapeLike"/>, named so EF InMemory honours it too.</summary>
    private const string LikeEscape = "\\";

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    private DateOnly Today => DateOnly.FromDateTime(Now);

    // ── Reads ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The feed list. The set is bounded by <c>NewsMaxFeeds</c> (at most 1,000), so it is filtered, sorted and paged
    /// after projection: that is what lets the <c>health</c> filter match the DERIVED value (§4.2) exactly.
    /// </summary>
    public Task<PagedResult<ExistingNewsFeed>> ListAsync(NewsFeedsQueryParams query, bool canManage, CancellationToken cancellationToken) =>
        NewsQueryGuard.RunAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(query);
            var feeds = context.NewsFeeds.AsNoTracking().News().Where(f => f.DeletionRequestedAtUtc == null);
            feeds = (query.State ?? NewsFeedState.Active) switch
            {
                NewsFeedState.Active => feeds.Where(f => f.ArchivedAtUtc == null && f.IsActive),
                NewsFeedState.Paused => feeds.Where(f => f.ArchivedAtUtc == null && !f.IsActive),
                NewsFeedState.Archived => feeds.Where(f => f.ArchivedAtUtc != null),
                _ => feeds,
            };

            if (query.TagId is { } tagId)
            {
                feeds = feeds.Where(f => context.NewsFeedTags.Any(l => l.NewsFeedId == f.NewsFeedId && l.NewsTagId == tagId));
            }

            feeds = ByKind(feeds, query.Kind);

            if (ListQuery.NormalizeSearch(query.Search) is { } term)
            {
                var pattern = ListQuery.ContainsPattern(term);
                feeds = feeds.Where(f => (f.CustomTitle != null && EF.Functions.Like(f.CustomTitle, pattern, LikeEscape))
                    || context.NewsFeedFetchStates.Any(s => s.NewsFeedId == f.NewsFeedId
                        && ((s.Title != null && EF.Functions.Like(s.Title, pattern, LikeEscape)) || (s.SiteUrl != null && EF.Functions.Like(s.SiteUrl, pattern, LikeEscape)))));
            }

            var rows = await LoadRowsAsync(feeds, cancellationToken);
            if (query.Health is { Length: > 0 } health)
            {
                var wanted = health.ToHashSet();
                rows = [.. rows.Where(r => wanted.Contains(r.Health))];
            }

            var defaults = await settings.GetAsync(cancellationToken);
            var today = Today;
            var projected = rows.Select(r => NewsFeedProjection.ToDto(r, defaults.DefaultFetchIntervalMinutes, canManage, today)).ToList();
            var sorted = Sort(projected, query.SortBy ?? NewsFeedSortBy.Title, query.SortDir).ToList();
            var (offset, limit) = ListQuery.ResolveWindow(query.Offset, Math.Min(query.Limit, NewsLimits.MaxPageSize));
            return ListQuery.ToPagedResult(sorted, offset, limit);
        });

    /// <summary>Every feed not pending deletion, unpaged, with no URL (§5.1 item 2), optionally of one kind (#556 §5.1 #4).</summary>
    public Task<List<NewsFeedCatalogueEntry>> CatalogueAsync(CancellationToken cancellationToken, NewsFeedKindFilter? kind = null) =>
        NewsQueryGuard.RunAsync(async () =>
        {
            var rows = await LoadRowsAsync(
                ByKind(context.NewsFeeds.AsNoTracking().News().Where(f => f.DeletionRequestedAtUtc == null), kind), cancellationToken);
            // Read articles changed since they were read: no maintained counter, so one grouped count per request.
            var updated = await context.NewsArticles.AsNoTracking()
                .Where(NewsReadStatusFilter.IsUpdatedSinceRead)
                .GroupBy(a => a.NewsFeedId)
                .Select(g => new { FeedId = g.Key, Count = g.Count() })
                .News().ToDictionaryAsync(g => g.FeedId, g => g.Count, cancellationToken);
            return rows.Select(r => NewsFeedProjection.ToCatalogueEntry(r) with { UpdatedCount = updated.GetValueOrDefault(r.Feed.NewsFeedId) })
                .OrderBy(e => e.DisplayTitle, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(e => e.Id)
                .ToList();
        });

    public Task<ExistingNewsFeed?> GetAsync(Guid id, bool canManage, CancellationToken cancellationToken) =>
        NewsQueryGuard.RunAsync(async () =>
        {
            var rows = await LoadRowsAsync(
                context.NewsFeeds.AsNoTracking().News().Where(f => f.NewsFeedId == id && f.DeletionRequestedAtUtc == null), cancellationToken);
            if (rows.Count == 0)
            {
                return null;
            }

            var defaults = await settings.GetAsync(cancellationToken);
            return (ExistingNewsFeed?)NewsFeedProjection.ToDto(rows[0], defaults.DefaultFetchIntervalMinutes, canManage, Today);
        });

    /// <summary>
    /// Unread counts from the maintained counters (§5.1 item 4). Archived feeds and feeds pending deletion are excluded.
    /// <c>byTag</c> counts EFFECTIVE tags: a feed's tag counts every unread article of that feed, and an article's own
    /// tag adds only the unread articles whose feed does not already carry it, so no article is counted twice per tag.
    /// </summary>
    public Task<NewsUnreadCounts> UnreadCountsAsync(CancellationToken cancellationToken) =>
        NewsQueryGuard.RunAsync(async () =>
        {
            // Podcast feeds stay out of the article stream's badges (issue #556 §5.1 #5).
            var visible = context.NewsFeeds.AsNoTracking()
                .Where(f => f.DeletionRequestedAtUtc == null && f.ArchivedAtUtc == null && f.Kind == NewsFeedKind.Article);
            var byFeed = await (from feed in visible
                                join counter in context.NewsFeedCounters.AsNoTracking() on feed.NewsFeedId equals counter.NewsFeedId
                                select new { feed.NewsFeedId, counter.UnreadCount })
                .News().ToListAsync(cancellationToken);
            var unread = byFeed.ToDictionary(f => f.NewsFeedId, f => Math.Max(0, f.UnreadCount));

            var feedTags = await (from link in context.NewsFeedTags.AsNoTracking()
                                  join feed in visible on link.NewsFeedId equals feed.NewsFeedId
                                  select new { link.NewsTagId, link.NewsFeedId })
                .News().ToListAsync(cancellationToken);
            var ownTags = await (from link in context.NewsArticleTags.AsNoTracking()
                                 join article in context.NewsArticles.AsNoTracking() on link.NewsArticleId equals article.NewsArticleId
                                 join feed in visible on article.NewsFeedId equals feed.NewsFeedId
                                 where !article.IsRead
                                     && !context.NewsFeedTags.Any(f => f.NewsFeedId == article.NewsFeedId && f.NewsTagId == link.NewsTagId)
                                 group link by link.NewsTagId into g
                                 select new { TagId = g.Key, Count = g.Count() })
                .News().ToListAsync(cancellationToken);

            var byTag = new Dictionary<Guid, int>();
            foreach (var link in feedTags)
            {
                byTag[link.NewsTagId] = byTag.GetValueOrDefault(link.NewsTagId) + unread.GetValueOrDefault(link.NewsFeedId);
            }

            foreach (var own in ownTags)
            {
                byTag[own.TagId] = byTag.GetValueOrDefault(own.TagId) + own.Count;
            }

            return new NewsUnreadCounts
            {
                Total = unread.Values.Sum(),
                ByFeed = [.. unread.OrderBy(f => f.Key).Select(f => new NewsFeedUnreadCount { FeedId = f.Key, UnreadCount = f.Value })],
                ByTag = [.. byTag.OrderBy(t => t.Key).Select(t => new NewsTagUnreadCount { TagId = t.Key, UnreadCount = t.Value })],
            };
        });

    // ── Writes ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>POST</c> (§5.1 item 5): the feed, its fetch state (a new fence, due at once) and its counters in ONE save, so a
    /// feed never exists without the rows the worker and the relative counter updates need (AC 78). No outbound request.
    /// </summary>
    public async Task<ExistingNewsFeed> CreateAsync(NewNewsFeed request, bool canManage, string userId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var kind = ValidateKind(request.Kind, request.AutoDownload, request.AutoDeleteEnabled);
        var address = NewsFeedUrlRules.Validate(request.Url, await settings.GetAllowListAsync(cancellationToken));
        var resolved = await settings.GetAsync(cancellationToken);
        var tagIds = NewsFilterIds.Normalize(request.TagIds);

        var feedId = await NewsQueryGuard.RunAsync(async () =>
        {
            await EnsureUniqueAsync(address.UrlKeyHash, excludeFeedId: null, cancellationToken);
            var count = await context.NewsFeeds.News().CountAsync(f => f.DeletionRequestedAtUtc == null, cancellationToken);
            if (count >= resolved.MaxFeeds)
            {
                throw NewsErrors.FeedLimit(resolved.MaxFeeds);
            }

            await EnsureTagsUsableAsync(tagIds, alreadyLinked: new HashSet<Guid>(), cancellationToken);

            var now = Now;
            var feed = new NewsFeed
            {
                NewsFeedId = Guid.NewGuid(),
                Url = address.Url,
                UrlKeyHash = address.UrlKeyHash,
                CustomTitle = Clean(request.CustomTitle),
                FetchIntervalMinutes = request.FetchIntervalMinutes,
                DownloadImages = request.DownloadImages,
                IsActive = true,
                CreatedAtUtc = now,
                SettingsVersion = Guid.NewGuid(),
                Kind = kind,
                AutoDownload = request.AutoDownload,
                AutoDeleteEnabled = request.AutoDeleteEnabled,
                AutoDeleteAfterDays = request.AutoDeleteAfterDays,
                AutoDeleteChangedAtUtc = request.AutoDeleteEnabled ? now : null,
            };
            context.NewsFeeds.Add(feed);
            context.NewsFeedFetchStates.Add(new NewsFeedFetchState
            {
                NewsFeedId = feed.NewsFeedId,
                FetchFence = Guid.NewGuid(),
                NextFetchAtUtc = now,
            });
            context.NewsFeedCounters.Add(new NewsFeedCounter { NewsFeedId = feed.NewsFeedId });
            context.NewsFeedTags.AddRange(tagIds.Select(t => new NewsFeedTag { NewsFeedTagId = Guid.NewGuid(), NewsFeedId = feed.NewsFeedId, NewsTagId = t }));
            await SaveUniqueAsync(address.UrlKeyHash, cancellationToken);
            return feed.NewsFeedId;
        });

        logger.LogInformation("News feed {FeedId} created by {ActorUserId} for host {Host}.", feedId, userId, address.Host);
        return (await GetAsync(feedId, canManage, cancellationToken))!;
    }

    /// <summary>
    /// <c>PUT</c> (§5.1 item 6): full replacement of the API-owned settings, not an upsert. A URL change runs the URL
    /// rules and the uniqueness check, sets only <c>ValidatorsResetRequestedAtUtc</c> (it never reschedules), and clears a
    /// pending suggestion — accepting it when the new URL equals it, overriding it otherwise. An unchanged URL leaves
    /// the suggestion pending.
    /// </summary>
    public async Task<ExistingNewsFeed?> UpdateAsync(Guid id, UpdateNewsFeed request, bool canManage, string userId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var kind = ValidateKind(request.Kind!.Value, request.AutoDownload!.Value, request.AutoDeleteEnabled!.Value);
        var outcome = await NewsQueryGuard.RunAsync(async () =>
        {
            var feed = await TrackedFeedAsync(id, cancellationToken);
            if (feed is null)
            {
                return (Found: false, UrlChanged: false, Accepted: false, Host: (string?)null, KindChange: (NewsFeedKind?)null);
            }

            if (request.Version != feed.SettingsVersion)
            {
                throw NewsErrors.FeedChanged(id);
            }

            var suggestion = await context.NewsFeedFetchStates.AsNoTracking().News()
                .Where(s => s.NewsFeedId == id).Select(s => s.SuggestedUrl).FirstOrDefaultAsync(cancellationToken);
            var requestedUrl = request.Url?.Trim() ?? string.Empty;
            var urlChanged = !string.Equals(requestedUrl, feed.Url, StringComparison.Ordinal);
            string? host = null;
            if (urlChanged)
            {
                var address = NewsFeedUrlRules.Validate(requestedUrl, await settings.GetAllowListAsync(cancellationToken));
                if (!address.UrlKeyHash.AsSpan().SequenceEqual(feed.UrlKeyHash))
                {
                    await EnsureUniqueAsync(address.UrlKeyHash, excludeFeedId: id, cancellationToken);
                }

                feed.Url = address.Url;
                feed.UrlKeyHash = address.UrlKeyHash;
                host = address.Host;
            }

            feed.CustomTitle = Clean(request.CustomTitle);
            feed.FetchIntervalMinutes = request.FetchIntervalMinutes;
            feed.DownloadImages = request.DownloadImages!.Value;
            feed.IsActive = request.IsActive!.Value;
            feed.SettingsVersion = Guid.NewGuid();
            var now = Now;
            var kindChanged = feed.Kind != kind;
            if (kindChanged)
            {
                // §5.1 #2: a type change re-parses the next fetch fully, and a podcast baseline older than it stops
                // counting, so switching never downloads a back catalogue.
                feed.Kind = kind;
                feed.KindChangedAtUtc = now;
            }

            var autoDeleteDays = request.AutoDeleteAfterDays!.Value;
            if (request.AutoDeleteEnabled!.Value && (!feed.AutoDeleteEnabled || feed.AutoDeleteAfterDays != autoDeleteDays))
            {
                // §3.7: enabling the rule or changing its days starts every countdown at the change, never earlier.
                feed.AutoDeleteChangedAtUtc = now;
            }

            feed.AutoDownload = request.AutoDownload!.Value;
            feed.AutoDeleteEnabled = request.AutoDeleteEnabled.Value;
            feed.AutoDeleteAfterDays = autoDeleteDays;
            if (request.TagIds is not null)
            {
                await StageTagsAsync(id, request.TagIds, cancellationToken);
            }

            var accepted = urlChanged && string.Equals(suggestion, feed.Url, StringComparison.Ordinal);
            await NewsTransactions.RunAsync(context, async () =>
            {
                await SaveVersionedAsync(id, urlChanged ? feed.UrlKeyHash : null, cancellationToken);
                if (urlChanged)
                {
                    if (suggestion is not null)
                    {
                        await flags.ClearSuggestionAsync(context, id, suggestion, cancellationToken);
                    }
                }

                if (urlChanged || kindChanged)
                {
                    await flags.RequestValidatorsResetAsync(context, id, Now, cancellationToken);
                }

                if (kindChanged && kind == NewsFeedKind.Article)
                {
                    // §5.1 #2: pending downloads stop now; stored audio is released by the purge loop's cleanup and a
                    // running download stops at its next lease renewal.
                    await audioRequests.CancelPendingForFeedAsync(context, id, cancellationToken);
                }
            }, cancellationToken);
            return (Found: true, UrlChanged: urlChanged, Accepted: accepted, Host: host, KindChange: kindChanged ? kind : (NewsFeedKind?)null);
        });

        if (!outcome.Found)
        {
            return null;
        }

        if (outcome.Accepted)
        {
            logger.LogInformation("News feed {FeedId}: suggested address accepted by {ActorUserId} (host {Host}).", id, userId, outcome.Host);
        }
        else if (outcome.UrlChanged)
        {
            logger.LogInformation("News feed {FeedId}: address changed by {ActorUserId} to host {Host}.", id, userId, outcome.Host);
        }
        else
        {
            logger.LogInformation("News feed {FeedId} updated by {ActorUserId}.", id, userId);
        }

        if (outcome.KindChange is { } changedTo)
        {
            logger.LogInformation("News feed {FeedId}: type changed to {Kind} by {ActorUserId}.", id, changedTo, userId);
        }

        context.ChangeTracker.Clear();
        return await GetAsync(id, canManage, cancellationToken);
    }

    /// <summary>
    /// <c>PUT …/tags</c> (§5.1 item 7): replaces the feed's tags. A tag already on the feed may stay even when archived;
    /// a new one must exist and not be archived (<c>422</c> keyed <c>tagIds</c>). Rotates the feed's version.
    /// </summary>
    public async Task<ExistingNewsFeed?> SetTagsAsync(Guid id, NewsFeedTagsUpdate request, bool canManage, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var found = await NewsQueryGuard.RunAsync(async () =>
        {
            var feed = await TrackedFeedAsync(id, cancellationToken);
            if (feed is null)
            {
                return false;
            }

            await StageTagsAsync(id, request.TagIds, cancellationToken);
            feed.SettingsVersion = Guid.NewGuid();
            await SaveVersionedAsync(id, null, cancellationToken);
            return true;
        });

        if (!found)
        {
            return null;
        }

        context.ChangeTracker.Clear();
        return await GetAsync(id, canManage, cancellationToken);
    }

    /// <summary>
    /// <c>POST …/refresh</c> (§5.1 item 8): the conditional refresh write (§3.3). A paused, archived or quarantined feed
    /// cannot be fetched, so it is <c>422 news-feed-inactive</c>. False for an unknown feed.
    /// </summary>
    public Task<bool> RefreshAsync(Guid id, CancellationToken cancellationToken) =>
        NewsQueryGuard.RunAsync(async () =>
        {
            var row = await (from feed in context.NewsFeeds.AsNoTracking().News()
                             where feed.NewsFeedId == id && feed.DeletionRequestedAtUtc == null
                             select new
                             {
                                 feed.IsActive,
                                 feed.ArchivedAtUtc,
                                 Quarantined = context.NewsFeedFetchStates.Any(s => s.NewsFeedId == id && s.QuarantinedAtUtc != null),
                             }).FirstOrDefaultAsync(cancellationToken);
            if (row is null)
            {
                return false;
            }

            if (!row.IsActive || row.ArchivedAtUtc is not null || row.Quarantined)
            {
                throw NewsErrors.FeedInactive(id);
            }

            await flags.RequestRefreshAsync(context, id, Now, cancellationToken);
            return true;
        });

    /// <summary><c>POST …/archive</c> and <c>…/unarchive</c> (§5.1 item 9). Idempotent; each rotates the version.</summary>
    public async Task<ExistingNewsFeed?> SetArchivedAsync(Guid id, bool archived, bool canManage, string userId, CancellationToken cancellationToken)
    {
        var found = await NewsQueryGuard.RunAsync(async () =>
        {
            var feed = await TrackedFeedAsync(id, cancellationToken);
            if (feed is null)
            {
                return false;
            }

            if (archived != feed.ArchivedAtUtc is not null)
            {
                feed.ArchivedAtUtc = archived ? Now : null;
                feed.SettingsVersion = Guid.NewGuid();
                await SaveVersionedAsync(id, null, cancellationToken);
            }

            return true;
        });

        if (!found)
        {
            return null;
        }

        logger.LogInformation("News feed {FeedId} {Action} by {ActorUserId}.", id, archived ? "archived" : "unarchived", userId);
        context.ChangeTracker.Clear();
        return await GetAsync(id, canManage, cancellationToken);
    }

    /// <summary>
    /// <c>DELETE</c> (§5.1 item 10): any feed, archived or not — there is no archive-first step (design system,
    /// NewsFeeds). Sets <c>DeletionRequestedAtUtc</c>, after which the feed is a 404 everywhere; the worker's batched purge
    /// removes the rest. A live feed is safe to delete mid-fetch: every fenced fetch write re-reads the deletion stamp and
    /// aborts, and the purge only claims a feed no live lease holds. False for an unknown feed.
    /// </summary>
    public async Task<bool> RequestDeleteAsync(Guid id, string userId, CancellationToken cancellationToken)
    {
        var found = await NewsQueryGuard.RunAsync(async () =>
        {
            var feed = await TrackedFeedAsync(id, cancellationToken);
            if (feed is null)
            {
                return false;
            }

            feed.DeletionRequestedAtUtc = Now;
            feed.SettingsVersion = Guid.NewGuid();
            await SaveVersionedAsync(id, null, cancellationToken);
            return true;
        });

        if (found)
        {
            logger.LogInformation("News feed {FeedId}: deletion requested by {ActorUserId}.", id, userId);
        }

        return found;
    }

    /// <summary>
    /// <c>POST …/release-quarantine</c> (§5.1 item 11): the conditional clear (§3.3). It writes no <c>NewsFeeds</c>
    /// column, so the version is unchanged. A feed that is not quarantined is <c>422</c>.
    /// </summary>
    public async Task<ExistingNewsFeed?> ReleaseQuarantineAsync(Guid id, bool canManage, string userId, CancellationToken cancellationToken)
    {
        var found = await NewsQueryGuard.RunAsync(async () =>
        {
            if (!await context.NewsFeeds.AsNoTracking().News().AnyAsync(f => f.NewsFeedId == id && f.DeletionRequestedAtUtc == null, cancellationToken))
            {
                return false;
            }

            if (!await flags.ReleaseQuarantineAsync(context, id, cancellationToken))
            {
                throw NewsErrors.FeedNotQuarantined(id);
            }

            return true;
        });

        if (!found)
        {
            return null;
        }

        logger.LogInformation("News feed {FeedId} released from quarantine by {ActorUserId}.", id, userId);
        context.ChangeTracker.Clear();
        return await GetAsync(id, canManage, cancellationToken);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The feed type and the automation rule (issue #556 §5.1): an unknown type is a <c>400</c>, and either automation
    /// switch on an article feed a <c>422</c> keyed by the switch.
    /// </summary>
    private static NewsFeedKind ValidateKind(NewsFeedKind kind, bool autoDownload, bool autoDeleteEnabled)
    {
        if (kind is not (NewsFeedKind.Article or NewsFeedKind.Podcast))
        {
            throw NewsErrors.FeedKindUnknown();
        }

        if (kind == NewsFeedKind.Article && autoDownload)
        {
            throw NewsErrors.AutomationNotPodcast("autoDownload");
        }

        if (kind == NewsFeedKind.Article && autoDeleteEnabled)
        {
            throw NewsErrors.AutomationNotPodcast("autoDeleteEnabled");
        }

        return kind;
    }

    private static IQueryable<NewsFeed> ByKind(IQueryable<NewsFeed> feeds, NewsFeedKindFilter? kind) => kind switch
    {
        NewsFeedKindFilter.Article => feeds.Where(f => f.Kind == NewsFeedKind.Article),
        NewsFeedKindFilter.Podcast => feeds.Where(f => f.Kind == NewsFeedKind.Podcast),
        _ => feeds,
    };

    private async Task<List<NewsFeedRow>> LoadRowsAsync(IQueryable<NewsFeed> feeds, CancellationToken cancellationToken)
    {
        var list = await feeds.ToListAsync(cancellationToken);
        if (list.Count == 0)
        {
            return [];
        }

        var ids = list.Select(f => f.NewsFeedId).ToList();
        var states = await context.NewsFeedFetchStates.AsNoTracking().News().Where(s => ids.Contains(s.NewsFeedId))
            .ToDictionaryAsync(s => s.NewsFeedId, cancellationToken);
        var counters = await context.NewsFeedCounters.AsNoTracking().News().Where(c => ids.Contains(c.NewsFeedId))
            .ToDictionaryAsync(c => c.NewsFeedId, cancellationToken);
        var tags = (await context.NewsFeedTags.AsNoTracking().News().Where(l => ids.Contains(l.NewsFeedId))
                .Select(l => new { l.NewsFeedId, l.NewsTagId }).ToListAsync(cancellationToken))
            .ToLookup(l => l.NewsFeedId, l => l.NewsTagId);
        var podcasts = await LoadPodcastFactsAsync([.. list.Where(f => f.Kind == NewsFeedKind.Podcast).Select(f => f.NewsFeedId)], cancellationToken);
        return [.. list.Select(f => new NewsFeedRow(
            f, states.GetValueOrDefault(f.NewsFeedId), counters.GetValueOrDefault(f.NewsFeedId), [.. tags[f.NewsFeedId].Order()],
            podcasts.GetValueOrDefault(f.NewsFeedId)))];
    }

    /// <summary>
    /// The podcast block's facts for the podcast feeds of one projection (issue #556 §5.1 #3): the details row, the show
    /// artwork when stored, three grouped counts on the per-feed audio index, and the episodes ever saved to a device
    /// (read on <c>IX_PodcastEpisodes_Feed_Sort</c>'s feed prefix).
    /// </summary>
    private async Task<Dictionary<Guid, NewsFeedPodcastFacts>> LoadPodcastFactsAsync(List<Guid> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        var details = await context.PodcastFeedDetails.AsNoTracking().News().Where(d => ids.Contains(d.NewsFeedId))
            .ToDictionaryAsync(d => d.NewsFeedId, cancellationToken);
        var artworkIds = details.Values.Where(d => d.ArtworkId != null).Select(d => d.ArtworkId!.Value).ToList();
        var storedArtwork = (await context.PodcastArtwork.AsNoTracking().News()
                .Where(a => artworkIds.Contains(a.PodcastArtworkId) && a.State == PodcastArtworkState.Stored)
                .Select(a => a.PodcastArtworkId).ToListAsync(cancellationToken))
            .ToHashSet();
        var episodes = await context.PodcastEpisodeAudio.AsNoTracking().News().Where(a => ids.Contains(a.NewsFeedId))
            .GroupBy(a => a.NewsFeedId)
            .Select(g => new
            {
                FeedId = g.Key,
                Episodes = g.Count(),
                Stored = g.Count(a => a.State == PodcastAudioState.Stored),
                Bytes = g.Where(a => a.State == PodcastAudioState.Stored).Sum(a => a.SizeBytes ?? 0),
            })
            .ToDictionaryAsync(g => g.FeedId, cancellationToken);
        // "Saved to a device" is LastPlayedAtUtc alone; a finished in-browser listen (LastListenedAtUtc, #593) is a
        // different signal and deliberately not counted here (docs/architecture/news-reader.md).
        var saved = await context.PodcastEpisodes.AsNoTracking().News()
            .Where(e => ids.Contains(e.NewsFeedId) && e.LastPlayedAtUtc != null)
            .GroupBy(e => e.NewsFeedId)
            .Select(g => new { FeedId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.FeedId, g => g.Count, cancellationToken);
        return ids.ToDictionary(id => id, id =>
        {
            var detail = details.GetValueOrDefault(id);
            var counts = episodes.GetValueOrDefault(id);
            return new NewsFeedPodcastFacts(
                detail,
                detail?.ArtworkId is { } artwork && storedArtwork.Contains(artwork) ? artwork : null,
                counts?.Episodes ?? 0,
                counts?.Stored ?? 0,
                counts?.Bytes ?? 0,
                saved.GetValueOrDefault(id));
        });
    }

    private static IEnumerable<ExistingNewsFeed> Sort(List<ExistingNewsFeed> feeds, NewsFeedSortBy sortBy, SortDirection? direction)
    {
        var ascending = ListQuery.Ascending(direction, naturalDefaultAscending: sortBy == NewsFeedSortBy.Title);
        IOrderedEnumerable<ExistingNewsFeed> ordered = sortBy switch
        {
            NewsFeedSortBy.UnreadCount => ascending ? feeds.OrderBy(f => f.UnreadCount) : feeds.OrderByDescending(f => f.UnreadCount),
            NewsFeedSortBy.LastSuccessAt => ascending ? feeds.OrderBy(f => f.LastSuccessAtUtc) : feeds.OrderByDescending(f => f.LastSuccessAtUtc),
            NewsFeedSortBy.CreatedAt => ascending ? feeds.OrderBy(f => f.CreatedAtUtc) : feeds.OrderByDescending(f => f.CreatedAtUtc),
            _ => ascending
                ? feeds.OrderBy(f => f.DisplayTitle, StringComparer.CurrentCultureIgnoreCase)
                : feeds.OrderByDescending(f => f.DisplayTitle, StringComparer.CurrentCultureIgnoreCase),
        };
        return ordered.ThenBy(f => f.Id);
    }

    private Task<NewsFeed?> TrackedFeedAsync(Guid id, CancellationToken cancellationToken) =>
        context.NewsFeeds.News().FirstOrDefaultAsync(f => f.NewsFeedId == id && f.DeletionRequestedAtUtc == null, cancellationToken);

    private async Task EnsureUniqueAsync(byte[] urlKeyHash, Guid? excludeFeedId, CancellationToken cancellationToken)
    {
        // Compared in memory: byte-array equality is not translated alike by both providers.
        var existing = await context.NewsFeeds.AsNoTracking().News()
            .Where(f => excludeFeedId == null || f.NewsFeedId != excludeFeedId)
            .Select(f => new { f.NewsFeedId, f.UrlKeyHash, f.DeletionRequestedAtUtc })
            .ToListAsync(cancellationToken);
        if (existing.FirstOrDefault(f => f.UrlKeyHash.AsSpan().SequenceEqual(urlKeyHash)) is { } duplicate)
        {
            throw NewsErrors.FeedDuplicate(duplicate.DeletionRequestedAtUtc is null ? duplicate.NewsFeedId : null);
        }
    }

    /// <summary>A save that may race another feed onto the same key: the unique index is the guard, answered as the pre-check is.</summary>
    private async Task SaveUniqueAsync(byte[] urlKeyHash, CancellationToken cancellationToken)
    {
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.GetBaseException() is MySqlException { ErrorCode: MySqlErrorCode.DuplicateKeyEntry })
        {
            context.ChangeTracker.Clear();
            await EnsureUniqueAsync(urlKeyHash, excludeFeedId: null, cancellationToken);
            throw;
        }
    }

    /// <summary>A save of a tracked feed under its version: a concurrent API write between the read and here is <c>409</c>.</summary>
    private async Task SaveVersionedAsync(Guid feedId, byte[]? changedUrlKeyHash, CancellationToken cancellationToken)
    {
        try
        {
            if (changedUrlKeyHash is null)
            {
                await context.SaveChangesAsync(cancellationToken);
            }
            else
            {
                await SaveUniqueWithExclusionAsync(feedId, changedUrlKeyHash, cancellationToken);
            }
        }
        catch (DbUpdateConcurrencyException)
        {
            throw NewsErrors.FeedChanged(feedId);
        }
    }

    private async Task SaveUniqueWithExclusionAsync(Guid feedId, byte[] urlKeyHash, CancellationToken cancellationToken)
    {
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex is not DbUpdateConcurrencyException
            && ex.GetBaseException() is MySqlException { ErrorCode: MySqlErrorCode.DuplicateKeyEntry })
        {
            context.ChangeTracker.Clear();
            await EnsureUniqueAsync(urlKeyHash, excludeFeedId: feedId, cancellationToken);
            throw;
        }
    }

    /// <summary>
    /// Stages the feed's whole tag set on the context, unsaved: shared by <c>PUT …/tags</c> and the tags carried on
    /// <c>PUT /api/news-feeds/{id}</c>, so both apply one rule (a kept tag may be archived, a new one may not).
    /// </summary>
    private async Task StageTagsAsync(Guid id, IReadOnlyCollection<Guid>? tagIds, CancellationToken cancellationToken)
    {
        var wanted = NewsFilterIds.Normalize(tagIds);
        var links = await context.NewsFeedTags.News().Where(l => l.NewsFeedId == id).ToListAsync(cancellationToken);
        await EnsureTagsUsableAsync(wanted, links.Select(l => l.NewsTagId).ToHashSet(), cancellationToken);

        context.NewsFeedTags.RemoveRange(links.Where(l => !wanted.Contains(l.NewsTagId)));
        var existing = links.Select(l => l.NewsTagId).ToHashSet();
        context.NewsFeedTags.AddRange(wanted.Where(t => !existing.Contains(t))
            .Select(t => new NewsFeedTag { NewsFeedTagId = Guid.NewGuid(), NewsFeedId = id, NewsTagId = t }));
    }

    private async Task EnsureTagsUsableAsync(IReadOnlyList<Guid> wanted, IReadOnlySet<Guid> alreadyLinked, CancellationToken cancellationToken)
    {
        if (wanted.Count == 0)
        {
            return;
        }

        var tags = await context.NewsTags.AsNoTracking().News().Where(t => wanted.Contains(t.NewsTagId))
            .Select(t => new { t.NewsTagId, t.ArchivedAtUtc }).ToListAsync(cancellationToken);
        if (tags.Count != wanted.Count || tags.Any(t => t.ArchivedAtUtc is not null && !alreadyLinked.Contains(t.NewsTagId)))
        {
            throw NewsErrors.TagNotUsable();
        }
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

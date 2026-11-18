using Odyssey.Context;
using Odyssey.Core.Internet.News.Jobs;
using Odyssey.Dtos.Internet;

namespace Odyssey.Core.Internet.News.Reading;

/// <summary>
/// One feed's two rows, its counters and its tag links, as every feed projection reads them (issue #470 §5.1). The
/// fetch state and the counters are always inserted with the feed; a missing one (possible only on the FK-free
/// InMemory tier) reads as an unfetched feed with zero counts.
/// </summary>
public sealed record NewsFeedRow(NewsFeed Feed, NewsFeedFetchState? State, NewsFeedCounter? Counter, IReadOnlyList<Guid> TagIds)
{
    public NewsFeedHealth Health => NewsFeedProjection.Health(Feed, State);
}

/// <summary>The feed projections: one display-title rule, one health derivation, one management gate.</summary>
public static class NewsFeedProjection
{
    public const string UntitledFeed = "Untitled feed";

    /// <summary><c>CustomTitle ?? Title ?? host(SiteUrl) ?? "Untitled feed"</c> (§8.5) — never the fetch URL's host.</summary>
    public static string DisplayTitle(string? customTitle, string? title, string? siteUrl)
    {
        if (!string.IsNullOrWhiteSpace(customTitle))
        {
            return customTitle;
        }

        if (!string.IsNullOrWhiteSpace(title))
        {
            return title;
        }

        return siteUrl is not null && Uri.TryCreate(siteUrl, UriKind.Absolute, out var site) && site.Host.Length > 0
            ? site.Host
            : UntitledFeed;
    }

    public static NewsFeedHealth Health(NewsFeed feed, NewsFeedFetchState? state) => NewsFeedHealthRules.Derive(new NewsFeedHealthFacts(
        Archived: feed.ArchivedAtUtc is not null,
        Quarantined: state?.QuarantinedAtUtc is not null,
        IsActive: feed.IsActive,
        LastErrorKind: state?.LastErrorKind ?? NewsFeedErrorKind.None,
        QuotaReached: state?.QuotaReachedAtUtc is not null,
        ConsecutiveFailures: state?.ConsecutiveFailures ?? 0,
        HasSucceeded: state?.LastSuccessAtUtc is not null));

    public static NewsFeedState StateOf(NewsFeed feed) =>
        feed.ArchivedAtUtc is not null ? NewsFeedState.Archived : feed.IsActive ? NewsFeedState.Active : NewsFeedState.Paused;

    public static ExistingNewsFeed ToDto(NewsFeedRow row, int defaultIntervalMinutes, bool canManage, DateOnly todayUtc)
    {
        ArgumentNullException.ThrowIfNull(row);
        var (feed, state) = (row.Feed, row.State);
        var health = row.Health;
        return new ExistingNewsFeed
        {
            Id = feed.NewsFeedId,
            Version = feed.SettingsVersion,
            DisplayTitle = DisplayTitle(feed.CustomTitle, state?.Title, state?.SiteUrl),
            Title = state?.Title,
            CustomTitle = feed.CustomTitle,
            SiteUrl = state?.SiteUrl,
            Description = state?.Description,
            Language = state?.Language,
            Format = state?.Format ?? NewsFeedFormat.Unknown,
            FetchIntervalMinutes = feed.FetchIntervalMinutes,
            EffectiveIntervalMinutes = (int)NewsFetchRules.EffectiveInterval(feed.FetchIntervalMinutes, defaultIntervalMinutes, state?.FeedTtlMinutes).TotalMinutes,
            DownloadImages = feed.DownloadImages,
            IsActive = feed.IsActive,
            ArchivedAtUtc = feed.ArchivedAtUtc,
            CreatedAtUtc = feed.CreatedAtUtc,
            LastSuccessAtUtc = state?.LastSuccessAtUtc,
            LastCheckedAtUtc = state?.LastFetchAttemptAtUtc,
            NextFetchAtUtc = state?.NextFetchAtUtc,
            Health = health,
            QuotaReason = NewsFeedHealthRules.ProjectQuotaReason(health, state?.QuotaReason),
            UnreadCount = Math.Max(0, row.Counter?.UnreadCount ?? 0),
            ArticleCount = Math.Max(0, row.Counter?.ArticleCount ?? 0),
            TagIds = [.. row.TagIds],
            Management = canManage ? Management(feed, state, todayUtc) : null,
        };
    }

    public static NewsFeedCatalogueEntry ToCatalogueEntry(NewsFeedRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var health = row.Health;
        return new NewsFeedCatalogueEntry
        {
            Id = row.Feed.NewsFeedId,
            DisplayTitle = DisplayTitle(row.Feed.CustomTitle, row.State?.Title, row.State?.SiteUrl),
            Health = health,
            QuotaReason = NewsFeedHealthRules.ProjectQuotaReason(health, row.State?.QuotaReason),
            Language = row.State?.Language,
            UnreadCount = Math.Max(0, row.Counter?.UnreadCount ?? 0),
            ArticleCount = Math.Max(0, row.Counter?.ArticleCount ?? 0),
            State = StateOf(row.Feed),
        };
    }

    // The daily counters are yesterday's until the worker rolls the day, so they read as zero on any other day.
    private static NewsFeedManagement Management(NewsFeed feed, NewsFeedFetchState? state, DateOnly todayUtc) => new()
    {
        Url = feed.Url,
        LastErrorKind = state?.LastErrorKind ?? NewsFeedErrorKind.None,
        LastHttpStatus = state?.LastHttpStatus,
        LastErrorAtUtc = state?.LastErrorAtUtc,
        LastErrorMessage = state?.LastErrorMessage,
        ConsecutiveFailures = state?.ConsecutiveFailures ?? 0,
        QuarantinedAtUtc = state?.QuarantinedAtUtc,
        RedirectedFromHttps = state?.RedirectedFromHttps ?? false,
        SuggestedUrl = state?.SuggestedUrl,
        ArticlesToday = state?.DailyQuotaDate == todayUtc ? state.ArticlesToday : 0,
        ImageBytesToday = state?.DailyQuotaDate == todayUtc ? state.ImageBytesToday : 0,
    };
}

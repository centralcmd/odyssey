using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Odyssey.Context;
using Odyssey.Core;
using Odyssey.Core.FileStorage;
using Odyssey.Core.Internet.News;
using Odyssey.Core.Internet.News.Reading;
using Odyssey.Dtos;
using Odyssey.Dtos.Internet;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// The relational half of the news API (issue #470 B4): <c>FULLTEXT</c> search with its bound expression, the
/// <c>max_statement_time</c> mapping to <c>news-busy</c>, the UNION branch plan and the keyset cursor over the real
/// indexes and Guid ordering, the literal <c>LIKE</c> escapes, and API writes landing while a fetch holds its lease.
/// None of it is observable on EF InMemory.
/// </summary>
[Collection(MariaDbCollection.Name)]
public sealed class NewsApiRelationalTests(MariaDbFixture fixture)
{
    private static readonly DateTime Base = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

    private string connection = string.Empty;

    private async Task<OdysseyContext> ContextAsync(int statementSeconds = NewsQueryCommandInterceptor.DefaultStatementSeconds)
    {
        if (connection.Length == 0)
        {
            connection = await fixture.CreateMigratedDatabaseAsync($"odyssey_newsapi_{Guid.NewGuid():N}"[..40]);
        }

        return new OdysseyContext(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(connection, ServerVersion.AutoDetect(connection), mySql => mySql.EnableRetryOnFailure())
            .AddInterceptors(new NewsQueryCommandInterceptor(statementSeconds))
            .Options);
    }

    private static NewsArticleService Articles(OdysseyContext context) => new(
        context, new NewsArticleSearch(context), new NewsListCursor(DataProtectionProvider.Create("odyssey-it-news")),
        new NewsCounterWriter(), null!, new FileStorageVolumeRegistry(NullLogger<FileStorageVolumeRegistry>.Instance, TimeProvider.System),
        TimeProvider.System, NullLogger<NewsArticleService>.Instance);

    private static NewsFeedService Feeds(OdysseyContext context) =>
        new(context, new DefaultNewsSettings(), new NewsFetchStateFlags(), TimeProvider.System, NullLogger<NewsFeedService>.Instance);

    private static async Task<Guid> FeedAsync(OdysseyContext context, string url, string? title = "Feed")
    {
        var id = Guid.NewGuid();
        context.NewsFeeds.Add(new NewsFeed
        {
            NewsFeedId = id, Url = url, UrlKeyHash = NewsUrlKey.Hash(NewsUrlKey.UrlKey(url)!), IsActive = true, DownloadImages = false,
            CreatedAtUtc = Base, SettingsVersion = Guid.NewGuid(),
        });
        context.NewsFeedFetchStates.Add(new NewsFeedFetchState { NewsFeedId = id, FetchFence = Guid.NewGuid(), NextFetchAtUtc = Base, Title = title });
        context.NewsFeedCounters.Add(new NewsFeedCounter { NewsFeedId = id });
        await context.SaveChangesAsync();
        return id;
    }

    private static async Task<List<Guid>> ArticlesAsync(OdysseyContext context, Guid feedId, int count, string title, TimeSpan step)
    {
        var ids = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            var id = Guid.NewGuid();
            var sortAt = Base + step * i;
            context.NewsArticles.Add(new NewsArticle
            {
                NewsArticleId = id, NewsFeedId = feedId, Bucket = 1, Title = $"{title} {i}", Excerpt = $"Opening words of {title}.",
                ContentHash = new byte[32], IdentityHash = new byte[32], FirstSeenAtUtc = sortAt, LastSeenAtUtc = sortAt, SortAtUtc = sortAt,
            });
            ids.Add(id);
        }

        await context.SaveChangesAsync();
        return ids;
    }

    /// <summary>AC 28: FTS operators never cause a 500, the terms are bound and required as prefixes, stopwords drop out.</summary>
    [SkippableFact]
    public async Task AC28_Full_text_search_binds_its_terms_and_survives_any_operator_in_the_input()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await using var context = await ContextAsync();
        var feed = await FeedAsync(context, "https://fts.example.org/rss");
        await ArticlesAsync(context, feed, 2, "Election results", TimeSpan.FromMinutes(1));
        await ArticlesAsync(context, feed, 2, "Weather warning", TimeSpan.FromMinutes(1));
        var service = Articles(context);

        async Task<int?> CountAsync(string search) =>
            (await service.ListAsync(new NewsArticlesQueryParams { Search = search, Limit = 50 }, CancellationToken.None)).TotalCount;

        Assert.Equal(2, await CountAsync("elect"));
        Assert.Equal(2, await CountAsync("the elect"));
        Assert.Equal(0, await CountAsync("election weather"));
        // Operators are separators: "-weather" becomes a required term, and no article has both words.
        Assert.Equal(2, await CountAsync("+elect* \"results\" (x) @2 ~<>"));
        Assert.Equal(0, await CountAsync("+elect* -weather"));
        Assert.Equal(2, await CountAsync("') OR 1=1 -- elect"));
        Assert.Equal(4, await CountAsync("opening"));
        // A body word past the excerpt is not in the index; no body is stored in the database at all.
        Assert.Equal(0, await CountAsync("paragraph"));
        var relevance = await service.ListAsync(
            new NewsArticlesQueryParams { Search = "warning", SortBy = NewsArticleSortBy.Relevance, Limit = 1 }, CancellationToken.None);
        Assert.Single(relevance.Items);
        Assert.NotNull(relevance.NextCursor);
        await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.ListAsync(new NewsArticlesQueryParams { Search = "to of" }, CancellationToken.None));
    }

    /// <summary>AC 28, 47: a tagged command past <c>max_statement_time</c> is <c>503 news-busy</c>; an untagged one is not limited.</summary>
    [SkippableFact]
    public async Task AC28_AC47_A_news_query_past_max_statement_time_is_news_busy()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await using var context = await ContextAsync(statementSeconds: 1);

        var busy = await Assert.ThrowsAsync<NewsException>(() => NewsQueryGuard.RunAsync(() =>
            context.Database.SqlQueryRaw<long>("SELECT SLEEP(3) AS `Value`").News().SingleAsync()));

        Assert.Equal((503, NewsErrorCodes.Busy, (int?)NewsErrors.BusyRetryAfterSeconds), (busy.StatusCode, busy.Code, busy.RetryAfterSeconds));
        Assert.Equal(1L, await context.Database.SqlQueryRaw<long>("SELECT 1 AS `Value`").News().SingleAsync());
    }

    /// <summary>AC 40, 66: the UNION branch plan pages every article exactly once over the real indexes, ties included.</summary>
    [SkippableTheory]
    [InlineData(NewsArticleSortBy.Newest)]
    [InlineData(NewsArticleSortBy.Oldest)]
    public async Task AC40_AC66_Branch_pages_return_each_article_once_while_the_pages_are_marked_read(NewsArticleSortBy sort)
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await using var context = await ContextAsync();
        var feeds = new List<Guid>();
        var all = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            feeds.Add(await FeedAsync(context, $"https://branch{i}-{Guid.NewGuid():N}.example.org/rss"));
            all.AddRange(await ArticlesAsync(context, feeds[i], 6, $"Feed {i}", i == 0 ? TimeSpan.Zero : TimeSpan.FromMinutes(1)));
        }

        var tag = Guid.NewGuid();
        context.NewsTags.Add(new NewsTag { NewsTagId = tag, Name = $"T{tag:N}"[..20], NameKey = $"t{tag:N}"[..20], CreatedAtUtc = Base, UpdatedAtUtc = Base });
        context.NewsFeedTags.Add(new NewsFeedTag { NewsFeedTagId = Guid.NewGuid(), NewsFeedId = feeds[0], NewsTagId = tag });
        var ownTagged = await context.NewsArticles.Where(a => a.NewsFeedId == feeds[1]).OrderBy(a => a.SortAtUtc).Take(2).ToListAsync();
        context.NewsArticleTags.AddRange(ownTagged.Select(a => new NewsArticleTag { NewsArticleTagId = Guid.NewGuid(), NewsArticleId = a.NewsArticleId, NewsTagId = tag, SortAtUtc = a.SortAtUtc }));
        await context.SaveChangesAsync();
        var service = Articles(context);

        // Feeds only, and tags only (an inherited-tag expansion plus an own-tag branch, one article in both).
        foreach (var (feedIds, tagIds, expected) in new (Guid[], Guid[], int)[] { ([.. feeds], [], 18), ([], [tag], 8) })
        {
            var seen = new List<Guid>();
            string? cursor = null;
            do
            {
                var page = await service.ListAsync(new NewsArticlesQueryParams
                {
                    FeedIds = feedIds, TagIds = tagIds, IsRead = false, SortBy = sort, Limit = 4, Cursor = cursor,
                }, CancellationToken.None);
                seen.AddRange(page.Items.Select(i => i.Id));
                await service.SetReadAsync(new NewsArticlesReadStateBulk { ArticleIds = [.. page.Items.Select(i => i.Id)], IsRead = true }, CancellationToken.None);
                cursor = page.NextCursor;
            }
            while (cursor is not null);

            Assert.Equal(expected, seen.Count);
            Assert.Equal(expected, seen.Distinct().Count());
            context.ChangeTracker.Clear();
            await context.Database.ExecuteSqlRawAsync("UPDATE `NewsArticles` SET `IsRead` = 0, `ReadAtUtc` = NULL");
        }
    }

    /// <summary>AC 40: a page over 50 feeds and 50 tags (the both-lists plan) runs as one statement and finds its rows.</summary>
    [SkippableFact]
    public async Task AC40_Fifty_feeds_and_fifty_tags_page_in_one_statement()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await using var context = await ContextAsync();
        var feeds = new List<Guid>();
        for (var i = 0; i < 50; i++)
        {
            feeds.Add(await FeedAsync(context, $"https://wide{i}-{Guid.NewGuid():N}.example.org/rss"));
        }

        var tags = Enumerable.Range(0, 50).Select(_ => Guid.NewGuid()).ToList();
        context.NewsTags.AddRange(tags.Select(t => new NewsTag { NewsTagId = t, Name = $"W{t:N}"[..20], NameKey = $"w{t:N}"[..20], CreatedAtUtc = Base, UpdatedAtUtc = Base }));
        await context.SaveChangesAsync();
        var articles = await ArticlesAsync(context, feeds[7], 3, "Wide", TimeSpan.FromMinutes(1));
        context.NewsFeedTags.Add(new NewsFeedTag { NewsFeedTagId = Guid.NewGuid(), NewsFeedId = feeds[7], NewsTagId = tags[3] });
        await context.SaveChangesAsync();

        var page = await Articles(context).ListAsync(new NewsArticlesQueryParams { FeedIds = [.. feeds], TagIds = [.. tags], Limit = 2 }, CancellationToken.None);

        Assert.Equal(3, page.TotalCount);
        Assert.Equal(2, page.Items.Count);
        Assert.All(page.Items, i => Assert.Contains(i.Id, articles));
    }

    /// <summary>AC 28: <c>%</c> and <c>_</c> in a feed search match literally on MariaDB.</summary>
    [SkippableFact]
    public async Task AC28_Percent_and_underscore_in_a_feed_search_match_literally()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await using var context = await ContextAsync();
        var literal = await FeedAsync(context, $"https://lit-{Guid.NewGuid():N}.example.org/rss", "Up 100% a_b");
        await FeedAsync(context, $"https://other-{Guid.NewGuid():N}.example.org/rss", "Up 1000 axb");
        var service = Feeds(context);

        foreach (var search in new[] { "100%", "a_b" })
        {
            var page = await service.ListAsync(new NewsFeedsQueryParams { Search = search, State = NewsFeedState.All }, canManage: false, CancellationToken.None);
            Assert.Equal(literal, Assert.Single(page.Items).Id);
        }
    }

    /// <summary>AC 77, 20: with a fetch holding its lease, a PUT, a refresh and a release all succeed and the fetch still commits.</summary>
    [SkippableFact]
    public async Task AC77_Api_writes_during_a_fetch_succeed_and_the_fetch_still_commits()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await using var api = await ContextAsync();
        await using var worker = await ContextAsync();
        var id = await FeedAsync(api, $"https://busy-{Guid.NewGuid():N}.example.org/rss");
        var claimTime = DateTime.UtcNow.AddSeconds(-5);

        // The worker's claim: it now holds the row tracked, with its fence.
        var state = await worker.NewsFeedFetchStates.SingleAsync(s => s.NewsFeedId == id);
        state.LeaseOwner = Guid.NewGuid();
        state.LeaseExpiresAtUtc = DateTime.UtcNow.AddMinutes(3);
        state.FetchFence = Guid.NewGuid();
        state.QuarantinedAtUtc = null;
        await worker.SaveChangesAsync();

        var feed = await Feeds(api).GetAsync(id, canManage: true, CancellationToken.None);
        var updated = await Feeds(api).UpdateAsync(id, new UpdateNewsFeed
        {
            Version = feed!.Version, Url = $"https://moved-{Guid.NewGuid():N}.example.org/rss", DownloadImages = true, IsActive = true,
        }, canManage: true, "it-user", CancellationToken.None);
        Assert.NotNull(updated);
        Assert.True(await Feeds(api).RefreshAsync(id, CancellationToken.None));
        await api.Database.ExecuteSqlRawAsync("UPDATE `NewsFeedFetchStates` SET `QuarantinedAtUtc` = UTC_TIMESTAMP(), `CrashSuspectCount` = 2 WHERE `NewsFeedId` = {0}", id);
        Assert.NotNull(await Feeds(api).ReleaseQuarantineAsync(id, canManage: true, "it-user", CancellationToken.None));

        // The fetch's fenced completion: its own columns only, so none of the API writes above fails it.
        state.Title = "Fetched";
        state.LastSuccessAtUtc = DateTime.UtcNow;
        state.LeaseOwner = null;
        state.FetchFence = Guid.NewGuid();
        await worker.SaveChangesAsync();
        var flags = new NewsFetchStateFlags();
        await flags.ClearRefreshAsync(worker, id, claimTime, CancellationToken.None);
        await flags.ClearValidatorsResetAsync(worker, id, claimTime, CancellationToken.None);

        var after = await api.NewsFeedFetchStates.AsNoTracking().SingleAsync(s => s.NewsFeedId == id);
        Assert.Equal("Fetched", after.Title);
        Assert.NotNull(after.RefreshRequestedAtUtc);          // requested mid-fetch: survives the conditional clear
        Assert.NotNull(after.ValidatorsResetRequestedAtUtc);  // the URL change made mid-fetch takes effect next time
        Assert.Null(after.QuarantinedAtUtc);
    }

    private sealed class DefaultNewsSettings : INewsSettingsLookup
    {
        public Task<NewsSettings> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(NewsSettings.Defaults);

        public Task<IReadOnlyList<NewsPrivateHostGrant>> GetAllowListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<NewsPrivateHostGrant>>([]);
    }
}

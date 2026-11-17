// EF1002 flags interpolation into ExecuteSqlRawAsync. Every value interpolated below is a Guid this test generated.
#pragma warning disable EF1002

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Context;
using Odyssey.Core.Internet.News;
using Odyssey.Core.Internet.News.Files;
using Odyssey.Core.Internet.News.Jobs;
using Odyssey.Testing.FileStorage;
using Odyssey.Testing.News;
using Odyssey.TestData.Fixtures;
using Xunit;
using static Odyssey.Testing.News.NewsFeedFixtures;

namespace Odyssey.IntegrationTests;

/// <summary>
/// The news job on real MariaDB (issue #470 B3): the relative counter <c>UPDATE</c>s, the explicit transactions under
/// <c>EnableRetryOnFailure</c>, the fence and the conditional flag clears as raw SQL, the conditional image lease, the
/// bucket's <c>FOR UPDATE</c>, and the foreign keys the purge and the edit must respect (the RESTRICT key to
/// <c>StoredObjects</c> among them). Two harnesses over one database are two worker replicas.
/// </summary>
[Collection(MariaDbCollection.Name)]
public sealed class NewsFeedJobRelationalTests(MariaDbFixture fixture) : IAsyncDisposable
{
    private const string FeedUrl = "https://news.example.com/rss";

    private readonly DirectoryInfo volume = Directory.CreateTempSubdirectory("odyssey-it-news-");
    private readonly List<NewsJobHarness> harnesses = [];
    private readonly TestClock clock = new();
    private string connection = string.Empty;

    public async ValueTask DisposeAsync()
    {
        foreach (var harness in harnesses)
        {
            await harness.DisposeAsync();
        }

        volume.Delete(recursive: true);
    }

    private async Task<NewsJobHarness> ReplicaAsync(FakeNewsServer? server = null, TimeSpan? throttleSpacing = null, int? bucketCapacity = null)
    {
        if (connection.Length == 0)
        {
            connection = await fixture.CreateMigratedDatabaseAsync($"odyssey_newsjob_{Guid.NewGuid():N}"[..40]);
        }

        var harness = await NewsJobHarness.CreateAsync(
            options => options.UseMySql(connection, ServerVersion.AutoDetect(connection), mySql => mySql.EnableRetryOnFailure()),
            volume.FullName, clock, server, bucketCapacity, throttleSpacing: throttleSpacing);
        harnesses.Add(harness);
        return harness;
    }

    private static Item Story(string guid, string body = "<p>Hello world.</p>") =>
        new(guid, $"Story {guid}", $"https://news.example.com/{guid}", body, PubDate: "Sun, 25 Oct 2026 07:00:00 GMT");

    /// <summary>AC 1, 14, 10, 78 on MariaDB: ingest, an edit in one transaction of two saves, then the purge.</summary>
    [SkippableFact]
    public async Task A_feed_is_ingested_edited_and_purged_against_the_real_keys()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var news = await ReplicaAsync();
        news.Server.Bytes("https://img.example.com/a.jpg", ContactImageFixtures.BaselineJpeg(), "image/jpeg");
        news.Server.Feed(FeedUrl, Rss(Story("a-1") with { Enclosure = "https://img.example.com/a.jpg" }, Story("a-2")));
        var feedId = await news.AddFeedAsync(FeedUrl, downloadImages: true);

        await news.Job.RunOnceAsync(CancellationToken.None);

        await news.WithContextAsync(async c =>
        {
            Assert.Equal(2, await c.NewsArticles.CountAsync());
            var counter = await c.NewsFeedCounters.AsNoTracking().SingleAsync(x => x.NewsFeedId == feedId);
            Assert.Equal((2, 2), (counter.ArticleCount, counter.UnreadCount)); // the raw relative UPDATEs
            var totals = await c.NewsStorageTotals.AsNoTracking().SingleAsync();
            Assert.Equal(await c.NewsArticleFiles.Where(f => f.Kind != NewsArticleFileKind.Image).SumAsync(f => f.SizeBytes), totals.TextAndSourceBytes);
            Assert.Equal(await c.NewsArticleFiles.Where(f => f.Kind == NewsArticleFileKind.Image).SumAsync(f => f.SizeBytes), totals.ImageBytes);
            Assert.Equal(NewsArticleImageState.Stored, (await c.NewsArticleImages.AsNoTracking().SingleAsync()).State);
            Assert.Equal(1, (await c.NewsFeedBuckets.AsNoTracking().SingleAsync()).CurrentBucket);
        });

        news.Server.Feed(FeedUrl, Rss(Story("a-1", "<p>Changed body.</p>") with { Enclosure = "https://img.example.com/a.jpg" }, Story("a-2")));
        await news.WithContextAsync(c => new NewsFetchStateFlags().RequestRefreshAsync(c, feedId, clock.GetUtcNow().UtcDateTime, CancellationToken.None));
        clock.Advance(TimeSpan.FromHours(2));
        await news.Job.RunOnceAsync(CancellationToken.None);

        await news.WithContextAsync(async c =>
        {
            var revision = await c.NewsArticleRevisions.AsNoTracking().SingleAsync();
            Assert.Equal(1, revision.RevisionNumber);
            Assert.Equal(2, await c.NewsArticleFiles.CountAsync(f => f.NewsArticleRevisionId == revision.NewsArticleRevisionId));
            Assert.Null((await c.NewsFeedFetchStates.AsNoTracking().SingleAsync()).RefreshRequestedAtUtc);
        });

        var files = await news.WithContextAsync(c => c.NewsArticleFiles.CountAsync());
        await news.WithContextAsync(async c =>
        {
            await c.Database.ExecuteSqlRawAsync($"UPDATE `NewsFeeds` SET `ArchivedAtUtc` = NOW(), `DeletionRequestedAtUtc` = NOW() WHERE `NewsFeedId` = '{feedId}'");
        });
        await news.Job.RunOnceAsync(CancellationToken.None);

        await news.WithContextAsync(async c =>
        {
            Assert.False(await c.NewsFeeds.AnyAsync());
            Assert.False(await c.NewsArticles.AnyAsync());
            Assert.False(await c.NewsArticleFiles.AnyAsync());
            Assert.Equal(files, await c.StoredObjects.CountAsync(o => o.State == StoredObjectState.PendingDelete));
            var totals = await c.NewsStorageTotals.AsNoTracking().SingleAsync();
            Assert.Equal((0L, 0L), (totals.ImageBytes, totals.TextAndSourceBytes));
        });
    }

    /// <summary>AC 20: of two replicas claiming the same due feed at once, exactly one takes it.</summary>
    [SkippableFact]
    public async Task Two_replicas_never_both_claim_one_feed()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var first = await ReplicaAsync();
        var second = await ReplicaAsync(first.Server);
        var feedIds = new List<Guid>();
        for (var k = 0; k < 6; k++)
        {
            feedIds.Add(await first.AddFeedAsync($"https://host{k}.example.com/rss"));
        }

        var claims = await Task.WhenAll(
            first.Job.ClaimDueAsync(NewsSettings.Defaults, 4, CancellationToken.None),
            second.Job.ClaimDueAsync(NewsSettings.Defaults, 4, CancellationToken.None));

        var all = claims.SelectMany(c => c).Select(c => c.FeedId).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
        Assert.True(all.Count >= 4, "the two replicas between them claimed at least one replica's worth");
        var owners = await first.WithContextAsync(c => c.NewsFeedFetchStates.Where(s => s.LeaseOwner != null).Select(s => s.LeaseOwner).ToListAsync());
        Assert.Equal(all.Count, owners.Count);
        Assert.All(owners, o => Assert.Contains(o, new Guid?[] { first.Job.Owner, second.Job.Owner }));
    }

    /// <summary>AC 22: two feeds on one host, claimed by two replicas at once, are never requested inside the 2 s spacing.</summary>
    [SkippableFact]
    public async Task Two_feeds_on_one_host_are_spaced_across_replicas()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var first = await ReplicaAsync(throttleSpacing: TimeSpan.FromSeconds(2));
        var second = await ReplicaAsync(first.Server, throttleSpacing: TimeSpan.FromSeconds(2));
        await first.AddFeedAsync("https://same.example.com/a");
        await first.AddFeedAsync("https://same.example.com/b");

        var claims = await Task.WhenAll(
            first.Job.ClaimDueAsync(NewsSettings.Defaults, 4, CancellationToken.None),
            second.Job.ClaimDueAsync(NewsSettings.Defaults, 4, CancellationToken.None));
        Assert.Single(claims.SelectMany(c => c));

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Empty(await second.Job.ClaimDueAsync(NewsSettings.Defaults, 4, CancellationToken.None));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Single(await second.Job.ClaimDueAsync(NewsSettings.Defaults, 4, CancellationToken.None));
    }

    /// <summary>AC 51, 20: a fenced write with the current fence succeeds and rotates it; after another writer's rotation it is the lease lost.</summary>
    [SkippableFact]
    public async Task A_fenced_write_rotates_the_fence_and_a_stale_one_is_the_lease_lost()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var news = await ReplicaAsync();
        var feedId = await news.AddFeedAsync(FeedUrl);
        var claim = Assert.Single(await news.Job.ClaimDueAsync(NewsSettings.Defaults, 4, CancellationToken.None));
        using var session = new NewsFetchSession(claim);

        var renewed = await news.WithContextAsync(c => session.WithFenceAsync(c, async state =>
        {
            state.LeaseExpiresAtUtc = clock.GetUtcNow().UtcDateTime + NewsJobTiming.Lease;
            await c.SaveChangesAsync();
        }, CancellationToken.None));
        Assert.Equal(NewsFenceOutcome.Ok, renewed);
        Assert.NotEqual(claim.Fence, session.CurrentFence);

        // An API write during the fetch (a conditional flag write) does not touch the fence…
        await news.WithContextAsync(c => new NewsFetchStateFlags().RequestRefreshAsync(c, feedId, clock.GetUtcNow().UtcDateTime, CancellationToken.None));
        Assert.Equal(NewsFenceOutcome.Ok, await news.WithContextAsync(c => session.WithFenceAsync(c, _ => c.SaveChangesAsync(), CancellationToken.None)));

        // …but another worker's rotation does: this fetch stops writing.
        await news.WithContextAsync(c => c.Database.ExecuteSqlRawAsync($"UPDATE `NewsFeedFetchStates` SET `FetchFence` = UUID() WHERE `NewsFeedId` = '{feedId}'"));
        Assert.Equal(NewsFenceOutcome.LeaseLost, await news.WithContextAsync(c => session.WithFenceAsync(c, _ => c.SaveChangesAsync(), CancellationToken.None)));
    }

    /// <summary>AC 77 on MariaDB: a refresh and a URL change made mid-fetch survive the completion's conditional clears.</summary>
    [SkippableFact]
    public async Task Flags_set_mid_fetch_survive_the_completion()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var news = await ReplicaAsync();
        var feedId = await news.AddFeedAsync(FeedUrl);
        news.Server.Map(FeedUrl, _ =>
        {
            news.WithContextAsync(async c =>
            {
                var at = clock.GetUtcNow().UtcDateTime + TimeSpan.FromSeconds(1);
                await new NewsFetchStateFlags().RequestRefreshAsync(c, feedId, at, CancellationToken.None);
                await new NewsFetchStateFlags().RequestValidatorsResetAsync(c, feedId, at, CancellationToken.None);
            }).GetAwaiter().GetResult();
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(Rss(Story("m-1"))) };
        });

        await news.Job.RunOnceAsync(CancellationToken.None);

        var state = await news.StateAsync(feedId);
        Assert.NotNull(state.LastSuccessAtUtc);
        Assert.Null(state.LeaseOwner);
        Assert.NotNull(state.RefreshRequestedAtUtc);
        Assert.NotNull(state.ValidatorsResetRequestedAtUtc);
    }

    /// <summary>AC 44 on MariaDB: the image lease is a conditional UPDATE, so two replicas never claim one image.</summary>
    [SkippableFact]
    public async Task Two_replicas_never_claim_one_image()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var first = await ReplicaAsync();
        var second = await ReplicaAsync(first.Server);
        first.Server.Feed(FeedUrl, Rss(Enumerable.Range(1, 8).Select(n => Story($"i-{n}") with { Enclosure = $"https://img.example.com/{n}.jpg" }).ToArray()));
        var feedId = await first.AddFeedAsync(FeedUrl, downloadImages: true);
        var claim = Assert.Single(await first.Job.ClaimDueAsync(NewsSettings.Defaults, 4, CancellationToken.None));
        // Ingest only (the images stay Pending): the fetcher, not the whole tick.
        await first.Host.Services.GetRequiredService<NewsFeedFetcher>().RunAsync(claim, NewsSettings.Defaults, [], 0, CancellationToken.None);
        Assert.Equal(8, await first.WithContextAsync(c => c.NewsArticleImages.CountAsync(i => i.State == NewsArticleImageState.Pending)));

        var claimed = await Task.WhenAll(
            first.Host.Services.GetRequiredService<NewsImageDownloader>().ClaimAsync(Guid.NewGuid(), NewsSettings.Defaults, CancellationToken.None),
            second.Host.Services.GetRequiredService<NewsImageDownloader>().ClaimAsync(Guid.NewGuid(), NewsSettings.Defaults, CancellationToken.None));

        var all = claimed.SelectMany(c => c).ToList();
        Assert.Equal(8, all.Count);
        Assert.Equal(8, all.Distinct().Count());
        _ = feedId;
    }

    /// <summary>AC 41: two replicas allocating one feed's folders at once never overfill a bucket (the row's FOR UPDATE).</summary>
    [SkippableFact]
    public async Task Concurrent_allocations_never_overfill_a_bucket()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var first = await ReplicaAsync(bucketCapacity: 3);
        var second = await ReplicaAsync(first.Server, bucketCapacity: 3);
        var feedId = await first.AddFeedAsync(FeedUrl);

        async Task<int> AllocateAsync(NewsJobHarness replica)
        {
            await using var scope = replica.Scope();
            return await scope.ServiceProvider.GetRequiredService<NewsBucketAllocator>().AllocateAsync(feedId, CancellationToken.None);
        }

        var buckets = await Task.WhenAll(Enumerable.Range(0, 12).Select(k => AllocateAsync(k % 2 == 0 ? first : second)));

        Assert.All(buckets.GroupBy(b => b), g => Assert.Equal(3, g.Count()));
        Assert.Equal([1, 2, 3, 4], buckets.Distinct().Order());
    }
}

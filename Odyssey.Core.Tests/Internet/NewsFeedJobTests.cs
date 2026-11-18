using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Context;
using Odyssey.Core.FileStorage;
using Odyssey.Core.Internet.News;
using Odyssey.Core.Internet.News.Files;
using Odyssey.Core.Internet.News.Jobs;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Internet;
using Odyssey.Testing.FileStorage;
using Odyssey.Testing.News;
using Odyssey.TestData.Fixtures;
using Xunit;
using static Odyssey.Testing.News.NewsFeedFixtures;

namespace Odyssey.Core.Tests.Internet;

/// <summary>
/// The news job end to end on EF InMemory (issue #470 B3, AC 44): claim, conditional GET, parse, ingest, files on the
/// object store, edits and revisions, quotas and caps, images, the purge, crashes and the maintenance pass. Every path
/// here runs with no <c>ExecuteUpdate</c>/<c>ExecuteDelete</c>, which is what lets this tier exercise it at all.
/// </summary>
public sealed class NewsFeedJobTests : IAsyncLifetime
{
    private const string FeedUrl = "https://news.example.com/rss";

    private static readonly InMemoryDatabaseRoot DatabaseRoot = new();

    private readonly string databaseName = $"news-job-{Guid.NewGuid():N}";
    private readonly DirectoryInfo volume = Directory.CreateTempSubdirectory("odyssey-news-");
    private NewsJobHarness news = null!;

    private void UseDatabase(DbContextOptionsBuilder<OdysseyContext> options) =>
        options.UseInMemoryDatabase(databaseName, DatabaseRoot).ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning));

    public async Task InitializeAsync() => news = await NewsJobHarness.CreateAsync(UseDatabase, volume.FullName);

    public async Task DisposeAsync()
    {
        await news.DisposeAsync();
        volume.Delete(recursive: true);
    }

    private Task TickAsync() => news.Job.RunOnceAsync(CancellationToken.None);

    /// <summary>The next tick after the feed's schedule: past its lease and its next fetch.</summary>
    private async Task NextFetchAsync(Guid feedId)
    {
        var state = await news.StateAsync(feedId);
        var wait = state.NextFetchAtUtc - news.Clock.GetUtcNow().UtcDateTime;
        news.Advance((wait > TimeSpan.Zero ? wait : TimeSpan.Zero) + TimeSpan.FromSeconds(1));
        await TickAsync();
    }

    private static Item Story(string guid, string title = "A story", string body = "<p>Hello <b>world</b>.</p>", string? link = null) =>
        new(guid, title, link ?? $"https://news.example.com/{guid}", body, PubDate: "Sun, 25 Oct 2026 07:00:00 GMT");

    // ── AC 1, AC 7 ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AC1_an_rss_feed_is_healthy_after_one_tick_with_source_and_text_files_in_the_article_folder()
    {
        news.Server.Feed(FeedUrl, Rss(Story("a-1", "First"), Story("a-2", "Second")), etag: "\"v1\"");
        var feedId = await news.AddFeedAsync(FeedUrl);

        await TickAsync();

        var state = await news.StateAsync(feedId);
        var feed = await news.WithContextAsync(c => c.NewsFeeds.AsNoTracking().SingleAsync(f => f.NewsFeedId == feedId));
        Assert.Equal(NewsFeedHealth.Healthy, NewsFeedHealthRules.Derive(new NewsFeedHealthFacts(
            feed.ArchivedAtUtc is not null, state.QuarantinedAtUtc is not null, feed.IsActive, state.LastErrorKind,
            state.QuotaReachedAtUtc is not null, state.ConsecutiveFailures, state.LastSuccessAtUtc is not null)));
        Assert.Null(state.LeaseOwner);
        Assert.Equal(0, state.CrashSuspectCount);
        Assert.Equal("Example news", state.Title);
        Assert.Equal(NewsFeedFormat.Rss, state.Format);
        Assert.Equal("\"v1\"", state.ETag);
        Assert.NotNull(state.LastBodySha256);
        Assert.Equal(news.Clock.GetUtcNow().UtcDateTime + TimeSpan.FromMinutes(60), state.NextFetchAtUtc);

        var articles = await news.ArticlesAsync(feedId);
        Assert.Equal(2, articles.Count);
        Assert.All(articles, a => Assert.Equal(1, a.Bucket));
        var counter = await news.WithContextAsync(c => c.NewsFeedCounters.AsNoTracking().SingleAsync(x => x.NewsFeedId == feedId));
        Assert.Equal((2, 2), (counter.ArticleCount, counter.UnreadCount));

        await news.WithContextAsync(async context =>
        {
            foreach (var article in articles)
            {
                var files = await context.NewsArticleFiles.AsNoTracking().Where(f => f.NewsArticleId == article.NewsArticleId).ToListAsync();
                Assert.Equal([NewsArticleFileKind.Source, NewsArticleFileKind.PlainText], files.Select(f => f.Kind).Order());
                foreach (var file in files)
                {
                    var stored = await context.StoredObjects.AsNoTracking().SingleAsync(o => o.Id == file.StoredObjectId);
                    // AC 7: committed, the news feature, opaque GUID names, nothing a publisher supplied in the path.
                    Assert.Equal(StoredObjectState.Committed, stored.State);
                    Assert.Equal(NewsArticleStorage.FeatureKey, stored.FeatureKey);
                    var extension = file.Kind == NewsArticleFileKind.Source ? ".xml" : ".txt";
                    Assert.Equal($"news/{feedId:N}/1/{article.NewsArticleId:N}/{stored.Id:N}{extension}", stored.RelativePath);
                    Assert.True(File.Exists(Path.Combine(volume.FullName, stored.RelativePath)));
                }
            }

            var totals = await context.NewsStorageTotals.AsNoTracking().SingleAsync();
            Assert.Equal(await context.NewsArticleFiles.SumAsync(f => f.SizeBytes), totals.TextAndSourceBytes);
        });

        var body = await File.ReadAllTextAsync(await TextPathAsync(articles[0].NewsArticleId));
        Assert.Equal("Hello world.", body);
    }

    [Fact]
    public async Task AC1_an_atom_feed_is_ingested_too()
    {
        news.Server.Feed(FeedUrl, Atom(new Item("urn:a", "Atom entry", "https://news.example.com/atom-1", "<p>Atom body</p>")), contentType: "application/atom+xml");
        var feedId = await news.AddFeedAsync(FeedUrl);

        await TickAsync();

        var state = await news.StateAsync(feedId);
        Assert.Equal(NewsFeedFormat.Atom, state.Format);
        Assert.NotNull(state.LastSuccessAtUtc);
        Assert.Single(await news.ArticlesAsync(feedId));
    }

    [Fact]
    public async Task AC7_a_news_object_cannot_be_opened_through_another_feature()
    {
        news.Server.Feed(FeedUrl, Rss(Story("a-1")));
        var feedId = await news.AddFeedAsync(FeedUrl);
        await TickAsync();

        await using var scope = news.Scope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var storedId = await context.NewsArticleFiles.AsNoTracking().Select(f => f.StoredObjectId).FirstAsync();
        var store = scope.ServiceProvider.GetRequiredService<IObjectStore>();
        Assert.NotNull(await store.OpenReadAsync(NewsArticleStorage.FeatureKey, storedId, CancellationToken.None));
        // The harness registers only the news feature; an unknown key is refused as a contract violation, never served.
        await Assert.ThrowsAnyAsync<Exception>(() => store.OpenReadAsync("PhotoOriginals", storedId, CancellationToken.None));
        _ = feedId;
    }

    // ── AC 11, AC 23 ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AC11_an_unchanged_feed_stores_no_row_and_writes_no_file_and_a_304_is_not_parsed()
    {
        news.Server.Feed(FeedUrl, Rss(Story("a-1")), etag: "\"v1\"");
        var feedId = await news.AddFeedAsync(FeedUrl);
        await TickAsync();
        var files = await news.WithContextAsync(c => c.StoredObjects.CountAsync());

        await NextFetchAsync(feedId);

        // The second request carried the validator and was answered 304.
        Assert.Contains(news.Server.Requests, r => r.Headers.TryGetValues("If-None-Match", out var v) && v.Contains("\"v1\""));
        Assert.Equal(files, await news.WithContextAsync(c => c.StoredObjects.CountAsync()));
        Assert.Single(await news.ArticlesAsync(feedId));
        Assert.Equal(304, (await news.StateAsync(feedId)).LastHttpStatus);

        // A body identical to the last one, without validators, is a 304 too.
        news.Server.Feed(FeedUrl, Rss(Story("a-1")));
        await NextFetchAsync(feedId);
        Assert.Equal(files, await news.WithContextAsync(c => c.StoredObjects.CountAsync()));
    }

    [Fact]
    public async Task AC23_an_etag_with_a_line_break_is_never_stored()
    {
        news.Server.Map(FeedUrl, _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Rss(Story("a-1"))) };
            response.Headers.TryAddWithoutValidation("ETag", "\"x\"é");
            return response;
        });
        var feedId = await news.AddFeedAsync(FeedUrl);

        await TickAsync();

        Assert.Null((await news.StateAsync(feedId)).ETag);
    }

    // ── AC 14, AC 35, AC 45 ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AC14_an_edit_creates_revision_one_relabels_the_old_files_and_keeps_read_state_and_sort()
    {
        news.Server.Feed(FeedUrl, Rss(Story("a-1", body: "<p>Version one.</p>")));
        var feedId = await news.AddFeedAsync(FeedUrl);
        await TickAsync();
        var original = Assert.Single(await news.ArticlesAsync(feedId));
        await news.WithContextAsync(async c =>
        {
            var a = await c.NewsArticles.SingleAsync();
            a.IsRead = true;
            a.ReadAtUtc = news.Clock.GetUtcNow().UtcDateTime;
            await c.SaveChangesAsync();
        });

        news.Server.Feed(FeedUrl, Rss(Story("a-1", body: "<p>Version two, longer.</p>")));
        await NextFetchAsync(feedId);

        var edited = Assert.Single(await news.ArticlesAsync(feedId));
        Assert.Equal(original.NewsArticleId, edited.NewsArticleId);
        Assert.True(edited.IsRead); // AC 35
        Assert.Equal(original.SortAtUtc, edited.SortAtUtc);
        Assert.Equal(1, edited.RevisionCount);
        Assert.NotNull(edited.ContentChangedAtUtc);
        await news.WithContextAsync(async c =>
        {
            var revision = await c.NewsArticleRevisions.AsNoTracking().SingleAsync();
            Assert.Equal(1, revision.RevisionNumber);
            Assert.Equal(original.ContentHash, revision.ContentHash);
            var files = await c.NewsArticleFiles.AsNoTracking().ToListAsync();
            Assert.Equal(2, files.Count(f => f.NewsArticleRevisionId == revision.NewsArticleRevisionId));
            // AC 45: exactly one current Source and one current PlainText.
            Assert.Single(files, f => f.NewsArticleRevisionId == null && f.Kind == NewsArticleFileKind.Source);
            Assert.Single(files, f => f.NewsArticleRevisionId == null && f.Kind == NewsArticleFileKind.PlainText);
        });
        Assert.Equal("Version two, longer.", await File.ReadAllTextAsync(await TextPathAsync(edited.NewsArticleId)));
    }

    [Fact]
    public async Task AC45_a_second_change_within_the_hour_overwrites_without_a_revision_and_releases_the_old_files()
    {
        news.Server.Feed(FeedUrl, Rss(Story("a-1", body: "<p>One.</p>")));
        var feedId = await news.AddFeedAsync(FeedUrl, fetchIntervalMinutes: 15);
        await TickAsync();
        news.Server.Feed(FeedUrl, Rss(Story("a-1", body: "<p>Two.</p>")));
        await NextFetchAsync(feedId);
        // The provisional schedule is an hour out; the refresh brings the next fetch inside the hour of the edit.
        await news.WithContextAsync(async c =>
        {
            var flags = new NewsFetchStateFlags();
            await flags.RequestRefreshAsync(c, feedId, news.Clock.GetUtcNow().UtcDateTime, CancellationToken.None);
        });
        news.Server.Feed(FeedUrl, Rss(Story("a-1", body: "<p>Three.</p>")));
        news.Advance(TimeSpan.FromMinutes(2));
        await TickAsync();

        var article = Assert.Single(await news.ArticlesAsync(feedId));
        Assert.Equal(1, article.RevisionCount);
        await news.WithContextAsync(async c =>
        {
            Assert.Single(await c.NewsArticleRevisions.ToListAsync());
            Assert.Equal(2, await c.StoredObjects.CountAsync(o => o.State == StoredObjectState.PendingDelete));
            var files = await c.NewsArticleFiles.AsNoTracking().ToListAsync();
            Assert.Equal(4, files.Count);
            Assert.Single(files, f => f.NewsArticleRevisionId == null && f.Kind == NewsArticleFileKind.PlainText);
        });
        Assert.Equal("Three.", await File.ReadAllTextAsync(await TextPathAsync(article.NewsArticleId)));
    }

    [Fact]
    public async Task AC14_the_twenty_first_edit_keeps_revision_one()
    {
        var feedId = await news.AddFeedAsync(FeedUrl);
        for (var version = 0; version <= 21; version++)
        {
            news.Server.Feed(FeedUrl, Rss(Story("a-1", body: $"<p>Version {version}.</p>")));
            await NextFetchAsync(feedId);
            news.Advance(TimeSpan.FromHours(1)); // past the overwrite window, so each change is a revision
        }

        var article = Assert.Single(await news.ArticlesAsync(feedId));
        Assert.Equal(20, article.RevisionCount);
        var numbers = await news.WithContextAsync(c => c.NewsArticleRevisions.Select(r => r.RevisionNumber).OrderBy(n => n).ToListAsync());
        Assert.Equal(20, numbers.Count);
        Assert.Equal(1, numbers[0]);
        Assert.Equal(21, numbers[^1]);
    }

    // ── AC 15 ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AC15_a_future_pub_date_sorts_one_hour_after_first_seen()
    {
        news.Server.Feed(FeedUrl, Rss(new Item("f-1", "Future", "https://news.example.com/f", "<p>x</p>", PubDate: "Fri, 01 Jan 2027 00:00:00 GMT")));
        var feedId = await news.AddFeedAsync(FeedUrl);

        await TickAsync();

        var article = Assert.Single(await news.ArticlesAsync(feedId));
        Assert.Equal(article.FirstSeenAtUtc + TimeSpan.FromHours(1), article.SortAtUtc);
        Assert.Equal(new DateTime(2026, 10, 25, 8, 0, 0, DateTimeKind.Utc), NewsFetchRules.SortAt(new DateTime(2026, 10, 25, 8, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 25, 9, 0, 0, DateTimeKind.Utc)));
    }

    // ── Failures and back-off (§9.3) ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_failure_backs_off_and_a_410_deactivates_the_feed()
    {
        news.Server.Status(FeedUrl, HttpStatusCode.ServiceUnavailable);
        var feedId = await news.AddFeedAsync(FeedUrl);
        await TickAsync();

        var state = await news.StateAsync(feedId);
        Assert.Equal(NewsFeedErrorKind.HttpStatus, state.LastErrorKind);
        Assert.Equal(503, state.LastHttpStatus);
        Assert.Equal(1, state.ConsecutiveFailures);
        Assert.Equal("news.example.com answered HTTP 503.", state.LastErrorMessage);
        var delay = state.NextFetchAtUtc - news.Clock.GetUtcNow().UtcDateTime;
        Assert.InRange(delay, TimeSpan.FromMinutes(108), TimeSpan.FromMinutes(132));

        news.Server.Status(FeedUrl, HttpStatusCode.Gone);
        await NextFetchAsync(feedId);
        Assert.Equal(NewsFeedErrorKind.Gone, (await news.StateAsync(feedId)).LastErrorKind);

        // Gone is not due again on its schedule; a refresh revives it.
        var requests = news.Server.CountFor(FeedUrl);
        news.Advance(TimeSpan.FromDays(2));
        await TickAsync();
        Assert.Equal(requests, news.Server.CountFor(FeedUrl));
        await news.WithContextAsync(c => new NewsFetchStateFlags().RequestRefreshAsync(c, feedId, news.Clock.GetUtcNow().UtcDateTime, CancellationToken.None));
        await TickAsync();
        Assert.Equal(requests + 1, news.Server.CountFor(FeedUrl));
    }

    [Fact]
    public async Task A_document_that_is_not_a_feed_is_not_a_feed()
    {
        news.Server.Feed(FeedUrl, "<html><body>hi</body></html>", contentType: "text/html");
        var feedId = await news.AddFeedAsync(FeedUrl);

        await TickAsync();

        Assert.Equal(NewsFeedErrorKind.NotAFeed, (await news.StateAsync(feedId)).LastErrorKind);
        Assert.Empty(await news.ArticlesAsync(feedId));
    }

    // ── AC 21, AC 53: redirects and the suggested URL ────────────────────────────────────────────────

    [Fact]
    public async Task AC53_a_same_host_301_records_a_suggestion_and_the_worker_never_writes_the_url()
    {
        const string httpUrl = "http://news.example.com/rss";
        news.Server.Redirect(httpUrl, HttpStatusCode.MovedPermanently, FeedUrl);
        news.Server.Feed(FeedUrl, Rss(Story("a-1")));
        var feedId = await news.AddFeedAsync(httpUrl);
        var before = await news.WithContextAsync(c => c.NewsFeeds.AsNoTracking().SingleAsync(f => f.NewsFeedId == feedId));

        await TickAsync();

        var state = await news.StateAsync(feedId);
        Assert.Equal(FeedUrl, state.SuggestedUrl);
        Assert.NotNull(state.LastSuccessAtUtc);
        var after = await news.WithContextAsync(c => c.NewsFeeds.AsNoTracking().SingleAsync(f => f.NewsFeedId == feedId));
        Assert.Equal((before.Url, before.SettingsVersion), (after.Url, after.SettingsVersion));
        Assert.Equal(before.UrlKeyHash, after.UrlKeyHash);
    }

    [Fact]
    public async Task AC21_a_301_to_another_host_including_a_subdomain_suggests_nothing()
    {
        news.Server.Redirect(FeedUrl, HttpStatusCode.MovedPermanently, "https://cdn.news.example.com/rss");
        news.Server.Feed("https://cdn.news.example.com/rss", Rss(Story("a-1")));
        var feedId = await news.AddFeedAsync(FeedUrl);

        await TickAsync();

        var state = await news.StateAsync(feedId);
        Assert.Null(state.SuggestedUrl);
        Assert.NotNull(state.LastSuccessAtUtc);
    }

    [Fact]
    public async Task AC21_the_sixth_redirect_is_too_many_redirects()
    {
        for (var hop = 0; hop < 6; hop++)
        {
            news.Server.Redirect($"https://news.example.com/r{hop}", HttpStatusCode.Found, $"https://news.example.com/r{hop + 1}");
        }

        var feedId = await news.AddFeedAsync("https://news.example.com/r0");
        await TickAsync();

        Assert.Equal(NewsFeedErrorKind.TooManyRedirects, (await news.StateAsync(feedId)).LastErrorKind);
        Assert.Equal(6, news.Server.Requests.Count);
    }

    [Fact]
    public async Task AC53_a_suggestion_that_is_another_feeds_address_is_dropped()
    {
        const string httpUrl = "http://news.example.com/rss";
        news.Server.Redirect(httpUrl, HttpStatusCode.MovedPermanently, FeedUrl);
        news.Server.Feed(FeedUrl, Rss(Story("a-1")));
        var feedId = await news.AddFeedAsync(httpUrl);
        await news.AddFeedAsync(FeedUrl);

        await TickAsync();

        Assert.Null((await news.StateAsync(feedId)).SuggestedUrl);
    }

    // ── AC 20, AC 77: API writes during a fetch ──────────────────────────────────────────────────────

    [Fact]
    public async Task AC77_a_refresh_and_a_url_change_made_mid_fetch_survive_the_completion()
    {
        var feedId = await news.AddFeedAsync(FeedUrl);
        news.Server.Map(FeedUrl, _ =>
        {
            // The API writes while the fetch holds the lease and the fence.
            news.WithContextAsync(async c =>
            {
                var flags = new NewsFetchStateFlags();
                var at = news.Clock.GetUtcNow().UtcDateTime + TimeSpan.FromSeconds(1);
                await flags.RequestRefreshAsync(c, feedId, at, CancellationToken.None);
                await flags.RequestValidatorsResetAsync(c, feedId, at, CancellationToken.None);
            }).GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Rss(Story("a-1"))) };
        });

        await TickAsync();

        var state = await news.StateAsync(feedId);
        Assert.NotNull(state.LastSuccessAtUtc); // the completion committed
        Assert.NotNull(state.RefreshRequestedAtUtc);
        Assert.NotNull(state.ValidatorsResetRequestedAtUtc);
        Assert.Single(await news.ArticlesAsync(feedId));
    }

    [Fact]
    public async Task A_refresh_requested_before_the_claim_is_cleared_and_a_url_change_clears_the_validators()
    {
        news.Server.Feed(FeedUrl, Rss(Story("a-1")), etag: "\"v1\"");
        var feedId = await news.AddFeedAsync(FeedUrl);
        await TickAsync();
        await news.WithContextAsync(async c =>
        {
            var flags = new NewsFetchStateFlags();
            await flags.RequestRefreshAsync(c, feedId, news.Clock.GetUtcNow().UtcDateTime, CancellationToken.None);
            await flags.RequestValidatorsResetAsync(c, feedId, news.Clock.GetUtcNow().UtcDateTime, CancellationToken.None);
        });
        news.Advance(TimeSpan.FromMinutes(2));

        await TickAsync();

        var state = await news.StateAsync(feedId);
        Assert.Null(state.RefreshRequestedAtUtc);
        Assert.Null(state.ValidatorsResetRequestedAtUtc);
        // The validators were neither sent on this fetch nor kept after it.
        Assert.False(news.Server.Requests.Last().Headers.Contains("If-None-Match"));
        Assert.Null(state.ETag);
    }

    // ── AC 19, AC 52: crashes ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AC19_an_expired_lease_is_a_crash_event_three_quarantine_and_the_breaker_trips()
    {
        var feedId = await news.AddFeedAsync(FeedUrl);
        news.Server.Feed(FeedUrl, Rss(Story("a-1")));
        for (var crash = 1; crash <= 3; crash++)
        {
            // A claim that never completes: the lease is left to expire, as after a crash or a hung parser.
            await LeaveClaimedAsync(feedId);
            news.Advance(NewsJobTiming.Lease + TimeSpan.FromSeconds(1));
            await news.Job.DetectCrashesAsync(new NewsJobTally(), CancellationToken.None);

            var state = await news.StateAsync(feedId);
            Assert.Null(state.LeaseOwner);
            Assert.True(state.NextFetchAtUtc >= news.Clock.GetUtcNow().UtcDateTime + TimeSpan.FromMinutes(50), "a crash leaves the next fetch about an hour out");
            Assert.Equal(crash == 3, state.QuarantinedAtUtc is not null);
            await news.WithContextAsync(async c =>
            {
                var s = await c.NewsFeedFetchStates.SingleAsync();
                s.NextFetchAtUtc = news.Clock.GetUtcNow().UtcDateTime;
                await c.SaveChangesAsync();
            });
        }

        Assert.Equal(3, await news.WithContextAsync(c => c.NewsFeedCrashEvents.CountAsync()));
        Assert.True(await news.Job.BreakerTrippedAsync(CancellationToken.None));
        Assert.Equal(NewsFeedErrorKind.Quarantined, (await news.StateAsync(feedId)).LastErrorKind);

        // Release clears it (the API's conditional write).
        Assert.True(await news.WithContextAsync(c => new NewsFetchStateFlags().ReleaseQuarantineAsync(c, feedId, CancellationToken.None)));
        var released = await news.StateAsync(feedId);
        Assert.Null(released.QuarantinedAtUtc);
        Assert.Equal(0, released.CrashSuspectCount);

        // After the hour, the breaker resets.
        news.Advance(NewsJobTiming.BreakerWindow);
        Assert.False(await news.Job.BreakerTrippedAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AC19_a_completed_fetch_resets_the_crash_count()
    {
        news.Server.Feed(FeedUrl, Rss(Story("a-1")));
        var feedId = await news.AddFeedAsync(FeedUrl);
        await LeaveClaimedAsync(feedId);
        news.Advance(NewsJobTiming.Lease + TimeSpan.FromSeconds(1));
        await news.Job.DetectCrashesAsync(new NewsJobTally(), CancellationToken.None);
        Assert.Equal(1, (await news.StateAsync(feedId)).CrashSuspectCount);

        await NextFetchAsync(feedId);

        Assert.Equal(0, (await news.StateAsync(feedId)).CrashSuspectCount);
    }

    private async Task LeaveClaimedAsync(Guid feedId)
    {
        var claims = await news.Job.ClaimDueAsync(NewsSettings.Defaults, 4, CancellationToken.None);
        Assert.Contains(claims, c => c.FeedId == feedId);
    }

    // ── AC 30, AC 59, AC 60: quotas and caps ─────────────────────────────────────────────────────────

    [Fact]
    public async Task AC59_at_the_text_quota_no_article_is_stored_and_the_reason_is_text_quota_with_no_held_reason()
    {
        // The first tick's hourly recount would correct a planted total; it runs before the plant.
        await TickAsync();
        await news.WithContextAsync(async c =>
        {
            (await TotalsAsync(c)).TextAndSourceBytes = NewsTextQuota.QuotaBytes(20);
            await c.SaveChangesAsync();
        });
        news.Server.Feed(FeedUrl, Rss(Story("a-1")), etag: "\"v1\"");
        var feedId = await news.AddFeedAsync(FeedUrl);

        await TickAsync();

        Assert.Empty(await news.ArticlesAsync(feedId));
        var state = await news.StateAsync(feedId);
        Assert.Equal(NewsFeedQuotaReason.TextQuota, state.QuotaReason);
        Assert.NotNull(state.QuotaReachedAtUtc);
        Assert.Null(state.ETag); // a transient skip clears the validators
        // AC 60: not a held reason.
        Assert.False(await news.WithContextAsync(c => c.JobRuns.AnyAsync(r => r.Status == JobRunStatus.Held)));
    }

    [Fact]
    public async Task AC59_the_daily_article_cap_skips_new_items_sets_daily_cap_and_lifts_the_next_utc_day()
    {
        await SetSettingAsync(SystemSettingsKeys.NewsFeedMaxArticlesPerDay, "10");
        news.Server.Feed(FeedUrl, Rss(Enumerable.Range(1, 12).Select(n => Story($"d-{n}", $"Story {n}")).ToArray()));
        var feedId = await news.AddFeedAsync(FeedUrl);

        await TickAsync();

        Assert.Equal(10, (await news.ArticlesAsync(feedId)).Count);
        var state = await news.StateAsync(feedId);
        Assert.Equal(NewsFeedQuotaReason.DailyCap, state.QuotaReason);
        Assert.Null(state.LastBodySha256);

        // The hourly maintenance clears DailyCap after UTC midnight for a feed not fetched since.
        news.Advance(TimeSpan.FromHours(16));
        var changed = await news.Host.Services.GetRequiredService<NewsMaintenance>().RecomputeQuotaReasonsAsync(NewsSettings.Defaults with { FeedMaxArticlesPerDay = 10 }, CancellationToken.None);
        Assert.Equal(1, changed);
        Assert.Null((await news.StateAsync(feedId)).QuotaReason);

        // The items are admitted again the next day, though the body is unchanged (the validators were cleared).
        await NextFetchAsync(feedId);
        Assert.Equal(12, (await news.ArticlesAsync(feedId)).Count);
    }

    [Fact]
    public async Task AC30_below_the_news_headroom_nothing_is_written_and_the_job_holds_storage_unavailable()
    {
        news.Host.Space.Override = (NewsArticleWriter.HeadroomThresholdBytes - 1, 1L << 40);
        news.Server.Feed(FeedUrl, Rss(Story("a-1")));
        var feedId = await news.AddFeedAsync(FeedUrl);

        await TickAsync();

        Assert.Empty(await news.ArticlesAsync(feedId));
        Assert.Equal(0, await news.WithContextAsync(c => c.StoredObjects.CountAsync()));
        var held = await news.WithContextAsync(c => c.JobRuns.AsNoTracking().SingleAsync(r => r.Status == JobRunStatus.Held));
        Assert.Equal(JobHeldReason.StorageUnavailable, held.HeldReason);
        Assert.NotNull((await news.StateAsync(feedId)).LastSuccessAtUtc);

        // Room again: the next poll re-parses (the validators were cleared) and stores it.
        news.Host.Space.Override = (NewsArticleWriter.HeadroomThresholdBytes + 1, 1L << 40);
        await NextFetchAsync(feedId);
        Assert.Single(await news.ArticlesAsync(feedId));
    }

    [Fact]
    public void AC59_the_quota_reason_rule_prefers_text_quota_and_keeps_its_first_timestamp()
    {
        var state = new NewsFeedFetchState();
        var t0 = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.True(NewsFetchRules.ApplyQuotaReason(state, textQuotaReached: true, dailyCapReached: true, t0));
        Assert.Equal((NewsFeedQuotaReason.TextQuota, t0), (state.QuotaReason!.Value, state.QuotaReachedAtUtc!.Value));
        Assert.False(NewsFetchRules.ApplyQuotaReason(state, true, false, t0.AddHours(1)));
        Assert.Equal(t0, state.QuotaReachedAtUtc);
        Assert.True(NewsFetchRules.ApplyQuotaReason(state, false, true, t0.AddHours(2)));
        Assert.Equal((NewsFeedQuotaReason.DailyCap, t0.AddHours(2)), (state.QuotaReason!.Value, state.QuotaReachedAtUtc!.Value));
        Assert.True(NewsFetchRules.ApplyQuotaReason(state, false, false, t0.AddHours(3)));
        Assert.Null(state.QuotaReason);
        Assert.Null(state.QuotaReachedAtUtc);
    }

    // ── AC 25, AC 30: images ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AC25_images_are_stored_stripped_per_article_and_a_gif_is_rejected()
    {
        news.Server.Bytes("https://img.example.com/a.jpg", ContactImageFixtures.JpegWithExifAndComment(), "image/jpeg");
        news.Server.Bytes("https://img.example.com/b.gif", ContactImageFixtures.Gif(), "image/png");
        news.Server.Feed(FeedUrl, Rss(
            Story("i-1") with { Enclosure = "https://img.example.com/a.jpg" },
            Story("i-2") with { Enclosure = "https://img.example.com/a.jpg" },
            Story("i-3") with { Enclosure = "https://img.example.com/b.gif" }));
        var feedId = await news.AddFeedAsync(FeedUrl, downloadImages: true);

        await TickAsync();

        await news.WithContextAsync(async c =>
        {
            var images = await c.NewsArticleImages.AsNoTracking().ToListAsync();
            Assert.Equal(2, images.Count(i => i.State == NewsArticleImageState.Stored));
            Assert.Single(images, i => i.State == NewsArticleImageState.Rejected);
            var stored = await (from image in c.NewsArticleImages
                                join file in c.NewsArticleFiles on image.NewsArticleFileId equals file.NewsArticleFileId
                                join so in c.StoredObjects on file.StoredObjectId equals so.Id
                                select new { image.NewsArticleId, so.RelativePath, file.SizeBytes }).ToListAsync();
            // The same image in two articles is stored twice, once in each article folder.
            Assert.Equal(2, stored.Select(s => s.RelativePath).Distinct().Count());
            Assert.All(stored, s => Assert.Contains($"/{s.NewsArticleId:N}/", s.RelativePath, StringComparison.Ordinal));
            Assert.All(stored, s => Assert.EndsWith(".jpg", s.RelativePath, StringComparison.Ordinal));
            var totals = await c.NewsStorageTotals.AsNoTracking().SingleAsync();
            Assert.Equal(stored.Sum(s => s.SizeBytes), totals.ImageBytes);
            var state = await c.NewsFeedFetchStates.AsNoTracking().SingleAsync(s => s.NewsFeedId == feedId);
            Assert.Equal(stored.Sum(s => s.SizeBytes), state.ImageBytesToday);
            // Stripped: the stored bytes are the validator's output, smaller than what was sent.
            Assert.All(stored, s => Assert.True(s.SizeBytes < ContactImageFixtures.JpegWithExifAndComment().Length));
        });
    }

    [Fact]
    public async Task AC30_at_the_image_store_quota_images_are_deferred_and_nothing_is_deleted()
    {
        // The first tick's hourly recount would correct a planted total; it runs before the plant.
        await TickAsync();
        await news.WithContextAsync(async c =>
        {
            (await TotalsAsync(c)).ImageBytes = 50L << 30;
            await c.SaveChangesAsync();
        });
        news.Server.Bytes("https://img.example.com/a.jpg", ContactImageFixtures.BaselineJpeg(), "image/jpeg");
        news.Server.Feed(FeedUrl, Rss(Story("i-1") with { Enclosure = "https://img.example.com/a.jpg" }));
        await news.AddFeedAsync(FeedUrl, downloadImages: true);

        await TickAsync();

        var image = await news.WithContextAsync(c => c.NewsArticleImages.AsNoTracking().SingleAsync());
        Assert.Equal(NewsArticleImageState.Deferred, image.State);
        Assert.Equal(NewsImageDeferredReason.StoreQuota, image.DeferredReason);
        Assert.Equal(0, news.Server.CountFor("https://img.example.com/a.jpg"));
        Assert.Single(await news.WithContextAsync(c => c.NewsArticles.ToListAsync()));
    }

    [Fact]
    public async Task Images_beyond_the_per_article_cap_are_skipped()
    {
        await SetSettingAsync(SystemSettingsKeys.NewsImageMaxPerArticle, "1");
        news.Server.Feed(FeedUrl, Rss(Story("i-1", body: "<p><img src=\"https://img.example.com/1.jpg\"><img src=\"https://img.example.com/2.jpg\"></p>")));
        await news.AddFeedAsync(FeedUrl, downloadImages: true);

        await TickAsync();

        var states = await news.WithContextAsync(c => c.NewsArticleImages.OrderBy(i => i.Position).Select(i => i.State).ToListAsync());
        Assert.Equal(NewsArticleImageState.Skipped, states[1]);
    }

    // ── AC 54: image repair ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AC54_maintenance_requeues_a_missing_image_at_most_once_a_day()
    {
        news.Server.Bytes("https://img.example.com/a.jpg", ContactImageFixtures.BaselineJpeg(), "image/jpeg");
        news.Server.Feed(FeedUrl, Rss(Story("i-1") with { Enclosure = "https://img.example.com/a.jpg" }));
        await news.AddFeedAsync(FeedUrl, downloadImages: true);
        await TickAsync();
        var path = await news.WithContextAsync(c => (from image in c.NewsArticleImages
                                                     join file in c.NewsArticleFiles on image.NewsArticleFileId equals file.NewsArticleFileId
                                                     join so in c.StoredObjects on file.StoredObjectId equals so.Id
                                                     select so.RelativePath).SingleAsync());
        File.Delete(Path.Combine(volume.FullName, path));
        var maintenance = news.Host.Services.GetRequiredService<NewsMaintenance>();

        var (requeued, _) = await maintenance.RepairImagesAsync(CancellationToken.None);

        Assert.Equal(1, requeued);
        var repaired = await news.WithContextAsync(c => c.NewsArticleImages.AsNoTracking().SingleAsync());
        Assert.Equal(NewsArticleImageState.Pending, repaired.State);
        Assert.Null(repaired.NewsArticleFileId);
        Assert.Equal(0, await news.WithContextAsync(c => c.NewsArticleFiles.CountAsync(f => f.Kind == NewsArticleFileKind.Image)));
        Assert.Equal(1, await news.WithContextAsync(c => c.StoredObjects.CountAsync(o => o.State == StoredObjectState.PendingDelete)));

        // Downloaded again, then lost again within the day: not re-queued a second time.
        await news.Host.Services.GetRequiredService<NewsImageDownloader>().RunAsync(Guid.NewGuid(), NewsSettings.Defaults, CancellationToken.None);
        var path2 = await news.WithContextAsync(c => (from image in c.NewsArticleImages
                                                      join file in c.NewsArticleFiles on image.NewsArticleFileId equals file.NewsArticleFileId
                                                      join so in c.StoredObjects on file.StoredObjectId equals so.Id
                                                      select so.RelativePath).SingleAsync());
        File.Delete(Path.Combine(volume.FullName, path2));
        Assert.Equal(0, (await maintenance.RepairImagesAsync(CancellationToken.None)).Requeued);
    }

    // ── AC 10: the purge ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AC10_the_purge_removes_every_row_and_stages_a_release_for_every_file()
    {
        news.Server.Bytes("https://img.example.com/a.jpg", ContactImageFixtures.BaselineJpeg(), "image/jpeg");
        news.Server.Feed(FeedUrl, Rss(Story("p-1") with { Enclosure = "https://img.example.com/a.jpg" }, Story("p-2")));
        var feedId = await news.AddFeedAsync(FeedUrl, downloadImages: true);
        await TickAsync();
        var files = await news.WithContextAsync(c => c.NewsArticleFiles.CountAsync());
        Assert.Equal(5, files);
        await news.WithContextAsync(async c =>
        {
            var feed = await c.NewsFeeds.SingleAsync();
            feed.DeletionRequestedAtUtc = news.Clock.GetUtcNow().UtcDateTime;
            feed.ArchivedAtUtc = feed.DeletionRequestedAtUtc;
            await c.SaveChangesAsync();
        });

        await TickAsync();

        await news.WithContextAsync(async c =>
        {
            Assert.False(await c.NewsFeeds.AnyAsync());
            Assert.False(await c.NewsFeedFetchStates.AnyAsync());
            Assert.False(await c.NewsArticles.AnyAsync());
            Assert.False(await c.NewsArticleFiles.AnyAsync());
            Assert.False(await c.NewsArticleImages.AnyAsync());
            Assert.False(await c.NewsArticleIdentities.AnyAsync());
            Assert.Equal(files, await c.StoredObjects.CountAsync(o => o.State == StoredObjectState.PendingDelete));
            var totals = await c.NewsStorageTotals.AsNoTracking().SingleAsync();
            Assert.Equal((0L, 0L), (totals.ImageBytes, totals.TextAndSourceBytes));
        });
        _ = feedId;
    }

    // ── AC 41: buckets ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AC41_the_article_past_the_bucket_capacity_lands_in_bucket_two_and_later_files_share_its_folder()
    {
        await using var small = await NewsJobHarness.CreateAsync(UseDatabase, volume.FullName, news.Clock, news.Server, bucketCapacity: 2);
        news.Server.Feed(FeedUrl, Rss(Story("b-1"), Story("b-2"), Story("b-3")));
        var feedId = await small.AddFeedAsync(FeedUrl);

        await small.Job.RunOnceAsync(CancellationToken.None);

        var buckets = (await small.ArticlesAsync(feedId)).Select(a => a.Bucket).Order().ToList();
        Assert.Equal([1, 1, 2], buckets);
        var paths = await small.WithContextAsync(c => (from file in c.NewsArticleFiles
                                                       join article in c.NewsArticles on file.NewsArticleId equals article.NewsArticleId
                                                       join so in c.StoredObjects on file.StoredObjectId equals so.Id
                                                       select new { article.NewsArticleId, article.Bucket, so.RelativePath }).ToListAsync());
        Assert.All(paths, p => Assert.StartsWith($"news/{feedId:N}/{p.Bucket}/{p.NewsArticleId:N}/", p.RelativePath, StringComparison.Ordinal));
    }

    // ── AC 12, AC 13: identity through the ingestor ──────────────────────────────────────────────────

    [Fact]
    public async Task AC12_a_changed_guid_with_the_same_link_is_the_same_article_and_two_items_with_one_guid_are_both_stored()
    {
        news.Server.Feed(FeedUrl, Rss(Story("old-guid", link: "https://news.example.com/story")));
        var feedId = await news.AddFeedAsync(FeedUrl);
        await TickAsync();
        news.Server.Feed(FeedUrl, Rss(Story("new-guid", link: "https://news.example.com/story")));
        await NextFetchAsync(feedId);
        Assert.Single(await news.ArticlesAsync(feedId));

        news.Server.Feed(FeedUrl, Rss(Story("dup", "One", link: "https://news.example.com/x1"), Story("dup", "Two", link: "https://news.example.com/x2")));
        await NextFetchAsync(feedId);
        Assert.Equal(3, (await news.ArticlesAsync(feedId)).Count);
    }

    [Fact]
    public async Task AC12_a_guidless_short_item_with_no_link_is_stored_once_across_three_fetches()
    {
        var feedId = await news.AddFeedAsync(FeedUrl);
        for (var k = 0; k < 3; k++)
        {
            news.Server.Feed(FeedUrl, Rss($"Feed {k}", new Item(null, "Short", null, "<p>Tiny.</p>")));
            await NextFetchAsync(feedId);
        }

        Assert.Single(await news.ArticlesAsync(feedId));
    }

    // ── The manual run, the run log ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_manual_run_marks_eligible_feeds_due_and_finishes_with_feeds_marked()
    {
        news.Server.Feed(FeedUrl, Rss(Story("m-1")));
        var feedId = await news.AddFeedAsync(FeedUrl);
        await TickAsync();
        news.Advance(TimeSpan.FromMinutes(16));
        var runId = Guid.NewGuid();
        await news.WithContextAsync(async c =>
        {
            c.JobRuns.Add(new NewsFeedJobRun
            {
                JobRunId = runId, Trigger = JobRunTrigger.Manual, Status = JobRunStatus.Running,
                StartedAtUtc = news.Clock.GetUtcNow().UtcDateTime, LastActivityAtUtc = news.Clock.GetUtcNow().UtcDateTime,
            });
            await c.SaveChangesAsync();
        });
        var requests = news.Server.CountFor(FeedUrl);

        await TickAsync();

        var run = await news.WithContextAsync(c => c.Set<NewsFeedJobRun>().AsNoTracking().SingleAsync(r => r.JobRunId == runId));
        Assert.Equal(JobRunStatus.Succeeded, run.Status);
        Assert.Equal(1, run.FeedsMarked);
        // The same tick then fetched the feed under its lease, and the completion cleared the refresh.
        Assert.Equal(requests + 1, news.Server.CountFor(FeedUrl));
        Assert.Null((await news.StateAsync(feedId)).RefreshRequestedAtUtc);
    }

    [Fact]
    public async Task A_tick_with_work_records_a_burst_with_its_counts()
    {
        news.Server.Feed(FeedUrl, Rss(Story("r-1"), Story("r-2")));
        await news.AddFeedAsync(FeedUrl);

        await TickAsync();

        var burst = await news.WithContextAsync(c => c.Set<NewsFeedJobRun>().AsNoTracking().SingleAsync(r => r.Trigger == JobRunTrigger.Scheduled));
        Assert.Equal((1, 2), (burst.FeedsFetched, burst.ItemsStored));
        Assert.True(await news.WithContextAsync(c => c.JobHeartbeats.AnyAsync(h => h.JobType == JobType.NewsFeedFetch)));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────────

    private Task SetSettingAsync(string key, string value) =>
        news.WithContextAsync(async c =>
        {
            // The verifier's EnsureCreated seeded every HasData row, the nine news settings included.
            var row = await c.SystemSettings.FirstOrDefaultAsync(r => r.Key == key);
            if (row is null)
            {
                c.SystemSettings.Add(new SystemSetting { Key = key, Value = value });
            }
            else
            {
                row.Value = value;
            }

            await c.SaveChangesAsync();
        });

    /// <summary>The seeded totals row (HasData, created by the verifier's EnsureCreated).</summary>
    private static async Task<NewsStorageTotal> TotalsAsync(OdysseyContext context) =>
        await context.NewsStorageTotals.SingleAsync(t => t.Id == NewsStorageTotal.SingletonId);

    private Task<string> TextPathAsync(Guid articleId) =>
        news.WithContextAsync(async c =>
        {
            var storedId = await c.NewsArticleFiles.AsNoTracking()
                .Where(f => f.NewsArticleId == articleId && f.NewsArticleRevisionId == null && f.Kind == NewsArticleFileKind.PlainText)
                .Select(f => f.StoredObjectId).SingleAsync();
            var path = await c.StoredObjects.AsNoTracking().Where(o => o.Id == storedId).Select(o => o.RelativePath).SingleAsync();
            return Path.Combine(volume.FullName, path);
        });
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Context;
using Odyssey.Core.Internet.News.Jobs;
using Odyssey.Core.Internet.News.Podcasts;
using Odyssey.Core.Journal.Thumbnails;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Internet;
using Odyssey.Testing.Fixtures;
using Odyssey.Testing.News;
using Xunit;

namespace Odyssey.Core.Tests.Internet;

/// <summary>
/// Retention, the type-change cleanup, the purge and the recount for podcast data on EF InMemory (issue #556 §3.7,
/// §3.8, AC 22, 23, 25, 33, 34).
/// </summary>
public sealed class PodcastRetentionAndPurgeTests : IAsyncLifetime
{
    private const string FeedUrl = "https://show.example.org/podcast.xml";
    private const string Audio = "https://cdn.example.org/1.mp3";
    private const string Cover = "https://show.example.org/cover.jpg";

    private static readonly InMemoryDatabaseRoot DatabaseRoot = new();

    private readonly string databaseName = $"podcast-retention-{Guid.NewGuid():N}";
    private readonly DirectoryInfo volume = Directory.CreateTempSubdirectory("odyssey-podcast-retention-");
    private NewsJobHarness news = null!;
    private Guid feedId;

    private void UseDatabase(DbContextOptionsBuilder<OdysseyContext> options) =>
        options.UseInMemoryDatabase(databaseName, DatabaseRoot).ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning));

    public async Task InitializeAsync() => news = await NewsJobHarness.CreateAsync(
        UseDatabase, volume.FullName, configureServices: services =>
        {
            services.AddSingleton<PodcastAudioDownloadJob>();
            services.AddSingleton<PodcastAudioRetentionJob>();
            services.AddSingleton<IPhotoThumbnailRenderer>(new Renderer());
            services.AddSingleton(new PhotoThumbnailProcessHooks { Exit = _ => { } });
        });

    private sealed class Renderer : IPhotoThumbnailRenderer
    {
        public ThumbnailRenderResult Render(ThumbnailRenderRequest request) =>
            ThumbnailRenderResult.Rendered(new ThumbnailRendition([1, 2, 3, 4], 400, 400), new ThumbnailRendition([1, 2], 200, 200));
    }

    public async Task DisposeAsync()
    {
        await news.DisposeAsync();
        volume.Delete(recursive: true);
    }

    private T Get<T>() where T : notnull => news.Host.Services.GetRequiredService<T>();

    private DateTime Now => news.Clock.GetUtcNow().UtcDateTime;

    private static string Show() => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <rss version="2.0" xmlns:itunes="http://www.itunes.com/dtds/podcast-1.0.dtd">
        <channel><title>The Show</title><link>https://show.example.org/</link><itunes:image href="{Cover}"/>
        <item><guid isPermaLink="false">ep-1</guid><title>Episode 1</title><description>Notes.</description>
        <pubDate>Sun, 25 Oct 2026 07:00:00 GMT</pubDate><enclosure url="{Audio}" type="audio/mpeg" length="10"/></item>
        </channel></rss>
        """;

    /// <summary>A podcast feed whose one episode has its audio stored and its artwork rendered.</summary>
    private async Task StoredAsync(bool autoDelete = true, int days = 30)
    {
        news.Server.Feed(FeedUrl, Show());
        news.Server.Bytes(Cover, ContactImageFixtures.BaselineJpeg(), "image/jpeg");
        news.Server.Bytes(Audio, PodcastAudioFixtures.Mp3([new(0, null, "Intro")]), "audio/mpeg");
        feedId = await news.AddFeedAsync(FeedUrl);
        await news.WithContextAsync(async c =>
        {
            var feed = await c.NewsFeeds.SingleAsync(f => f.NewsFeedId == feedId);
            feed.Kind = NewsFeedKind.Podcast;
            feed.AutoDeleteEnabled = autoDelete;
            feed.AutoDeleteAfterDays = days;
            await c.SaveChangesAsync();
        });
        await news.Job.RunOnceAsync(CancellationToken.None);
        await news.WithContextAsync(async c =>
            await new PodcastAudioRequests().RequestAsync(c, (await c.PodcastEpisodeAudio.SingleAsync()).NewsArticleId, Now, CancellationToken.None));
        await Get<PodcastAudioDownloadJob>().RunOnceAsync(CancellationToken.None);
        Assert.Equal(PodcastAudioState.Stored, (await AudioAsync()).State);
    }

    private Task<PodcastEpisodeAudio> AudioAsync() => news.WithContextAsync(c => c.PodcastEpisodeAudio.AsNoTracking().SingleAsync());

    private Task<NewsStorageTotal> TotalsAsync() => news.WithContextAsync(c => c.NewsStorageTotals.AsNoTracking().SingleAsync());

    private Task PlayedAsync(DateTime at) => news.WithContextAsync(async c =>
    {
        (await c.PodcastEpisodes.SingleAsync()).LastPlayedAtUtc = at;
        await c.SaveChangesAsync();
    });

    private Task<(int Released, long Bytes)> PassAsync() => Get<PodcastAudioRetentionJob>().PassAsync(CancellationToken.None);

    [Fact]
    public async Task AC22_Downloaded_audio_past_its_days_is_released_with_the_grace_and_the_episode_kept()
    {
        await StoredAsync();
        var size = (await AudioAsync()).SizeBytes!.Value;
        await PlayedAsync(Now);
        news.Advance(TimeSpan.FromDays(29));
        Assert.Equal(0, (await PassAsync()).Released);

        news.Advance(TimeSpan.FromDays(1));
        Assert.Equal((1, size), await PassAsync());

        var audio = await AudioAsync();
        Assert.Equal((PodcastAudioState.None, (Guid?)null), (audio.State, audio.StoredObjectId));
        var totals = await TotalsAsync();
        Assert.Equal((0L, size), (totals.AudioBytes, totals.AudioPendingReleaseBytes));
        await news.WithContextAsync(async c =>
        {
            Assert.NotNull((await c.PodcastEpisodes.SingleAsync()).LastPlayedAtUtc);
            Assert.True(await c.PodcastChapters.AnyAsync());
            // AC 23: graced, so the file is still on disk.
            Assert.Equal(StoredObjectState.PendingDelete, (await c.StoredObjects.SingleAsync(o => o.FeatureKey == "PodcastAudio")).State);
        });
    }

    [Fact]
    public async Task AC22_Audio_never_downloaded_or_stored_again_after_its_last_download_is_kept()
    {
        await StoredAsync();
        news.Advance(TimeSpan.FromDays(90));
        Assert.Equal(0, (await PassAsync()).Released);

        await PlayedAsync(Now - TimeSpan.FromDays(60) - TimeSpan.FromDays(60));
        Assert.Equal(0, (await PassAsync()).Released);
    }

    [Fact]
    public async Task AC22_Turning_auto_delete_on_starts_the_countdown_at_the_change()
    {
        await StoredAsync(autoDelete: false);
        await PlayedAsync(Now);
        news.Advance(TimeSpan.FromDays(60));
        await news.WithContextAsync(async c =>
        {
            var feed = await c.NewsFeeds.SingleAsync();
            feed.AutoDeleteEnabled = true;
            feed.AutoDeleteChangedAtUtc = Now;
            await c.SaveChangesAsync();
        });

        Assert.Equal(0, (await PassAsync()).Released);
        news.Advance(TimeSpan.FromDays(30));
        Assert.Equal(1, (await PassAsync()).Released);
    }

    [Fact]
    public async Task An_archived_feed_keeps_its_stored_audio()
    {
        await StoredAsync();
        await PlayedAsync(Now);
        await news.WithContextAsync(async c =>
        {
            (await c.NewsFeeds.SingleAsync()).ArchivedAtUtc = Now;
            await c.SaveChangesAsync();
        });
        news.Advance(TimeSpan.FromDays(60));

        Assert.Equal(0, (await PassAsync()).Released);
        Assert.Equal(0, await Get<NewsFeedPurger>().CleanUpAudioAsync(CancellationToken.None));
        Assert.Equal(PodcastAudioState.Stored, (await AudioAsync()).State);
    }

    [Fact]
    public async Task AC25_A_switch_to_article_releases_stored_audio_within_a_purge_tick()
    {
        await StoredAsync();
        var size = (await AudioAsync()).SizeBytes!.Value;
        await news.WithContextAsync(async c =>
        {
            (await c.NewsFeeds.SingleAsync()).Kind = NewsFeedKind.Article;
            await c.SaveChangesAsync();
        });

        await Get<NewsFeedPurger>().RunAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(PodcastAudioState.None, (await AudioAsync()).State);
        Assert.Equal((0L, size), ((await TotalsAsync()).AudioBytes, (await TotalsAsync()).AudioPendingReleaseBytes));
        Assert.Equal(NewsFeedKind.Article, await news.WithContextAsync(c => c.NewsArticles.Select(a => a.FeedKind).SingleAsync()));
    }

    [Fact]
    public async Task AC33_A_switch_to_podcast_marks_the_feeds_unread_articles_read()
    {
        news.Server.Feed(FeedUrl, Show());
        feedId = await news.AddFeedAsync(FeedUrl);
        await news.Job.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, await news.WithContextAsync(c => c.NewsFeedCounters.Select(x => x.UnreadCount).SingleAsync()));
        await news.WithContextAsync(async c =>
        {
            (await c.NewsFeeds.SingleAsync()).Kind = NewsFeedKind.Podcast;
            await c.SaveChangesAsync();
        });

        await Get<NewsFeedPurger>().RewriteFeedKindAsync(CancellationToken.None);

        await news.WithContextAsync(async c =>
        {
            var article = await c.NewsArticles.AsNoTracking().SingleAsync();
            Assert.Equal((NewsFeedKind.Podcast, true, Now), (article.FeedKind, article.IsRead, article.ReadAtUtc!.Value));
            Assert.Equal(0, await c.NewsFeedCounters.Select(x => x.UnreadCount).SingleAsync());
        });
    }

    [Fact]
    public async Task AC34_Deleting_a_podcast_feed_releases_its_audio_and_artwork_and_returns_the_totals()
    {
        await StoredAsync();
        Assert.True((await TotalsAsync()).ArtworkBytes > 0);
        await news.WithContextAsync(async c =>
        {
            (await c.NewsFeeds.SingleAsync()).DeletionRequestedAtUtc = Now;
            await c.SaveChangesAsync();
        });

        await news.Job.RunOnceAsync(CancellationToken.None);

        await news.WithContextAsync(async c =>
        {
            Assert.False(await c.NewsFeeds.AnyAsync());
            Assert.False(await c.PodcastEpisodes.AnyAsync());
            Assert.False(await c.PodcastEpisodeAudio.AnyAsync());
            Assert.False(await c.PodcastChapters.AnyAsync());
            Assert.False(await c.PodcastArtwork.AnyAsync());
            Assert.False(await c.PodcastArtworkFiles.AnyAsync());
            Assert.False(await c.PodcastFeedDetails.AnyAsync());
            Assert.All(await c.StoredObjects.Where(o => o.FeatureKey.StartsWith("Podcast")).ToListAsync(),
                o => Assert.Equal(StoredObjectState.PendingDelete, o.State));
        });
        var totals = await TotalsAsync();
        Assert.Equal((0L, 0L), (totals.AudioBytes, totals.ArtworkBytes));
    }

    [Fact]
    public async Task A_purge_waits_for_a_download_under_a_live_lease()
    {
        await StoredAsync();
        await news.WithContextAsync(async c =>
        {
            var audio = await c.PodcastEpisodeAudio.SingleAsync();
            audio.State = PodcastAudioState.Downloading;
            audio.LeaseOwner = Guid.NewGuid();
            audio.LeaseExpiresAtUtc = Now.AddMinutes(2);
            (await c.NewsFeeds.SingleAsync()).DeletionRequestedAtUtc = Now;
            await c.SaveChangesAsync();
        });

        await Get<NewsFeedPurger>().RunAsync(Guid.NewGuid(), CancellationToken.None);
        Assert.True(await news.WithContextAsync(c => c.NewsFeeds.AnyAsync()));

        news.Advance(TimeSpan.FromMinutes(3));
        await Get<NewsFeedPurger>().RunAsync(Guid.NewGuid(), CancellationToken.None);
        Assert.False(await news.WithContextAsync(c => c.NewsFeeds.AnyAsync()));
    }

    [Fact]
    public async Task The_recount_rebuilds_every_podcast_total()
    {
        await StoredAsync();
        var expected = await TotalsAsync();
        await news.WithContextAsync(async c =>
        {
            var totals = await c.NewsStorageTotals.SingleAsync();
            totals.AudioBytes = 1;
            totals.ArtworkBytes = 2;
            totals.AudioReservedBytes = 3;
            totals.AudioPendingReleaseBytes = 4;
            await c.SaveChangesAsync();
        });

        Assert.True(await Get<NewsMaintenance>().RecountTotalsAsync(CancellationToken.None));

        var recounted = await TotalsAsync();
        Assert.Equal((expected.AudioBytes, expected.ArtworkBytes, 0L, 0L),
            (recounted.AudioBytes, recounted.ArtworkBytes, recounted.AudioReservedBytes, recounted.AudioPendingReleaseBytes));
    }

    [Fact]
    public async Task A_manual_retention_run_is_claimed_and_finished_with_its_counts()
    {
        await StoredAsync();
        await PlayedAsync(Now);
        news.Advance(TimeSpan.FromDays(31));
        var runId = Guid.NewGuid();
        await news.WithContextAsync(async c =>
        {
            c.JobRuns.Add(new PodcastAudioRetentionJobRun
            {
                JobRunId = runId, Trigger = JobRunTrigger.Manual, Status = JobRunStatus.Running, StartedAtUtc = Now, LastActivityAtUtc = Now,
            });
            await c.SaveChangesAsync();
        });

        await Get<PodcastAudioRetentionJob>().RunOnceAsync(CancellationToken.None);

        var run = await news.WithContextAsync(c => c.Set<PodcastAudioRetentionJobRun>().AsNoTracking().SingleAsync(r => r.JobRunId == runId));
        Assert.Equal((JobRunStatus.Succeeded, 1), (run.Status, run.AudioReleased));
    }
}

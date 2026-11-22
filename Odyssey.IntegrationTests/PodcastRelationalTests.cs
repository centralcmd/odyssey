// EF1002 flags interpolation into ExecuteSqlRawAsync. Every value interpolated below is a Guid this test generated.
#pragma warning disable EF1002

using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Odyssey.Context;
using Odyssey.Core.FileStorage;
using Odyssey.Core.Internet.News;
using Odyssey.Core.Internet.News.Jobs;
using Odyssey.Core.Internet.News.Podcasts;
using Odyssey.Core.Internet.News.Reading;
using Odyssey.Core.Journal.Thumbnails;
using Odyssey.Dtos.Internet;
using Odyssey.Testing.FileStorage;
using Odyssey.Testing.Fixtures;
using Odyssey.Testing.News;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// Podcasts on real MariaDB (issue #556 §3.6, §3.8, §4, AC 19, 34, 35): the generated download guards that keep one
/// download per feed and per host across replicas, the reservation's <c>FOR UPDATE</c>, the relative counter updates,
/// the purge against the RESTRICT keys to <c>StoredObjects</c>, the retention predicate's date arithmetic, and the
/// episode list's query.
/// </summary>
[Collection(MariaDbSharedDatabaseCollection.Name)]
public sealed class PodcastRelationalTests(MariaDbFixture fixture) : IAsyncDisposable
{
    private const string FeedUrl = "https://show.example.org/podcast.xml";
    private const string Audio = "https://cdn.example.org/1.mp3";
    private const string Cover = "https://show.example.org/cover.jpg";
    private const int DuplicateKey = 1062;

    private readonly DirectoryInfo volume = Directory.CreateTempSubdirectory("odyssey-it-podcast-");
    private readonly TestClock clock = new();
    private readonly List<NewsJobHarness> extra = [];
    private NewsJobHarness? news;
    private string connection = string.Empty;

    public async ValueTask DisposeAsync()
    {
        if (news is not null)
        {
            await news.DisposeAsync();
        }

        foreach (var harness in extra)
        {
            await harness.DisposeAsync();
        }

        volume.Delete(recursive: true);
    }

    private async Task<NewsJobHarness> HarnessAsync()
    {
        connection = await fixture.CreateMigratedDatabaseAsync($"odyssey_podcast_{Guid.NewGuid():N}"[..40]);
        news = await NewsJobHarness.CreateAsync(
            options => options.UseMySql(connection, ServerVersion.AutoDetect(connection), OdysseyDatabaseRetry.Configure),
            volume.FullName, clock, configureServices: services =>
            {
                services.AddSingleton<PodcastAudioDownloadJob>();
                services.AddSingleton<PodcastAudioRetentionJob>();
                services.AddSingleton<IPhotoThumbnailRenderer>(new Renderer());
                services.AddSingleton(new PhotoThumbnailProcessHooks { Exit = _ => { } });
            });
        return news;
    }

    /// <summary>A second worker replica over the same database and the same fake publishers.</summary>
    private async Task<NewsJobHarness> ReplicaAsync(NewsJobHarness first)
    {
        var replica = await NewsJobHarness.CreateAsync(
            options => options.UseMySql(connection, ServerVersion.AutoDetect(connection), OdysseyDatabaseRetry.Configure),
            volume.FullName, clock, first.Server, configureServices: services => services.AddSingleton<PodcastAudioDownloadJob>());
        extra.Add(replica);
        return replica;
    }

    private sealed class Renderer : IPhotoThumbnailRenderer
    {
        public ThumbnailRenderResult Render(ThumbnailRenderRequest request) =>
            ThumbnailRenderResult.Rendered(new ThumbnailRendition([1, 2, 3, 4], 400, 400), new ThumbnailRendition([1, 2], 200, 200));
    }

    private static string Show(params string[] audio) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <rss version="2.0" xmlns:itunes="http://www.itunes.com/dtds/podcast-1.0.dtd">
        <channel><title>The Show</title><link>https://show.example.org/</link><itunes:image href="{Cover}"/>
        {string.Concat(audio.Select((url, i) => $"""
            <item><guid isPermaLink="false">ep-{i}</guid><title>Episode {i}</title><description>Notes.</description>
            <pubDate>Sun, 2{i} Oct 2026 07:00:00 GMT</pubDate><enclosure url="{url}" type="audio/mpeg" length="10"/></item>
            """))}
        </channel></rss>
        """;

    private async Task<Guid> PodcastAsync(NewsJobHarness harness, params string[] audio)
    {
        harness.Server.Feed(FeedUrl, Show(audio));
        harness.Server.Bytes(Cover, ContactImageFixtures.BaselineJpeg(), "image/jpeg");
        var feedId = await harness.AddFeedAsync(FeedUrl);
        await harness.WithContextAsync(c => c.Database.ExecuteSqlRawAsync(
            $"UPDATE `NewsFeeds` SET `Kind` = 1, `AutoDeleteEnabled` = 1, `AutoDeleteAfterDays` = 30 WHERE `NewsFeedId` = '{feedId}'"));
        await harness.Job.RunOnceAsync(CancellationToken.None);
        return feedId;
    }

    private Task QueueAllAsync(NewsJobHarness harness) =>
        harness.WithContextAsync(async c =>
        {
            var requests = new PodcastAudioRequests();
            foreach (var id in await c.PodcastEpisodeAudio.Select(a => a.NewsArticleId).ToListAsync())
            {
                await requests.RequestAsync(c, id, clock.GetUtcNow().UtcDateTime, CancellationToken.None);
            }
        });

    /// <summary>AC 16, 19 on MariaDB: the reservation under its lock, the store write, the relative counters.</summary>
    [SkippableFact]
    public async Task A_download_reserves_stores_and_counts_its_bytes_through_the_relational_paths()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var harness = await HarnessAsync();
        var file = PodcastAudioFixtures.Mp3([new(0, null, "Intro"), new(5_000, null, "Main")]);
        harness.Server.Bytes(Audio, file, "audio/mpeg");
        await PodcastAsync(harness, Audio);
        await QueueAllAsync(harness);

        await harness.Host.Services.GetRequiredService<PodcastAudioDownloadJob>().RunOnceAsync(CancellationToken.None);

        await harness.WithContextAsync(async c =>
        {
            var audio = await c.PodcastEpisodeAudio.AsNoTracking().SingleAsync();
            Assert.Equal((PodcastAudioState.Stored, (long?)file.Length, 0L), (audio.State, audio.SizeBytes, audio.ReservedBytes));
            Assert.Null(audio.FeedDownloadGuard);
            var totals = await c.NewsStorageTotals.AsNoTracking().SingleAsync();
            Assert.Equal((file.LongLength, 0L), (totals.AudioBytes, totals.AudioReservedBytes));
            Assert.Equal(2, await c.PodcastChapters.CountAsync(ch => ch.Source == PodcastChapterSource.AudioFile));
        });
    }

    /// <summary>§3.6 step 2 (arch R3): the generated guards refuse a second live download of one feed or one host.</summary>
    [SkippableFact]
    public async Task The_generated_guards_allow_one_download_per_feed_and_per_host()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var harness = await HarnessAsync();
        await PodcastAsync(harness, Audio, "https://cdn.example.org/2.mp3");
        var ids = await harness.WithContextAsync(c => c.PodcastEpisodeAudio.Select(a => a.NewsArticleId).ToListAsync());

        await harness.WithContextAsync(c => c.Database.ExecuteSqlRawAsync(
            $"UPDATE `PodcastEpisodeAudio` SET `State` = 2 WHERE `NewsArticleId` = '{ids[0]}'"));
        var second = await Assert.ThrowsAsync<MySqlException>(() => harness.WithContextAsync(c => c.Database.ExecuteSqlRawAsync(
            $"UPDATE `PodcastEpisodeAudio` SET `State` = 2 WHERE `NewsArticleId` = '{ids[1]}'")));
        Assert.Equal(DuplicateKey, second.Number);

        // Another feed on the same host is refused by the host guard; freeing the first lets it through.
        await harness.WithContextAsync(c => c.Database.ExecuteSqlRawAsync(
            $"UPDATE `PodcastEpisodeAudio` SET `State` = 1 WHERE `NewsArticleId` = '{ids[0]}'"));
        Assert.Equal(1, await harness.WithContextAsync(c => c.Database.ExecuteSqlRawAsync(
            $"UPDATE `PodcastEpisodeAudio` SET `State` = 2 WHERE `NewsArticleId` = '{ids[1]}'")));
    }

    /// <summary>AC 34 on MariaDB: the purge releases audio and artwork through the RESTRICT keys and zeroes the totals.</summary>
    [SkippableFact]
    public async Task Deleting_a_podcast_feed_purges_its_rows_and_releases_its_files()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var harness = await HarnessAsync();
        harness.Server.Bytes(Audio, PodcastAudioFixtures.Mp3(), "audio/mpeg");
        var feedId = await PodcastAsync(harness, Audio);
        await QueueAllAsync(harness);
        await harness.Host.Services.GetRequiredService<PodcastAudioDownloadJob>().RunOnceAsync(CancellationToken.None);
        await harness.WithContextAsync(c => c.Database.ExecuteSqlRawAsync(
            $"UPDATE `NewsFeeds` SET `DeletionRequestedAtUtc` = NOW() WHERE `NewsFeedId` = '{feedId}'"));

        await harness.Job.RunOnceAsync(CancellationToken.None);

        await harness.WithContextAsync(async c =>
        {
            Assert.False(await c.NewsFeeds.AnyAsync());
            Assert.False(await c.PodcastEpisodeAudio.AnyAsync());
            Assert.False(await c.PodcastArtworkFiles.AnyAsync());
            var released = await c.StoredObjects.Where(o => o.FeatureKey.StartsWith("Podcast")).ToListAsync();
            Assert.NotEmpty(released);
            Assert.All(released, o => Assert.Equal(StoredObjectState.PendingDelete, o.State));
            var totals = await c.NewsStorageTotals.AsNoTracking().SingleAsync();
            Assert.Equal((0L, 0L), (totals.AudioBytes, totals.ArtworkBytes));
            Assert.True(totals.AudioPendingReleaseBytes > 0);
        });
    }

    /// <summary>AC 22 on MariaDB: the retention predicate's date arithmetic translates and releases the due row.</summary>
    [SkippableFact]
    public async Task Retention_releases_audio_past_its_countdown()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var harness = await HarnessAsync();
        harness.Server.Bytes(Audio, PodcastAudioFixtures.Mp3(), "audio/mpeg");
        await PodcastAsync(harness, Audio);
        await QueueAllAsync(harness);
        await harness.Host.Services.GetRequiredService<PodcastAudioDownloadJob>().RunOnceAsync(CancellationToken.None);
        var played = clock.GetUtcNow().UtcDateTime;
        await harness.WithContextAsync(c => c.Database.ExecuteSqlInterpolatedAsync($"UPDATE `PodcastEpisodes` SET `LastPlayedAtUtc` = {played}"));
        var retention = harness.Host.Services.GetRequiredService<PodcastAudioRetentionJob>();

        clock.Advance(TimeSpan.FromDays(29));
        Assert.Equal(0, (await retention.PassAsync(CancellationToken.None)).Released);
        clock.Advance(TimeSpan.FromDays(2));
        Assert.Equal(1, (await retention.PassAsync(CancellationToken.None)).Released);
        Assert.Equal(PodcastAudioState.None, await harness.WithContextAsync(c => c.PodcastEpisodeAudio.Select(a => a.State).SingleAsync()));
    }

    /// <summary>§5.2 #6 on MariaDB: the list's joins, the audio-state filter and the keyset translate.</summary>
    [SkippableFact]
    public async Task The_episode_list_filters_and_pages_on_mariadb()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var harness = await HarnessAsync();
        await PodcastAsync(harness, Audio, "https://cdn.example.org/2.mp3", "https://cdn.example.org/3.mp3");
        await using var scope = harness.Scope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var volumes = scope.ServiceProvider.GetRequiredService<FileStorageVolumeRegistry>();
        var protection = DataProtectionProvider.Create("odyssey-it-podcasts");
        var search = new NewsArticleSearch(context);
        var articles = new NewsArticleService(context, search, new NewsListCursor(protection), new NewsCounterWriter(), null!, volumes,
            clock, NullLogger<NewsArticleService>.Instance);
        var service = new PodcastEpisodeService(context, articles, search, new PodcastEpisodeCursor(protection), new PodcastAudioRequests(),
            new NewsCounterWriter(), scope.ServiceProvider.GetRequiredService<INewsSettingsLookup>(), null!, volumes, clock,
            NullLogger<PodcastEpisodeService>.Instance);

        var first = await service.ListAsync(new PodcastEpisodesQueryParams { Limit = 2 }, CancellationToken.None);
        var second = await service.ListAsync(new PodcastEpisodesQueryParams { Limit = 2, Cursor = first.NextCursor }, CancellationToken.None);
        var none = await service.ListAsync(new PodcastEpisodesQueryParams { AudioState = [PodcastAudioState.Stored] }, CancellationToken.None);

        Assert.Equal(3, first.TotalCount);
        Assert.Equal(3, first.Items.Concat(second.Items).Select(i => i.Id).Distinct().Count());
        Assert.True(first.Items[0].SortAtUtc >= first.Items[1].SortAtUtc);
        Assert.Empty(none.Items);
    }

    /// <summary>AC 17 on MariaDB: two replicas running at once never download two episodes of one feed together.</summary>
    [SkippableFact]
    public async Task Two_replicas_running_at_once_download_one_episode_of_a_feed_at_a_time()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var first = await HarnessAsync();
        var second = await ReplicaAsync(first);
        first.Server.Bytes(Audio, PodcastAudioFixtures.Mp3(), "audio/mpeg");
        first.Server.Bytes("https://cdn.example.org/2.mp3", PodcastAudioFixtures.Mp3(), "audio/mpeg");
        await PodcastAsync(first, Audio, "https://cdn.example.org/2.mp3");
        await QueueAllAsync(first);

        await Task.WhenAll(
            first.Host.Services.GetRequiredService<PodcastAudioDownloadJob>().RunOnceAsync(CancellationToken.None),
            second.Host.Services.GetRequiredService<PodcastAudioDownloadJob>().RunOnceAsync(CancellationToken.None));

        var states = await first.WithContextAsync(c => c.PodcastEpisodeAudio.Select(a => a.State).ToListAsync());
        Assert.Equal(1, states.Count(s => s == PodcastAudioState.Stored));
        Assert.Equal(1, states.Count(s => s == PodcastAudioState.Queued));
    }

    /// <summary>AC 19 on MariaDB: two replicas whose downloads would together cross the quota — the lock admits one.</summary>
    [SkippableFact]
    public async Task The_reservation_lock_admits_one_of_two_downloads_that_together_cross_the_quota()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var first = await HarnessAsync();
        var file = PodcastAudioFixtures.Mp3(frames: 64);
        const string Other = "https://other.example.net/podcast.xml";
        const string OtherAudio = "https://media.example.net/1.mp3";
        first.Server.Bytes(Audio, file, "audio/mpeg");
        first.Server.Bytes(OtherAudio, file, "audio/mpeg");
        await first.WithContextAsync(c =>
            c.Database.ExecuteSqlRawAsync("UPDATE `SystemSettings` SET `Value` = '1' WHERE `Key` = 'PodcastAudioStoreMaxGiB'"));
        var second = await ReplicaAsync(first);
        await PodcastAsync(first, Audio);
        first.Server.Feed(Other, Show(OtherAudio));
        var otherFeed = await first.AddFeedAsync(Other);
        await first.WithContextAsync(c => c.Database.ExecuteSqlRawAsync($"UPDATE `NewsFeeds` SET `Kind` = 1 WHERE `NewsFeedId` = '{otherFeed}'"));
        await first.Job.RunOnceAsync(CancellationToken.None);
        await QueueAllAsync(first);
        // After the news ticks, whose maintenance recounts the totals: room for one file and a half, so both downloads
        // pass every check alone, not together.
        await first.WithContextAsync(c =>
            c.Database.ExecuteSqlRawAsync($"UPDATE `NewsStorageTotals` SET `AudioBytes` = {(1L << 30) - (file.Length * 3 / 2)}"));

        await Task.WhenAll(
            first.Host.Services.GetRequiredService<PodcastAudioDownloadJob>().RunOnceAsync(CancellationToken.None),
            second.Host.Services.GetRequiredService<PodcastAudioDownloadJob>().RunOnceAsync(CancellationToken.None));

        await first.WithContextAsync(async c =>
        {
            var rows = await c.PodcastEpisodeAudio.AsNoTracking().ToListAsync();
            Assert.Equal(2, rows.Count);
            Assert.Equal(1, rows.Count(r => r.State == PodcastAudioState.Stored));
            Assert.Equal(1, rows.Count(r => r.State == PodcastAudioState.Deferred && r.DeferredReason == PodcastAudioDeferredReason.AudioQuota));
            Assert.Equal(0L, (await c.NewsStorageTotals.AsNoTracking().SingleAsync()).AudioReservedBytes);
        });
    }
}

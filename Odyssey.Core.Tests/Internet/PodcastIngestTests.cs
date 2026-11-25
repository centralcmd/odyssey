using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Odyssey.Context;
using Odyssey.Dtos.Internet;
using Odyssey.Testing.News;
using Xunit;

namespace Odyssey.Core.Tests.Internet;

/// <summary>
/// The episode step of ingest end to end on EF InMemory (issue #556 §3.4): episodes, their audio rows and chapters,
/// podcast articles born read, artwork de-duplicated per feed, the baseline and auto-download's caps.
/// </summary>
public sealed class PodcastIngestTests : IAsyncLifetime
{
    private const string FeedUrl = "https://show.example.org/podcast.xml";

    private static readonly InMemoryDatabaseRoot DatabaseRoot = new();

    private readonly string databaseName = $"podcast-ingest-{Guid.NewGuid():N}";
    private readonly DirectoryInfo volume = Directory.CreateTempSubdirectory("odyssey-podcast-");
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

    private async Task NextFetchAsync(Guid feedId)
    {
        var state = await news.StateAsync(feedId);
        var wait = state.NextFetchAtUtc - news.Clock.GetUtcNow().UtcDateTime;
        news.Advance((wait > TimeSpan.Zero ? wait : TimeSpan.Zero) + TimeSpan.FromSeconds(1));
        await TickAsync();
    }

    private Task<Guid> PodcastAsync(bool autoDownload = false, NewsFeedKind kind = NewsFeedKind.Podcast) =>
        news.AddFeedAsync(FeedUrl).ContinueWith(async created =>
        {
            var id = await created;
            await news.WithContextAsync(async c =>
            {
                var feed = await c.NewsFeeds.SingleAsync(f => f.NewsFeedId == id);
                feed.Kind = kind;
                feed.AutoDownload = autoDownload;
                await c.SaveChangesAsync();
            });
            return id;
        }).Unwrap();

    private Task SetSettingAsync(string key, string value) =>
        news.WithContextAsync(async c =>
        {
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

    private static string Show(params int[] episodes) => Show(episodes.Select(n => (n, (string?)$"https://cdn.example.org/ep{n}.mp3")).ToArray());

    private static string Show(params (int Number, string? Audio)[] episodes)
    {
        var builder = new StringBuilder("""
            <?xml version="1.0" encoding="utf-8"?>
            <rss version="2.0" xmlns:itunes="http://www.itunes.com/dtds/podcast-1.0.dtd" xmlns:psc="http://podlove.org/simple-chapters">
            <channel><title>The Show</title><link>https://show.example.org/</link>
            <itunes:author>Jane Host</itunes:author><itunes:image href="https://show.example.org/cover.jpg"/>
            """);
        foreach (var (number, audio) in episodes)
        {
            var date = new DateTime(2026, 10, 1, 7, 0, 0, DateTimeKind.Utc).AddDays(number).ToString("r", CultureInfo.InvariantCulture);
            builder.Append(CultureInfo.InvariantCulture, $"<item><guid isPermaLink=\"false\">ep-{number}</guid><title>Episode {number}</title>");
            builder.Append(CultureInfo.InvariantCulture, $"<description>Notes for {number}.</description><pubDate>{date}</pubDate>");
            if (audio is not null)
            {
                builder.Append(CultureInfo.InvariantCulture, $"<enclosure url=\"{audio}\" type=\"audio/mpeg\" length=\"1000\"/>");
                builder.Append(CultureInfo.InvariantCulture, $"<itunes:duration>{number}:00</itunes:duration>");
                builder.Append("<itunes:image href=\"https://show.example.org/cover.jpg\"/>");
                builder.Append("<psc:chapters><psc:chapter start=\"00:00:00\" title=\"Intro\"/><psc:chapter start=\"00:01:00\" title=\"Main\"/></psc:chapters>");
            }

            builder.Append("</item>");
        }

        return builder.Append("</channel></rss>").ToString();
    }

    [Fact]
    public async Task AC3_A_podcast_fetch_stores_episodes_born_read_with_chapters_and_one_shared_artwork()
    {
        news.Server.Feed(FeedUrl, Show(1, 2));
        var feedId = await PodcastAsync();

        await TickAsync();

        await news.WithContextAsync(async c =>
        {
            var articles = await c.NewsArticles.AsNoTracking().Where(a => a.NewsFeedId == feedId).ToListAsync();
            Assert.Equal(2, articles.Count);
            Assert.All(articles, a => Assert.True(a.IsRead && a.ReadAtUtc is not null && a.FeedKind == NewsFeedKind.Podcast));
            Assert.Equal(0, (await c.NewsFeedCounters.SingleAsync(x => x.NewsFeedId == feedId)).UnreadCount);

            var episodes = await c.PodcastEpisodes.AsNoTracking().Where(e => e.NewsFeedId == feedId).ToListAsync();
            Assert.Equal([60, 120], episodes.Select(e => e.DurationSeconds ?? 0).Order());
            Assert.All(await c.PodcastEpisodeAudio.AsNoTracking().ToListAsync(), a => Assert.Equal(PodcastAudioState.None, a.State));
            Assert.Equal(4, await c.PodcastChapters.CountAsync());

            var details = await c.PodcastFeedDetails.AsNoTracking().SingleAsync(d => d.NewsFeedId == feedId);
            var artwork = await c.PodcastArtwork.AsNoTracking().SingleAsync();
            Assert.Equal("Jane Host", details.Author);
            Assert.Equal(artwork.PodcastArtworkId, details.ArtworkId);
            Assert.All(episodes, e => Assert.Equal(artwork.PodcastArtworkId, e.ArtworkId));
            Assert.NotNull(details.BaselineAtUtc);
        });
    }

    [Fact]
    public async Task AC9_The_first_full_fetch_queues_nothing_and_a_later_new_episode_is_queued_up_to_the_daily_cap()
    {
        news.Server.Feed(FeedUrl, Show(1, 2));
        var feedId = await PodcastAsync(autoDownload: true);
        await TickAsync();
        Assert.Equal(0, await news.WithContextAsync(c => c.PodcastEpisodeAudio.CountAsync(a => a.State == PodcastAudioState.Queued)));

        news.Server.Feed(FeedUrl, Show(1, 2, 3, 4, 5, 6, 7, 8));
        await NextFetchAsync(feedId);

        await news.WithContextAsync(async c =>
        {
            var queued = await c.PodcastEpisodeAudio.AsNoTracking().Where(a => a.State == PodcastAudioState.Queued).ToListAsync();
            // The default unfetched cap (5) and daily cap (5) both bind at five.
            Assert.Equal(5, queued.Count);
            Assert.All(queued, a => Assert.Equal(PodcastAudioRequestedBy.Auto, a.RequestedBy));
            Assert.Equal(5, (await c.PodcastFeedDetails.SingleAsync(d => d.NewsFeedId == feedId)).AutoDownloadsToday);
        });
    }

    [Fact]
    public async Task AC10_With_the_unfetched_cap_reached_no_new_episode_is_queued()
    {
        await SetSettingAsync(SystemSettingsKeys.PodcastAutoDownloadMaxUnfetchedPerFeed, "1");
        news.Server.Feed(FeedUrl, Show(1));
        var feedId = await PodcastAsync(autoDownload: true);
        await TickAsync();

        news.Server.Feed(FeedUrl, Show(1, 2, 3));
        await NextFetchAsync(feedId);

        Assert.Equal(1, await news.WithContextAsync(c => c.PodcastEpisodeAudio.CountAsync(a => a.State == PodcastAudioState.Queued)));
    }

    /// <summary>
    /// Issue #593 AC 15: the unfetched cap counts an auto download as consumed once a finished in-browser listen made
    /// after it was stored is on the episode — exactly like a device download.
    /// </summary>
    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public async Task Playback_AC15_A_listened_auto_download_frees_its_unfetched_slot(bool listened, int queuedAfter)
    {
        await SetSettingAsync(SystemSettingsKeys.PodcastAutoDownloadMaxUnfetchedPerFeed, "1");
        news.Server.Feed(FeedUrl, Show(1));
        var feedId = await PodcastAsync(autoDownload: true);
        await TickAsync();
        var stored = news.Clock.GetUtcNow().UtcDateTime;
        await news.WithContextAsync(async c =>
        {
            var audio = await c.PodcastEpisodeAudio.SingleAsync();
            // The first fetch only sets the baseline, so the stored copy is made an automatic one by hand.
            audio.State = PodcastAudioState.Stored;
            audio.RequestedBy = PodcastAudioRequestedBy.Auto;
            audio.StoredAtUtc = stored;
            if (listened)
            {
                (await c.PodcastEpisodes.SingleAsync(e => e.NewsArticleId == audio.NewsArticleId)).LastListenedAtUtc = stored.AddMinutes(1);
            }

            await c.SaveChangesAsync();
        });

        news.Server.Feed(FeedUrl, Show(1, 2));
        await NextFetchAsync(feedId);

        Assert.Equal(queuedAfter, await news.WithContextAsync(c => c.PodcastEpisodeAudio.CountAsync(a => a.State == PodcastAudioState.Queued)));
    }

    [Fact]
    public async Task AC9_A_first_fetch_cut_short_by_the_daily_article_cap_sets_no_baseline_so_its_leftovers_are_not_queued()
    {
        await SetSettingAsync(SystemSettingsKeys.NewsFeedMaxArticlesPerDay, "10");
        news.Server.Feed(FeedUrl, Show(Enumerable.Range(1, 12).ToArray()));
        var feedId = await PodcastAsync(autoDownload: true);

        await TickAsync();
        Assert.Null(await news.WithContextAsync(async c => (await c.PodcastFeedDetails.SingleAsync(d => d.NewsFeedId == feedId)).BaselineAtUtc));

        // The next UTC day: the two leftovers are ingested by a full parse, which stamps the baseline but queues neither.
        news.Advance(TimeSpan.FromDays(1));
        await NextFetchAsync(feedId);

        await news.WithContextAsync(async c =>
        {
            Assert.Equal(12, await c.PodcastEpisodes.CountAsync(e => e.NewsFeedId == feedId));
            Assert.NotNull((await c.PodcastFeedDetails.SingleAsync(d => d.NewsFeedId == feedId)).BaselineAtUtc);
            Assert.Equal(0, await c.PodcastEpisodeAudio.CountAsync(a => a.State == PodcastAudioState.Queued));
        });
    }

    [Fact]
    public async Task AC8_An_article_feed_switched_to_podcasts_gains_episodes_for_its_existing_items_and_queues_none()
    {
        news.Server.Feed(FeedUrl, Show(1, 2));
        var feedId = await PodcastAsync(kind: NewsFeedKind.Article);
        await TickAsync();
        Assert.Equal(0, await news.WithContextAsync(c => c.PodcastEpisodes.CountAsync()));

        await news.WithContextAsync(async c =>
        {
            var feed = await c.NewsFeeds.SingleAsync(f => f.NewsFeedId == feedId);
            feed.Kind = NewsFeedKind.Podcast;
            feed.AutoDownload = true;
            feed.KindChangedAtUtc = news.Clock.GetUtcNow().UtcDateTime;
            (await c.NewsFeedFetchStates.SingleAsync(s => s.NewsFeedId == feedId)).ValidatorsResetRequestedAtUtc = feed.KindChangedAtUtc;
            await c.SaveChangesAsync();
        });
        await NextFetchAsync(feedId);

        await news.WithContextAsync(async c =>
        {
            Assert.Equal(2, await c.PodcastEpisodes.CountAsync(e => e.NewsFeedId == feedId));
            Assert.Equal(0, await c.PodcastEpisodeAudio.CountAsync(a => a.State == PodcastAudioState.Queued));
        });
    }

    [Fact]
    public async Task AC5_An_item_without_audio_on_a_podcast_feed_is_stored_with_no_episode_row()
    {
        news.Server.Feed(FeedUrl, Show((1, "https://cdn.example.org/1.mp3"), (2, null)));
        var feedId = await PodcastAsync();

        await TickAsync();

        await news.WithContextAsync(async c =>
        {
            Assert.Equal(2, await c.NewsArticles.CountAsync(a => a.NewsFeedId == feedId));
            Assert.Equal(1, await c.PodcastEpisodes.CountAsync(e => e.NewsFeedId == feedId));
        });
    }

    [Fact]
    public async Task An_episode_whose_own_fields_change_is_updated_in_place_and_its_chapters_are_rewritten_without_a_duplicate()
    {
        news.Server.Feed(FeedUrl, Show(1));
        var feedId = await PodcastAsync();
        await TickAsync();

        news.Server.Feed(FeedUrl, Show(1).Replace("<itunes:duration>1:00</itunes:duration>", "<itunes:duration>9:00</itunes:duration>")
            .Replace("title=\"Main\"/>", "title=\"Main\"/><psc:chapter start=\"00:02:00\" title=\"Outro\"/>"));
        await NextFetchAsync(feedId);

        await news.WithContextAsync(async c =>
        {
            var episode = await c.PodcastEpisodes.AsNoTracking().SingleAsync(e => e.NewsFeedId == feedId);
            Assert.Equal(540, episode.DurationSeconds);
            Assert.Equal(["Intro", "Main", "Outro"],
                await c.PodcastChapters.Where(ch => ch.NewsArticleId == episode.NewsArticleId).OrderBy(ch => ch.Position).Select(ch => ch.Title).ToListAsync());
            Assert.Equal(1, await c.PodcastEpisodeAudio.CountAsync());
        });
    }

    [Fact]
    public async Task An_article_feed_is_untouched_by_the_podcast_step()
    {
        news.Server.Feed(FeedUrl, Show(1));
        var feedId = await PodcastAsync(kind: NewsFeedKind.Article);

        await TickAsync();

        await news.WithContextAsync(async c =>
        {
            var article = await c.NewsArticles.AsNoTracking().SingleAsync(a => a.NewsFeedId == feedId);
            Assert.False(article.IsRead);
            Assert.Equal(NewsFeedKind.Article, article.FeedKind);
            Assert.Equal(0, await c.PodcastFeedDetails.CountAsync());
        });
    }
}

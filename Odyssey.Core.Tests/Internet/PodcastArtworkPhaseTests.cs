using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Context;
using Odyssey.Core.Internet.News.Podcasts;
using Odyssey.Core.Journal.Thumbnails;
using Odyssey.Dtos.Internet;
using Odyssey.Testing.Fixtures;
using Odyssey.Testing.News;
using Xunit;

namespace Odyssey.Core.Tests.Internet;

/// <summary>
/// The artwork and chapter-file phases of the news job (issue #556 §3.4) on EF InMemory: renditions stored with the
/// original, the per-feed incident rule on a hang and on a crash found by its marker, the incident scope kept apart
/// from photo thumbnails, and chapter files parsed under the §8.6 rules.
/// </summary>
public sealed class PodcastArtworkPhaseTests : IAsyncLifetime
{
    private const string FeedUrl = "https://show.example.org/podcast.xml";
    private const string Cover = "https://show.example.org/cover.jpg";

    private static readonly InMemoryDatabaseRoot DatabaseRoot = new();

    private readonly string databaseName = $"podcast-artwork-{Guid.NewGuid():N}";
    private readonly DirectoryInfo volume = Directory.CreateTempSubdirectory("odyssey-podcast-art-");
    private readonly FakeRenderer renderer = new();
    private NewsJobHarness news = null!;

    private void UseDatabase(DbContextOptionsBuilder<OdysseyContext> options) =>
        options.UseInMemoryDatabase(databaseName, DatabaseRoot).ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning));

    public async Task InitializeAsync() => news = await NewsJobHarness.CreateAsync(
        UseDatabase, volume.FullName,
        configureServices: services =>
        {
            services.AddSingleton<IPhotoThumbnailRenderer>(renderer);
            services.AddSingleton(new PhotoThumbnailProcessHooks { Exit = _ => { } });
        });

    public async Task DisposeAsync()
    {
        renderer.Release();
        await news.DisposeAsync();
        volume.Delete(recursive: true);
    }

    private Task TickAsync() => news.Job.RunOnceAsync(CancellationToken.None);

    private static string Show(string? chaptersUrl = null) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <rss version="2.0" xmlns:itunes="http://www.itunes.com/dtds/podcast-1.0.dtd" xmlns:podcast="https://podcastindex.org/namespace/1.0">
        <channel><title>The Show</title><link>https://show.example.org/</link><itunes:image href="{Cover}"/>
        <item><guid isPermaLink="false">ep-1</guid><title>Episode 1</title><description>Notes.</description>
        <pubDate>Sun, 25 Oct 2026 07:00:00 GMT</pubDate>
        <enclosure url="https://cdn.example.org/1.mp3" type="audio/mpeg" length="10"/>
        {(chaptersUrl is null ? string.Empty : $"<podcast:chapters url=\"{chaptersUrl}\" type=\"application/json+chapters\"/>")}
        </item></channel></rss>
        """;

    private async Task<Guid> PodcastAsync()
    {
        var id = await news.AddFeedAsync(FeedUrl);
        await news.WithContextAsync(async c =>
        {
            (await c.NewsFeeds.SingleAsync(f => f.NewsFeedId == id)).Kind = NewsFeedKind.Podcast;
            await c.SaveChangesAsync();
        });
        return id;
    }

    [Fact]
    public async Task AC26_Show_artwork_is_stored_once_with_its_two_renditions_and_counted_as_artwork()
    {
        news.Server.Feed(FeedUrl, Show());
        news.Server.Bytes(Cover, ContactImageFixtures.BaselineJpeg(), "image/jpeg");
        await PodcastAsync();

        await TickAsync();

        await news.WithContextAsync(async c =>
        {
            var artwork = await c.PodcastArtwork.AsNoTracking().SingleAsync();
            Assert.Equal(PodcastArtworkState.Stored, artwork.State);
            Assert.Null(artwork.RenderStartedAtUtc);
            var files = await c.PodcastArtworkFiles.AsNoTracking().Where(f => f.PodcastArtworkId == artwork.PodcastArtworkId).ToListAsync();
            Assert.Equal([PodcastArtworkFileKind.Original, PodcastArtworkFileKind.Small, PodcastArtworkFileKind.Medium], files.Select(f => f.Kind).Order());
            Assert.Equal(files.Sum(f => f.SizeBytes), (await c.NewsStorageTotals.SingleAsync()).ArtworkBytes);
        });
        Assert.Equal(1, news.Server.CountFor(Cover));
    }

    [Fact]
    public async Task AC26_An_animated_or_non_image_body_is_rejected_and_never_rendered()
    {
        news.Server.Feed(FeedUrl, Show());
        news.Server.Bytes(Cover, ContactImageFixtures.AnimatedPng(), "image/png");
        await PodcastAsync();

        await TickAsync();

        Assert.Equal(PodcastArtworkState.Rejected, await news.WithContextAsync(async c => (await c.PodcastArtwork.SingleAsync()).State));
        Assert.Equal(0, renderer.Calls);
    }

    [Fact]
    public async Task AC27_A_crash_found_by_its_render_marker_rejects_the_artwork_blocks_the_feed_and_leaves_photo_incidents_alone()
    {
        news.Server.Feed(FeedUrl, Show());
        var feedId = await PodcastAsync();
        // The first tick ingests and fails the artwork fetch; the crash is simulated by the marker a dead render leaves.
        news.Server.Status(Cover, System.Net.HttpStatusCode.ServiceUnavailable);
        await TickAsync();
        var now = news.Clock.GetUtcNow().UtcDateTime;
        var second = Guid.NewGuid();
        await news.WithContextAsync(async c =>
        {
            var artwork = await c.PodcastArtwork.SingleAsync();
            artwork.State = PodcastArtworkState.Pending;
            artwork.RenderStartedAtUtc = now;
            artwork.LeaseOwner = Guid.NewGuid();
            artwork.LeaseExpiresAtUtc = now.AddMinutes(1);
            c.PodcastArtwork.Add(new PodcastArtwork
            {
                PodcastArtworkId = second, NewsFeedId = feedId, SourceUrl = "https://show.example.org/other.jpg",
                SourceUrlKeyHash = new byte[32], State = PodcastArtworkState.Pending, CreatedAtUtc = now,
            });
            await c.SaveChangesAsync();
        });
        news.Advance(TimeSpan.FromMinutes(2));

        await TickAsync();

        await news.WithContextAsync(async c =>
        {
            var all = await c.PodcastArtwork.AsNoTracking().ToListAsync();
            Assert.All(all, a => Assert.Equal(PodcastArtworkState.Rejected, a.State));
            Assert.Contains(all, a => a.RejectedReason == PodcastArtworkRejectedReason.RenderIncident);
            Assert.Contains(all, a => a.PodcastArtworkId == second && a.RejectedReason == PodcastArtworkRejectedReason.FeedIncident);
            Assert.Equal(PodcastArtworkIncidentKind.Crash, (await c.PodcastArtworkIncidents.SingleAsync()).Kind);
            Assert.Equal(1, (await c.PodcastFeedDetails.SingleAsync(d => d.NewsFeedId == feedId)).ArtworkIncidentCount);
            Assert.Equal(0, await c.PhotoThumbnailIncidents.CountAsync());
        });
    }

    [Fact]
    public async Task AC27_A_hung_render_is_abandoned_and_rejects_the_rest_of_the_feeds_artwork()
    {
        news.Server.Feed(FeedUrl, Show());
        news.Server.Bytes(Cover, ContactImageFixtures.BaselineJpeg(), "image/jpeg");
        var feedId = await PodcastAsync();
        renderer.Block = true;

        var tick = TickAsync();
        Assert.True(await renderer.Entered.WaitAsync(TimeSpan.FromSeconds(30)));
        news.Advance(PhotoThumbnailTiming.Watchdog + TimeSpan.FromSeconds(1));
        await tick;

        await news.WithContextAsync(async c =>
        {
            var artwork = await c.PodcastArtwork.AsNoTracking().SingleAsync();
            Assert.Equal((PodcastArtworkState.Rejected, PodcastArtworkRejectedReason.RenderIncident), (artwork.State, artwork.RejectedReason));
            Assert.Equal(PodcastArtworkIncidentKind.Hang, (await c.PodcastArtworkIncidents.SingleAsync()).Kind);
            Assert.Equal(1, (await c.PodcastFeedDetails.SingleAsync(d => d.NewsFeedId == feedId)).ArtworkIncidentCount);
        });
    }

    [Fact]
    public async Task AC28_A_chapter_file_is_read_under_the_shared_rules_and_a_malformed_one_yields_none()
    {
        const string ChaptersUrl = "https://show.example.org/1.json";
        news.Server.Feed(FeedUrl, Show(ChaptersUrl));
        news.Server.Bytes(Cover, ContactImageFixtures.BaselineJpeg(), "image/jpeg");
        news.Server.Bytes(ChaptersUrl, Encoding.UTF8.GetBytes("""
            {"version":"1.2.0","chapters":[
              {"startTime":60,"title":"Second"},
              {"startTime":0,"endTime":-3,"title":"Intro\u0007","url":"javascript:alert(1)"},
              {"startTime":"NaN","title":"Bad start"}]}
            """), "application/json+chapters");
        await PodcastAsync();

        await TickAsync();

        await news.WithContextAsync(async c =>
        {
            var episode = await c.PodcastEpisodes.AsNoTracking().SingleAsync();
            Assert.Equal(PodcastChaptersFetchState.Fetched, episode.ChaptersFetchState);
            var chapters = await c.PodcastChapters.AsNoTracking().Where(ch => ch.Source == PodcastChapterSource.Json).OrderBy(ch => ch.Position).ToListAsync();
            Assert.Equal([(0L, (long?)null, "Intro", (string?)null), (60_000L, null, "Second", null)],
                chapters.Select(ch => (ch.StartMs, ch.EndMs, ch.Title, ch.Url)));
        });
        Assert.Empty(PodcastChaptersPhase.ParseChapters(Encoding.UTF8.GetBytes(new string('[', 40) + new string(']', 40))));
        Assert.Empty(PodcastChaptersPhase.ParseChapters(Encoding.UTF8.GetBytes("""{"version":"2.0","chapters":[{"startTime":1}]}""")));
    }

    [Fact]
    public async Task AC26_An_svg_is_rejected_and_never_rendered()
    {
        news.Server.Feed(FeedUrl, Show());
        news.Server.Bytes(Cover, Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>x</script></svg>"), "image/svg+xml");
        await PodcastAsync();

        await TickAsync();

        Assert.Equal(PodcastArtworkState.Rejected, await news.WithContextAsync(async c => (await c.PodcastArtwork.SingleAsync()).State));
        Assert.Equal(0, renderer.Calls);
    }

    [Fact]
    public async Task AC26_A_feed_at_its_artwork_cap_inserts_no_further_artwork_row()
    {
        news.Server.Feed(FeedUrl, Show());
        var feedId = await PodcastAsync();
        var now = news.Clock.GetUtcNow().UtcDateTime;
        await news.WithContextAsync(async c =>
        {
            for (var i = 0; i < Dtos.PodcastLimits.MaxArtworkPerFeed; i++)
            {
                var key = new byte[32];
                BitConverter.GetBytes(i).CopyTo(key, 0);
                c.PodcastArtwork.Add(new PodcastArtwork
                {
                    PodcastArtworkId = Guid.NewGuid(), NewsFeedId = feedId, SourceUrl = $"https://show.example.org/{i}.jpg",
                    SourceUrlKeyHash = key, State = PodcastArtworkState.Rejected, CreatedAtUtc = now,
                });
            }

            await c.SaveChangesAsync();
        });

        await TickAsync();

        await news.WithContextAsync(async c =>
        {
            Assert.Equal(Dtos.PodcastLimits.MaxArtworkPerFeed, await c.PodcastArtwork.CountAsync());
            Assert.False(await c.PodcastArtwork.AnyAsync(a => a.SourceUrl == Cover));
            Assert.Null((await c.PodcastEpisodes.AsNoTracking().SingleAsync()).ArtworkId);
        });
    }

    [Fact]
    public async Task AC28_A_chapter_file_over_its_size_limit_yields_no_chapters()
    {
        const string ChaptersUrl = "https://show.example.org/big.json";
        news.Server.Feed(FeedUrl, Show(ChaptersUrl));
        news.Server.Bytes(ChaptersUrl, Encoding.UTF8.GetBytes("{\"version\":\"1.2.0\",\"chapters\":[" + new string(' ', 2 * 1024 * 1024) + "]}"), "application/json+chapters");
        await PodcastAsync();

        await TickAsync();

        await news.WithContextAsync(async c =>
        {
            Assert.Equal(PodcastChaptersFetchState.Failed, (await c.PodcastEpisodes.AsNoTracking().SingleAsync()).ChaptersFetchState);
            Assert.False(await c.PodcastChapters.AnyAsync(ch => ch.Source == PodcastChapterSource.Json));
        });
    }

    private sealed class FakeRenderer : IPhotoThumbnailRenderer
    {
        private readonly ManualResetEventSlim gate = new(true);

        public SemaphoreSlim Entered { get; } = new(0);

        public int Calls { get; private set; }

        public bool Block
        {
            set
            {
                if (value)
                {
                    gate.Reset();
                }
                else
                {
                    gate.Set();
                }
            }
        }

        public void Release() => gate.Set();

        public ThumbnailRenderResult Render(ThumbnailRenderRequest request)
        {
            Calls++;
            Entered.Release();
            gate.Wait(TimeSpan.FromMinutes(1));
            return ThumbnailRenderResult.Rendered(new ThumbnailRendition([1, 2, 3, 4], 400, 400), new ThumbnailRendition([1, 2], 200, 200));
        }
    }
}

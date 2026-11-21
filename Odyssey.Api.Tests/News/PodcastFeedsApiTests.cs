using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Dtos;
using Odyssey.Dtos.Internet;
using Xunit;
using static Odyssey.Api.Tests.News.NewsApiTestSupport;

namespace Odyssey.Api.Tests.News;

/// <summary>Feed type and podcast automation on the feed endpoints (issue #556 §5.1). Names carry the AC they pin.</summary>
public class PodcastFeedsApiTests(PodcastFeedsApiTests.Host host) : SharedApiHostTests<PodcastFeedsApiTests.Host>(host)
{
    private const string Feeds = "/api/news-feeds";

    private static NewNewsFeed Podcast(string url) => new()
    {
        Url = url, DownloadImages = false, Kind = NewsFeedKind.Podcast, AutoDownload = true, AutoDeleteEnabled = true, AutoDeleteAfterDays = 14,
    };

    private static UpdateNewsFeed Put(ExistingNewsFeed feed, NewsFeedKind kind, bool autoDownload = false, bool autoDelete = false, int days = 30) => new()
    {
        Version = feed.Version, Url = feed.Management!.Url, DownloadImages = feed.DownloadImages, IsActive = feed.IsActive,
        Kind = kind, AutoDownload = autoDownload, AutoDeleteEnabled = autoDelete, AutoDeleteAfterDays = days,
    };

    [Fact]
    public async Task AC1_A_feed_is_an_article_feed_unless_created_as_a_podcast()
    {
        var factory = Factory.ForCaller(AllNewsClaims);
        using var client = factory.CreateClient();

        var article = await (await client.PostAsJsonAsync(Feeds, new NewNewsFeed { Url = "https://plain.example.org/rss" })).ReadAsync<ExistingNewsFeed>();
        var response = await client.PostAsJsonAsync(Feeds, Podcast("https://show.example.org/podcast.xml"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var podcast = await response.ReadAsync<ExistingNewsFeed>();
        Assert.Equal(NewsFeedKind.Article, article.Kind);
        Assert.Null(article.Podcast);
        Assert.Equal(NewsFeedKind.Podcast, podcast.Kind);
        Assert.Equal((true, true, 14, 0, 0, 0L), (podcast.Podcast!.AutoDownload, podcast.Podcast.AutoDeleteEnabled,
            podcast.Podcast.AutoDeleteAfterDays, podcast.Podcast.EpisodeCount, podcast.Podcast.StoredAudioCount, podcast.Podcast.StoredAudioBytes));
        await factory.WithContextAsync(async context =>
            Assert.NotNull((await context.NewsFeeds.SingleAsync(f => f.NewsFeedId == podcast.Id)).AutoDeleteChangedAtUtc));
    }

    [Theory]
    [InlineData(true, false, "autoDownload")]
    [InlineData(false, true, "autoDeleteEnabled")]
    public async Task AC2_Automation_on_an_article_feed_is_422_keyed_by_the_switch(bool autoDownload, bool autoDelete, string field)
    {
        using var client = Factory.ForCaller(AllNewsClaims).CreateClient();

        var response = await client.PostAsJsonAsync(Feeds, new NewNewsFeed
        {
            Url = $"https://auto-{field}.example.org/rss", AutoDownload = autoDownload, AutoDeleteEnabled = autoDelete,
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.ProblemAsync();
        Assert.Equal(NewsErrorCodes.FeedAutomationNotPodcast, problem.Code());
        Assert.True(problem.HasError(field));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3651)]
    public async Task AC2_An_out_of_range_auto_delete_day_count_is_400(int days)
    {
        using var client = Factory.ForCaller(AllNewsClaims).CreateClient();

        var response = await client.PostAsJsonAsync(Feeds, Podcast($"https://days-{days}.example.org/rss") with { AutoDeleteAfterDays = days });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AC2_A_put_that_omits_the_type_or_the_automation_is_400()
    {
        var factory = Factory.ForCaller(AllNewsClaims);
        using var client = factory.CreateClient();
        var feed = await (await client.PostAsJsonAsync(Feeds, new NewNewsFeed { Url = "https://omit.example.org/rss" })).ReadAsync<ExistingNewsFeed>();

        var response = await client.PutAsync($"{Feeds}/{feed.Id}", JsonBody(new
        {
            version = feed.Version, url = feed.Management!.Url, downloadImages = true, isActive = true,
        }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.ProblemAsync();
        Assert.True(problem.HasError("Kind"));
        Assert.True(problem.HasError("AutoDownload"));
    }

    [Fact]
    public async Task An_unknown_type_name_is_400_keyed_kind()
    {
        using var client = Factory.ForCaller(AllNewsClaims).CreateClient();

        var response = await client.PostAsync(Feeds, JsonBody(new { url = "https://unknown-kind.example.org/rss", kind = "Video" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await response.ProblemAsync()).HasError("kind"));
    }

    [Fact]
    public async Task AC8_Switching_types_resets_the_validators_and_stamps_the_change()
    {
        var factory = Factory.ForCaller(AllNewsClaims);
        using var client = factory.CreateClient();
        var feed = await (await client.PostAsJsonAsync(Feeds, new NewNewsFeed { Url = "https://switch.example.org/rss" })).ReadAsync<ExistingNewsFeed>();

        var response = await client.PutAsJsonAsync($"{Feeds}/{feed.Id}", Put(feed, NewsFeedKind.Podcast, autoDownload: true));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = await response.ReadAsync<ExistingNewsFeed>();
        Assert.Equal(NewsFeedKind.Podcast, saved.Kind);
        Assert.True(saved.Podcast!.AutoDownload);
        await factory.WithContextAsync(async context =>
        {
            Assert.NotNull((await context.NewsFeeds.SingleAsync(f => f.NewsFeedId == feed.Id)).KindChangedAtUtc);
            Assert.NotNull((await context.NewsFeedFetchStates.SingleAsync(s => s.NewsFeedId == feed.Id)).ValidatorsResetRequestedAtUtc);
        });
    }

    [Fact]
    public async Task AC25_Switching_a_podcast_to_articles_cancels_its_pending_downloads_and_keeps_stored_audio()
    {
        var factory = Factory.ForCaller(AllNewsClaims);
        using var client = factory.CreateClient();
        var feed = await (await client.PostAsJsonAsync(Feeds, Podcast("https://leaving.example.org/podcast.xml"))).ReadAsync<ExistingNewsFeed>();
        var queued = await SeedEpisodeAsync(factory, feed.Id, PodcastAudioState.Queued);
        var failed = await SeedEpisodeAsync(factory, feed.Id, PodcastAudioState.Failed);
        var downloading = await SeedEpisodeAsync(factory, feed.Id, PodcastAudioState.Downloading);

        var response = await client.PutAsJsonAsync($"{Feeds}/{feed.Id}", Put(feed, NewsFeedKind.Article));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = await response.ReadAsync<ExistingNewsFeed>();
        Assert.Null(saved.Podcast);
        await factory.WithContextAsync(async context =>
        {
            var states = await context.PodcastEpisodeAudio.AsNoTracking().ToDictionaryAsync(a => a.NewsArticleId, a => a.State);
            Assert.Equal(PodcastAudioState.None, states[queued]);
            Assert.Equal(PodcastAudioState.None, states[failed]);
            // A running download stops at its next lease renewal (§3.6 step 7), not here.
            Assert.Equal(PodcastAudioState.Downloading, states[downloading]);
            Assert.False((await context.NewsFeeds.SingleAsync(f => f.NewsFeedId == feed.Id)).AutoDownload);
        });
    }

    [Fact]
    public async Task Turning_auto_delete_on_or_changing_its_days_restarts_the_countdown_and_leaving_it_alone_does_not()
    {
        var factory = Factory.ForCaller(AllNewsClaims);
        using var client = factory.CreateClient();
        var feed = await (await client.PostAsJsonAsync(Feeds, Podcast("https://countdown.example.org/podcast.xml") with { AutoDeleteEnabled = false }))
            .ReadAsync<ExistingNewsFeed>();
        DateTime? Stamp() => factory.WithContextAsync(async c => (await c.NewsFeeds.SingleAsync(f => f.NewsFeedId == feed.Id)).AutoDeleteChangedAtUtc).Result;
        Assert.Null(Stamp());

        feed = await (await client.PutAsJsonAsync($"{Feeds}/{feed.Id}", Put(feed, NewsFeedKind.Podcast, autoDelete: true, days: 30))).ReadAsync<ExistingNewsFeed>();
        var enabled = Stamp();
        Assert.NotNull(enabled);

        feed = await (await client.PutAsJsonAsync($"{Feeds}/{feed.Id}", Put(feed, NewsFeedKind.Podcast, autoDelete: true, days: 30))).ReadAsync<ExistingNewsFeed>();
        Assert.Equal(enabled, Stamp());

        await (await client.PutAsJsonAsync($"{Feeds}/{feed.Id}", Put(feed, NewsFeedKind.Podcast, autoDelete: true, days: 10))).ReadAsync<ExistingNewsFeed>();
        Assert.NotNull(Stamp());
    }

    [Fact]
    public async Task The_feed_list_and_catalogue_filter_by_kind_and_carry_it()
    {
        var factory = Factory.ForCaller(AllNewsClaims);
        using var client = factory.CreateClient();
        var article = await (await client.PostAsJsonAsync(Feeds, new NewNewsFeed { Url = "https://kind-article.example.org/rss" })).ReadAsync<ExistingNewsFeed>();
        var podcast = await (await client.PostAsJsonAsync(Feeds, Podcast("https://kind-podcast.example.org/podcast.xml"))).ReadAsync<ExistingNewsFeed>();

        var podcasts = await (await client.GetAsync($"{Feeds}?state=All&kind=Podcast&limit=200")).ReadAsync<PagedResult<ExistingNewsFeed>>();
        var catalogue = await (await client.GetAsync($"{Feeds}/catalogue?kind=Article")).ReadAsync<List<NewsFeedCatalogueEntry>>();
        var all = await (await client.GetAsync($"{Feeds}/catalogue")).ReadAsync<List<NewsFeedCatalogueEntry>>();

        Assert.Contains(podcasts.Items, f => f.Id == podcast.Id);
        Assert.DoesNotContain(podcasts.Items, f => f.Id == article.Id);
        Assert.Contains(catalogue, f => f.Id == article.Id);
        Assert.DoesNotContain(catalogue, f => f.Id == podcast.Id);
        Assert.Equal(NewsFeedKind.Podcast, all.Single(f => f.Id == podcast.Id).Kind);
    }

    [Fact]
    public async Task AC33_Podcast_feeds_stay_out_of_the_article_list_and_the_unread_badges_unless_named()
    {
        var factory = Factory.ForCaller(AllNewsClaims);
        var article = await factory.SeedFeedAsync(url: "https://stream-article.example.org/rss", unread: 0);
        var podcast = await factory.SeedFeedAsync(url: "https://stream-podcast.example.org/podcast.xml");
        await factory.WithContextAsync(async context =>
        {
            (await context.NewsFeeds.SingleAsync(f => f.NewsFeedId == podcast)).Kind = NewsFeedKind.Podcast;
            await context.SaveChangesAsync();
        });
        var articleIds = await factory.SeedArticlesAsync(article, 2, "Story");
        var episodeIds = await factory.SeedArticlesAsync(podcast, 2, "Episode");
        using var client = factory.CreateClient();

        var list = await (await client.GetAsync("/api/news-articles?limit=200")).ReadAsync<NewsArticlePage>();
        var named = await (await client.GetAsync($"/api/news-articles?feedIds={podcast}")).ReadAsync<NewsArticlePage>();
        var counts = await (await client.GetAsync($"{Feeds}/unread-counts")).ReadAsync<NewsUnreadCounts>();

        Assert.Contains(list.Items, a => articleIds.Contains(a.Id));
        Assert.DoesNotContain(list.Items, a => episodeIds.Contains(a.Id));
        Assert.Equal(episodeIds.Order(), named.Items.Select(a => a.Id).Order());
        Assert.DoesNotContain(counts.ByFeed, c => c.FeedId == podcast);
    }

    [Fact]
    public async Task AC25_A_feed_switched_back_to_articles_shows_its_articles_before_the_purge_loop_rewrites_them()
    {
        var factory = Factory.ForCaller(AllNewsClaims);
        var feed = await factory.SeedFeedAsync(url: "https://switched-back.example.org/podcast.xml");
        var ids = await factory.SeedArticlesAsync(feed, 2, "Episode");
        // The articles still carry the podcast copy of the kind; the feed changed type a moment ago.
        await factory.WithContextAsync(async context =>
        {
            foreach (var article in await context.NewsArticles.Where(a => a.NewsFeedId == feed).ToListAsync())
            {
                article.FeedKind = NewsFeedKind.Podcast;
            }

            (await context.NewsFeeds.SingleAsync(f => f.NewsFeedId == feed)).KindChangedAtUtc = Base;
            await context.SaveChangesAsync();
        });
        using var client = factory.CreateClient();

        var list = await (await client.GetAsync("/api/news-articles?limit=200")).ReadAsync<NewsArticlePage>();

        Assert.Equal(ids.Order(), list.Items.Where(a => a.FeedId == feed).Select(a => a.Id).Order());
    }

    [Fact]
    public async Task AC33_Mark_all_read_leaves_podcast_feeds_alone()
    {
        var factory = Factory.ForCaller(AllNewsClaims);
        var podcast = await factory.SeedFeedAsync(url: "https://markread-podcast.example.org/podcast.xml");
        await factory.WithContextAsync(async context =>
        {
            (await context.NewsFeeds.SingleAsync(f => f.NewsFeedId == podcast)).Kind = NewsFeedKind.Podcast;
            await context.SaveChangesAsync();
        });
        var episodes = await factory.SeedArticlesAsync(podcast, 2, "Episode", start: Base.AddDays(-1));
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/news-articles/mark-read", new MarkNewsRead { AsOfUtc = DateTimeOffset.UtcNow });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await factory.WithContextAsync(async context =>
            Assert.All(await context.NewsArticles.Where(a => episodes.Contains(a.NewsArticleId)).ToListAsync(), a => Assert.False(a.IsRead)));
    }

    /// <summary>An episode of <paramref name="feedId"/> with its article and audio row in the given state.</summary>
    internal static Task<Guid> SeedEpisodeAsync(OdysseyApiFactory factory, Guid feedId, PodcastAudioState state) =>
        factory.WithContextAsync(async context =>
        {
            var id = Guid.NewGuid();
            context.NewsArticles.Add(new NewsArticle
            {
                NewsArticleId = id, NewsFeedId = feedId, Bucket = 1, Title = "Episode", Excerpt = "Excerpt",
                ContentHash = new byte[32], IdentityHash = new byte[32], FirstSeenAtUtc = Base, LastSeenAtUtc = Base,
                SortAtUtc = Base, IsRead = true, ReadAtUtc = Base, FeedKind = NewsFeedKind.Podcast,
            });
            context.PodcastEpisodes.Add(new PodcastEpisode
            {
                NewsArticleId = id, NewsFeedId = feedId, EnclosureUrl = "https://cdn.example.org/a.mp3", EpisodeHash = new byte[32], SortAtUtc = Base,
            });
            context.PodcastEpisodeAudio.Add(new PodcastEpisodeAudio
            {
                NewsArticleId = id, NewsFeedId = feedId, EnclosureHostKeyHash = new byte[32], State = state, AudioFence = Guid.NewGuid(),
                RequestedBy = state == PodcastAudioState.None ? null : PodcastAudioRequestedBy.User,
            });
            await context.SaveChangesAsync();
            return id;
        });

    public sealed class Host() : OdysseyApiFactory([]);
}

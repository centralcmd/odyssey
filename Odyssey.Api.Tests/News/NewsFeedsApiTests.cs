using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Internet;
using Xunit;
using static Odyssey.Api.Tests.News.NewsApiTestSupport;

namespace Odyssey.Api.Tests.News;

/// <summary>The feed endpoints of issue #470 §5.1 over HTTP. Test names carry the AC they pin.</summary>
public class NewsFeedsApiTests(NewsFeedsApiTests.Host host) : SharedApiHostTests<NewsFeedsApiTests.Host>(host)
{
    private const string Feeds = "/api/news-feeds";

    private static NewNewsFeed NewFeed(string url = "https://news.example.org/feed.xml", List<Guid>? tags = null) =>
        new() { Url = url, CustomTitle = null, FetchIntervalMinutes = 30, DownloadImages = true, TagIds = tags };

    [Fact]
    public async Task AC78_Create_inserts_the_fetch_state_and_counters_in_the_same_save_and_is_due_at_once()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(Base));
        await using var factory = new OdysseyApiFactory(AllNewsClaims, configureServices: s => s.Replace(ServiceDescriptor.Singleton<TimeProvider>(time)));
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(Feeds, NewFeed());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var feed = await response.ReadAsync<ExistingNewsFeed>();
        Assert.Equal(NewsFeedHealth.Pending, feed.Health);
        Assert.Equal("news.example.org", new Uri(feed.Management!.Url).Host);
        Assert.Equal(NewsFeedProjectionTitle, feed.DisplayTitle);
        await factory.WithContextAsync(async context =>
        {
            var state = await context.NewsFeedFetchStates.SingleAsync(s => s.NewsFeedId == feed.Id);
            Assert.NotEqual(Guid.Empty, state.FetchFence);
            Assert.Equal(Base, state.NextFetchAtUtc);
            var counter = await context.NewsFeedCounters.SingleAsync(c => c.NewsFeedId == feed.Id);
            Assert.Equal((0, 0), (counter.ArticleCount, counter.UnreadCount));
        });
    }

    private const string NewsFeedProjectionTitle = "Untitled feed";

    [Fact]
    public async Task AC3_Management_is_null_with_news_read_only_and_no_field_names_the_fetch_host()
    {
        var owner = Factory.ForCaller(AllNewsClaims);
        var id = await owner.SeedFeedAsync(url: "https://secret-fetch.example.net/rss?token=abc", title: null, siteUrl: "https://www.public-site.example/");
        var reader = Factory;
        using var client = reader.ClientFor(ReadOnly);

        var body = await client.GetStringAsync($"{Feeds}/{id}");
        var feed = System.Text.Json.JsonSerializer.Deserialize<ExistingNewsFeed>(body, Json)!;

        Assert.Null(feed.Management);
        Assert.Equal("www.public-site.example", feed.DisplayTitle);
        Assert.DoesNotContain("secret-fetch", body, StringComparison.Ordinal);
        Assert.DoesNotContain("token", body, StringComparison.Ordinal);

        using var ownerClient = owner.CreateClient();
        var managed = await (await ownerClient.GetAsync($"{Feeds}/{id}")).ReadAsync<ExistingNewsFeed>();
        Assert.Contains("secret-fetch", managed.Management!.Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AC4_A_duplicate_address_after_normalisation_is_409_with_the_existing_feed()
    {
        var factory = Factory.ForCaller(AllNewsClaims);
        using var client = factory.CreateClient();
        var first = await (await client.PostAsJsonAsync(Feeds, NewFeed("https://News.Example.org/feed.xml"))).ReadAsync<ExistingNewsFeed>();

        var response = await client.PostAsJsonAsync(Feeds, NewFeed("https://news.example.org/feed.xml?utm_source=x#top"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.ProblemAsync();
        Assert.Equal(NewsErrorCodes.FeedDuplicate, problem.Code());
        Assert.Equal(first.Id, problem.GetProperty("existingFeedId").GetGuid());
    }

    [Theory]
    [InlineData("ftp://news.example.org/feed")]
    [InlineData("https://user:pass@news.example.org/feed")]
    [InlineData("http://127.0.0.1/feed")]
    [InlineData("http://169.254.169.254/latest")]
    [InlineData("https://news.example.org:8443/feed")]
    [InlineData("not a url")]
    public async Task AC4_A_refused_address_is_422_keyed_url(string url)
    {
        var factory = Factory.ForCaller(AllNewsClaims);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(Feeds, NewFeed(url));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.ProblemAsync();
        Assert.Equal(NewsErrorCodes.FeedUrlRejected, problem.Code());
        Assert.True(problem.HasError("url"));
        Assert.DoesNotContain(url, problem.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AC4_An_allow_list_grant_opens_a_private_literal_and_its_port_but_never_a_hard_denied_one()
    {
        var factory = Factory.ForCaller(AllNewsClaims);
        await factory.SetSystemSettingAsync(SystemSettingsKeys.NewsPrivateHostAllowList, "127.0.0.1:8080, 169.254.169.254");
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(Feeds, NewFeed("http://127.0.0.1:8080/feed"))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsJsonAsync(Feeds, NewFeed("http://169.254.169.254/feed"))).StatusCode);
    }

    [Fact]
    public async Task AC4_The_feed_limit_counts_archived_feeds_and_is_422()
    {
        var factory = Factory.ForCaller(AllNewsClaims);
        await factory.SetSystemSettingAsync(SystemSettingsKeys.NewsMaxFeeds, SystemSettingsBounds.NewsMaxFeedsMin.ToString(System.Globalization.CultureInfo.InvariantCulture));
        for (var i = 0; i < SystemSettingsBounds.NewsMaxFeedsMin; i++)
        {
            await factory.SeedFeedAsync(url: $"https://f{i}.example.org/rss", archived: i == 0);
        }

        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync(Feeds, NewFeed());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(NewsErrorCodes.FeedLimit, (await response.ProblemAsync()).Code());
    }

    [Fact]
    public async Task AC4_An_unknown_or_archived_tag_on_create_is_422_keyed_tagIds()
    {
        var factory = Factory.ForCaller(AllNewsClaims);
        var archived = await factory.SeedTagAsync("Old", archived: true);
        using var client = factory.CreateClient();

        foreach (var tags in new[] { new List<Guid> { Guid.NewGuid() }, new List<Guid> { archived } })
        {
            var response = await client.PostAsJsonAsync(Feeds, NewFeed(tags: tags));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.True((await response.ProblemAsync()).HasError("tagIds"));
        }
    }

    [Fact]
    public async Task AC5_Annotations_reject_out_of_range_input_and_a_non_guid_id_is_a_routing_404()
    {
        var factory = Factory.ForCaller(AllNewsClaims);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Feeds, new { customTitle = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Feeds, NewFeed() with { FetchIntervalMinutes = 5 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Feeds, NewFeed() with { CustomTitle = new string('x', 257) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Feeds, NewFeed() with { TagIds = [.. Enumerable.Range(0, 33).Select(_ => Guid.NewGuid())] })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"{Feeds}?limit=201")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Feeds}/not-a-guid")).StatusCode);
    }

    [Fact]
    public async Task AC8_Nested_objects_and_unknown_members_in_a_body_create_or_change_nothing()
    {
        var factory = Factory.ForCaller(AllNewsClaims);
        using var client = factory.CreateClient();

        var response = await client.PostAsync(Feeds, JsonBody(new
        {
            url = "https://news.example.org/feed.xml",
            downloadImages = true,
            management = new { url = "http://10.0.0.1/" },
            id = Guid.Empty,
            settingsVersion = Guid.Empty,
            fetchState = new { nextFetchAtUtc = "2000-01-01T00:00:00Z", quarantinedAtUtc = "2000-01-01T00:00:00Z" },
        }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var feed = await response.ReadAsync<ExistingNewsFeed>();
        Assert.NotEqual(Guid.Empty, feed.Id);
        Assert.Equal("https://news.example.org/feed.xml", feed.Management!.Url);
        Assert.Null(feed.Management.QuarantinedAtUtc);
        Assert.Equal(1, await factory.WithContextAsync(c => c.NewsFeeds.CountAsync()));
    }

    [Fact]
    public async Task AC46_AC10_Delete_takes_any_feed_and_then_hides_it_everywhere()
    {
        var factory = Factory.ForCaller(AllNewsClaims);
        var active = await factory.SeedFeedAsync(url: "https://a.example.org/rss");
        var archived = await factory.SeedFeedAsync(url: "https://b.example.org/rss", archived: true);
        using var client = factory.CreateClient();

        // No archive-first step: a live feed is deleted as directly as an archived one.
        foreach (var id in new[] { active, archived })
        {
            Assert.Equal(HttpStatusCode.Accepted, (await client.DeleteAsync($"{Feeds}/{id}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Feeds}/{id}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"{Feeds}/{id}")).StatusCode);
        }

        var all = await (await client.GetAsync($"{Feeds}?state=All")).ReadAsync<PagedResult<ExistingNewsFeed>>();
        Assert.DoesNotContain(all.Items, f => f.Id == active || f.Id == archived);
        var catalogue = await (await client.GetAsync($"{Feeds}/catalogue")).ReadAsync<List<NewsFeedCatalogueEntry>>();
        Assert.DoesNotContain(catalogue, f => f.Id == active || f.Id == archived);
        Assert.Equal(2, await factory.WithContextAsync(c => c.NewsFeeds.CountAsync(f => f.DeletionRequestedAtUtc != null)));
    }

    [Fact]
    public async Task Put_is_not_an_upsert_and_a_stale_version_is_409_news_feed_changed()
    {
        var factory = Factory.ForCaller(AllNewsClaims);
        var id = await factory.SeedFeedAsync();
        using var client = factory.CreateClient();
        var feed = await (await client.GetAsync($"{Feeds}/{id}")).ReadAsync<ExistingNewsFeed>();
        var update = new UpdateNewsFeed { Version = feed.Version, Url = feed.Management!.Url, CustomTitle = "Mine", DownloadImages = false, IsActive = true };

        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync($"{Feeds}/{Guid.NewGuid()}", update)).StatusCode);
        var ok = await client.PutAsJsonAsync($"{Feeds}/{id}", update);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var updated = await ok.ReadAsync<ExistingNewsFeed>();
        Assert.Equal("Mine", updated.DisplayTitle);
        Assert.NotEqual(feed.Version, updated.Version);

        var stale = await client.PutAsJsonAsync($"{Feeds}/{id}", update);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(NewsErrorCodes.FeedChanged, (await stale.ProblemAsync()).Code());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{Feeds}/{id}", update with { Version = null })).StatusCode);
    }

    [Fact]
    public async Task AC65_AC53_A_url_equal_to_the_suggestion_accepts_it_through_the_same_rules()
    {
        var factory = Factory.ForCaller(AllNewsClaims);
        const string suggested = "https://news.example.org/new-feed.xml";
        var id = await factory.SeedFeedAsync(url: "http://news.example.org/feed.xml", state: s => s.SuggestedUrl = suggested);
        await factory.SeedFeedAsync(url: "https://other.example.org/rss");
        using var client = factory.CreateClient();
        var feed = await (await client.GetAsync($"{Feeds}/{id}")).ReadAsync<ExistingNewsFeed>();
        var request = new UpdateNewsFeed { Version = feed.Version, Url = feed.Management!.Url, DownloadImages = true, IsActive = true };

        // Unchanged: the suggestion stays pending and the validators are not reset.
        var unchanged = await (await client.PutAsJsonAsync($"{Feeds}/{id}", request)).ReadAsync<ExistingNewsFeed>();
        Assert.Equal(suggested, unchanged.Management!.SuggestedUrl);
        Assert.Null(await factory.WithContextAsync(c => c.NewsFeedFetchStates.Where(s => s.NewsFeedId == id).Select(s => s.ValidatorsResetRequestedAtUtc).SingleAsync()));

        // A duplicate is refused like any URL change.
        var duplicate = await client.PutAsJsonAsync($"{Feeds}/{id}", request with { Version = unchanged.Version, Url = "https://other.example.org/rss" });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal(NewsErrorCodes.FeedDuplicate, (await duplicate.ProblemAsync()).Code());

        var accepted = await (await client.PutAsJsonAsync($"{Feeds}/{id}", request with { Version = unchanged.Version, Url = suggested })).ReadAsync<ExistingNewsFeed>();
        Assert.Equal(suggested, accepted.Management!.Url);
        Assert.Null(accepted.Management.SuggestedUrl);
        Assert.NotNull(await factory.WithContextAsync(c => c.NewsFeedFetchStates.Where(s => s.NewsFeedId == id).Select(s => s.ValidatorsResetRequestedAtUtc).SingleAsync()));
    }

    [Fact]
    public async Task AC65_Another_changed_url_overrides_and_clears_the_suggestion()
    {
        var factory = Factory.ForCaller(AllNewsClaims);
        var id = await factory.SeedFeedAsync(url: "http://news.example.org/feed.xml", state: s => s.SuggestedUrl = "https://news.example.org/feed.xml");
        using var client = factory.CreateClient();
        var feed = await (await client.GetAsync($"{Feeds}/{id}")).ReadAsync<ExistingNewsFeed>();

        var updated = await (await client.PutAsJsonAsync($"{Feeds}/{id}",
            new UpdateNewsFeed { Version = feed.Version, Url = "https://mirror.example.org/feed.xml", DownloadImages = true, IsActive = true }))
            .ReadAsync<ExistingNewsFeed>();

        Assert.Equal("https://mirror.example.org/feed.xml", updated.Management!.Url);
        Assert.Null(updated.Management.SuggestedUrl);
    }

    [Fact]
    public async Task Refresh_sets_the_request_flag_and_a_paused_feed_is_422_inactive()
    {
        var factory = Factory.ForCaller(AllNewsClaims);
        var active = await factory.SeedFeedAsync(url: "https://a.example.org/rss");
        var paused = await factory.SeedFeedAsync(url: "https://b.example.org/rss", active: false);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync($"{Feeds}/{active}/refresh", null)).StatusCode);
        Assert.NotNull(await factory.WithContextAsync(c => c.NewsFeedFetchStates.Where(s => s.NewsFeedId == active).Select(s => s.RefreshRequestedAtUtc).SingleAsync()));
        var refused = await client.PostAsync($"{Feeds}/{paused}/refresh", null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal(NewsErrorCodes.FeedInactive, (await refused.ProblemAsync()).Code());
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"{Feeds}/{Guid.NewGuid()}/refresh", null)).StatusCode);
    }

    [Fact]
    public async Task AC77_Release_quarantine_clears_it_without_rotating_the_version_and_otherwise_is_422()
    {
        var factory = Factory.ForCaller(AllNewsClaims);
        var quarantined = await factory.SeedFeedAsync(url: "https://a.example.org/rss", state: s =>
        {
            s.QuarantinedAtUtc = Base;
            s.CrashSuspectCount = 3;
            s.LeaseOwner = Guid.NewGuid();
        });
        var healthy = await factory.SeedFeedAsync(url: "https://b.example.org/rss");
        using var client = factory.CreateClient();
        var before = await (await client.GetAsync($"{Feeds}/{quarantined}")).ReadAsync<ExistingNewsFeed>();
        Assert.Equal(NewsFeedHealth.Quarantined, before.Health);
        var fence = await factory.WithContextAsync(c => c.NewsFeedFetchStates.Where(s => s.NewsFeedId == quarantined).Select(s => s.FetchFence).SingleAsync());

        var released = await (await client.PostAsync($"{Feeds}/{quarantined}/release-quarantine", null)).ReadAsync<ExistingNewsFeed>();

        Assert.Equal(before.Version, released.Version);
        Assert.NotEqual(NewsFeedHealth.Quarantined, released.Health);
        await factory.WithContextAsync(async c =>
        {
            var state = await c.NewsFeedFetchStates.SingleAsync(s => s.NewsFeedId == quarantined);
            Assert.Equal((0, fence), ((int)state.CrashSuspectCount, state.FetchFence));
        });
        var refused = await client.PostAsync($"{Feeds}/{healthy}/release-quarantine", null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal(NewsErrorCodes.FeedNotQuarantined, (await refused.ProblemAsync()).Code());
    }

    [Fact]
    public async Task AC79_Health_takes_the_highest_precedence_and_the_filter_matches_the_derived_value()
    {
        var factory = Factory.ForCaller(AllNewsClaims);
        var all = await factory.SeedFeedAsync(url: "https://a.example.org/rss", archived: true, state: s =>
        {
            s.QuarantinedAtUtc = Base;
            s.QuotaReachedAtUtc = Base;
            s.QuotaReason = NewsFeedQuotaReason.TextQuota;
        });
        var quota = await factory.SeedFeedAsync(url: "https://b.example.org/rss", state: s =>
        {
            s.QuotaReachedAtUtc = Base;
            s.QuotaReason = NewsFeedQuotaReason.DailyCap;
            s.ConsecutiveFailures = 2;
        });
        var failing = await factory.SeedFeedAsync(url: "https://c.example.org/rss", state: s => s.ConsecutiveFailures = 1);
        var gone = await factory.SeedFeedAsync(url: "https://d.example.org/rss", state: s =>
        {
            s.LastErrorKind = NewsFeedErrorKind.Gone;
            s.QuotaReachedAtUtc = Base;
            s.QuotaReason = NewsFeedQuotaReason.DailyCap;
        });
        using var client = factory.CreateClient();

        var archived = await (await client.GetAsync($"{Feeds}/{all}")).ReadAsync<ExistingNewsFeed>();
        Assert.Equal((NewsFeedHealth.Archived, (NewsFeedQuotaReason?)null), (archived.Health, archived.QuotaReason));
        var goneFeed = await (await client.GetAsync($"{Feeds}/{gone}")).ReadAsync<ExistingNewsFeed>();
        Assert.Equal((NewsFeedHealth.Gone, (NewsFeedQuotaReason?)null), (goneFeed.Health, goneFeed.QuotaReason));

        var quotaPage = await (await client.GetAsync($"{Feeds}?state=All&health=QuotaReached")).ReadAsync<PagedResult<ExistingNewsFeed>>();
        var single = Assert.Single(quotaPage.Items);
        Assert.Equal((quota, NewsFeedQuotaReason.DailyCap), (single.Id, single.QuotaReason!.Value));
        var failingPage = await (await client.GetAsync($"{Feeds}?state=All&health=Failing")).ReadAsync<PagedResult<ExistingNewsFeed>>();
        Assert.Equal(failing, Assert.Single(failingPage.Items).Id);
        var both = await (await client.GetAsync($"{Feeds}?state=All&health=Failing&health=Archived")).ReadAsync<PagedResult<ExistingNewsFeed>>();
        Assert.Equal(2, both.TotalCount);
    }

    [Fact]
    public async Task The_list_filters_by_state_tag_and_search_with_LIKE_wildcards_escaped_and_sorts_with_an_id_tie_break()
    {
        var factory = Factory.ForCaller(AllNewsClaims);
        var tag = await factory.SeedTagAsync("World");
        var alpha = await factory.SeedFeedAsync(url: "https://a.example.org/rss", title: "Alpha 100%", unread: 5);
        var beta = await factory.SeedFeedAsync(url: "https://b.example.org/rss", title: "Beta", unread: 9);
        await factory.SeedFeedAsync(url: "https://c.example.org/rss", title: "Gamma", active: false);
        await factory.SeedFeedAsync(url: "https://d.example.org/rss", title: "Delta", archived: true);
        await factory.TagFeedAsync(beta, tag);
        using var client = factory.CreateClient();

        var active = await (await client.GetAsync(Feeds)).ReadAsync<PagedResult<ExistingNewsFeed>>();
        Assert.Equal(new[] { "Alpha 100%", "Beta" }, active.Items.Select(f => f.DisplayTitle));
        Assert.Single((await (await client.GetAsync($"{Feeds}?state=Paused")).ReadAsync<PagedResult<ExistingNewsFeed>>()).Items);
        Assert.Equal(4, (await (await client.GetAsync($"{Feeds}?state=All")).ReadAsync<PagedResult<ExistingNewsFeed>>()).TotalCount);
        Assert.Equal(beta, Assert.Single((await (await client.GetAsync($"{Feeds}?tagId={tag}")).ReadAsync<PagedResult<ExistingNewsFeed>>()).Items).Id);
        Assert.Equal(alpha, Assert.Single((await (await client.GetAsync($"{Feeds}?search=100%25")).ReadAsync<PagedResult<ExistingNewsFeed>>()).Items).Id);
        Assert.Empty((await (await client.GetAsync($"{Feeds}?search=_eta")).ReadAsync<PagedResult<ExistingNewsFeed>>()).Items);
        var byUnread = await (await client.GetAsync($"{Feeds}?sortBy=UnreadCount")).ReadAsync<PagedResult<ExistingNewsFeed>>();
        Assert.Equal(new[] { beta, alpha }, byUnread.Items.Select(f => f.Id));
    }

    [Fact]
    public async Task AC74_Every_catalogue_count_equals_the_feed_dto_and_it_carries_no_url()
    {
        var factory = Factory.ForCaller(AllNewsClaims);
        var id = await factory.SeedFeedAsync(articles: 7, unread: 3, archived: true);
        using var client = factory.CreateClient();

        var body = await client.GetStringAsync($"{Feeds}/catalogue");
        var entry = Assert.Single(System.Text.Json.JsonSerializer.Deserialize<List<NewsFeedCatalogueEntry>>(body, Json)!);
        var feed = await (await client.GetAsync($"{Feeds}/{id}")).ReadAsync<ExistingNewsFeed>();

        Assert.Equal((feed.ArticleCount, feed.UnreadCount, NewsFeedState.Archived), (entry.ArticleCount, entry.UnreadCount, entry.State));
        Assert.DoesNotContain("feeds.example.net", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unread_counts_exclude_archived_feeds_and_count_effective_tags_once()
    {
        var factory = Factory.ForCaller(AllNewsClaims);
        var tag = await factory.SeedTagAsync("Tech");
        var tagged = await factory.SeedFeedAsync(url: "https://a.example.org/rss");
        var plain = await factory.SeedFeedAsync(url: "https://b.example.org/rss");
        var archived = await factory.SeedFeedAsync(url: "https://c.example.org/rss", archived: true);
        var taggedArticles = await factory.SeedArticlesAsync(tagged, 3);
        var plainArticles = await factory.SeedArticlesAsync(plain, 2);
        await factory.SeedArticlesAsync(archived, 4);
        await factory.TagFeedAsync(tagged, tag);
        await factory.TagArticleAsync(taggedArticles[0], tag); // already inherited: not counted twice
        await factory.TagArticleAsync(plainArticles[0], tag);
        using var client = factory.CreateClient();

        var counts = await (await client.GetAsync($"{Feeds}/unread-counts")).ReadAsync<NewsUnreadCounts>();

        Assert.Equal(5, counts.Total);
        Assert.Equal(2, counts.ByFeed.Count);
        Assert.Equal(4, Assert.Single(counts.ByTag).UnreadCount);
    }

    [Fact]
    public async Task Put_tags_replaces_the_feed_tags_and_rotates_the_version()
    {
        var factory = Factory.ForCaller(AllNewsClaims);
        var id = await factory.SeedFeedAsync();
        var one = await factory.SeedTagAsync("One");
        var two = await factory.SeedTagAsync("Two");
        await factory.TagFeedAsync(id, one);
        using var client = factory.CreateClient();
        var before = await (await client.GetAsync($"{Feeds}/{id}")).ReadAsync<ExistingNewsFeed>();

        var after = await (await client.PutAsJsonAsync($"{Feeds}/{id}/tags", new NewsFeedTagsUpdate { TagIds = [two, two] })).ReadAsync<ExistingNewsFeed>();

        Assert.Equal([two], after.TagIds);
        Assert.NotEqual(before.Version, after.Version);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{Feeds}/{id}/tags", new { })).StatusCode);
    }

    [Fact]
    public async Task Put_carries_the_tags_with_the_settings_null_keeps_and_an_archived_new_tag_is_refused()
    {
        var factory = Factory.ForCaller(AllNewsClaims);
        var id = await factory.SeedFeedAsync();
        var one = await factory.SeedTagAsync("One");
        var two = await factory.SeedTagAsync("Two");
        var gone = await factory.SeedTagAsync("Gone", archived: true);
        await factory.TagFeedAsync(id, one);
        using var client = factory.CreateClient();

        async Task<ExistingNewsFeed> Current() => await (await client.GetAsync($"{Feeds}/{id}")).ReadAsync<ExistingNewsFeed>();
        UpdateNewsFeed Body(ExistingNewsFeed f, List<Guid>? tags) => new()
        {
            Version = f.Version, Url = f.Management!.Url, CustomTitle = "Renamed", FetchIntervalMinutes = null,
            DownloadImages = f.DownloadImages, IsActive = f.IsActive, TagIds = tags,
        };

        var kept = await (await client.PutAsJsonAsync($"{Feeds}/{id}", Body(await Current(), null))).ReadAsync<ExistingNewsFeed>();
        Assert.Equal([one], kept.TagIds);
        Assert.Equal("Renamed", kept.DisplayTitle);

        var replaced = await (await client.PutAsJsonAsync($"{Feeds}/{id}", Body(kept, [two]))).ReadAsync<ExistingNewsFeed>();
        Assert.Equal([two], replaced.TagIds);

        var refused = await client.PutAsJsonAsync($"{Feeds}/{id}", Body(replaced, [two, gone]));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal([two], (await Current()).TagIds);

        var cleared = await (await client.PutAsJsonAsync($"{Feeds}/{id}", Body(await Current(), []))).ReadAsync<ExistingNewsFeed>();
        Assert.Empty(cleared.TagIds);
    }

    [Fact]
    public async Task A_refused_tag_on_put_rolls_the_settings_back_too()
    {
        var factory = Factory.ForCaller(AllNewsClaims);
        var id = await factory.SeedFeedAsync();
        var one = await factory.SeedTagAsync("One");
        var gone = await factory.SeedTagAsync("Gone", archived: true);
        await factory.TagFeedAsync(id, one);
        using var client = factory.CreateClient();
        var before = await (await client.GetAsync($"{Feeds}/{id}")).ReadAsync<ExistingNewsFeed>();

        // The settings half CHANGES here, so an unchanged title afterwards proves it was rolled back with the tags.
        var refused = await client.PutAsJsonAsync($"{Feeds}/{id}", Body(before, "A brand-new title", [one, gone]));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        var after = await (await client.GetAsync($"{Feeds}/{id}")).ReadAsync<ExistingNewsFeed>();
        Assert.Equal(before.DisplayTitle, after.DisplayTitle);
        Assert.Equal(before.Version, after.Version);
        Assert.Equal([one], after.TagIds);
    }

    [Fact]
    public async Task Put_refuses_an_unknown_tag_and_collapses_duplicate_ids()
    {
        var factory = Factory.ForCaller(AllNewsClaims);
        var id = await factory.SeedFeedAsync();
        var one = await factory.SeedTagAsync("One");
        using var client = factory.CreateClient();
        var current = await (await client.GetAsync($"{Feeds}/{id}")).ReadAsync<ExistingNewsFeed>();

        var unknown = await client.PutAsJsonAsync($"{Feeds}/{id}", Body(current, "Renamed", [one, Guid.NewGuid()]));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);

        var duplicated = await (await client.PutAsJsonAsync($"{Feeds}/{id}", Body(current, "Renamed", [one, one]))).ReadAsync<ExistingNewsFeed>();
        Assert.Equal([one], duplicated.TagIds);
    }

    [Fact]
    public async Task Put_with_tags_needs_news_feeds_update()
    {
        await using var owner = new OdysseyApiFactory(AllNewsClaims);
        var id = await owner.SeedFeedAsync();
        var one = await owner.SeedTagAsync("One");
        using var ownerClient = owner.CreateClient();
        var current = await (await ownerClient.GetAsync($"{Feeds}/{id}")).ReadAsync<ExistingNewsFeed>();
        await using var reader = new OdysseyApiFactory([.. AllNewsClaims.Where(c => c != PermissionClaims.NewsFeedsUpdate)], sharingStoreWith: owner);
        using var client = reader.CreateClient();

        var response = await client.PutAsJsonAsync($"{Feeds}/{id}", Body(current, "Renamed", [one]));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty((await (await ownerClient.GetAsync($"{Feeds}/{id}")).ReadAsync<ExistingNewsFeed>()).TagIds);
    }

    private static UpdateNewsFeed Body(ExistingNewsFeed f, string title, List<Guid>? tags) => new()
    {
        Version = f.Version, Url = f.Management!.Url, CustomTitle = title, FetchIntervalMinutes = null,
        DownloadImages = f.DownloadImages, IsActive = f.IsActive, TagIds = tags,
    };

    [Fact]
    public async Task Archive_and_unarchive_are_idempotent_and_move_the_feed_between_states()
    {
        var factory = Factory.ForCaller(AllNewsClaims);
        var id = await factory.SeedFeedAsync();
        using var client = factory.CreateClient();

        var archived = await (await client.PostAsync($"{Feeds}/{id}/archive", null)).ReadAsync<ExistingNewsFeed>();
        var again = await (await client.PostAsync($"{Feeds}/{id}/archive", null)).ReadAsync<ExistingNewsFeed>();
        Assert.NotNull(archived.ArchivedAtUtc);
        Assert.Equal(archived.Version, again.Version);
        var restored = await (await client.PostAsync($"{Feeds}/{id}/unarchive", null)).ReadAsync<ExistingNewsFeed>();
        Assert.Null(restored.ArchivedAtUtc);
        Assert.Equal(NewsFeedHealth.Healthy, restored.Health);
    }

    /// <summary>The class's shared host: every caller's claims travel per request (<see cref="SharedApiHostClients"/>).</summary>
    public sealed class Host() : OdysseyApiFactory([]);
}

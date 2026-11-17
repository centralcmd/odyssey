using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.DataProtection;
using Odyssey.Core.Internet.News.Reading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Core.FileStorage;
using Odyssey.Dtos;
using Odyssey.Dtos.Internet;
using Xunit;
using static Odyssey.Api.Tests.News.NewsApiTestSupport;

namespace Odyssey.Api.Tests.News;

/// <summary>The article list, its cursor and mark-read (issue #470 §5.2, §5.3) over HTTP, on EF InMemory.</summary>
public sealed class NewsArticlesApiTests
{
    private const string Articles = "/api/news-articles";

    private static string Ids(string name, IEnumerable<Guid> ids) => string.Join('&', ids.Select(id => $"{name}={id}"));

    private static async Task<NewsArticlePage> PageAsync(HttpClient client, string query)
    {
        var response = await client.GetAsync($"{Articles}?{query}");
        Assert.True(response.IsSuccessStatusCode, $"{query}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return await response.ReadAsync<NewsArticlePage>();
    }

    [Fact]
    public async Task AC6_AC29_Two_feeds_and_two_tags_or_within_and_across_and_mark_read_marks_exactly_those()
    {
        await using var factory = new OdysseyApiFactory(AllNewsClaims);
        var tagA = await factory.SeedTagAsync("A");
        var tagB = await factory.SeedTagAsync("B");
        var feed1 = await factory.SeedFeedAsync(url: "https://one.example.org/rss");
        var feed2 = await factory.SeedFeedAsync(url: "https://two.example.org/rss");
        var feed3 = await factory.SeedFeedAsync(url: "https://three.example.org/rss");
        var f1 = await factory.SeedArticlesAsync(feed1, 3, "One");
        var f2 = await factory.SeedArticlesAsync(feed2, 3, "Two");
        var f3 = await factory.SeedArticlesAsync(feed3, 3, "Three");
        await factory.TagFeedAsync(feed2, tagB);          // all of feed 2 inherit B
        await factory.TagArticleAsync(f1[0], tagA);       // one own tag in feed 1
        await factory.TagArticleAsync(f3[0], tagA);       // feed 3 is not in the feed list
        using var client = factory.CreateClient();

        var feedsOnly = await PageAsync(client, Ids("feedIds", [feed1, feed2]));
        Assert.Equal(6, feedsOnly.TotalCount);
        var tagsOnly = await PageAsync(client, Ids("tagIds", [tagA, tagB]));
        Assert.True(new HashSet<Guid>([.. f2, f1[0], f3[0]]).SetEquals(tagsOnly.Items.Select(i => i.Id)));
        Assert.Contains(tagsOnly.Items, i => i.Id == f2[0] && i.FeedTagIds.SequenceEqual([tagB]) && i.TagIds.Count == 0);

        var query = $"{Ids("feedIds", [feed1, feed2])}&{Ids("tagIds", [tagA, tagB])}";
        var both = await PageAsync(client, query);
        var expected = new HashSet<Guid>([.. f2, f1[0]]);
        Assert.True(expected.SetEquals(both.Items.Select(i => i.Id)));

        var marked = await (await client.PostAsJsonAsync($"{Articles}/mark-read", new MarkNewsRead
        {
            FeedIds = [feed1, feed2],
            TagIds = [tagA, tagB],
            AsOfUtc = both.AsOfUtc,
        })).ReadAsync<MarkNewsReadResult>();

        Assert.Equal((expected.Count, false), (marked.MarkedCount, marked.HasMore));
        var read = await factory.WithContextAsync(c => c.NewsArticles.Where(a => a.IsRead).Select(a => a.NewsArticleId).ToListAsync());
        Assert.True(expected.SetEquals(read));
        var counters = await factory.WithContextAsync(c => c.NewsFeedCounters.ToDictionaryAsync(x => x.NewsFeedId, x => x.UnreadCount));
        Assert.Equal((2, 0, 3), (counters[feed1], counters[feed2], counters[feed3]));
    }

    [Theory]
    [InlineData("Newest")]
    [InlineData("Oldest")]
    public async Task AC66_Marking_the_first_page_read_then_following_the_cursor_skips_and_repeats_nothing(string sort)
    {
        await using var factory = new OdysseyApiFactory(AllNewsClaims);
        var feed = await factory.SeedFeedAsync();
        // Three articles share each sort key, so the id tie-break is exercised.
        var ids = await factory.SeedArticlesAsync(feed, 12, step: TimeSpan.Zero);
        ids.AddRange(await factory.SeedArticlesAsync(feed, 12, title: "Later", step: TimeSpan.FromMinutes(1)));
        using var client = factory.CreateClient();

        var seen = new List<Guid>();
        var page = await PageAsync(client, $"isRead=false&sortBy={sort}&limit=5");
        while (true)
        {
            seen.AddRange(page.Items.Select(i => i.Id));
            await client.PostAsJsonAsync($"{Articles}/read-state", new NewsArticlesReadStateBulk { ArticleIds = [.. page.Items.Select(i => i.Id)], IsRead = true });
            if (page.NextCursor is null)
            {
                break;
            }

            page = await PageAsync(client, $"isRead=false&sortBy={sort}&limit=5&cursor={Uri.EscapeDataString(page.NextCursor)}");
            Assert.Null(page.TotalCount);
        }

        Assert.Equal(ids.Count, seen.Count);
        Assert.True(ids.ToHashSet().SetEquals(seen));
    }

    [Fact]
    public async Task AC67_An_article_first_seen_after_the_first_page_is_not_on_later_pages_but_on_a_fresh_one()
    {
        await using var factory = new OdysseyApiFactory(AllNewsClaims);
        var feed = await factory.SeedFeedAsync();
        await factory.SeedArticlesAsync(feed, 4);
        using var client = factory.CreateClient();

        var first = await PageAsync(client, "limit=2&sortBy=Oldest");
        var late = await factory.SeedArticlesAsync(feed, 1, "Late", firstSeen: first.AsOfUtc.AddMilliseconds(1), start: Base.AddMinutes(10));
        await Task.Delay(20);
        var second = await PageAsync(client, $"limit=10&sortBy=Oldest&cursor={Uri.EscapeDataString(first.NextCursor!)}");

        Assert.DoesNotContain(second.Items, i => i.Id == late[0]);
        Assert.Equal(2, second.Items.Count);
        Assert.Null(second.NextCursor);
        // A fresh first page, whose asOfUtc is past the article's first sight, finds it.
        Assert.Contains((await PageAsync(client, "limit=10&sortBy=Oldest")).Items, i => i.Id == late[0]);
    }

    [Fact]
    public async Task AC68_A_modified_cursor_or_one_with_other_filters_is_400_keyed_cursor_and_cursor_with_offset_is_400()
    {
        await using var factory = new OdysseyApiFactory(AllNewsClaims);
        var feed = await factory.SeedFeedAsync();
        await factory.SeedArticlesAsync(feed, 5);
        using var client = factory.CreateClient();
        var cursor = (await PageAsync(client, "limit=2")).NextCursor!;

        var tampered = cursor[..^3] + (cursor[^3] == 'A' ? 'B' : 'A') + cursor[^2..];
        foreach (var query in new[]
        {
            $"limit=2&cursor={Uri.EscapeDataString(tampered)}",
            $"limit=2&isRead=false&cursor={Uri.EscapeDataString(cursor)}",
            $"limit=2&sortBy=Oldest&cursor={Uri.EscapeDataString(cursor)}",
            $"limit=2&cursor=not-base64!!",
        })
        {
            var response = await client.GetAsync($"{Articles}?{query}");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var problem = await response.ProblemAsync();
            Assert.True(problem.HasError("cursor"), query);
            Assert.DoesNotContain(cursor, problem.ToString(), StringComparison.Ordinal);
        }

        // The page size is not bound: a different limit continues fine.
        Assert.Equal(3, (await PageAsync(client, $"limit=50&cursor={Uri.EscapeDataString(cursor)}")).Items.Count);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"{Articles}?offset=2&cursor={Uri.EscapeDataString(cursor)}")).StatusCode);
    }

    [Fact]
    public async Task AC70_Every_cursor_fits_its_bound_and_one_under_a_revoked_key_is_400()
    {
        await using var factory = new OdysseyApiFactory(AllNewsClaims);
        var feeds = new List<Guid>();
        for (var i = 0; i < 50; i++)
        {
            feeds.Add(await factory.SeedFeedAsync(url: $"https://f{i}.example.org/rss"));
        }

        await factory.SeedArticlesAsync(feeds[0], 3);
        using var client = factory.CreateClient();
        var page = await PageAsync(client, $"limit=1&{Ids("feedIds", feeds)}");
        Assert.InRange(page.NextCursor!.Length, 1, NewsLimits.MaxCursorLength);

        // A cursor protected under a key this host's ring does not hold — a revoked or rotated-out key — never decrypts.
        var filter = new NewsArticleFilter([.. feeds.Order()], [], null, null, null, null, false, NewsArticleSortBy.Newest);
        var foreign = new NewsListCursor(DataProtectionProvider.Create("foreign-ring"))
            .Issue(filter, new NewsCursorPosition(Base, Guid.NewGuid(), 0, DateTime.UtcNow));
        var response = await client.GetAsync($"{Articles}?limit=1&{Ids("feedIds", feeds)}&cursor={Uri.EscapeDataString(foreign)}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await response.ProblemAsync()).HasError("cursor"));
    }

    [Fact]
    public async Task AC48_AC69_TotalCount_is_capped_at_10000_and_present_on_the_first_page_only()
    {
        await using var factory = new OdysseyApiFactory(AllNewsClaims);
        var feed = await factory.SeedFeedAsync();
        await factory.SeedArticlesAsync(feed, NewsLimits.MaxCountedArticles + 1, step: TimeSpan.FromSeconds(1));
        using var client = factory.CreateClient();

        var first = await PageAsync(client, "limit=10");
        Assert.Equal((10_000, true), (first.TotalCount, first.TotalCountCapped));
        var second = await PageAsync(client, $"limit=10&cursor={Uri.EscapeDataString(first.NextCursor!)}");
        Assert.Equal(((int?)null, (bool?)null), (second.TotalCount, second.TotalCountCapped));
    }

    [Fact]
    public async Task AC69_Relevance_needs_a_search_and_offset_paging_stops_at_10000()
    {
        await using var factory = new OdysseyApiFactory(AllNewsClaims);
        var feed = await factory.SeedFeedAsync();
        await factory.SeedArticlesAsync(feed, 3, "Election");
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"{Articles}?sortBy=Relevance")).StatusCode);
        var relevance = await PageAsync(client, "sortBy=Relevance&search=election&limit=2");
        Assert.Equal(2, relevance.Items.Count);
        var rest = await PageAsync(client, $"sortBy=Relevance&search=election&limit=2&cursor={Uri.EscapeDataString(relevance.NextCursor!)}");
        Assert.Single(rest.Items);
        Assert.Null(rest.NextCursor);
        var atCap = await PageAsync(client, $"sortBy=Relevance&search=election&offset={NewsLimits.MaxCountedArticles}");
        Assert.Empty(atCap.Items);
        Assert.Null(atCap.NextCursor);
    }

    [Fact]
    public async Task AC72_AC73_Id_lists_are_deduplicated_before_the_cap_and_unknown_or_pending_ids_are_422()
    {
        await using var factory = new OdysseyApiFactory(AllNewsClaims);
        var archived = await factory.SeedFeedAsync(url: "https://a.example.org/rss", archived: true);
        var pending = await factory.SeedFeedAsync(url: "https://b.example.org/rss", archived: true, pendingDeletion: true);
        var archivedTag = await factory.SeedTagAsync("Gone", archived: true);
        var article = (await factory.SeedArticlesAsync(archived, 1))[0];
        await factory.TagArticleAsync(article, archivedTag);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"{Articles}?{Ids("feedIds", Enumerable.Range(0, 51).Select(_ => Guid.NewGuid()))}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"{Articles}?{Ids("tagIds", Enumerable.Range(0, 51).Select(_ => Guid.NewGuid()))}")).StatusCode);

        // 51 copies of one archived feed: one id, and named explicitly, so included without includeArchivedFeeds.
        var named = await PageAsync(client, Ids("feedIds", Enumerable.Repeat(archived, 51)));
        Assert.Equal(article, Assert.Single(named.Items).Id);
        Assert.Empty((await PageAsync(client, "")).Items);
        Assert.Single((await PageAsync(client, "includeArchivedFeeds=true")).Items);
        Assert.Single((await PageAsync(client, $"includeArchivedFeeds=true&tagIds={archivedTag}")).Items);

        foreach (var (query, field) in new[] { ($"feedIds={pending}", "feedIds"), ($"feedIds={Guid.NewGuid()}", "feedIds"), ($"tagIds={Guid.NewGuid()}", "tagIds") })
        {
            var response = await client.GetAsync($"{Articles}?{query}");
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.True((await response.ProblemAsync()).HasError(field));
        }
    }

    [Fact]
    public async Task Search_uses_words_of_three_letters_and_a_two_letter_only_query_is_400_keyed_search()
    {
        await using var factory = new OdysseyApiFactory(AllNewsClaims);
        var feed = await factory.SeedFeedAsync();
        await factory.SeedArticlesAsync(feed, 2, "Election night");
        await factory.SeedArticlesAsync(feed, 2, "Weather");
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"{Articles}?search=to%20of");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.ProblemAsync();
        Assert.Equal(NewsErrorCodes.SearchUnusable, problem.Code());
        Assert.True(problem.HasError("search"));
        Assert.Equal(2, (await PageAsync(client, "search=%2Belect*%20%22night%22%20the")).TotalCount);
    }

    [Fact]
    public async Task AC47_The_search_limiter_answers_429_with_retry_after_and_an_unsearched_list_is_never_limited()
    {
        await using var factory = new OdysseyApiFactory(AllNewsClaims, configuration: new Dictionary<string, string?>
        {
            ["RateLimiting:NewsSearch:PermitLimit"] = "2",
        });
        var feed = await factory.SeedFeedAsync();
        await factory.SeedArticlesAsync(feed, 1, "Election");
        using var client = factory.CreateClient();

        await PageAsync(client, "search=election");
        await PageAsync(client, "search=election");
        var limited = await client.GetAsync($"{Articles}?search=election");

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.NotNull(limited.Headers.RetryAfter);
        Assert.Equal(NewsErrorCodes.SearchLimited, (await limited.ProblemAsync()).Code());
        await PageAsync(client, "");
        var other = new HttpRequestMessage(HttpMethod.Get, $"{Articles}?search=election");
        other.Headers.Add(TestAuthHandler.ActorOverrideHeader, "another-user");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(other)).StatusCode);
    }

    [Fact]
    public async Task AC55_Mark_read_caps_each_request_at_5000_and_repeating_it_marks_the_rest()
    {
        await using var factory = new OdysseyApiFactory(AllNewsClaims);
        var feed = await factory.SeedFeedAsync();
        await factory.SeedArticlesAsync(feed, NewsLimits.MaxMarkReadPerRequest + 3, step: TimeSpan.FromSeconds(1));
        using var client = factory.CreateClient();
        var request = new MarkNewsRead { AsOfUtc = DateTimeOffset.UtcNow };

        var first = await (await client.PostAsJsonAsync($"{Articles}/mark-read", request)).ReadAsync<MarkNewsReadResult>();
        var second = await (await client.PostAsJsonAsync($"{Articles}/mark-read", request)).ReadAsync<MarkNewsReadResult>();

        Assert.Equal((NewsLimits.MaxMarkReadPerRequest, true), (first.MarkedCount, first.HasMore));
        Assert.Equal((3, false), (second.MarkedCount, second.HasMore));
        Assert.Equal(0, await factory.WithContextAsync(c => c.NewsFeedCounters.Where(x => x.NewsFeedId == feed).Select(x => x.UnreadCount).SingleAsync()));
    }

    [Fact]
    public async Task Mark_read_refuses_a_future_asOf_and_unknown_ids_with_422_and_stops_at_the_asOf_edge()
    {
        await using var factory = new OdysseyApiFactory(AllNewsClaims);
        var feed = await factory.SeedFeedAsync();
        var old = await factory.SeedArticlesAsync(feed, 2);
        var fresh = await factory.SeedArticlesAsync(feed, 1, "Fresh", firstSeen: Base.AddDays(1));
        using var client = factory.CreateClient();

        foreach (var (body, field) in new (MarkNewsRead, string)[]
        {
            (new MarkNewsRead { AsOfUtc = DateTimeOffset.UtcNow.AddHours(1) }, "asOfUtc"),
            (new MarkNewsRead { AsOfUtc = DateTimeOffset.UtcNow, FeedIds = [Guid.NewGuid()] }, "feedIds"),
            (new MarkNewsRead { AsOfUtc = DateTimeOffset.UtcNow, TagIds = [Guid.NewGuid()] }, "tagIds"),
        })
        {
            var response = await client.PostAsJsonAsync($"{Articles}/mark-read", body);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            var problem = await response.ProblemAsync();
            Assert.Equal(NewsErrorCodes.MarkReadScopeInvalid, problem.Code());
            Assert.True(problem.HasError(field), field);
        }

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"{Articles}/mark-read", new { })).StatusCode);
        var marked = await (await client.PostAsJsonAsync($"{Articles}/mark-read", new MarkNewsRead { AsOfUtc = new DateTimeOffset(Base.AddHours(1)) })).ReadAsync<MarkNewsReadResult>();
        Assert.Equal(2, marked.MarkedCount);
        Assert.False(await factory.WithContextAsync(c => c.NewsArticles.Where(a => a.NewsArticleId == fresh[0]).Select(a => a.IsRead).SingleAsync()));
        Assert.Equal(old.Count, marked.MarkedCount);
    }

    [Fact]
    public async Task Read_state_is_idempotent_and_moves_the_unread_counter_once()
    {
        await using var factory = new OdysseyApiFactory(AllNewsClaims);
        var feed = await factory.SeedFeedAsync();
        var id = (await factory.SeedArticlesAsync(feed, 2))[0];
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync($"{Articles}/{id}/read-state", new NewsArticleReadState { IsRead = true })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync($"{Articles}/{id}/read-state", new NewsArticleReadState { IsRead = true })).StatusCode);
        Assert.Equal(1, await factory.WithContextAsync(c => c.NewsFeedCounters.Where(x => x.NewsFeedId == feed).Select(x => x.UnreadCount).SingleAsync()));
        var article = await factory.WithContextAsync(c => c.NewsArticles.SingleAsync(a => a.NewsArticleId == id));
        Assert.True(article.IsRead && article.ReadAtUtc is not null);

        await client.PutAsJsonAsync($"{Articles}/{id}/read-state", new NewsArticleReadState { IsRead = false });
        Assert.Equal(2, await factory.WithContextAsync(c => c.NewsFeedCounters.Where(x => x.NewsFeedId == feed).Select(x => x.UnreadCount).SingleAsync()));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{Articles}/{id}/read-state", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync($"{Articles}/{Guid.NewGuid()}/read-state", new NewsArticleReadState { IsRead = true })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"{Articles}/read-state", new NewsArticlesReadStateBulk { ArticleIds = [], IsRead = true })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"{Articles}/read-state",
            new NewsArticlesReadStateBulk { ArticleIds = [.. Enumerable.Range(0, 501).Select(_ => Guid.NewGuid())], IsRead = true })).StatusCode);
    }

    [Fact]
    public async Task Article_tags_replace_add_and_remove_with_an_archived_tag_refused_on_add()
    {
        await using var factory = new OdysseyApiFactory(AllNewsClaims);
        var feed = await factory.SeedFeedAsync();
        var id = (await factory.SeedArticlesAsync(feed, 1))[0];
        var live = await factory.SeedTagAsync("Live");
        var other = await factory.SeedTagAsync("Other");
        var archived = await factory.SeedTagAsync("Archived", archived: true);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync($"{Articles}/{id}/tags", new NewsArticleTagsUpdate { TagIds = [live] })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsync($"{Articles}/{id}/tags/{other}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsync($"{Articles}/{id}/tags/{other}", null)).StatusCode);
        var refused = await client.PutAsync($"{Articles}/{id}/tags/{archived}", null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal(NewsErrorCodes.TagArchived, (await refused.ProblemAsync()).Code());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PutAsJsonAsync($"{Articles}/{id}/tags", new NewsArticleTagsUpdate { TagIds = [archived] })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsync($"{Articles}/{id}/tags/{Guid.NewGuid()}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{Articles}/{id}/tags/{live}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{Articles}/{id}/tags/{live}")).StatusCode);

        var links = await factory.WithContextAsync(c => c.NewsArticleTags.Where(l => l.NewsArticleId == id).ToListAsync());
        var link = Assert.Single(links);
        Assert.Equal(other, link.NewsTagId);
        Assert.Equal(Base, link.SortAtUtc);
    }
}

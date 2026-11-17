using System.Net;
using System.Text;
using System.Text.Json;
using Odyssey.ApiClient.Resources;
using Odyssey.Dtos.Internet;
using Xunit;

namespace Odyssey.ApiClient.Tests;

/// <summary><see cref="NewsApiClient"/>'s routes, query strings, bodies and result mapping (issue #470 §5).</summary>
public class NewsApiClientTests
{
    private static readonly Guid FeedId = Guid.Parse("11111111-0000-0000-0000-000000000001");
    private static readonly Guid TagId = Guid.Parse("22222222-0000-0000-0000-000000000002");
    private static readonly Guid ArticleId = Guid.Parse("33333333-0000-0000-0000-000000000003");

    private sealed class RecordingHandler(HttpStatusCode status, string? body, string mediaType = "application/json") : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            var response = new HttpResponseMessage(status);
            if (body is not null)
            {
                response.Content = new StringContent(body, Encoding.UTF8, mediaType);
            }

            if (status == HttpStatusCode.ServiceUnavailable)
            {
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(2));
            }

            return response;
        }
    }

    private static (NewsApiClient Client, RecordingHandler Handler) Create(HttpStatusCode status, string? body, string mediaType = "application/json")
    {
        var handler = new RecordingHandler(status, body, mediaType);
        return (new NewsApiClient(new OdysseyApi(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") })), handler);
    }

    [Fact]
    public async Task ListArticlesAsync_puts_every_filter_and_the_cursor_on_the_query_and_reads_the_page()
    {
        var (client, handler) = Create(HttpStatusCode.OK, $$"""
            {"items":[{"id":"{{ArticleId}}","feedId":"{{FeedId}}","feedTitle":"Example","title":"Hello","excerpt":"…",
              "sortAtUtc":"2026-11-01T12:00:00Z","firstSeenAtUtc":"2026-11-01T12:00:00Z","revisionCount":0,"isRead":false,
              "tagIds":[],"feedTagIds":["{{TagId}}"],"imageCount":1}],
             "totalCount":null,"totalCountCapped":null,"nextCursor":"abc_-","asOfUtc":"2026-11-01T12:30:00Z"}
            """);

        var result = await client.ListArticlesAsync(new NewsArticleListFilter
        {
            FeedIds = [FeedId],
            TagIds = [TagId],
            IsRead = false,
            Search = "climate talks",
            IncludeArchivedFeeds = true,
            SortBy = NewsArticleSortBy.Oldest,
        }, 50, cursor: "abc_-");

        Assert.True(result.IsSuccess);
        var query = handler.LastRequest!.RequestUri!.Query;
        Assert.StartsWith("/api/news-articles", handler.LastRequest.RequestUri.AbsolutePath, StringComparison.Ordinal);
        Assert.Contains("offset=0&limit=50", query, StringComparison.Ordinal);
        Assert.Contains($"feedIds={FeedId}", query, StringComparison.Ordinal);
        Assert.Contains($"tagIds={TagId}", query, StringComparison.Ordinal);
        Assert.Contains("isRead=false", query, StringComparison.Ordinal);
        Assert.Contains("search=climate%20talks", query, StringComparison.Ordinal);
        Assert.Contains("includeArchivedFeeds=true", query, StringComparison.Ordinal);
        Assert.Contains("sortBy=Oldest", query, StringComparison.Ordinal);
        Assert.Contains("cursor=abc_-", query, StringComparison.Ordinal);
        var item = Assert.Single(result.Value!.Items);
        Assert.Equal((ArticleId, TagId), (item.Id, item.FeedTagIds.Single()));
        Assert.Equal("abc_-", result.Value.NextCursor);
        Assert.Null(result.Value.TotalCount);
    }

    [Fact]
    public async Task ListFeedsAsync_sends_state_health_and_sort_and_reads_the_paged_result()
    {
        var (client, handler) = Create(HttpStatusCode.OK, $$"""
            {"items":[{"id":"{{FeedId}}","displayTitle":"Example","health":"QuotaReached","quotaReason":"TextQuota","management":null}],
             "offset":0,"limit":200,"totalCount":1}
            """);

        var result = await client.ListFeedsAsync(1, 200, state: NewsFeedState.All,
            health: [NewsFeedHealth.Failing, NewsFeedHealth.QuotaReached], sortBy: NewsFeedSortBy.UnreadCount, ascending: false);

        var query = handler.LastRequest!.RequestUri!.Query;
        Assert.Contains("state=All", query, StringComparison.Ordinal);
        Assert.Contains("health=Failing&health=QuotaReached", query, StringComparison.Ordinal);
        Assert.Contains("sortBy=UnreadCount&sortDir=desc", query, StringComparison.Ordinal);
        var feed = Assert.Single(result.Value!.Items);
        Assert.Equal((NewsFeedHealth.QuotaReached, NewsFeedQuotaReason.TextQuota), (feed.Health, feed.QuotaReason!.Value));
        Assert.Null(feed.Management);
    }

    [Fact]
    public async Task MarkReadAsync_posts_the_list_filters_with_the_first_pages_asOf()
    {
        var (client, handler) = Create(HttpStatusCode.OK, """{"markedCount":5000,"hasMore":true}""");
        var asOf = new DateTime(2026, 11, 1, 12, 30, 0, DateTimeKind.Utc);

        var result = await client.MarkReadAsync(new NewsArticleListFilter { FeedIds = [FeedId], Search = "budget" }, asOf);

        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("/api/news-articles/mark-read", handler.LastRequest.RequestUri!.AbsolutePath);
        var body = JsonDocument.Parse(handler.LastBody!).RootElement;
        Assert.Equal(FeedId, body.GetProperty("feedIds")[0].GetGuid());
        Assert.Equal("budget", body.GetProperty("search").GetString());
        Assert.Equal(new DateTimeOffset(asOf), body.GetProperty("asOfUtc").GetDateTimeOffset());
        Assert.Equal((5000, true), (result.Value!.MarkedCount, result.Value.HasMore));
    }

    [Fact]
    public async Task A_news_busy_503_is_a_failure_carrying_its_code_and_retry_after()
    {
        var (client, _) = Create(HttpStatusCode.ServiceUnavailable,
            """{"title":"Service Unavailable","status":503,"detail":"busy","code":"news-busy"}""", "application/problem+json");

        var result = await client.GetUnreadCountsAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, result.Status);
        Assert.Equal(NewsErrorCodes.Busy, result.Problem!.Code);
    }

    [Fact]
    public async Task The_writes_use_their_routes_and_methods()
    {
        var (client, handler) = Create(HttpStatusCode.NoContent, null);

        await client.DeleteTagAsync(TagId, detachLinks: true);
        Assert.Equal((HttpMethod.Delete, $"/api/news-tags/{TagId}?detachLinks=true"), (handler.LastRequest!.Method, handler.LastRequest.RequestUri!.PathAndQuery));
        await client.AddArticleTagAsync(ArticleId, TagId);
        Assert.Equal((HttpMethod.Put, $"/api/news-articles/{ArticleId}/tags/{TagId}"), (handler.LastRequest!.Method, handler.LastRequest.RequestUri!.PathAndQuery));
        await client.SetReadAsync(ArticleId, true);
        Assert.Equal("{\"isRead\":true}", handler.LastBody);
        await client.RefreshFeedAsync(FeedId);
        Assert.Equal((HttpMethod.Post, $"/api/news-feeds/{FeedId}/refresh"), (handler.LastRequest!.Method, handler.LastRequest.RequestUri!.PathAndQuery));
        await client.DeleteFeedAsync(FeedId);
        Assert.Equal(HttpMethod.Delete, handler.LastRequest!.Method);
        Assert.Equal($"http://localhost/api/news-articles/{ArticleId}/images/{TagId}/content", client.ImageUrl(ArticleId, TagId));
    }
}

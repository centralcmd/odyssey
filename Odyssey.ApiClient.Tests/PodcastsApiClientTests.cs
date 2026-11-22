using System.Net;
using System.Text;
using Odyssey.ApiClient.Resources;
using Odyssey.Dtos.Internet;
using Odyssey.Testing.Http;
using Xunit;

namespace Odyssey.ApiClient.Tests;

/// <summary><see cref="PodcastsApiClient"/>'s episode-list query string (issue #556 §5.2 #6, the #559 saved filter).</summary>
public class PodcastsApiClientTests
{
    private static readonly Guid FeedId = Guid.Parse("11111111-0000-0000-0000-000000000001");

    private static (PodcastsApiClient Client, RecordingHttpHandler Handler) Create()
    {
        var handler = new RecordingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"items":[],"totalCount":0,"totalCountCapped":false,"nextCursor":null,"asOfUtc":"2026-11-01T12:30:00Z"}""",
                Encoding.UTF8, "application/json"),
        });
        return (new PodcastsApiClient(new OdysseyApi(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") })), handler);
    }

    [Fact]
    public async Task ListEpisodesAsync_puts_every_filter_and_the_cursor_on_the_query()
    {
        var (client, handler) = Create();

        var result = await client.ListEpisodesAsync(new PodcastEpisodeListFilter
        {
            FeedIds = [FeedId],
            AudioStates = [PodcastAudioState.Stored, PodcastAudioState.Queued],
            Search = "lighthouse",
            IncludeArchivedFeeds = true,
            SavedToDevice = false,
            SortBy = PodcastEpisodeSortBy.Oldest,
        }, 50, cursor: "abc_-");

        Assert.True(result.IsSuccess);
        var query = handler.LastRequest!.RequestUri!.Query;
        Assert.StartsWith("/api/podcast-episodes", handler.LastRequest.RequestUri.AbsolutePath, StringComparison.Ordinal);
        Assert.Contains($"feedIds={FeedId}", query, StringComparison.Ordinal);
        Assert.Contains("audioState=Stored", query, StringComparison.Ordinal);
        Assert.Contains("audioState=Queued", query, StringComparison.Ordinal);
        Assert.Contains("search=lighthouse", query, StringComparison.Ordinal);
        Assert.Contains("includeArchivedFeeds=true", query, StringComparison.Ordinal);
        Assert.Contains("savedToDevice=false", query, StringComparison.Ordinal);
        Assert.Contains("sortBy=Oldest", query, StringComparison.Ordinal);
        Assert.Contains("cursor=abc_-", query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListFeedsAsync_asks_for_podcast_feeds_in_every_state()
    {
        var handler = new RecordingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[]", Encoding.UTF8, "application/json"),
        });
        var client = new PodcastsApiClient(new OdysseyApi(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") }));

        await client.ListFeedsAsync(2, 200);

        var uri = handler.LastRequest!.RequestUri!;
        Assert.Equal("/api/news-feeds", uri.AbsolutePath);
        Assert.Contains("state=All", uri.Query, StringComparison.Ordinal);
        Assert.Contains("kind=Podcast", uri.Query, StringComparison.Ordinal);
        Assert.Contains("offset=200&limit=200", uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListEpisodesAsync_leaves_out_savedToDevice_when_both_are_wanted()
    {
        var (client, handler) = Create();

        await client.ListEpisodesAsync(new PodcastEpisodeListFilter(), 25);

        Assert.DoesNotContain("savedToDevice", handler.LastRequest!.RequestUri!.Query, StringComparison.Ordinal);
    }
}

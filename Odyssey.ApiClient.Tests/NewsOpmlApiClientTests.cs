using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Odyssey.ApiClient.Resources;
using Odyssey.Dtos.Internet;
using Xunit;

namespace Odyssey.ApiClient.Tests;

/// <summary>
/// The OPML methods of <see cref="NewsApiClient"/> (issue #508 §14, amendments v6 and v7): the GET for every feed, the
/// POST for a list of ids, the row count from <c>X-Odyssey-Export-Rows</c>, and the multipart import.
/// </summary>
public class NewsOpmlApiClientTests
{
    private static readonly Guid FeedId = Guid.Parse("11111111-0000-0000-0000-000000000001");

    private sealed class Handler(HttpStatusCode status, string body, string mediaType, string? rows, string? fileName = "odyssey-news-feeds-2026-11-17.opml")
        : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) };
            if (rows is not null)
            {
                response.Headers.TryAddWithoutValidation("X-Odyssey-Export-Rows", rows);
            }

            if (fileName is not null && status == HttpStatusCode.OK)
            {
                response.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = fileName };
            }

            return response;
        }
    }

    private static (NewsApiClient Client, Handler Handler) Create(
        HttpStatusCode status = HttpStatusCode.OK, string body = "<opml/>", string mediaType = "text/x-opml", string? rows = "2", string? fileName = "odyssey-news-feeds-2026-11-17.opml")
    {
        var handler = new Handler(status, body, mediaType, rows, fileName);
        return (new NewsApiClient(new OdysseyApi(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") })), handler);
    }

    [Fact]
    public async Task ExportOpmlAsync_with_no_ids_gets_every_feed_and_reads_the_row_count()
    {
        var (client, handler) = Create();

        var result = await client.ExportOpmlAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(HttpMethod.Get, handler.Request!.Method);
        Assert.Equal("/api/news-feeds/opml", handler.Request.RequestUri!.AbsolutePath);
        Assert.Equal(2, result.Value!.Rows);
        Assert.Equal("odyssey-news-feeds-2026-11-17.opml", result.Value.File.FileName);
        Assert.Equal("text/x-opml", result.Value.File.ContentType);
        Assert.Equal("<opml/>", Encoding.UTF8.GetString(result.Value.File.Bytes));
    }

    [Fact]
    public async Task ExportOpmlAsync_with_ids_posts_them_as_json()
    {
        var (client, handler) = Create(rows: "1");

        var result = await client.ExportOpmlAsync([FeedId]);

        Assert.True(result.IsSuccess);
        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal("/api/news-feeds/opml/export", handler.Request.RequestUri!.AbsolutePath);
        Assert.Equal("application/json", handler.Request.Content!.Headers.ContentType!.MediaType);
        using var json = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal(FeedId, json.RootElement.GetProperty("feedIds")[0].GetGuid());
        Assert.Equal(1, result.Value!.Rows);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("-1")]
    [InlineData("two")]
    [InlineData("1.5")]
    public async Task ExportOpmlAsync_reads_a_missing_or_unusable_row_count_as_null(string? rows)
    {
        var (client, _) = Create(rows: rows);

        var result = await client.ExportOpmlAsync();

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value!.Rows);
    }

    [Fact]
    public async Task ExportOpmlAsync_falls_back_to_a_default_file_name()
    {
        var (client, _) = Create(fileName: null);

        Assert.Equal("odyssey-news-feeds.opml", (await client.ExportOpmlAsync()).Value!.File.FileName);
    }

    [Fact]
    public async Task ExportOpmlAsync_surfaces_a_busy_problem_with_its_code()
    {
        var (client, _) = Create(HttpStatusCode.ServiceUnavailable,
            """{"status":503,"detail":"The news reader is busy right now; try again in a moment.","code":"news-busy"}""",
            "application/problem+json", rows: null);

        var result = await client.ExportOpmlAsync([FeedId]);

        Assert.False(result.IsSuccess);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, result.Status);
        Assert.Equal(NewsErrorCodes.Busy, result.Problem!.Code);
    }

    [Fact]
    public async Task ImportOpmlAsync_posts_the_file_as_multipart_and_reads_the_report()
    {
        var (client, handler) = Create(body: $$"""
            {"feeds":2,"mergedOutlines":1,"created":1,"alreadySubscribed":1,"pendingDeletion":0,"invalid":0,"feedLimit":0,
             "tagsCreated":["Local"],"tagsSkipped":[{"name":"Gardening","reason":"NoPermission"}],"tagsSkippedTotal":1,
             "items":[{"position":1,"url":"https://a.example/rss","title":"A","outcome":"Created","feedId":"{{FeedId}}","tags":["Local"],"tagsDropped":0,"message":null},
                      {"position":2,"url":"https://b.example/rss","title":null,"outcome":"SomethingNew","feedId":null,"tags":[],"tagsDropped":0,"message":null}],
             "itemsTruncated":false}
            """, mediaType: "application/json", rows: null);
        var bytes = Encoding.UTF8.GetBytes("<opml><body/></opml>");

        var result = await client.ImportOpmlAsync(new ApiUpload("", "", bytes.Length, () => new MemoryStream(bytes)));

        Assert.True(result.IsSuccess);
        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal("/api/news-feeds/opml", handler.Request.RequestUri!.AbsolutePath);
        Assert.Equal("multipart/form-data", handler.Request.Content!.Headers.ContentType!.MediaType);
        Assert.Contains("name=file", handler.RequestBody, StringComparison.Ordinal);
        Assert.Contains("filename=feeds.opml", handler.RequestBody, StringComparison.Ordinal);
        Assert.Equal(NewsOpmlImportOutcome.Created, result.Value!.Items[0].Outcome);
        Assert.Equal(NewsOpmlImportOutcome.Unknown, result.Value.Items[1].Outcome);
        Assert.Equal(NewsOpmlTagSkipReason.NoPermission, result.Value.TagsSkipped[0].Reason);
    }
}

using System.Net;
using Odyssey.Dtos;
using Xunit;

namespace Odyssey.ApiClient.Tests;

/// <summary>
/// Covers the transport core's own behaviour, independently of any resource client.
/// </summary>
public class OdysseyApiTests
{
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        public HttpResponseMessage Response { get; set; } =
            new(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"items":[],"offset":0,"limit":50,"totalCount":0}""",
                                            System.Text.Encoding.UTF8, "application/json"),
            };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(Response);
        }
    }

    private static (OdysseyApi Api, RecordingHandler Handler) Create()
    {
        var handler = new RecordingHandler();
        return (new OdysseyApi(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") }), handler);
    }

    /// <summary>
    /// "Load all" owns its window: a caller-supplied page/offset/limit must be stripped and replaced,
    /// so a reference-data load can never be silently truncated to a smaller page. This is why the
    /// pre-existing <c>?offset=0&amp;limit=100</c> fragments on such URLs were inert.
    /// </summary>
    [Theory]
    [InlineData("api/x?offset=0&limit=100")]
    [InlineData("api/x?limit=25")]
    [InlineData("api/x?page=3&pageSize=10")]
    [InlineData("api/x?offset=500")]
    public async Task GetAllAsync_overrides_any_caller_supplied_window(string url)
    {
        var (api, handler) = Create();

        await api.GetAllAsync<object>(url);

        var query = handler.LastRequest!.RequestUri!.Query;
        Assert.Contains($"offset=0&limit={PagedQuery.LimitAll}", query);
        Assert.DoesNotContain("limit=100", query);
        Assert.DoesNotContain("limit=25", query);
        Assert.DoesNotContain("page=3", query);
        Assert.DoesNotContain("pageSize=10", query);
        Assert.DoesNotContain("offset=500", query);
    }

    /// <summary>Non-paging filters on the URL must survive the window override.</summary>
    [Fact]
    public async Task GetAllAsync_preserves_non_paging_filters()
    {
        var (api, handler) = Create();

        await api.GetAllAsync<object>("api/x?limit=10&types=Person&search=acme");

        var query = handler.LastRequest!.RequestUri!.Query;
        Assert.Contains($"offset=0&limit={PagedQuery.LimitAll}", query);
        Assert.Contains("types=Person", query);
        Assert.Contains("search=acme", query);
        Assert.DoesNotContain("limit=10", query);
    }

    [Fact]
    public async Task GetAllAsync_adds_a_window_to_a_url_that_had_none()
    {
        var (api, handler) = Create();

        await api.GetAllAsync<object>("api/x");

        Assert.Equal($"?offset=0&limit={PagedQuery.LimitAll}", handler.LastRequest!.RequestUri!.Query);
    }

    /// <summary>A failed paged read must not masquerade as an empty one.</summary>
    [Fact]
    public async Task GetAllAsync_propagates_failure_rather_than_returning_an_empty_list()
    {
        var (api, handler) = Create();
        handler.Response = new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("""{"detail":"Nope."}""",
                                        System.Text.Encoding.UTF8, "application/problem+json"),
        };

        var result = await api.GetAllAsync<object>("api/x");

        Assert.False(result.IsSuccess);
        Assert.Equal(HttpStatusCode.Forbidden, result.Status);
        Assert.Equal("Nope.", result.Error);
    }

    [Fact]
    public async Task A_network_failure_becomes_a_failure_result_rather_than_an_exception()
    {
        var api = new OdysseyApi(new HttpClient(new ThrowingHandler())
        {
            BaseAddress = new Uri("http://localhost/"),
        });

        var result = await api.GetAsync<object>("api/x");

        Assert.False(result.IsSuccess);
        Assert.Contains("boom", result.Error);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("boom");
    }

    [Theory]
    [InlineData("http://localhost/api/contacts/11111111-1111-1111-1111-111111111111", true)]
    [InlineData("http://localhost/api/contacts/11111111-1111-1111-1111-111111111111/", true)]
    [InlineData("http://localhost/api/contacts", false)]     // no id segment
    [InlineData("http://localhost/api/contacts/not-a-guid", false)]
    public void ApiLocation_extracts_a_guid_only_from_a_guid_segment(string location, bool expected) =>
        Assert.Equal(expected, ApiLocation.ExtractId(new Uri(location)) is not null);

    [Fact]
    public void ApiLocation_handles_a_missing_header() => Assert.Null(ApiLocation.ExtractId(null));
}

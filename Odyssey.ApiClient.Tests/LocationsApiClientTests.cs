using System.Net;
using System.Text;
using System.Text.Json;
using Odyssey.ApiClient.Resources;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.ApiClient.Tests;

/// <summary><see cref="LocationsApiClient"/>'s routes, verbs and query assembly (issue #350 AC 21).</summary>
public class LocationsApiClientTests
{
    private sealed class RecordingHandler(string body = "{}") : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static LocationsApiClient CreateClient(RecordingHandler handler) =>
        new(new OdysseyApi(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") }));

    [Fact]
    public async Task Search_is_a_POST_with_the_text_in_the_body_never_the_url()
    {
        var handler = new RecordingHandler("""{"region":1,"candidates":[],"partial":false,"attribution":{"text":"© Kartverket","license":"CC BY 4.0","url":"https://www.kartverket.no/"}}""");
        var client = CreateClient(handler);

        var result = await client.SearchAsync(new LocationSearchRequest { Query = "Storgata 1", Region = LocationSearchRegion.Norway });

        Assert.True(result.IsSuccess);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("/api/locations/search", handler.LastRequest.RequestUri!.PathAndQuery);
        Assert.DoesNotContain("Storgata", handler.LastRequest.RequestUri.ToString());
        using var body = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal("Storgata 1", body.RootElement.GetProperty("query").GetString());
    }

    [Theory]
    [InlineData("Active", "status=Active")]
    [InlineData("Archived", "status=Archived")]
    public async Task List_sends_status_when_exactly_one_is_selected(string status, string expected)
    {
        string[] statuses = [status];
        var handler = new RecordingHandler("""{"items":[],"offset":0,"limit":25,"totalCount":0}""");

        await CreateClient(handler).ListAsync(1, 25, statuses: statuses);

        Assert.Contains(expected, handler.LastRequest!.RequestUri!.Query);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Active,Archived")]
    public async Task List_omits_status_when_none_or_both_are_selected(string csv)
    {
        var statuses = csv.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var handler = new RecordingHandler("""{"items":[],"offset":0,"limit":25,"totalCount":0}""");

        await CreateClient(handler).ListAsync(1, 25, statuses: statuses);

        Assert.DoesNotContain("status=", handler.LastRequest!.RequestUri!.Query);
    }

    [Fact]
    public async Task List_carries_the_window_search_and_sort()
    {
        var handler = new RecordingHandler("""{"items":[],"offset":25,"limit":25,"totalCount":0}""");

        await CreateClient(handler).ListAsync(2, 25, search: "ringebu", sortBy: "UpdatedAt", sortDir: "desc");

        var query = handler.LastRequest!.RequestUri!.Query;
        Assert.StartsWith("/api/locations?", handler.LastRequest.RequestUri.PathAndQuery);
        Assert.Contains("offset=25", query);
        Assert.Contains("limit=25", query);
        Assert.Contains("search=ringebu", query);
        Assert.Contains("sortBy=UpdatedAt", query);
        Assert.Contains("sortDir=desc", query);
    }

    [Fact]
    public async Task Update_is_a_PUT_to_the_location_route()
    {
        var handler = new RecordingHandler("""{"locationId":"00000000-0000-0000-0000-000000000001","name":"Cabin","timeZoneId":"Europe/Oslo"}""");
        var id = Guid.NewGuid();

        await CreateClient(handler).UpdateAsync(id, new UpdateLocation
        {
            Name = "Cabin", Latitude = 1, Longitude = 2, TimeZoneId = "Europe/Oslo", Source = LocationSource.Manual, Archived = true,
        });

        Assert.Equal(HttpMethod.Put, handler.LastRequest!.Method);
        Assert.Equal($"/api/locations/{id}", handler.LastRequest.RequestUri!.PathAndQuery);
        using var body = JsonDocument.Parse(handler.LastBody!);
        Assert.True(body.RootElement.GetProperty("archived").GetBoolean());
    }

    [Fact]
    public async Task Create_posts_to_the_collection()
    {
        var handler = new RecordingHandler("""{"locationId":"00000000-0000-0000-0000-000000000001","name":"Cabin","timeZoneId":"Europe/Oslo"}""");

        var result = await CreateClient(handler).CreateAsync(new NewLocation
        {
            Name = "Cabin", Latitude = 1, Longitude = 2, TimeZoneId = "Europe/Oslo", Source = LocationSource.Manual,
        });

        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("/api/locations", handler.LastRequest.RequestUri!.PathAndQuery);
        Assert.Equal("Cabin", result.Value!.Name);
    }
}

using System.Net;
using System.Text;
using Odyssey.ApiClient.Resources;
using Xunit;

namespace Odyssey.ApiClient.Tests;

/// <summary><see cref="MapStylesApiClient"/>'s route and result mapping (issue #370 AC 16).</summary>
public class MapStylesApiClientTests
{
    private sealed class StubHandler(HttpStatusCode status, string body, string mediaType) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) });
        }
    }

    private static MapStylesApiClient CreateClient(StubHandler handler) =>
        new(new OdysseyApi(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") }));

    [Fact]
    public async Task A_200_is_a_success_carrying_the_four_values()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """
            {"lightStyleUrl":"https://tiles.openfreemap.org/styles/positron","darkStyleUrl":"/maps/blank-dark.json",
             "attribution":"OpenFreeMap © OpenMapTiles Data from OpenStreetMap","attributionUrl":"https://www.openstreetmap.org/copyright"}
            """, "application/json");

        var result = await CreateClient(handler).GetAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(HttpMethod.Get, handler.LastRequest!.Method);
        Assert.Equal("/api/map-styles", handler.LastRequest.RequestUri!.PathAndQuery);
        Assert.Equal("https://tiles.openfreemap.org/styles/positron", result.Value!.LightStyleUrl);
        Assert.Equal("/maps/blank-dark.json", result.Value.DarkStyleUrl);
        Assert.Equal("OpenFreeMap © OpenMapTiles Data from OpenStreetMap", result.Value.Attribution);
        Assert.Equal("https://www.openstreetmap.org/copyright", result.Value.AttributionUrl);
    }

    [Fact]
    public async Task A_403_is_a_failure_carrying_the_problem()
    {
        var handler = new StubHandler(HttpStatusCode.Forbidden, """
            {"type":"https://tools.ietf.org/html/rfc9110#section-15.5.4","title":"Forbidden","status":403,"detail":"Missing locations.read."}
            """, "application/problem+json");

        var result = await CreateClient(handler).GetAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(HttpStatusCode.Forbidden, result.Status);
        Assert.NotNull(result.Problem);
        Assert.Null(result.Value);
    }
}

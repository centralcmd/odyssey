using System.Net;
using System.Text;
using System.Text.Json;
using Odyssey.ApiClient.Resources;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.ApiClient.Tests;

/// <summary>
/// <see cref="WeatherApiClient"/>'s and <see cref="LocationsApiClient.SetWeatherPointAsync"/>'s routes, verbs and
/// query assembly (issue #387 §5; #388 §5) — and that no call still reaches a retired <c>/api/weather/locations</c> route.
/// </summary>
public class WeatherApiClientTests
{
    private static readonly Guid Point = Guid.Parse("4f5a1c2e-0000-4000-8000-000000000001");
    private static readonly Guid Place = Guid.Parse("4f5a1c2e-0000-4000-8000-000000000002");

    private sealed class RecordingHandler(string body = "{}") : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        public HttpRequestMessage Last => Requests[^1];

        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static OdysseyApi Api(RecordingHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") });

    public static TheoryData<string, string, string> Calls => new()
    {
        { "points", "GET", "/api/weather/points?offset=25&limit=25&search=oslo&sortBy=LinkedLocationCount&sortDir=desc" },
        { "point", "GET", $"/api/weather/points/{Point}" },
        { "linked", "GET", $"/api/weather/points/{Point}/linked-locations" },
        { "create", "POST", "/api/weather/points" },
        { "update", "PUT", $"/api/weather/points/{Point}" },
        { "refresh", "POST", $"/api/weather/points/{Point}/refresh" },
        { "purge", "DELETE", $"/api/weather/points/{Point}/history" },
        { "delete", "DELETE", $"/api/weather/points/{Point}" },
        { "options", "GET", "/api/weather/points/options" },
        { "near", "GET", $"/api/weather/points/options?nearLocationId={Place}" },
        { "current", "GET", $"/api/weather/current?weatherPointId={Point}" },
        { "daily", "GET", $"/api/weather/daily?from=2026-10-01&to=2026-10-02&weatherPointId={Point}" },
        { "link", "PUT", $"/api/locations/{Place}/weather-point" },
    };

    [Theory]
    [MemberData(nameof(Calls))]
    public async Task Each_method_hits_its_route_with_its_verb(string call, string method, string pathAndQuery)
    {
        var handler = new RecordingHandler(call == "points" ? """{"items":[],"offset":25,"limit":25,"totalCount":0}""" : call == "linked" ? "[]" : "{}");
        var weather = new WeatherApiClient(Api(handler));
        var locations = new LocationsApiClient(Api(handler));

        await (call switch
        {
            "points" => weather.PointsAsync(2, 25, "oslo", "LinkedLocationCount", "desc"),
            "point" => weather.PointAsync(Point),
            "linked" => weather.LinkedLocationsAsync(Point),
            "create" => weather.CreatePointAsync(new NewWeatherPoint { Name = "Oslo" }),
            "update" => weather.UpdatePointAsync(Point, new UpdateWeatherPoint { Name = "Oslo" }),
            "refresh" => weather.RefreshPointAsync(Point),
            "purge" => weather.PurgePointHistoryAsync(Point),
            "delete" => weather.DeletePointAsync(Point),
            "options" => weather.OptionsAsync(),
            "near" => weather.OptionsAsync(Place),
            "current" => weather.CurrentAsync(Point),
            "daily" => weather.DailyAsync(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 2), Point),
            "link" => (Task)locations.SetWeatherPointAsync(Place, Point),
            _ => throw new ArgumentOutOfRangeException(nameof(call)),
        });

        Assert.Equal(new HttpMethod(method), handler.Last.Method);
        var expected = new Uri("http://localhost" + pathAndQuery);
        Assert.Equal(expected.AbsolutePath, handler.Last.RequestUri!.AbsolutePath);
        // The reads go through the shared query builder, which may add its window; every expected pair is there.
        var sent = handler.Last.RequestUri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
        Assert.All(expected.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries), pair => Assert.Contains(pair, sent));
        if (call is "options" or "points")
        {
            Assert.Equal(expected.Query, handler.Last.RequestUri.Query);
        }
        Assert.DoesNotContain("/api/weather/locations", handler.Last.RequestUri.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Clearing_a_link_sends_an_explicit_null()
    {
        var handler = new RecordingHandler("""{"locationId":"4f5a1c2e-0000-4000-8000-000000000002","weatherPoint":null,"distanceKm":null,"warnings":[]}""");

        var result = await new LocationsApiClient(Api(handler)).SetWeatherPointAsync(Place, null);

        Assert.True(result.IsSuccess);
        using var body = JsonDocument.Parse(handler.LastBody!);
        Assert.True(body.RootElement.TryGetProperty("weatherPointId", out var value));
        Assert.Equal(JsonValueKind.Null, value.ValueKind);
    }

    [Fact]
    public async Task The_preference_is_sent_as_defaultPointId()
    {
        var handler = new RecordingHandler();

        await new WeatherApiClient(Api(handler)).SetPreferenceAsync(Point);

        Assert.Equal("/api/weather/preferences", handler.Last.RequestUri!.PathAndQuery);
        using var body = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal(Point, body.RootElement.GetProperty("defaultPointId").GetGuid());
    }
}

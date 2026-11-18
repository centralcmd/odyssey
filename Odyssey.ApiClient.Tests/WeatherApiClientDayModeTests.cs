using System.Globalization;
using System.Net;
using System.Text;
using Odyssey.ApiClient.Resources;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.ApiClient.Tests;

/// <summary><see cref="WeatherApiClient.HourlyForDayAsync"/>: the day-mode route and its wire shape (issue #414).</summary>
public class WeatherApiClientDayModeTests
{
    private sealed class StubHandler(string body, HttpStatusCode status = HttpStatusCode.OK, string mediaType = "application/json") : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) });
        }
    }

    private static WeatherApiClient Client(StubHandler handler) =>
        new(new OdysseyApi(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") }));

    [Fact]
    public async Task Without_a_point_it_sends_no_weatherPointId_so_the_server_resolves_the_callers_own()
    {
        var handler = new StubHandler(Body);

        await Client(handler).HourlyForDayAsync(new DateOnly(2026, 10, 25));

        Assert.DoesNotContain("weatherPointId", handler.LastRequest!.RequestUri!.Query, StringComparison.Ordinal);
        Assert.Contains("date=2026-10-25", handler.LastRequest.RequestUri.Query, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task A_failure_is_a_failure_carrying_its_status_and_no_value(HttpStatusCode status)
    {
        var handler = new StubHandler("{\"title\":\"Refused\"}", status, "application/problem+json");

        var result = await Client(handler).HourlyForDayAsync(new DateOnly(2026, 10, 25), Guid.NewGuid());

        Assert.False(result.IsSuccess);
        Assert.Equal(status, result.Status);
        Assert.Null(result.Value);
    }

    private const string Body = """
        {"point":{"weatherPointId":"6f9619ff-8b86-d011-b42d-00cf4fc964ff","name":"Oslo"},"status":"Ok","stale":false,
         "day":{"date":"2026-10-25","isToday":false,"fromUtc":"2026-10-24T22:00:00Z","toUtc":"2026-10-25T23:00:00Z","lengthHours":25},
         "steps":[{"timeUtc":"2026-10-25T00:00:00Z","periodHours":6,"localStart":"2026-10-25T02:00:00+02:00","localEnd":"2026-10-25T07:00:00+01:00",
                   "issuedAtUtc":"2026-10-24T10:00:00Z","symbolCode":"rain","airTemperatureC":6.4}],
         "attribution":{"text":"Weather data from MET Norway","license":"NLOD 2.0 / CC BY 4.0","url":"https://api.met.no/"}}
        """;

    [Theory]
    [InlineData("en-US")]
    [InlineData("nb-NO")]
    [InlineData("ar-SA")]
    public async Task It_sends_the_date_as_invariant_yyyy_MM_dd_whatever_the_culture(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo(culture);
        try
        {
            var handler = new StubHandler(Body);
            var client = new WeatherApiClient(new OdysseyApi(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") }));
            var point = Guid.Parse("6f9619ff-8b86-d011-b42d-00cf4fc964ff");

            var result = await client.HourlyForDayAsync(new DateOnly(2026, 10, 25), point);

            Assert.True(result.IsSuccess);
            var uri = handler.LastRequest!.RequestUri!;
            Assert.Equal("/api/weather/hourly", uri.AbsolutePath);
            var query = uri.Query.TrimStart('?').Split('&');
            Assert.Contains("date=2026-10-25", query);
            Assert.Contains($"weatherPointId={point}", query);
            Assert.DoesNotContain(query, q => q.StartsWith("fromUtc=", StringComparison.Ordinal) || q.StartsWith("toUtc=", StringComparison.Ordinal));
            Assert.Equal(new DateOnly(2026, 10, 25), result.Value!.Day!.Date);
            Assert.Equal(25m, result.Value.Day.LengthHours);
            var step = Assert.Single(result.Value.Steps);
            Assert.Equal(TimeSpan.FromHours(2), step.LocalStart!.Value.Offset);
            Assert.Equal(TimeSpan.FromHours(1), step.LocalEnd!.Value.Offset);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}

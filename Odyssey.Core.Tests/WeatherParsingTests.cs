using System.Net;
using System.Text;
using System.Text.Json;
using Odyssey.Core.Weather;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Core.Tests;

/// <summary>
/// The two MET parsers and their HTTP mapping (issue #348 §7.8, §8), against a REAL Locationforecast
/// <c>/complete</c> response recorded for Oslo (<c>Fixtures/Weather/met-oslo-complete.json</c>) and a Frost
/// response written from the documented schema — Frost cannot be recorded without a client id.
/// </summary>
public class WeatherParsingTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Weather", name));

    private static readonly WeatherSiteQuery Oslo = new(59.913m, 10.750m, 12);

    [Fact]
    public void Locationforecast_fixture_parses_every_step_with_its_period()
    {
        using var document = JsonDocument.Parse(Fixture("met-oslo-complete.json"));

        var payload = MetNorwayForecastSource.Parse(document.RootElement);

        Assert.NotNull(payload);
        Assert.Equal(new DateTime(2026, 10, 23, 21, 30, 7, DateTimeKind.Utc), payload!.UpdatedAtUtc);
        Assert.Equal(87, payload.Steps.Count);

        var first = payload.Steps[0];
        Assert.Equal(new DateTime(2026, 10, 23, 22, 0, 0, DateTimeKind.Utc), first.TimeUtc);
        Assert.Equal(1, first.PeriodHours);
        Assert.Equal(8.5m, first.AirTemperatureC);
        Assert.Equal("cloudy", first.SymbolCode);
        Assert.Equal(0.0m, first.PrecipitationMm);
        Assert.Equal(11.1m, first.PrecipitationProbabilityPct);
        Assert.Equal(1009.6m, first.AirPressureHpa);

        Assert.Equal(56, payload.Steps.Count(s => s.PeriodHours == 1 && s.SymbolCode is not null));
        var six = payload.Steps.Single(s => s.TimeUtc == new DateTime(2026, 10, 26, 6, 0, 0, DateTimeKind.Utc));
        Assert.Equal(6, six.PeriodHours);
        Assert.Equal("partlycloudy_day", six.SymbolCode);
        Assert.Equal(8.1m, six.TempMinC);
        Assert.Equal(12.2m, six.TempMaxC);
    }

    [Fact]
    public void Out_of_range_values_become_null_and_a_bad_symbol_is_dropped()
    {
        var json = """
        { "properties": { "meta": { "updated_at": "2026-10-23T21:30:07Z" }, "timeseries": [
          { "time": "2026-10-23T22:00:00Z", "data": {
            "instant": { "details": { "air_temperature": 75.0, "relative_humidity": 140, "wind_speed": 3.0 } },
            "next_1_hours": { "summary": { "symbol_code": "<script>" }, "details": { "precipitation_amount": 0.4 } } } } ] } }
        """;
        using var document = JsonDocument.Parse(json);

        var step = Assert.Single(MetNorwayForecastSource.Parse(document.RootElement)!.Steps);

        Assert.Null(step.AirTemperatureC);
        Assert.Null(step.RelativeHumidityPct);
        Assert.Null(step.SymbolCode);
        Assert.Equal(3.0m, step.WindSpeedMps);
        Assert.Equal(0.4m, step.PrecipitationMm);
    }

    [Theory]
    [InlineData("""{ "properties": { "timeseries": [] } }""")]
    [InlineData("""{ "properties": { "meta": { "updated_at": "2026-10-23T21:30:07Z" }, "timeseries": [] } }""")]
    [InlineData("""{ "type": "Feature" }""")]
    public void A_payload_without_updated_at_or_without_a_valid_step_is_malformed(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Null(MetNorwayForecastSource.Parse(document.RootElement));
    }

    [Fact]
    public async Task Request_carries_the_frozen_site_three_decimals_and_the_contact_user_agent()
    {
        HttpRequestMessage? sent = null;
        var source = new MetNorwayForecastSource(Client(request =>
        {
            sent = request;
            var response = Json(Fixture("met-oslo-complete.json"));
            response.Content.Headers.Expires = new DateTimeOffset(2026, 10, 23, 22, 5, 0, TimeSpan.Zero);
            response.Content.Headers.LastModified = new DateTimeOffset(2026, 10, 23, 21, 30, 7, TimeSpan.Zero);
            return response;
        }, MetNorwayForecastSource.BaseAddress));

        var result = await source.FetchAsync(Oslo, "ops@example.org", null, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(WeatherFetchOutcome.Ok, result.Outcome);
        Assert.Equal("https://api.met.no/weatherapi/locationforecast/2.0/complete?lat=59.913&lon=10.75&altitude=12", sent!.RequestUri!.ToString());
        Assert.Contains("ops@example.org", sent.Headers.UserAgent.ToString(), StringComparison.Ordinal);
        Assert.Equal(new DateTime(2026, 10, 23, 22, 5, 0, DateTimeKind.Utc), result.ExpiresUtc);
        Assert.NotNull(result.LastModified);
    }

    [Fact]
    public async Task Not_modified_is_its_own_outcome_and_keeps_the_validator()
    {
        var source = new MetNorwayForecastSource(Client(_ => new HttpResponseMessage(HttpStatusCode.NotModified), MetNorwayForecastSource.BaseAddress));

        var result = await source.FetchAsync(Oslo, "ops@example.org", "Fri, 23 Oct 2026 21:30:07 GMT", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(WeatherFetchOutcome.NotModified, result.Outcome);
        Assert.Equal("Fri, 23 Oct 2026 21:30:07 GMT", result.LastModified);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, WeatherFetchFailureKind.ProviderRejected)]
    [InlineData(HttpStatusCode.TooManyRequests, WeatherFetchFailureKind.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, WeatherFetchFailureKind.ProviderError)]
    [InlineData(HttpStatusCode.ServiceUnavailable, WeatherFetchFailureKind.ProviderError)]
    public async Task Provider_statuses_map_to_a_failure_kind_and_never_relay_the_body(HttpStatusCode status, WeatherFetchFailureKind kind)
    {
        var source = new MetNorwayForecastSource(Client(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent("secret provider text"),
        }, MetNorwayForecastSource.BaseAddress));

        var result = await source.FetchAsync(Oslo, "ops@example.org", null, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(WeatherFetchOutcome.Failed, result.Outcome);
        Assert.Equal(kind, result.FailureKind);
        Assert.Null(result.Payload);
    }

    [Fact]
    public async Task A_response_over_two_megabytes_is_too_large_and_nothing_is_parsed()
    {
        var big = "{\"properties\":{\"meta\":{\"updated_at\":\"2026-10-23T21:30:07Z\"},\"pad\":\"" + new string('x', MetNorwayForecastSource.MaxResponseBytes) + "\"}}";
        var source = new MetNorwayForecastSource(Client(_ => Json(big), MetNorwayForecastSource.BaseAddress));

        var result = await source.FetchAsync(Oslo, "ops@example.org", null, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(WeatherFetchFailureKind.ResponseTooLarge, result.FailureKind);
    }

    [Fact]
    public async Task A_slow_provider_times_out_within_the_budget()
    {
        var source = new MetNorwayForecastSource(Client(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return Json("{}");
        }, MetNorwayForecastSource.BaseAddress));

        var result = await source.FetchAsync(Oslo, "ops@example.org", null, TimeSpan.FromMilliseconds(200), CancellationToken.None);

        Assert.Equal(WeatherFetchFailureKind.Timeout, result.FailureKind);
    }

    [Fact]
    public void Frost_daily_parse_takes_the_06_utc_precipitation_day_and_drops_out_of_range_values()
    {
        using var document = JsonDocument.Parse(Fixture("frost-observations-daily.json"));

        var days = FrostObservationSource.Parse(document.RootElement);

        Assert.Equal(2, days.Count);
        var first = days[0];
        Assert.Equal(new DateOnly(2026, 10, 21), first.Date);
        Assert.Equal(6.8m, first.MeanTempC);
        Assert.Equal(3.6m, first.MinTempC);
        Assert.Equal(10.1m, first.MaxTempC);
        Assert.Equal(4.9m, first.MeanWindSpeedMps);
        Assert.Equal(15.2m, first.MaxWindGustMps);
        Assert.Equal(7.4m, first.PrecipitationMm);

        Assert.Equal(new DateOnly(2026, 10, 22), days[1].Date);
        Assert.Null(days[1].MinTempC);
        Assert.Equal(5.2m, days[1].MeanTempC);
    }

    [Fact]
    public async Task Frost_sends_basic_auth_and_treats_404_as_no_data()
    {
        HttpRequestMessage? sent = null;
        var source = new FrostObservationSource(Client(request =>
        {
            sent = request;
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }, FrostObservationSource.BaseAddress));

        var result = await source.FetchDailyAsync("SN18700", new DateOnly(2026, 10, 16), new DateOnly(2026, 10, 22),
            "ops@example.org", "client-id", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Empty(result.Days);
        Assert.Equal("Basic", sent!.Headers.Authorization!.Scheme);
        Assert.Equal(Convert.ToBase64String(Encoding.ASCII.GetBytes("client-id:")), sent.Headers.Authorization.Parameter);
        Assert.Contains("referencetime=2026-10-16/2026-10-23", Uri.UnescapeDataString(sent.RequestUri!.Query), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Frost_station_lookup_ignores_a_station_beyond_25_km()
    {
        var source = new FrostObservationSource(Client(_ => Json("""
            { "data": [ { "id": "SN99999", "distance": 31.2 } ] }
            """), FrostObservationSource.BaseAddress));

        var result = await source.FindStationAsync(Oslo, "ops@example.org", "client-id", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Null(result.StationId);
    }

    [Fact]
    public void Site_query_never_prints_its_coordinates()
    {
        Assert.DoesNotContain("59.913", Oslo.ToString(), StringComparison.Ordinal);
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> respond, string baseAddress) =>
        Client((request, _) => Task.FromResult(respond(request)), baseAddress);

    private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond, string baseAddress) =>
        new(new DelegateHandler(respond)) { BaseAddress = new Uri(baseAddress) };

    private sealed class DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request, cancellationToken);
    }
}

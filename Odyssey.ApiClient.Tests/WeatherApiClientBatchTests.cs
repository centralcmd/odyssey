using System.Net;
using System.Text;
using System.Text.Json;
using Odyssey.ApiClient.Resources;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.ApiClient.Tests;

/// <summary>
/// The event and entry weather reads on <see cref="IWeatherApiClient"/> (issue #430 §5.4): the batch methods
/// de-duplicate, chunk at <see cref="WeatherRules.BatchMaxIds"/>, send chunks one after another and are all or
/// nothing; the single reads hit their routes.
/// </summary>
public class WeatherApiClientBatchTests
{
    /// <summary>Echoes each requested id back as a journal or calendar item; the status of call N can be set.</summary>
    private sealed class EchoHandler(bool calendar, Dictionary<int, HttpStatusCode>? failAt = null) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            if (failAt is not null && failAt.TryGetValue(Requests.Count, out var status))
            {
                return Task.FromResult(new HttpResponseMessage(status)
                {
                    Content = new StringContent("{\"title\":\"Refused\"}", Encoding.UTF8, "application/problem+json"),
                });
            }

            var ids = request.RequestUri!.Query.TrimStart('?').Split('&').Select(p => p["ids=".Length..]).ToList();
            var key = calendar ? "calendarEventId" : "journalEntryId";
            var items = string.Join(",", ids.Select(id => $"{{\"{key}\":\"{id}\",\"status\":\"NoPlace\"}}"));
            var body = $"{{\"items\":[{items}],\"attribution\":{{\"text\":\"Weather data from MET Norway\",\"license\":\"NLOD 2.0 / CC BY 4.0\",\"url\":\"https://api.met.no/\"}}}}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private static WeatherApiClient Client(HttpMessageHandler handler) =>
        new(new OdysseyApi(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") }));

    [Fact]
    public async Task A_long_list_is_de_duplicated_chunked_and_merged_in_input_order()
    {
        var ids = Enumerable.Range(0, 110).Select(_ => Guid.NewGuid()).ToList();
        var input = ids.Concat(ids.Take(10)).ToList(); // 120 ids, 10 of them repeated
        var handler = new EchoHandler(calendar: false);

        var result = await Client(handler).JournalEntriesAsync(input);

        Assert.True(result.IsSuccess);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal([50, 50, 10], handler.Requests.Select(u => u.Query.Split('&').Length));
        Assert.All(handler.Requests, u => Assert.StartsWith("/api/journal-entries/weather", u.AbsolutePath));
        Assert.Equal(ids, result.Value!.Items.Select(i => i.JournalEntryId));
    }

    [Fact]
    public async Task A_failing_chunk_is_returned_and_no_later_chunk_is_sent()
    {
        var ids = Enumerable.Range(0, 120).Select(_ => Guid.NewGuid()).ToList();
        var handler = new EchoHandler(calendar: true, new Dictionary<int, HttpStatusCode> { [2] = HttpStatusCode.TooManyRequests });

        var result = await Client(handler).CalendarEventsAsync(ids);

        Assert.False(result.IsSuccess);
        Assert.Equal(HttpStatusCode.TooManyRequests, result.Status);
        Assert.Null(result.Value);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task An_empty_list_is_an_empty_success_without_a_request()
    {
        var handler = new EchoHandler(calendar: true);

        var result = await Client(handler).CalendarEventsAsync([]);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value!.Items);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Ids_are_sent_in_the_D_format_the_server_accepts()
    {
        var id = Guid.NewGuid();
        var handler = new EchoHandler(calendar: true);

        await Client(handler).CalendarEventsAsync([id]);

        Assert.Equal($"?ids={id:D}", Assert.Single(handler.Requests).Query);
        Assert.True(WeatherBatchIds.TryParse([id.ToString("D")], out _));
    }

    [Fact]
    public async Task The_single_reads_hit_their_routes()
    {
        var handler = new EchoHandler(calendar: true);
        var id = Guid.NewGuid();

        await Client(handler).CalendarEventAsync(id);
        await Client(handler).JournalEntryAsync(id);

        Assert.Equal([$"/api/calendar-events/{id}/weather", $"/api/journal-entries/{id}/weather"], handler.Requests.Select(u => u.AbsolutePath));
    }

    [Fact]
    public void The_batch_shapes_round_trip_by_name()
    {
        var json = """
            {"items":[{"calendarEventId":"6f9619ff-8b86-d011-b42d-00cf4fc964ff","status":"Ok","stale":false,"isAllDay":false,
              "summary":{"basis":"Hourly","symbolCode":"rain","minTempC":6,"maxTempC":9,"precipitationMm":0.4,"precipitationHours":3,"measured":false}}],
             "attribution":{"text":"t","license":"l","url":"u"}}
            """;

        var batch = JsonSerializer.Deserialize<CalendarEventWeatherBatch>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        Assert.Equal(WeatherSummaryBasis.Hourly, batch.Items[0].Summary!.Basis);
        Assert.Equal(3, batch.Items[0].Summary!.PrecipitationHours);
    }
}

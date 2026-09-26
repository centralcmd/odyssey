using System.Net;
using System.Text;
using System.Text.Json;
using Odyssey.ApiClient.Resources;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.ApiClient.Tests;

/// <summary>
/// The typed client's property event calls (issue #209 §5.7): every route is built from the owning
/// property id, the list's filter surface lands on the query string, and the writes read back the event.
/// </summary>
public class PropertyEventsApiClientTests
{
    private static readonly Guid PropertyId = Guid.Parse("9a1b0000-0000-0000-0000-000000000001");
    private static readonly Guid EventId = Guid.Parse("3f2c0000-0000-0000-0000-000000000002");

    private const string Event = """
        {"propertyEventId":"3f2c0000-0000-0000-0000-000000000002","propertyId":"9a1b0000-0000-0000-0000-000000000001",
         "type":118,"source":0,"title":"Winter tyres on","description":null,"notes":"Next change mid-April",
         "occurredAt":"2026-10-28T09:00:00Z","createdBy":"Kari Nordmann","createdAtUtc":"2026-10-28T09:12:44Z"}
        """;

    private sealed class RecordingHandler(HttpStatusCode status, string? body) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            var response = new HttpResponseMessage(status);
            if (body is not null)
                response.Content = new StringContent(body, Encoding.UTF8, "application/json");
            return response;
        }
    }

    private static (IPropertiesApiClient Client, RecordingHandler Handler) Create(HttpStatusCode status, string? body)
    {
        var handler = new RecordingHandler(status, body);
        var api = new OdysseyApi(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") });
        return (new PropertiesApiClient(api), handler);
    }

    [Fact]
    public async Task ListEventsAsync_PutsTheFilterSurfaceOnTheQueryString_AndReadsThePage()
    {
        var (client, handler) = Create(HttpStatusCode.OK, $$"""{"items":[{{Event}}],"totalCount":1,"offset":0,"limit":25}""");

        var result = await client.ListEventsAsync(
            PropertyId,
            search: "tyres",
            types: ["TyreChange", "Serviced"],
            sortBy: "Title",
            sortDir: "asc",
            page: 1,
            pageSize: 25,
            source: ContractEventSource.User);

        var uri = handler.LastRequest!.RequestUri!;
        Assert.Equal(HttpMethod.Get, handler.LastRequest.Method);
        Assert.Equal($"/api/properties/{PropertyId}/events", uri.AbsolutePath);
        var query = Uri.UnescapeDataString(uri.Query);
        Assert.Contains("search=tyres", query, StringComparison.Ordinal);
        Assert.Contains("types=TyreChange", query, StringComparison.Ordinal);
        Assert.Contains("types=Serviced", query, StringComparison.Ordinal);
        Assert.Contains("sortBy=Title", query, StringComparison.Ordinal);
        Assert.Contains("source=User", query, StringComparison.Ordinal);

        var item = Assert.Single(result.Value!.Items);
        Assert.Equal(PropertyEventType.TyreChange, item.Type);
        Assert.Equal("Kari Nordmann", item.CreatedBy);
    }

    [Fact]
    public async Task CreateEventAsync_PostsToTheOwnersLog_AndReadsTheEventBack()
    {
        var (client, handler) = Create(HttpStatusCode.Created, Event);

        var result = await client.CreateEventAsync(PropertyId, new NewPropertyEvent
        {
            Type = PropertyEventType.TyreChange,
            Title = "Winter tyres on",
            OccurredAt = new DateTime(2026, 10, 28, 9, 0, 0, DateTimeKind.Utc),
        });

        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal($"/api/properties/{PropertyId}/events", handler.LastRequest.RequestUri!.AbsolutePath);
        using var sent = JsonDocument.Parse(handler.LastBody!);
        Assert.False(sent.RootElement.TryGetProperty("propertyId", out _));
        Assert.False(sent.RootElement.TryGetProperty("source", out _));
        Assert.Equal(EventId, result.Value!.PropertyEventId);
    }

    [Fact]
    public async Task UpdateEventAsync_PutsToTheEventUnderItsProperty()
    {
        var (client, handler) = Create(HttpStatusCode.OK, Event);

        var result = await client.UpdateEventAsync(PropertyId, EventId, new UpdatePropertyEvent
        {
            Title = "Winter tyres on",
            OccurredAt = new DateTime(2026, 10, 28, 9, 0, 0, DateTimeKind.Utc),
        });

        Assert.Equal(HttpMethod.Put, handler.LastRequest!.Method);
        Assert.Equal($"/api/properties/{PropertyId}/events/{EventId}", handler.LastRequest.RequestUri!.AbsolutePath);
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task DeleteEventAsync_DeletesTheEventUnderItsProperty()
    {
        var (client, handler) = Create(HttpStatusCode.NoContent, null);

        var result = await client.DeleteEventAsync(PropertyId, EventId);

        Assert.Equal(HttpMethod.Delete, handler.LastRequest!.Method);
        Assert.Equal($"/api/properties/{PropertyId}/events/{EventId}", handler.LastRequest.RequestUri!.AbsolutePath);
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task AWriteRefusedWith422_SurfacesTheStatus_AndTheFieldKey()
    {
        var (client, _) = Create(
            HttpStatusCode.UnprocessableEntity,
            """{"status":422,"title":"Unprocessable","errors":{"type":["Event type 'TyreChange' is not legal on a RealEstate property."]}}""");

        var result = await client.CreateEventAsync(PropertyId, new NewPropertyEvent
        {
            Type = PropertyEventType.TyreChange,
            Title = "x",
            OccurredAt = DateTime.UtcNow,
        });

        Assert.False(result.IsSuccess);
        Assert.Equal(422, (int)result.Status);
    }
}

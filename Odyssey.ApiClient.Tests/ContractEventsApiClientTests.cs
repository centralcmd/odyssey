using System.Net;
using System.Text;
using Odyssey.ApiClient.Resources;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.ApiClient.Tests;

/// <summary>
/// The typed client for the contract event log (issue #138): the query string it builds and the paged
/// envelope it reads back.
/// </summary>
/// <remarks>
/// The parent scoping of the four routes is pinned in <c>ScopedWriteRouteTests</c> alongside the other
/// contract sub-resources; what is here is the query contract, which that theory does not reach.
/// </remarks>
public class ContractEventsApiClientTests
{
    private static readonly Guid ContractId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private sealed class RecordingHandler(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static (IContractsApiClient Client, RecordingHandler Handler) Create(string body)
    {
        var handler = new RecordingHandler(body);
        var api = new OdysseyApi(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") });
        return (new ContractsApiClient(api), handler);
    }

    private const string EmptyPage = """{"items":[],"offset":0,"limit":50,"totalCount":0}""";

    [Fact]
    public async Task ListEventsAsync_BuildsEveryFilterOntoTheQueryString()
    {
        var (client, handler) = Create(EmptyPage);

        await client.ListEventsAsync(
            ContractId,
            search: "rent increase",
            types: ["EmailSent", "TermChanged"],
            from: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            to: new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc),
            sortBy: "OccurredAt",
            sortDir: "Desc");

        var query = handler.LastRequest!.RequestUri!.Query;
        Assert.Contains("search=rent%20increase", query, StringComparison.Ordinal);
        Assert.Contains("types=EmailSent", query, StringComparison.Ordinal);
        Assert.Contains("types=TermChanged", query, StringComparison.Ordinal);
        Assert.Contains("from=2026-01-01", query, StringComparison.Ordinal);
        Assert.Contains("to=2026-12-31", query, StringComparison.Ordinal);
        Assert.Contains("sortBy=OccurredAt", query, StringComparison.Ordinal);
        Assert.Contains("sortDir=Desc", query, StringComparison.Ordinal);
    }

    /// <summary>
    /// Issue #154 §5.1 / #155 AC 21 — the <c>source</c> filter reaches the query string, and its
    /// parameter is the LAST one before the <see cref="CancellationToken"/>.
    /// </summary>
    /// <remarks>
    /// The position is asserted by reflection rather than by a positional call. Every call site uses
    /// named arguments today, so either position compiles — but a ten-parameter method is exactly
    /// where a positional call will eventually be written, and an inserted mid-signature parameter
    /// would silently re-bind every one of them.
    /// </remarks>
    [Fact]
    public async Task ListEventsAsync_CarriesTheSourceFilter_AsTheLastParameterBeforeTheToken()
    {
        var (client, handler) = Create(EmptyPage);

        await client.ListEventsAsync(ContractId, source: ContractEventSource.System);

        Assert.Contains("source=System", handler.LastRequest!.RequestUri!.Query, StringComparison.Ordinal);

        var parameters = typeof(IContractsApiClient)
            .GetMethod(nameof(IContractsApiClient.ListEventsAsync))!
            .GetParameters();

        Assert.Equal(typeof(CancellationToken), parameters[^1].ParameterType);
        Assert.Equal("source", parameters[^2].Name);
        Assert.Equal(typeof(ContractEventSource?), parameters[^2].ParameterType);
    }

    /// <summary>A filter the caller did not set contributes nothing — a blank value is dropped, not sent empty.</summary>
    [Fact]
    public async Task ListEventsAsync_OmitsUnsetFilters()
    {
        var (client, handler) = Create(EmptyPage);

        await client.ListEventsAsync(ContractId);

        var query = handler.LastRequest!.RequestUri!.Query;
        Assert.DoesNotContain("search=", query, StringComparison.Ordinal);
        Assert.DoesNotContain("types=", query, StringComparison.Ordinal);
        Assert.DoesNotContain("from=", query, StringComparison.Ordinal);
        Assert.DoesNotContain("sortBy=", query, StringComparison.Ordinal);
        // The v1 surface never sends it, so an omitted source must not become an empty parameter that
        // the server's [EnumDataType] would then reject.
        Assert.DoesNotContain("source=", query, StringComparison.Ordinal);
    }

    /// <summary>
    /// The default window asks for the whole log in one read, matching every other list client whose
    /// pager UI is deferred; a caller that pages supplies its own page and size.
    /// </summary>
    [Fact]
    public async Task ListEventsAsync_DefaultsToTheWholeSetAndHonoursAnExplicitWindow()
    {
        var (whole, wholeHandler) = Create(EmptyPage);
        await whole.ListEventsAsync(ContractId);
        Assert.Contains(
            $"offset=0&limit={PagedQuery.LimitAll}",
            wholeHandler.LastRequest!.RequestUri!.Query,
            StringComparison.Ordinal);

        var (windowed, windowedHandler) = Create(EmptyPage);
        await windowed.ListEventsAsync(ContractId, page: 3, pageSize: 20);
        Assert.Contains(
            "offset=40&limit=20", windowedHandler.LastRequest!.RequestUri!.Query, StringComparison.Ordinal);
    }

    /// <summary>
    /// The read projection carries a display LABEL and no user id, so a client that expected an id
    /// would be reading a property the server never sends (issue #138 §7.3).
    /// </summary>
    [Fact]
    public async Task ListEventsAsync_ReadsBackThePagedEnvelopeIncludingTheNotesAndTheAuthorLabel()
    {
        var (client, _) = Create("""
            {"items":[{
               "contractEventId":"22222222-2222-2222-2222-222222222222",
               "contractId":"11111111-1111-1111-1111-111111111111",
               "type":7,
               "title":"Emailed landlord about the rent increase",
               "description":"Asked for the CPI basis in writing.",
               "notes":"Chase on the 21st if no reply.",
               "occurredAt":"2026-06-14T09:31:00Z",
               "createdBy":"Jane Doe",
               "createdAtUtc":"2026-06-14T09:35:12Z"}],
             "offset":0,"limit":50,"totalCount":1}
            """);

        var result = await client.ListEventsAsync(ContractId);

        Assert.True(result.IsSuccess);
        var only = Assert.Single(result.Value!.Items);
        Assert.Equal(ContractEventType.EmailSent, only.Type);
        Assert.Equal("Chase on the 21st if no reply.", only.Notes);
        Assert.Equal("Jane Doe", only.CreatedBy);
        Assert.Equal(1, result.Value.TotalCount);
    }
}

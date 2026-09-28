using System.Net;
using System.Text;
using Odyssey.ApiClient.Resources;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.ApiClient.Tests;

/// <summary>
/// The typed client for the contract-scoped smart-tag match (issue #226): the route, the window and
/// the search/sort keys it puts on the query string, and the envelope it reads back — including the
/// empty reason, which crosses the wire as an ordinal.
/// </summary>
public class ContractSmartTagTransactionsApiClientTests
{
    private static readonly Guid ContractId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private sealed class RecordingHandler(string body) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private const string Envelope = """
        {
          "scope": { "from": "2026-01-01T00:00:00Z", "toExclusive": null, "smartTagCount": 2, "partyContactCount": 1, "emptyReason": 2 },
          "summary": { "transactionCount": 3, "byCurrency": [ { "currencyCode": "NOK", "transactionCount": 3, "totalIn": 0, "totalOut": 12.345678, "net": -12.345678 } ] },
          "page": { "items": [], "offset": 20, "limit": 10, "totalCount": 3 }
        }
        """;

    private static (IContractsApiClient Client, RecordingHandler Handler) Create()
    {
        var handler = new RecordingHandler(Envelope);
        var api = new OdysseyApi(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") });
        return (new ContractsApiClient(api), handler);
    }

    [Fact]
    public async Task Builds_the_scoped_route_with_the_window_search_and_sort()
    {
        var (client, handler) = Create();

        await client.ListSmartTagTransactionsAsync(ContractId, page: 3, pageSize: 10,
            search: "power & grid", sortBy: "Amount", sortDir: "Asc");

        var uri = handler.LastRequest!.RequestUri!;
        Assert.Equal($"/api/contracts/{ContractId}/smart-tag-transactions", uri.AbsolutePath);
        Assert.Contains("offset=20&limit=10", uri.Query);
        Assert.Contains("search=power%20%26%20grid", uri.Query);
        Assert.Contains("sortBy=Amount", uri.Query);
        Assert.Contains("sortDir=Asc", uri.Query);
    }

    [Fact]
    public async Task Leaves_blank_search_and_sort_off_the_query_string()
    {
        var (client, handler) = Create();

        await client.ListSmartTagTransactionsAsync(ContractId, page: 1, pageSize: 25);

        var query = handler.LastRequest!.RequestUri!.Query;
        Assert.DoesNotContain("search=", query);
        Assert.DoesNotContain("sortBy=", query);
    }

    [Fact]
    public async Task Reads_the_envelope_back_with_the_ordinal_reason_and_exact_amounts()
    {
        var (client, _) = Create();

        var result = await client.ListSmartTagTransactionsAsync(ContractId, page: 1, pageSize: 10);

        Assert.True(result.IsSuccess);
        var value = result.Value!;
        Assert.Equal(ContractSmartTagEmptyReason.NoContactParties, value.Scope.EmptyReason);
        Assert.Null(value.Scope.ToExclusive);
        Assert.Equal(12.345678m, Assert.Single(value.Summary.ByCurrency).TotalOut);
        Assert.Equal(3, value.Page.TotalCount);
    }
}

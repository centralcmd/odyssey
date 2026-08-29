using System.Net;
using Odyssey.ApiClient.Resources;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.ApiClient.Tests;

/// <summary>
/// Covers the query-string assembly the pages used to hand-build, and the <c>201</c>/<c>Location</c>
/// handling the create flows depend on.
/// </summary>
public class TransactionsApiClientTests
{
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        public HttpResponseMessage Response { get; set; } = Json("""{"items":[],"offset":0,"limit":50,"totalCount":0}""");

        public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(Response);
        }
    }

    private static NewTransaction SampleTransaction() =>
        new() { Description = "Rent", Amount = 1200m, AccountId = Guid.NewGuid() };

    private static (TransactionsApiClient Client, RecordingHandler Handler) Create()
    {
        var handler = new RecordingHandler();
        return (new TransactionsApiClient(new OdysseyApi(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") })), handler);
    }

    [Fact]
    public async Task ListAsync_windows_by_page_and_size()
    {
        var (client, handler) = Create();

        await client.ListAsync(page: 3, pageSize: 25);

        Assert.Contains("offset=50&limit=25", handler.LastRequest!.RequestUri!.Query);
    }

    [Fact]
    public async Task ListAsync_composes_every_filter()
    {
        var (client, handler) = Create();
        var account = Guid.NewGuid();
        var tag = Guid.NewGuid();

        await client.ListAsync(1, 25,
            search: "rent & utilities",
            accountIds: [account.ToString()],
            statuses: ["Approved"],
            tagIds: [tag.ToString()],
            direction: ["Expense"],
            sortBy: "date",
            sortDir: "desc");

        var query = handler.LastRequest!.RequestUri!.Query;
        Assert.Contains("search=rent%20%26%20utilities", query);
        Assert.Contains($"accountIds={account}", query);
        Assert.Contains("statuses=Approved", query);
        Assert.Contains($"tagIds={tag}", query);
        Assert.Contains("direction=Expense", query);
        Assert.Contains("sortBy=date", query);
        Assert.Contains("sortDir=desc", query);
    }

    // Direction is a two-value toggle: "neither" and "both" must mean no filter, otherwise selecting
    // both income and expense would return nothing.
    [Theory]
    [InlineData("")]                 // nothing selected
    [InlineData("Income,Expense")]   // both selected
    public async Task ListAsync_omits_direction_unless_exactly_one_is_selected(string selected)
    {
        var (client, handler) = Create();
        var direction = selected.Split(',', StringSplitOptions.RemoveEmptyEntries);

        await client.ListAsync(1, 25, direction: direction);

        Assert.DoesNotContain("direction=", handler.LastRequest!.RequestUri!.Query);
    }

    // ListAllAsync must not be truncated by a caller-supplied window — the reference-data contract.
    [Fact]
    public async Task ListAllAsync_requests_the_full_window()
    {
        var (client, handler) = Create();

        await client.ListAllAsync();

        Assert.Contains($"offset=0&limit={PagedQuery.LimitAll}", handler.LastRequest!.RequestUri!.Query);
    }

    [Fact]
    public async Task ListAllAsync_unwraps_the_paged_body_into_items()
    {
        var (client, handler) = Create();
        handler.Response = RecordingHandler.Json(
            """
            {"items":[{"transactionId":"11111111-1111-1111-1111-111111111111","description":"Rent",
                       "amount":1200,"timeStamp":"2030-01-01T00:00:00Z",
                       "accountId":"22222222-2222-2222-2222-222222222222"}],
             "offset":0,"limit":99999,"totalCount":1}
            """);

        var result = await client.ListAllAsync();

        Assert.True(result.IsSuccess);
        Assert.Single(result.ValueOr([]));
    }

    /// <summary>
    /// The create endpoints return <c>201</c> with an empty body, so the new id is only in the
    /// <c>Location</c> header — the attach-files step depends on reading it back.
    /// </summary>
    [Fact]
    public async Task CreateAsync_exposes_the_new_id_from_the_Location_header()
    {
        var (client, handler) = Create();
        var newId = Guid.NewGuid();
        handler.Response = new HttpResponseMessage(HttpStatusCode.Created)
        {
            Headers = { Location = new Uri($"http://localhost/api/transactions/{newId}") },
        };

        var result = await client.CreateAsync(SampleTransaction());

        Assert.True(result.IsSuccess);
        Assert.Equal(newId, result.CreatedId);
    }

    [Fact]
    public async Task CreateAsync_without_a_Location_header_reports_no_id_rather_than_throwing()
    {
        var (client, handler) = Create();
        handler.Response = new HttpResponseMessage(HttpStatusCode.Created);

        var result = await client.CreateAsync(SampleTransaction());

        Assert.True(result.IsSuccess);
        Assert.Null(result.CreatedId);
    }

    [Fact]
    public async Task DeleteAsync_surfaces_the_problem_detail()
    {
        var (client, handler) = Create();
        handler.Response = RecordingHandler.Json(
            """{"detail":"That transaction is referenced by a budget."}""", HttpStatusCode.Conflict);

        var result = await client.DeleteAsync(Guid.NewGuid());

        Assert.False(result.IsSuccess);
        Assert.Equal(HttpStatusCode.Conflict, result.Status);
        Assert.Equal("That transaction is referenced by a budget.", result.Error);
    }
}

using System.Net;
using System.Text;
using Odyssey.ApiClient.Resources;
using Xunit;

namespace Odyssey.ApiClient.Tests;

/// <summary>
/// The four <c>GetSummaryAsync</c> methods added by issue #372. Each is asserted on the two things a
/// typed client owes its caller: the exact route it hits (a wrong path would silently 404 into a
/// null, and the page would render its zero state forever), and that a failure surfaces as null
/// rather than an exception — the pages call these fire-and-forget alongside the list load.
/// </summary>
public class SummaryApiClientTests
{
    private sealed class RecordingHandler(HttpStatusCode status, string body) : HttpMessageHandler
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

    private static (IOdysseyApi Api, RecordingHandler Handler) Create(
        string body = "{}", HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new RecordingHandler(status, body);
        return (new OdysseyApi(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") }), handler);
    }

    [Fact]
    public async Task TransactionsGetSummaryAsync_hits_the_summary_route()
    {
        var (api, handler) = Create("""{"totalTransactions":7,"countsByStatus":{"new":3,"approved":2,"flagged":2},"totalIn":100,"totalOut":40}""");

        var summary = await new TransactionsApiClient(api).GetSummaryAsync();

        Assert.Equal("/api/transactions/summary", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Equal(7, summary!.TotalTransactions);
        Assert.Equal(3, summary.CountsByStatus.New);
        Assert.Equal(40m, summary.TotalOut);
    }

    [Fact]
    public async Task AccountsGetSummaryAsync_hits_the_summary_route()
    {
        var (api, handler) = Create("""{"totalAccounts":2,"countsByStatus":{"open":2,"closed":0,"archived":0},"combinedValue":180,"allocations":[{"accountId":"11111111-1111-1111-1111-111111111111","name":"Everyday","currencyCode":"NOK","value":300}]}""");

        var summary = await new AccountsApiClient(api).GetSummaryAsync();

        Assert.Equal("/api/accounts/summary", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Equal(180m, summary!.CombinedValue);
        var allocation = Assert.Single(summary.Allocations);
        Assert.Equal("Everyday", allocation.Name);
        Assert.Equal("NOK", allocation.CurrencyCode);
    }

    [Fact]
    public async Task BudgetsGetSummaryAsync_hits_the_summary_route()
    {
        var (api, handler) = Create("""{"totalBudgets":3,"activeCount":2,"archivedCount":1,"plannedBalance":3200}""");

        var summary = await new BudgetsApiClient(api).GetSummaryAsync();

        Assert.Equal("/api/budgets/summary", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Equal(2, summary!.ActiveCount);
        Assert.Equal(3200m, summary.PlannedBalance);
    }

    [Fact]
    public async Task TaxStatementsGetSummaryAsync_hits_the_summary_route()
    {
        var (api, handler) = Create("""{"totalStatements":2,"activeCount":2,"firstFiscalYear":2022,"latestFiscalYear":2024,"years":[{"fiscalYear":2022,"baseCurrencyCode":"NOK","declaredNetWorth":600000}]}""");

        var summary = await new TaxStatementsApiClient(api).GetSummaryAsync();

        Assert.Equal("/api/tax-statements/summary", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Equal(2024, summary!.LatestFiscalYear);
        Assert.Equal(600000m, Assert.Single(summary.Years).DeclaredNetWorth);
    }

    [Fact]
    public async Task GetSummaryAsync_returns_null_on_failure()
    {
        var (api, _) = Create("""{"title":"Server error"}""", HttpStatusCode.InternalServerError);

        Assert.Null(await new TransactionsApiClient(api).GetSummaryAsync());
        Assert.Null(await new AccountsApiClient(api).GetSummaryAsync());
        Assert.Null(await new BudgetsApiClient(api).GetSummaryAsync());
        Assert.Null(await new TaxStatementsApiClient(api).GetSummaryAsync());
    }
}

using System.Net;
using Odyssey.ApiClient.Resources;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.ApiClient.Tests;

public class AccountsApiClientTests
{
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        public HttpResponseMessage Response { get; set; } =
            new(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"items":[],"offset":0,"limit":50,"totalCount":0}""",
                                            System.Text.Encoding.UTF8, "application/json"),
            };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(Response);
        }
    }

    private static (AccountsApiClient Client, RecordingHandler Handler) Create()
    {
        var handler = new RecordingHandler();
        return (new AccountsApiClient(new OdysseyApi(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") })), handler);
    }

    private static readonly Guid Account = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Child = Guid.Parse("22222222-2222-2222-2222-222222222222");

    /// <summary>
    /// <c>AccountsQueryParams.Statuses</c> is an <b>array</b>, unlike the single-value <c>status</c>
    /// most other resources take — so it must bind as repeated <c>statuses=</c> pairs. Getting this
    /// wrong would silently drop the archived/active filter rather than fail.
    /// </summary>
    [Fact]
    public async Task ListAllAsync_sends_statuses_as_repeated_pairs()
    {
        var (client, handler) = Create();

        await client.ListAllAsync(types: ["Savings", "Checking"], statuses: ["Active", "Archived"]);

        var query = handler.LastRequest!.RequestUri!.Query;
        Assert.Contains("statuses=Active", query);
        Assert.Contains("statuses=Archived", query);
        Assert.DoesNotContain("status=Active", query);   // not the singular form
        Assert.Contains("types=Savings", query);
        Assert.Contains("types=Checking", query);
    }

    [Fact]
    public async Task ListAllAsync_requests_the_full_window()
    {
        var (client, handler) = Create();

        await client.ListAllAsync();

        Assert.Contains($"offset=0&limit={PagedQuery.LimitAll}", handler.LastRequest!.RequestUri!.Query);
    }

    /// <summary>
    /// Totals answer <c>503</c> when an exchange rate is missing. The Accounts card renders that as
    /// "totals unavailable" rather than an error, so the status must stay readable on the result.
    /// </summary>
    [Fact]
    public async Task GetTotalsAsync_keeps_ServiceUnavailable_distinguishable()
    {
        var (client, handler) = Create();
        handler.Response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("""{"detail":"No rate for NOK->USD."}""",
                                        System.Text.Encoding.UTF8, "application/problem+json"),
        };

        var result = await client.GetTotalsAsync("NOK");

        Assert.False(result.IsSuccess);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, result.Status);
    }

    [Fact]
    public async Task GetTotalsAsync_escapes_the_currency_parameter()
    {
        var (client, handler) = Create();

        await client.GetTotalsAsync("NO K");

        Assert.Contains("mainCurrency=NO%20K", handler.LastRequest!.RequestUri!.Query);
    }

    public static TheoryData<string, string> SubResourceRoutes() => new()
    {
        { "term",     $"/api/accounts/{Account}/terms/{Child}" },
        { "estimate", $"/api/accounts/{Account}/estimates/{Child}" },
        { "smarttag", $"/api/accounts/{Account}/smart-tags/{Child}" },
        { "file",     $"/api/accounts/{Account}/files/{Child}" },
    };

    /// <summary>Every account sub-resource DELETE is addressed through its parent account id.</summary>
    [Theory]
    [MemberData(nameof(SubResourceRoutes))]
    public async Task Sub_resource_deletes_are_scoped_to_the_account(string kind, string expectedPath)
    {
        var (client, handler) = Create();
        handler.Response = new HttpResponseMessage(HttpStatusCode.NoContent);

        Task<ApiResult> call = kind switch
        {
            "term" => client.DeleteTermAsync(Account, Child),
            "estimate" => client.DeleteEstimateAsync(Account, Child),
            "smarttag" => client.RemoveSmartTagAsync(Account, Child),
            _ => client.DetachFileAsync(Account, Child),
        };
        await call;

        Assert.Equal(expectedPath, handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Equal(HttpMethod.Delete, handler.LastRequest.Method);
    }

    /// <summary>Adding a smart tag is identified entirely by the path — there is no body to send.</summary>
    [Fact]
    public async Task AddSmartTagAsync_posts_to_the_scoped_route_with_no_body()
    {
        var (client, handler) = Create();
        handler.Response = new HttpResponseMessage(HttpStatusCode.NoContent);

        await client.AddSmartTagAsync(Account, Child);

        Assert.Equal($"/api/accounts/{Account}/smart-tags/{Child}", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Equal(HttpMethod.Post, handler.LastRequest.Method);
        Assert.Null(handler.LastRequest.Content);
    }

    [Fact]
    public async Task GetResumableAnalysisJobsAsync_targets_the_account_scoped_route()
    {
        var (client, handler) = Create();
        handler.Response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[]", System.Text.Encoding.UTF8, "application/json"),
        };

        await client.GetResumableAnalysisJobsAsync(Account);

        Assert.Equal($"/api/accounts/{Account}/files/analysis/resumable",
                     handler.LastRequest!.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task AnalyzeFileAsync_targets_the_account_and_file_scoped_route()
    {
        var (client, handler) = Create();
        handler.Response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);

        var result = await client.AnalyzeFileAsync(Account, Child, new AnalyzeFileRequest());

        Assert.Equal($"/api/accounts/{Account}/files/{Child}/analyze", handler.LastRequest!.RequestUri!.AbsolutePath);
        // The feature flag being off is a distinct phase in the dialog, not a generic failure.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, result.Status);
    }

    public static TheoryData<string, string> SubResourceUpdateRoutes() => new()
    {
        { "term",     $"/api/accounts/{Account}/terms/{Child}" },
        { "estimate", $"/api/accounts/{Account}/estimates/{Child}" },
        { "file",     $"/api/accounts/{Account}/files/{Child}" },
    };

    /// <summary>
    /// The same guarantee for the updates. Covering only the deletes left
    /// <c>UpdateTermAsync</c>/<c>UpdateEstimateAsync</c>/<c>UpdateFileAsync</c> free to be flattened
    /// without failing anything — verified by mutation, so this is the half that was missing.
    /// </summary>
    [Theory]
    [MemberData(nameof(SubResourceUpdateRoutes))]
    public async Task Sub_resource_updates_are_scoped_to_the_account(string kind, string expectedPath)
    {
        var (client, handler) = Create();
        handler.Response = new HttpResponseMessage(HttpStatusCode.NoContent);

        Task<ApiResult> call = kind switch
        {
            "term" => client.UpdateTermAsync(Account, Child, SampleTerm()),
            "estimate" => client.UpdateEstimateAsync(Account, Child, SampleEstimate()),
            _ => client.UpdateFileAsync(Account, Child, new UpdateAccountFileRequest()),
        };
        await call;

        Assert.Equal(expectedPath, handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Equal(HttpMethod.Put, handler.LastRequest.Method);
    }

    /// <summary>Creates are scoped to the parent collection, not a flat one.</summary>
    [Fact]
    public async Task Sub_resource_creates_post_to_the_parent_collection()
    {
        var (client, handler) = Create();
        handler.Response = new HttpResponseMessage(HttpStatusCode.Created);

        await client.AddTermAsync(Account, SampleTerm());
        Assert.Equal($"/api/accounts/{Account}/terms", handler.LastRequest!.RequestUri!.AbsolutePath);

        await client.AddEstimateAsync(Account, SampleEstimate());
        Assert.Equal($"/api/accounts/{Account}/estimates", handler.LastRequest!.RequestUri!.AbsolutePath);

        await client.AttachFileAsync(Account, new AttachAccountFileRequest(Child));
        Assert.Equal($"/api/accounts/{Account}/files", handler.LastRequest!.RequestUri!.AbsolutePath);
    }

    private static NewAccountTerm SampleTerm() => new()
    {
        TermKind = TermKind.InterestRate,
        ValueUnit = TermValueUnit.Percentage,
        Value = 3.5m,
        EffectiveFrom = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    private static NewAccountEstimate SampleEstimate() => new()
    {
        Value = 1000m,
        CurrencyCode = "NOK",
        EffectiveFrom = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };
}

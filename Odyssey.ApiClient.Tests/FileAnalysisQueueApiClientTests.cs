using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Odyssey.ApiClient.Resources;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.ApiClient.Tests;

/// <summary>
/// The typed client's share of the queued analysis (issue #445): a <c>202</c> is a success, a polled
/// <c>200</c> carries its <c>Retry-After</c>, and the code / id / limit helpers read only the shared constants.
/// </summary>
public class FileAnalysisQueueApiClientTests
{
    private sealed class StubHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond());
    }

    private static OdysseyApi Api(Func<HttpResponseMessage> respond) =>
        new(new HttpClient(new StubHandler(respond)) { BaseAddress = new Uri("http://localhost/") });

    private static ApiProblem Problem(string json) =>
        System.Text.Json.JsonSerializer.Deserialize<ApiProblem>(json, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;

    [Fact]
    public async Task Analyze_202_IsASuccess_WithItsBodyAndLocation()
    {
        var jobId = Guid.NewGuid();
        var accounts = new AccountsApiClient(Api(() =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                Content = new StringContent($$"""{"analysisJobId":"{{jobId}}"}""", Encoding.UTF8, "application/json"),
            };
            response.Headers.Location = new Uri($"http://localhost/api/file-analysis/{jobId}");
            return response;
        }));

        var result = await accounts.AnalyzeFileAsync(Guid.NewGuid(), Guid.NewGuid(), new AnalyzeFileRequest());

        Assert.True(result.IsSuccess);
        Assert.Equal(HttpStatusCode.Accepted, result.Status);
        Assert.Equal(jobId, result.Value!.AnalysisJobId);
        Assert.Equal(jobId, result.CreatedId);
    }

    [Fact]
    public async Task APolledSuccess_CarriesItsRetryAfter()
    {
        var api = Api(() =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(2));
            return response;
        });

        var result = await api.GetAsync<object>("api/file-analysis/x");

        Assert.True(result.IsSuccess);
        Assert.Equal(TimeSpan.FromSeconds(2), result.RetryAfter);
        Assert.Equal(TimeSpan.FromSeconds(2), result.RetryAfterDelta);
    }

    [Fact]
    public async Task RetryAfterDelta_IgnoresAnHttpDate_WhileRetryAfterStillParsesIt()
    {
        var now = new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
        var api = Api(() =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
            response.Headers.Date = now;
            response.Headers.RetryAfter = new RetryConditionHeaderValue(now.AddSeconds(5));
            return response;
        });

        var result = await api.GetAsync<object>("api/file-analysis/x");

        Assert.Null(result.RetryAfterDelta);
        Assert.Equal(TimeSpan.FromSeconds(5), result.RetryAfter);
    }

    [Fact]
    public void InProgress_ExposesTheOpenJobId()
    {
        var id = Guid.NewGuid();
        var problem = Problem($$"""{"status":409,"code":"{{FileAnalysisCodes.AnalysisInProgress}}","analysisJobId":"{{id}}"}""");

        Assert.True(problem.IsFileAnalysisInProgress());
        Assert.False(problem.IsFileAnalysisDisclosureChanged());
        Assert.True(problem.TryGetAnalysisJobId(out var parsed));
        Assert.Equal(id, parsed);
    }

    [Theory]
    [InlineData("""{"status":409,"code":"analysis_in_progress"}""")]
    [InlineData("""{"status":409,"code":"analysis_in_progress","analysisJobId":"not-a-guid"}""")]
    [InlineData("""{"status":409,"code":"analysis_in_progress","analysisJobId":42}""")]
    [InlineData("""{"status":409,"code":"analysis_in_progress","analysisJobId":"00000000-0000-0000-0000-000000000000"}""")]
    public void AMissingOrMalformedJobId_IsNeverParsed(string json)
    {
        Assert.False(Problem(json).TryGetAnalysisJobId(out _));
    }

    [Fact]
    public void TooLarge_ExposesTheServerLimit()
    {
        var problem = Problem($$"""{"status":422,"code":"{{FileAnalysisCodes.FileTooLargeForAnalysis}}","limitBytes":33554432}""");

        Assert.True(problem.IsFileAnalysisTooLarge());
        Assert.True(problem.TryGetAnalysisSizeLimit(out var limit));
        Assert.Equal(33554432L, limit);
    }

    [Fact]
    public void TooLarge_WithoutALimit_ReportsNone()
    {
        Assert.False(Problem("""{"status":422,"code":"file_too_large_for_analysis"}""").TryGetAnalysisSizeLimit(out _));
    }

    [Theory]
    [InlineData("feature_disabled", true)]
    [InlineData("configuration_unavailable", false)]
    [InlineData(null, false)]
    public void FeatureDisabled_MatchesOnlyItsCode(string? code, bool expected)
    {
        Assert.Equal(expected, new ApiProblem { Status = 503, Code = code }.IsFileAnalysisFeatureDisabled());
    }
}

using System.Net;
using System.Net.Http.Headers;
using Xunit;

namespace Odyssey.ApiClient.Tests;

/// <summary>
/// <c>ApiResult.RetryAfter</c> and the destination-changed code (issue #434): both <c>Retry-After</c>
/// forms, the absent and invalid cases, and the one literal the analyze dialog keys on.
/// </summary>
public class ApiRetryAfterTests
{
    private sealed class StubHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond());
    }

    private static OdysseyApi Api(Func<HttpResponseMessage> respond) =>
        new(new HttpClient(new StubHandler(respond)) { BaseAddress = new Uri("http://localhost/") });

    private static HttpResponseMessage TooMany(Action<HttpResponseMessage>? configure = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("""{"title":"Too many requests","status":429}""",
                System.Text.Encoding.UTF8, "application/problem+json"),
        };
        configure?.Invoke(response);
        return response;
    }

    [Fact]
    public async Task DeltaSeconds_IsCarriedOnTheFailure()
    {
        var api = Api(() => TooMany(r => r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(1500))));

        var result = await api.GetAsync<object>("api/x");

        Assert.Equal(HttpStatusCode.TooManyRequests, result.Status);
        Assert.Equal(TimeSpan.FromSeconds(1500), result.RetryAfter);
    }

    [Fact]
    public async Task HttpDate_IsMeasuredFromTheResponseDateHeader_NotTheClientClock()
    {
        var serverNow = new DateTimeOffset(2020, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var api = Api(() => TooMany(r =>
        {
            r.Headers.Date = serverNow;
            r.Headers.RetryAfter = new RetryConditionHeaderValue(serverNow.AddMinutes(10));
        }));

        var result = await api.GetAsync<object>("api/x");

        Assert.Equal(TimeSpan.FromMinutes(10), result.RetryAfter);
    }

    [Fact]
    public void HttpDate_InThePast_ClampsToZero()
    {
        var now = new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);
        using var response = TooMany(r => r.Headers.RetryAfter = new RetryConditionHeaderValue(now.AddMinutes(-5)));

        Assert.Equal(TimeSpan.Zero, ApiRetryAfter.Of(response, now));
    }

    [Fact]
    public async Task MissingHeader_IsNull()
    {
        var api = Api(() => TooMany());

        var result = await api.GetAsync<object>("api/x");

        Assert.Null(result.RetryAfter);
    }

    [Fact]
    public async Task InvalidHeader_IsNull()
    {
        var api = Api(() => TooMany(r => r.Headers.TryAddWithoutValidation("Retry-After", "soon")));

        var result = await api.GetAsync<object>("api/x");

        Assert.Null(result.RetryAfter);
    }

    [Fact]
    public async Task CastFailure_KeepsTheWait()
    {
        var api = Api(() => TooMany(r => r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30))));

        var result = (await api.GetAsync<object>("api/x")).CastFailure<string>();

        Assert.Equal(TimeSpan.FromSeconds(30), result.RetryAfter);
    }

    [Theory]
    [InlineData("file_analysis_destination_changed", true)]
    [InlineData("FILE_ANALYSIS_DESTINATION_CHANGED", false)]
    [InlineData("disclosure_changed", false)]
    [InlineData(null, false)]
    public void IsFileAnalysisDestinationChanged_MatchesOnlyTheCode(string? code, bool expected)
    {
        Assert.Equal(expected, new ApiProblem { Status = 409, Code = code }.IsFileAnalysisDestinationChanged());
    }

    [Fact]
    public void IsFileAnalysisDestinationChanged_IsFalseForNoProblem()
    {
        ApiProblem? none = null;

        Assert.False(none.IsFileAnalysisDestinationChanged());
    }
}

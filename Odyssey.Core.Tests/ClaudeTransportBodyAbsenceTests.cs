using System.Net;
using System.Text;
using Odyssey.Core.Finance;
using Odyssey.Testing.Http;
using Odyssey.Testing.Logging;
using Xunit;

namespace Odyssey.Core.Tests;

/// <summary>
/// Issue #602 §3.1 (security R2-N1, AC 20–21): the shared Claude transport never logs or throws a
/// provider body. A body is attacker-influenceable and can echo the document, so a failure carries the
/// status, the error type and the request id — and nothing else. Statement analysis rides the same
/// transport, so its exception message and log no longer carry the body either.
/// </summary>
public class ClaudeTransportBodyAbsenceTests
{
    private const string Marker = "BODY-MARKER-7f3a";

    private static (ClaudeFileAnalysisProvider Provider, RecordingLogger<ClaudeFileAnalysisProvider> Log) Over(HttpStatusCode status, string body)
    {
        var handler = new RecordingHttpHandler(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
            Headers = { { "request-id", "req_123" } },
        });
        var log = new RecordingLogger<ClaudeFileAnalysisProvider>();
        return (new ClaudeFileAnalysisProvider(new HttpClient(handler), log), log);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "{\"type\":\"error\",\"error\":{\"type\":\"api_error\",\"message\":\"" + Marker + "\"}}")]
    [InlineData(HttpStatusCode.BadRequest, "{\"type\":\"error\",\"error\":{\"type\":\"invalid_request_error\",\"message\":\"" + Marker + "\"}}")]
    [InlineData(HttpStatusCode.BadGateway, "<html>" + Marker + "</html>")]
    [InlineData(HttpStatusCode.TooManyRequests, "{\"error\":{\"type\":\"" + Marker + "\"}}")]
    public async Task A_provider_error_body_reaches_neither_the_exception_nor_the_log(HttpStatusCode status, string body)
    {
        var (provider, log) = Over(status, body);

        var ex = await Assert.ThrowsAsync<FileAnalysisProviderException>(() => provider.ExtractTransactionsAsync(
            [1, 2, 3], "text/plain", "USD", "Extract.", new FileAnalysisTarget("https://api.anthropic.com", "claude-sonnet-5"), 1024, null));

        Assert.DoesNotContain(Marker, ex.Message, StringComparison.Ordinal);
        Assert.Contains(((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture), ex.Message, StringComparison.Ordinal);
        Assert.All(log.Entries, e =>
        {
            Assert.DoesNotContain(Marker, e.Message, StringComparison.Ordinal);
            Assert.All(e.ArgumentText, a => Assert.DoesNotContain(Marker, a, StringComparison.Ordinal));
        });
        Assert.Contains(log.Entries, e => e.ArgumentText.Contains("req_123"));
    }
}

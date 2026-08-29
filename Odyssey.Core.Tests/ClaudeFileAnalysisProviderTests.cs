using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Odyssey.Core.Finance;

namespace Odyssey.Core.Tests;

/// <summary>
/// The outbound half of issue #439: where a request actually goes, and what happens when the host on
/// the other end does something other than answer.
///
/// <para>
/// <strong>Redirects are refused, not followed, and that is a security property rather than a
/// preference.</strong> .NET strips only <c>Authorization</c> across origins — a custom
/// <c>x-api-key</c> header survives — and a <c>307</c>/<c>308</c> preserves method and body, so the
/// full document would be re-POSTed too. Following one would make the bound this feature rests on
/// ("the key goes to the host that is set") untrue whenever the set host answers a redirect, and would
/// make <c>FileAnalysisJob.AnalyzerBaseUrlHost</c> record the configured host rather than the one the
/// data reached — breaking the Art. 30(1)(e) recipient record in exactly the case where it matters.
/// </para>
///
/// <para>
/// Registration pins <c>AllowAutoRedirect = false</c> on the primary handler; this tier proves the
/// other half, that a <c>3xx</c> arriving here becomes a curated provider error rather than being
/// chased. The <c>Location</c> header is treated exactly like a response body — attacker-influenceable
/// once the responding host is not one we control — so it reaches the log and nothing else.
/// </para>
/// </summary>
public class ClaudeFileAnalysisProviderTests
{
    private static readonly FileAnalysisTarget Target = new("https://gateway.internal", "claude-sonnet-5");

    /// <summary>Records every request URI it sees and answers from a scripted queue.</summary>
    private sealed class RecordingHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses)
        : HttpMessageHandler
    {
        public List<Uri> RequestUris { get; } = [];

        public List<string?> ApiKeysSeen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri!);
            ApiKeysSeen.Add(request.Headers.TryGetValues("x-api-key", out var values) ? values.First() : null);
            var index = Math.Min(RequestUris.Count - 1, responses.Length - 1);
            return Task.FromResult(responses[index](request));
        }
    }

    private static ClaudeFileAnalysisProvider ProviderOver(RecordingHandler handler)
    {
        // No BaseAddress — the destination is a per-call parameter now (issue #439). Setting one here
        // would mask the very thing these tests check.
        var client = new HttpClient(handler);
        client.DefaultRequestHeaders.Add("x-api-key", "sk-test-key");
        return new ClaudeFileAnalysisProvider(client, NullLogger<ClaudeFileAnalysisProvider>.Instance);
    }

    private static HttpResponseMessage Redirect(HttpStatusCode status, string location)
    {
        var response = new HttpResponseMessage(status);
        response.Headers.Location = new Uri(location);
        return response;
    }

    // ── AC 12's request-builder half ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The request URI is the base URL's authority plus <c>/v1/messages</c>, always. The relative part
    /// is root-absolute, so it replaces any path the base carries — which is exactly why the write
    /// validator rejects a non-empty path: the value that is accepted and the value that is used have
    /// to be the same value.
    /// </summary>
    [Theory]
    [InlineData("https://api.anthropic.com", "https://api.anthropic.com/v1/messages")]
    [InlineData("https://gateway.internal", "https://gateway.internal/v1/messages")]
    [InlineData("https://127.0.0.1:8443", "https://127.0.0.1:8443/v1/messages")]
    public async Task TheRequestGoesToTheTargetsAuthorityPlusTheFixedPath(string baseUrl, string expected)
    {
        var handler = new RecordingHandler(_ => Ok());
        var provider = ProviderOver(handler);

        await provider.ExtractTransactionsAsync(
            [1, 2, 3], "text/plain", "USD", "Extract.", new FileAnalysisTarget(baseUrl, "claude-sonnet-5"), 1024);

        Assert.Equal(new Uri(expected), Assert.Single(handler.RequestUris));
    }

    /// <summary>The model in the target is the model in the request body — not one the provider chose.</summary>
    [Fact]
    public async Task TheTargetsModel_IsWhatTheRequestBodyAsksFor()
    {
        string? body = null;
        var handler = new RecordingHandler(request =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Ok();
        });

        await ProviderOver(handler).ExtractTransactionsAsync(
            [1, 2, 3], "text/plain", "USD", "Extract.",
            new FileAnalysisTarget("https://gateway.internal", "claude-opus-5"), 1024);

        Assert.Contains("\"model\":\"claude-opus-5\"", body!, StringComparison.Ordinal);
    }

    // ── AC 27-28 — redirects ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// AC 27 — a <c>3xx</c> from the configured host makes <strong>no</strong> request to the redirect
    /// target and sends it no <c>x-api-key</c>. Both status codes that preserve method and body are
    /// covered, since those are the ones that would carry the document onward.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData(HttpStatusCode.PermanentRedirect)]
    public async Task ARedirect_IsRefused_WithNoRequestToTheRedirectTarget(HttpStatusCode status)
    {
        var handler = new RecordingHandler(_ => Redirect(status, "https://other.host/v1/messages"));
        var provider = ProviderOver(handler);

        await Assert.ThrowsAsync<FileAnalysisProviderException>(() =>
            provider.ExtractTransactionsAsync([1, 2, 3], "text/plain", "USD", "Extract.", Target, 1024));

        var uri = Assert.Single(handler.RequestUris);
        Assert.Equal("gateway.internal", uri.Host);
        Assert.DoesNotContain(handler.RequestUris, u => u.Host == "other.host");

        // The key went to the configured host and to nowhere else — the bound this feature rests on.
        Assert.Equal(["sk-test-key"], handler.ApiKeysSeen);
    }

    /// <summary>The match call takes the same handler, so it gets the same refusal.</summary>
    [Fact]
    public async Task ARedirect_IsRefusedOnTheMatchCallToo()
    {
        var handler = new RecordingHandler(_ =>
            Redirect(HttpStatusCode.PermanentRedirect, "https://other.host/v1/messages"));
        var provider = ProviderOver(handler);

        await Assert.ThrowsAsync<FileAnalysisProviderException>(() =>
            provider.MatchTransactionsAsync([], [], [], Target, 1024));

        Assert.DoesNotContain(handler.RequestUris, u => u.Host == "other.host");
    }

    /// <summary>
    /// AC 28 — the <c>Location</c> header reaches no user-facing surface. Same rule and same reasoning
    /// as a response body: a redirect target is chosen by the responding host, so it is exactly as
    /// attacker-influenceable. <c>FileAnalysisService</c> turns this exception into the curated
    /// <c>provider_error</c> message, so what matters here is that the message it could reflect carries
    /// nothing.
    /// </summary>
    [Fact]
    public async Task TheRedirectTarget_NeverAppearsInTheExceptionMessage()
    {
        var handler = new RecordingHandler(_ =>
            Redirect(HttpStatusCode.PermanentRedirect, "https://attacker.example/collect?exfil=1"));
        var provider = ProviderOver(handler);

        var exception = await Assert.ThrowsAsync<FileAnalysisProviderException>(() =>
            provider.ExtractTransactionsAsync([1, 2, 3], "text/plain", "USD", "Extract.", Target, 1024));

        Assert.DoesNotContain("attacker.example", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("exfil", exception.Message, StringComparison.OrdinalIgnoreCase);
        // Curated, and it says why — so an operator reading the job's failure has somewhere to go.
        Assert.Contains("redirect", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static HttpResponseMessage Ok() => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            """
            {
              "id": "msg_1",
              "model": "claude-sonnet-5",
              "content": [
                { "type": "tool_use", "name": "store_transactions", "input": { "transactions": [] } }
              ]
            }
            """,
            Encoding.UTF8, "application/json"),
    };
}

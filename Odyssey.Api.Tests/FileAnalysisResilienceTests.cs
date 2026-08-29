using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Odyssey.Api.Tests.Infrastructure;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// The file-analysis provider is the app's one third-party network call, so its <c>HttpClient</c>
/// carries the standard resilience handler (issue #382). These tests pin the two things that make it
/// useful: that a transient provider failure is actually retried, and that the configured
/// <c>FileAnalysis:TimeoutSeconds</c> budget survives as the <em>per-attempt</em> timeout rather than
/// being silently replaced by the handler's 10s default.
/// </summary>
public class FileAnalysisResilienceTests
{
    // AddHttpClient<IFileAnalysisProvider, ClaudeFileAnalysisProvider> names the client after the
    // interface; the standard handler registers its options under "<client name>-standard".
    private const string ClientName = "IFileAnalysisProvider";
    private const string ResilienceOptionsName = $"{ClientName}-standard";

    /// <summary>
    /// An ABSOLUTE request URI, because the typed client no longer has a <c>BaseAddress</c> (issue
    /// #439): the destination is admin-editable, so the provider resolves it per call from the settings
    /// snapshot. These tests exercise the resilience pipeline, not the destination, so any absolute
    /// address does.
    /// </summary>
    private const string Endpoint = "https://api.anthropic.com/v1/messages";

    private sealed class SequenceHandler(params HttpStatusCode[] statusCodes) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var status = statusCodes[Math.Min(CallCount, statusCodes.Length - 1)];
            CallCount++;
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }

    private static OdysseyApiFactory FactoryFor(
        SequenceHandler handler, IReadOnlyDictionary<string, string?>? configuration = null) =>
        new(configuration: configuration, configureServices: services =>
        {
            services.AddHttpClient(ClientName).ConfigurePrimaryHttpMessageHandler(() => handler);
            // Keep the retry ladder instant — the delays themselves are the library's concern, and
            // the default 2s exponential backoff would add seconds to the suite for nothing.
            services.Configure<HttpStandardResilienceOptions>(ResilienceOptionsName, options =>
            {
                options.Retry.Delay = TimeSpan.Zero;
                options.Retry.UseJitter = false;
            });
        });

    [Fact]
    public async Task TransientProviderFailure_IsRetried_AndSucceeds()
    {
        // Two 503s — an overloaded provider — then success. Before the resilience handler the first
        // 503 surfaced to the user as a failed analysis job.
        var handler = new SequenceHandler(
            HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);
        using var factory = FactoryFor(handler);

        var client = factory.Services.GetRequiredService<IHttpClientFactory>().CreateClient(ClientName);
        var response = await client.GetAsync(Endpoint);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, handler.CallCount);
    }

    [Fact]
    public async Task RetriesAreBounded_SoAFailingProviderStillFails()
    {
        var handler = new SequenceHandler(HttpStatusCode.TooManyRequests);
        using var factory = FactoryFor(handler);

        var client = factory.Services.GetRequiredService<IHttpClientFactory>().CreateClient(ClientName);
        var response = await client.GetAsync(Endpoint);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(3, handler.CallCount); // the initial attempt plus MaxRetryAttempts = 2
    }

    [Fact]
    public void ConfiguredTimeout_BecomesThePerAttemptBudget()
    {
        var handler = new SequenceHandler(HttpStatusCode.OK);
        using var factory = FactoryFor(handler, new Dictionary<string, string?>
        {
            ["FileAnalysis:TimeoutSeconds"] = "90",
        });

        var options = factory.Services
            .GetRequiredService<IOptionsMonitor<HttpStandardResilienceOptions>>()
            .Get(ResilienceOptionsName);

        Assert.Equal(TimeSpan.FromSeconds(90), options.AttemptTimeout.Timeout);
        // Two attempts' worth of headroom, and a sampling window of at least double the attempt
        // timeout — below that the options validator rejects the pipeline outright.
        Assert.Equal(TimeSpan.FromSeconds(180), options.TotalRequestTimeout.Timeout);
        Assert.True(options.CircuitBreaker.SamplingDuration >= options.AttemptTimeout.Timeout * 2);
    }
}

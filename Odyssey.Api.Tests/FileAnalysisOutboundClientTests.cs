using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using Odyssey.Api.Tests.Infrastructure;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// The outbound file-analysis client's <strong>registration</strong> (issue #439 §5.3a) — specifically
/// that its primary handler really is pinned to <c>AllowAutoRedirect = false</c>.
///
/// <para>
/// <strong>Why this needs its own test rather than being covered by the provider's.</strong>
/// <c>ClaudeFileAnalysisProviderTests</c> proves the other half — that a <c>3xx</c> which
/// <em>arrives</em> becomes a curated error — but it constructs its own <c>HttpClient</c> over a stub
/// handler, and so does every other double for this client. A stub handler never auto-follows a
/// redirect regardless of the flag, so none of those tests can tell "correctly wired" from "silently
/// deleted". Deleting the <c>ConfigurePrimaryHttpMessageHandler</c> line in <c>Program.cs</c> left the
/// entire suite green, on the fix for this feature's headline security finding.
/// </para>
///
/// <para>
/// What is at stake if it regresses: .NET strips only <c>Authorization</c> across origins, so a custom
/// <c>x-api-key</c> header survives a cross-host redirect, and a <c>307</c>/<c>308</c> preserves method
/// and body — the API key and the whole document would be re-POSTed to a host the administrator never
/// set, while <c>AnalyzerBaseUrlHost</c> went on recording the configured one.
/// </para>
/// </summary>
public class FileAnalysisOutboundClientTests
{
    /// <summary>
    /// <c>AddHttpClient&lt;IFileAnalysisProvider, ClaudeFileAnalysisProvider&gt;</c> names the client
    /// after the interface.
    /// </summary>
    private const string ClientName = "IFileAnalysisProvider";

    /// <summary>
    /// Replays the registered handler-builder actions onto a probe and inspects the primary handler
    /// they leave behind — the same actions <c>IHttpClientFactory</c> runs when it builds the pipeline,
    /// so this reads the real registration rather than a restatement of it.
    /// </summary>
    private sealed class ProbeBuilder(IServiceProvider services) : HttpMessageHandlerBuilder
    {
        public override string? Name { get; set; } = ClientName;

        /// <summary>
        /// Starts as the framework default so the assertion is meaningful: if the registration stopped
        /// pinning a handler, this is what would survive — and it follows redirects.
        /// </summary>
        public override HttpMessageHandler PrimaryHandler { get; set; } = new HttpClientHandler();

        public override IList<DelegatingHandler> AdditionalHandlers { get; } = [];

        /// <summary>
        /// Real services, because the resilience handler's action resolves its pipeline provider from
        /// here. Replaying the actions against a stub provider throws before the assertion is reached.
        /// </summary>
        public override IServiceProvider Services { get; } = services;

        public override HttpMessageHandler Build() => PrimaryHandler;
    }

    [Fact]
    public void TheFileAnalysisClient_PinsAPrimaryHandlerThatDoesNotFollowRedirects()
    {
        using var factory = new OdysseyApiFactory();

        var options = factory.Services
            .GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>()
            .Get(ClientName);

        var probe = new ProbeBuilder(factory.Services);
        foreach (var configure in options.HttpMessageHandlerBuilderActions)
        {
            configure(probe);
        }

        // A SocketsHttpHandler specifically, because that is the type whose AllowAutoRedirect the
        // registration sets; a plain HttpClientHandler would mean the pin was dropped.
        var primary = Assert.IsType<SocketsHttpHandler>(probe.PrimaryHandler);
        Assert.False(primary.AllowAutoRedirect,
            "The file-analysis client must not follow redirects: .NET keeps a custom x-api-key header "
            + "across origins, and a 307/308 re-POSTs the document with it.");
    }

    /// <summary>
    /// The default is <see langword="true"/>, so the assertion above is a real constraint rather than
    /// one the framework satisfies for free. Without this, a reader cannot tell whether the test would
    /// still pass with the registration line removed — which is exactly how the gap arose.
    /// </summary>
    [Fact]
    public void TheFrameworkDefault_WouldFollowRedirects()
    {
        using var handler = new SocketsHttpHandler();

        Assert.True(handler.AllowAutoRedirect);
    }
}

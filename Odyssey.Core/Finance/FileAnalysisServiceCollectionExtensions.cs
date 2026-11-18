using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;

namespace Odyssey.Core.Finance;

public static class FileAnalysisServiceCollectionExtensions
{
    /// <summary>
    /// The whole file-analysis graph, ONE registration both hosts call (issue #444): the API enqueues and
    /// still runs the synchronous match step; the worker performs the extraction transfer. Sharing it is
    /// what keeps the redirect, credential and retry posture identical on both sides of the queue.
    /// The caller provides <c>OdysseyContext</c>, <c>IMemoryCache</c>, <c>TimeProvider</c> and
    /// <c>ISecretSettingsReader</c>. The API also registers <see cref="FileAnalysisService"/> (enqueue,
    /// review, match, import), which the worker does not need.
    /// </summary>
    public static IServiceCollection AddFileAnalysis(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<FileAnalysisOptions>()
            .Bind(configuration.GetSection(FileAnalysisOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<IFileAnalysisPromptTemplate, EmbeddedFileAnalysisPromptTemplate>();
        services.AddScoped<IFileAnalysisSettingsLookup, FileAnalysisSettingsLookup>();
        services.AddScoped<FileAnalysisJobQueue>();
        services.AddScoped<FileAnalysisExtractionRunner>();

        // Transient, like every DelegatingHandler: IHttpClientFactory owns its lifetime and pools it with
        // the handler chain, so it must resolve nothing scoped in its constructor.
        services.AddTransient<FileAnalysisApiKeyHandler>();

        services.AddHttpClient<IFileAnalysisProvider, ClaudeFileAnalysisProvider>(client =>
        {
            // No BaseAddress (issue #439): the destination is admin-editable, resolved per run from the
            // settings snapshot and passed to the provider as part of FileAnalysisTarget. A registration-
            // time BaseAddress would be fixed at startup and could not follow a repoint — and would be the
            // value stamped on jobs while requests went somewhere else.
            //
            // The API key is not set here (issue #445 Wave 1): this callback is synchronous and evaluated
            // once, so it could never follow a rotation. FileAnalysisApiKeyHandler attaches it per request.
            client.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");

            // The resilience handler below owns every timeout: HttpClient.Timeout wraps the whole
            // pipeline, so leaving it set would cut the retries off mid-flight.
            client.Timeout = Timeout.InfiniteTimeSpan;
        })
            // Redirects are NOT followed (issue #439 §5.3a). .NET strips only Authorization across
            // origins — the custom x-api-key would survive, and a 307/308 preserves method and body, so
            // the key and the whole document would be re-POSTed to whatever host the configured one
            // names. A 3xx becomes a provider error instead; see ClaudeFileAnalysisProvider.SendAsync.
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })
            // OUTSIDE the resilience pipeline, deliberately (issue #445 Wave 1): handlers registered
            // earlier sit further out, so a missing or unreadable credential throws once here rather than
            // being retried and counted against the circuit breaker. A credential fault is not transient.
            .AddHttpMessageHandler<FileAnalysisApiKeyHandler>()
            // A transient 429/503 from the provider used to surface as a hard failure (issue #382).
            // Retries are safe: both call sites treat a failure as a recorded job/match failure, not a
            // partial write. Up to 3 HTTP attempts per run (MaxRetryAttempts = 2), each only after a
            // transient failure — "at most once" in issue #444 applies to RUNS, not to attempts.
            .AddStandardResilienceHandler()
            .Configure((resilience, sp) =>
            {
                var opts = sp.GetRequiredService<IOptions<FileAnalysisOptions>>().Value;
                var attempt = TimeSpan.FromSeconds(opts.TimeoutSeconds);

                // The configured budget is the per-attempt one; two attempts' worth caps the total
                // (FileAnalysisJobTiming.TotalRequestTimeout, which the worker's lease is derived from).
                // The sampling duration must be at least double the attempt timeout or the options
                // validator rejects the pipeline.
                resilience.AttemptTimeout.Timeout = attempt;
                resilience.TotalRequestTimeout.Timeout = FileAnalysisJobTiming.TotalRequestTimeout(opts.TimeoutSeconds);
                resilience.CircuitBreaker.SamplingDuration = attempt * 2;
                resilience.Retry.MaxRetryAttempts = 2;
            });

        return services;
    }
}

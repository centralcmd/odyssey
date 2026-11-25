using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Odyssey.Core.Ai;

namespace Odyssey.Core.Recipes.Import;

public static class RecipeImportServiceCollectionExtensions
{
    /// <summary>
    /// The recipe-import graph both hosts share (issue #602 §3): the settings lookup, the queue, the runner and the
    /// provider on its OWN named client and circuit breaker, so statement failures cannot open it nor the reverse. The
    /// caller must also call <c>AddFileAnalysis</c>, which registers the processor settings and the transport timeout.
    /// The API also registers <see cref="RecipeImportService"/>.
    /// </summary>
    public static IServiceCollection AddRecipeImport(this IServiceCollection services)
    {
        services.AddScoped<IRecipeImportSettingsLookup, RecipeImportSettingsLookup>();
        services.AddScoped<RecipeImportQueue>();
        services.AddScoped<RecipeImportRunner>();
        services.AddTransient<ClaudeApiKeyHandler>();

        services.AddHttpClient<IRecipeExtractionProvider, ClaudeRecipeExtractionProvider>(client =>
        {
            // No BaseAddress: the destination is admin-editable and comes from the import's checked snapshot.
            client.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
            client.Timeout = Timeout.InfiniteTimeSpan;
        })
            // Redirects are never followed: the key and every source would travel to an unconfigured host.
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })
            // Outside the resilience pipeline: a missing key is not transient and must not count against the breaker.
            .AddHttpMessageHandler<ClaudeApiKeyHandler>()
            .AddStandardResilienceHandler()
            .Configure((resilience, sp) =>
            {
                var timeout = Math.Min(sp.GetRequiredService<AiTransportOptions>().TimeoutSeconds, RecipeImportJobTiming.MaxTimeoutSeconds);
                var attempt = TimeSpan.FromSeconds(timeout);
                resilience.AttemptTimeout.Timeout = attempt;
                resilience.TotalRequestTimeout.Timeout = RecipeImportJobTiming.TotalRequestTimeout(timeout);
                resilience.CircuitBreaker.SamplingDuration = attempt * 2;

                // ONE transport retry (A16), and only on 429, 503 and 529 — the repeat transmission the consent names.
                resilience.Retry.MaxRetryAttempts = 1;
                resilience.Retry.ShouldHandle = args => ValueTask.FromResult(
                    args.Outcome.Result is { } response && (int)response.StatusCode is 429 or 503 or 529);
            });

        return services;
    }
}

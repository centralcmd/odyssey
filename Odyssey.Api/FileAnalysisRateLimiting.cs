namespace Odyssey.Api;

/// <summary>
/// The per-user limits on the two file-analysis calls that reach the third-party processor (issue #433
/// §5, OWASP LLM10). With the vocabulary cap retired, these and the payload budget are what bound
/// third-party spend.
/// </summary>
/// <remarks>
/// They stay in <c>RateLimiting:*</c> deploy-time configuration for the reason
/// <see cref="ProfileImageRateLimiting"/> gives: the partitioner caches its limiter per key, so an
/// admin-editable value would never reach a live partition. The defaults are a starting point, not a
/// measurement; a household importing many statements at once may want to raise the analyze limit.
/// </remarks>
public static class FileAnalysisRateLimiting
{
    public const string AnalyzePolicy = "file-analysis-analyze";
    public const string MatchPolicy = "file-analysis-match";

    public static IServiceCollection AddFileAnalysisRateLimiter(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddPerUserFixedWindowOptions<FileAnalysisAnalyzeRateLimitOptions>(
            configuration, FileAnalysisAnalyzeRateLimitOptions.SectionName);
        services.AddPerUserFixedWindowOptions<FileAnalysisMatchRateLimitOptions>(
            configuration, FileAnalysisMatchRateLimitOptions.SectionName);

        // Its own AddRateLimiter call setting neither OnRejected nor GlobalLimiter, so it composes with the
        // others and inherits IdentityRateLimiting's RFC 7807 429 with Retry-After.
        services.AddRateLimiter(options => options
            .AddPerUserFixedWindowPolicy<FileAnalysisAnalyzeRateLimitOptions>(AnalyzePolicy)
            .AddPerUserFixedWindowPolicy<FileAnalysisMatchRateLimitOptions>(MatchPolicy));

        return services;
    }
}

/// <summary>Bound from <c>RateLimiting:FileAnalysisAnalyze</c>: 10 analyses per user per hour.</summary>
public sealed class FileAnalysisAnalyzeRateLimitOptions()
    : PerUserFixedWindowRateLimitOptions(permitLimit: 10, windowSeconds: 3600)
{
    public const string SectionName = "RateLimiting:FileAnalysisAnalyze";
}

/// <summary>Bound from <c>RateLimiting:FileAnalysisMatch</c>: 30 matches per user per hour.</summary>
public sealed class FileAnalysisMatchRateLimitOptions()
    : PerUserFixedWindowRateLimitOptions(permitLimit: 30, windowSeconds: 3600)
{
    public const string SectionName = "RateLimiting:FileAnalysisMatch";
}

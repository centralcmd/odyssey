namespace Odyssey.Api;

/// <summary>
/// The per-user limit in front of the background-jobs manual run (issue #409 §3.5, S2): five requests a minute per
/// user, a fixed window, ahead of each job's own 60-second cooldown. Deploy-time configuration for the reason
/// <see cref="ProfileImageRateLimiting"/> gives.
/// </summary>
public static class BackgroundJobsRateLimiting
{
    public const string RunPolicy = "background-jobs-run";

    public static IServiceCollection AddBackgroundJobsRateLimiter(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddPerUserFixedWindowOptions<BackgroundJobsRunRateLimitOptions>(configuration, BackgroundJobsRunRateLimitOptions.SectionName);

        // Its own AddRateLimiter call setting neither OnRejected nor GlobalLimiter, so it composes with the others and
        // inherits IdentityRateLimiting's RFC 7807 429 — which adds retryAfterUtc for this policy.
        services.AddRateLimiter(options => options.AddPerUserFixedWindowPolicy<BackgroundJobsRunRateLimitOptions>(RunPolicy));
        return services;
    }
}

/// <summary>Bound from <c>RateLimiting:BackgroundJobsRun</c>: 5 manual-run requests per user per minute.</summary>
public sealed class BackgroundJobsRunRateLimitOptions()
    : PerUserFixedWindowRateLimitOptions(permitLimit: 5, windowSeconds: 60)
{
    public const string SectionName = "RateLimiting:BackgroundJobsRun";
}

namespace Odyssey.Api;

/// <summary>
/// The two per-user limits on weather (issue #348 §5; issue #387). Reads can trigger an on-demand MET request
/// for an untracked point, and the Admin's refresh, history purge and point delete each start outbound or bulk
/// work, so both are bounded. Deploy-time configuration for the reason <see cref="ProfileImageRateLimiting"/> gives.
/// </summary>
public static class WeatherRateLimiting
{
    public const string ReadPolicy = "weather-read";
    public const string RefreshPolicy = "weather-refresh";

    /// <summary>
    /// The event and entry weather batch reads (issue #430 §7.7): their own, tighter window, so a batch of up to
    /// <c>WeatherRules.BatchMaxIds</c> items cannot spend the single read's budget a hundred times over.
    /// </summary>
    public const string BatchPolicy = "weather-read-batch";

    public static IServiceCollection AddWeatherRateLimiter(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddPerUserFixedWindowOptions<WeatherReadRateLimitOptions>(configuration, WeatherReadRateLimitOptions.SectionName);
        services.AddPerUserFixedWindowOptions<WeatherRefreshRateLimitOptions>(configuration, WeatherRefreshRateLimitOptions.SectionName);
        services.AddPerUserFixedWindowOptions<WeatherReadBatchRateLimitOptions>(configuration, WeatherReadBatchRateLimitOptions.SectionName);

        // Its own AddRateLimiter call setting neither OnRejected nor GlobalLimiter, so it composes with the
        // others and inherits IdentityRateLimiting's RFC 7807 429.
        services.AddRateLimiter(options => options
            .AddPerUserFixedWindowPolicy<WeatherReadRateLimitOptions>(ReadPolicy)
            .AddPerUserFixedWindowPolicy<WeatherRefreshRateLimitOptions>(RefreshPolicy)
            .AddPerUserFixedWindowPolicy<WeatherReadBatchRateLimitOptions>(BatchPolicy));

        return services;
    }
}

/// <summary>Bound from <c>RateLimiting:WeatherRead</c>: 120 reads per user per minute.</summary>
public sealed class WeatherReadRateLimitOptions()
    : PerUserFixedWindowRateLimitOptions(permitLimit: 120, windowSeconds: 60)
{
    public const string SectionName = "RateLimiting:WeatherRead";
}

/// <summary>Bound from <c>RateLimiting:WeatherRefresh</c>: 6 refresh/purge/delete calls per user per 5 minutes.</summary>
public sealed class WeatherRefreshRateLimitOptions()
    : PerUserFixedWindowRateLimitOptions(permitLimit: 6, windowSeconds: 300)
{
    public const string SectionName = "RateLimiting:WeatherRefresh";
}

/// <summary>Bound from <c>RateLimiting:WeatherReadBatch</c>: 20 batch reads per user per minute (issue #430 §7.7).</summary>
public sealed class WeatherReadBatchRateLimitOptions()
    : PerUserFixedWindowRateLimitOptions(permitLimit: 20, windowSeconds: 60)
{
    public const string SectionName = "RateLimiting:WeatherReadBatch";
}

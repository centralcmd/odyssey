using System.ComponentModel.DataAnnotations;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;

namespace Odyssey.Api;

/// <summary>
/// The two limits on the geocoding search proxy (issue #350 §5 endpoint 5, S6, N1). The query leaves the
/// deployment for a third-party provider, so both a per-user and an instance-wide budget apply.
/// </summary>
/// <remarks>
/// <para>
/// <b>Per user first, then instance-wide.</b> The per-user limit is an ordinary
/// <see cref="PerUserFixedWindowRateLimit"/> policy, enforced by the rate-limiting middleware before the
/// action runs. The global limit is <see cref="LocationSearchGlobalLimiter"/>, acquired inside the action.
/// The order is load-bearing: a fixed-window permit already taken is not returned when a later limiter
/// rejects, so checking the global limit first would let one user's rejected spam drain the shared budget.
/// </para>
/// <para>
/// Both stay in <c>RateLimiting:*</c> deploy-time configuration for the reason
/// <see cref="ProfileImageRateLimiting"/> gives: the partitioner caches its limiter per key, so an
/// admin-editable value would never reach a live partition.
/// </para>
/// </remarks>
public static class LocationSearchRateLimiting
{
    public const string PerUserPolicy = "location-search";

    public static IServiceCollection AddLocationSearchRateLimiter(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddPerUserFixedWindowOptions<LocationSearchRateLimitOptions>(
            configuration, LocationSearchRateLimitOptions.SectionName);
        services.AddOptions<LocationSearchGlobalRateLimitOptions>()
            .Bind(configuration.GetSection(LocationSearchGlobalRateLimitOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<LocationSearchGlobalLimiter>();

        // Its own AddRateLimiter call setting neither OnRejected nor GlobalLimiter, so it composes with the
        // others and inherits IdentityRateLimiting's RFC 7807 429.
        services.AddRateLimiter(options => options
            .AddPerUserFixedWindowPolicy<LocationSearchRateLimitOptions>(PerUserPolicy));

        return services;
    }
}

/// <summary>Bound from <c>RateLimiting:LocationSearch</c>: 30 searches per user per minute.</summary>
public sealed class LocationSearchRateLimitOptions()
    : PerUserFixedWindowRateLimitOptions(permitLimit: 30, windowSeconds: 60)
{
    public const string SectionName = "RateLimiting:LocationSearch";
}

/// <summary>
/// Bound from <c>RateLimiting:LocationSearchGlobal</c>: 120 searches per minute across the whole
/// deployment, one partition, so provider traffic is capped however many users search. Note that four
/// users each at their own 30 reach it.
/// </summary>
public sealed class LocationSearchGlobalRateLimitOptions
{
    public const string SectionName = "RateLimiting:LocationSearchGlobal";

    [Range(1, int.MaxValue)]
    public int PermitLimit { get; set; } = 120;

    [Range(1, 86400)]
    public int WindowSeconds { get; set; } = 60;
}

/// <summary>The instance-wide half of the search limit — one fixed window for the whole process.</summary>
public sealed class LocationSearchGlobalLimiter : IDisposable
{
    private readonly FixedWindowRateLimiter limiter;

    public LocationSearchGlobalLimiter(IOptions<LocationSearchGlobalRateLimitOptions> options)
    {
        var limits = options.Value;
        limiter = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
        {
            PermitLimit = limits.PermitLimit,
            Window = TimeSpan.FromSeconds(limits.WindowSeconds),
            QueueLimit = 0,
        });
    }

    /// <summary>Takes one permit; the lease says whether it was granted and, if not, when to retry.</summary>
    public RateLimitLease Acquire() => limiter.AttemptAcquire();

    public void Dispose() => limiter.Dispose();
}

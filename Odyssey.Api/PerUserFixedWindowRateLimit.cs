using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Odyssey.Api;

/// <summary>
/// The shape every per-user fixed-window limit shares: a permit count per window, bound from a
/// <c>RateLimiting:*</c> section. Each subclass supplies its section name and its defaults; the bounds
/// live here once (issue #287 L7).
/// </summary>
public abstract class PerUserFixedWindowRateLimitOptions
{
    protected PerUserFixedWindowRateLimitOptions(int permitLimit, int windowSeconds)
    {
        PermitLimit = permitLimit;
        WindowSeconds = windowSeconds;
    }

    /// <summary>Requests one caller may make per window.</summary>
    [Range(1, int.MaxValue)]
    public int PermitLimit { get; set; }

    [Range(1, 86400)]
    public int WindowSeconds { get; set; }
}

/// <summary>
/// Registration and partitioning for <see cref="PerUserFixedWindowRateLimitOptions"/> policies, shared
/// by <see cref="AdminActionRateLimiting"/> and <see cref="ProfileImageRateLimiting"/>.
/// </summary>
internal static class PerUserFixedWindowRateLimit
{
    /// <summary>
    /// Binds <typeparamref name="TOptions"/> with <c>ValidateOnStart</c>, not just
    /// <c>ValidateDataAnnotations</c>: an out-of-range limit is a misconfigured security control, and
    /// surfacing it at startup beats discovering it on the first request that needed limiting.
    /// </summary>
    public static IServiceCollection AddPerUserFixedWindowOptions<TOptions>(
        this IServiceCollection services, IConfiguration configuration, string sectionName)
        where TOptions : PerUserFixedWindowRateLimitOptions
    {
        services.AddOptions<TOptions>()
            .Bind(configuration.GetSection(sectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        return services;
    }

    /// <summary>
    /// Adds a fixed-window policy partitioned per authenticated user. The partitioner runs per request,
    /// so the limits are resolved from options rather than captured at startup — the value a test (or a
    /// deployment) overrides is the one that applies.
    /// </summary>
    public static RateLimiterOptions AddPerUserFixedWindowPolicy<TOptions>(
        this RateLimiterOptions options, string policy)
        where TOptions : PerUserFixedWindowRateLimitOptions
    {
        options.AddPolicy(policy, context =>
        {
            var limits = context.RequestServices.GetRequiredService<IOptions<TOptions>>().Value;

            return RateLimitPartition.GetFixedWindowLimiter(
                $"{policy}:{UserPartitionKey(context)}",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = limits.PermitLimit,
                    Window = TimeSpan.FromSeconds(limits.WindowSeconds),
                    // Reject immediately rather than parking the request behind a queue slot.
                    QueueLimit = 0,
                });
        });

        return options;
    }

    // UseRateLimiter runs after UseAuthentication/UseAuthorization, so an authenticated user id is always
    // present on a request that reaches any of these endpoints; the fallback is defensive only, and errs
    // toward one shared bucket rather than an unpartitioned pass.
    private static string UserPartitionKey(HttpContext context) =>
        context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown";
}

using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Odyssey.Api;

/// <summary>
/// Concurrency limiter policies for the bulk import/export surfaces (issue #343 §5 design item 3).
/// Registered via a second, separate <c>AddRateLimiter</c> call rather than folded into
/// <see cref="IdentityRateLimiting"/>'s: <c>AddPolicy</c> registrations from multiple
/// <c>AddRateLimiter</c> calls compose (each just adds a dictionary entry to the same
/// <c>RateLimiterOptions</c>), but <c>OnRejected</c>/<c>GlobalLimiter</c> are plain property
/// assignments, not additive — a second assignment would silently replace, not compose with,
/// <see cref="IdentityRateLimiting"/>'s existing per-IP mail-endpoint window (issue #398/#393). This
/// class therefore deliberately does <b>not</b> set either: <see cref="IdentityRateLimiting"/>
/// remains the single owner of both, and its <c>OnRejectedAsync</c> already produces the correct
/// generic RFC 7807 429 for any non-mail-endpoint rejection — which is exactly what an import/export
/// concurrency rejection is.
/// <para>
/// A <see cref="ConcurrencyLimiter"/> has no fixed replenishment interval (a permit frees whenever an
/// in-flight request finishes, not on a clock), so its rejection lease carries no
/// <c>RetryAfter</c> metadata — <see cref="IdentityRateLimiting.OnRejectedAsync"/>'s existing
/// <c>TryGetMetadata</c> call is a no-op for these policies and the response is 429 with no
/// <c>Retry-After</c> header, which is the accurate signal for this limiter kind.
/// </para>
/// </summary>
public static class ImportExportRateLimiting
{
    /// <summary>
    /// Bounds concurrent bulk imports instance-wide (issue #343 §5): 2 permits, partitioned
    /// <b>globally</b> — not per-user — because the resource being protected is process memory, which
    /// one caller can exhaust alone. Queue depth 0: a queued import still holds its request body
    /// open, so a clear 429 beats parking the connection.
    /// </summary>
    public const string ImportConcurrencyPolicy = "ImportConcurrencyPolicy";

    /// <summary>
    /// Bounds concurrent bulk exports <b>per authenticated user</b> (issue #343 §5): 2 permits, queue
    /// depth 0. Deliberately not global — pairs with <c>ExportConcurrencyFilter</c>'s separate global
    /// ceiling (4 permits) to bound both blast radius (one caller) and aggregate hold (the instance),
    /// acquired in that order (§5 "Acquisition order, stated explicitly").
    /// </summary>
    public const string ExportConcurrencyPolicy = "ExportConcurrencyPolicy";

    private const string ImportPartitionKey = "import";
    private const int ImportPermitLimit = 2;
    private const int ExportPermitLimitPerUser = 2;

    public static IServiceCollection AddImportExportRateLimiter(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.AddPolicy(ImportConcurrencyPolicy, _ =>
                RateLimitPartition.GetConcurrencyLimiter(
                    ImportPartitionKey,
                    _ => new ConcurrencyLimiterOptions
                    {
                        PermitLimit = ImportPermitLimit,
                        QueueLimit = 0,
                    }));

            options.AddPolicy(ExportConcurrencyPolicy, context =>
                RateLimitPartition.GetConcurrencyLimiter(
                    UserPartitionKey(context),
                    _ => new ConcurrencyLimiterOptions
                    {
                        PermitLimit = ExportPermitLimitPerUser,
                        QueueLimit = 0,
                    }));
        });

        return services;
    }

    // UseRateLimiter runs after UseAuthentication/UseAuthorization, so an authenticated user id is
    // always present on every request that reaches a policy-tagged export endpoint; the fallback is
    // defensive only.
    private static string UserPartitionKey(HttpContext context) =>
        context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown";
}

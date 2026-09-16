using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Odyssey.Api;

/// <summary>
/// The concurrency limiter on <c>GET /api/accounts/net-worth-history</c> (issue #90 §10.3).
/// </summary>
/// <remarks>
/// <para>
/// This is a <b>resource bound</b>, and only that. Every request scans the full transaction history —
/// the opening seed needs it, so the cost is invariant in <c>from</c> — which is worth bounding on a
/// surface any authenticated reader can call. It is deliberately <b>not</b> one of the conditions
/// §10.3's accepted disclosure rests on: a <see cref="ConcurrencyLimiter"/> caps requests
/// <i>in flight</i>, and the ~120 <i>sequential</i> requests that would walk a decade at
/// <c>Daily</c> resolution pass a 2-permit limiter untouched. Reading it as a constraint on that walk
/// would be a mistake; the conditions are AC13 and the fact that the caller already holds
/// <c>transactions.read</c>.
/// </para>
/// <para>
/// Partitioned <b>per authenticated user</b>, following the export half of the import/export
/// precedent — the import policy next to it partitions on a constant key, which is a global ceiling
/// on the whole instance and a very different thing to copy by accident.
/// </para>
/// <para>
/// The export precedent also holds a separate global ceiling acquired after the per-user one, which
/// bounds aggregate hold as well as blast radius. <b>Omitting that here is a deliberate choice</b> at
/// family scale rather than an oversight: aggregate database load across users is correspondingly
/// unbounded, and a deployment that outgrows that assumption should add the global half rather than
/// raise this one.
/// </para>
/// <para>
/// Registered via <c>AddPolicy</c> only. <c>IdentityRateLimiting</c> owns <c>OnRejected</c> and
/// <c>GlobalLimiter</c>, which are plain property assignments rather than additive registrations, so
/// setting either here would silently replace its per-IP mail-endpoint window. Its
/// <c>OnRejectedAsync</c> already produces the correct generic RFC 7807 <c>429</c>, with no
/// <c>Retry-After</c> — the accurate signal for a limiter whose permits free on completion rather
/// than on a clock.
/// </para>
/// </remarks>
public static class NetWorthHistoryRateLimiting
{
    public const string NetWorthHistoryConcurrencyPolicy = "NetWorthHistoryConcurrencyPolicy";

    private const int PermitLimitPerUser = 2;

    public static IServiceCollection AddNetWorthHistoryRateLimiter(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
            options.AddPolicy(NetWorthHistoryConcurrencyPolicy, context =>
                RateLimitPartition.GetConcurrencyLimiter(
                    UserPartitionKey(context),
                    _ => new ConcurrencyLimiterOptions
                    {
                        PermitLimit = PermitLimitPerUser,
                        // Queue depth 0: a queued request holds a connection open for a scan that is
                        // already the expensive part, so a clear 429 beats parking it.
                        QueueLimit = 0,
                    })));

        return services;
    }

    // UseRateLimiter runs after UseAuthentication/UseAuthorization, so an authenticated user id is
    // always present on a request that reaches this claim-gated endpoint; the fallback is defensive.
    private static string UserPartitionKey(HttpContext context) =>
        context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown";
}

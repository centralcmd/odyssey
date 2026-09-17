using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Odyssey.Api;

/// <summary>
/// Per-<b>caller</b> fixed-window limits on the three profile-picture endpoints (issue #94 §10.12).
/// <b>Three policies, not one</b>, and each of the three splits is load-bearing.
/// </summary>
/// <remarks>
/// <para>
/// Registered via its own <c>AddRateLimiter</c> call that sets <b>neither <c>OnRejected</c> nor
/// <c>GlobalLimiter</c></b>, following <see cref="AdminActionRateLimiting"/>. <c>AddPolicy</c>
/// registrations from multiple calls compose, but those two are plain property assignments that would
/// silently replace <see cref="IdentityRateLimiting"/>'s per-IP mail window. Setting neither also
/// inherits that class's <c>OnRejectedAsync</c> — the RFC 7807 <c>429</c> with <c>Retry-After</c> —
/// for free.
/// </para>
/// <para>
/// <b>Why these numbers stay in <c>RateLimiting:*</c> deploy-time configuration</b> rather than moving
/// into the admin-editable settings store: the partitioner is synchronous and caches its limiter per
/// partition key, so a changed limit would reach a partition that has never been seen before and never
/// reach a live one. That is the "I changed the limit and it did nothing" failure the settings feature
/// exists to refuse — and worse, because it is intermittent. Same reasoning as the existing
/// <c>RateLimiting:*</c> Non-Goal.
/// </para>
/// </remarks>
public static class ProfileImageRateLimiting
{
    /// <summary>
    /// Bounds the write. A double container walk over attacker-supplied bytes, reachable by every
    /// signed-in user, makes this the cheapest CPU-amplification primitive in the application.
    ///
    /// <para>
    /// <b>Sized against the GLOBAL upload cap, not the 2 MB surface cap.</b> <c>IFormFile.Length</c>
    /// exists only after multipart parsing and <c>[UploadSizeLimit]</c> resolves the <i>global</i>
    /// ceiling, so the action's own 2 MB check rejects a body the transport has already admitted — the
    /// real per-user I/O budget is this permit limit × the global cap.
    /// </para>
    /// </summary>
    public const string WritePolicy = "profile-image-write";

    /// <summary>
    /// Bounds the delete, on a <b>separate budget</b> from the write. Sharing one would mean a user who
    /// has exhausted it uploading cannot <b>remove</b> their picture — throttling the GDPR Art. 17
    /// self-service control itself, which is not an acceptable failure mode.
    /// </summary>
    public const string DeletePolicy = "profile-image-delete";

    /// <summary>
    /// Bounds the read. Unlimited would be wrong — it is <c>no-cache</c>, up to 2 MB per response, and
    /// id-feasible at scale given the raw user ids the two finance file surfaces already leak — but it
    /// must be generous enough that a <c>/users</c> page at maximum size plus the header, on a cold
    /// cache, is never affected. The worst case it is sized against is 100 rows + 1 header image in one
    /// minute, and the default triples that.
    ///
    /// <para>
    /// <b>It fails silently to the user, by design.</b> A <c>429</c> on an <c>&lt;img&gt;</c> reaches
    /// the error handler, which swaps to the monogram and surfaces nothing. A rejected read is
    /// therefore a <i>server-side metric</i>, never a user-visible error — which is also why it must
    /// not be sized tightly.
    /// </para>
    /// </summary>
    public const string ReadPolicy = "profile-image-read";

    public static IServiceCollection AddProfileImageRateLimiter(
        this IServiceCollection services, IConfiguration configuration)
    {
        // ValidateOnStart, not just ValidateDataAnnotations: an out-of-range limit is a misconfigured
        // security control, and surfacing it at startup beats discovering it on the first request that
        // needed limiting.
        services.AddOptions<ProfileImageWriteRateLimitOptions>()
            .Bind(configuration.GetSection(ProfileImageWriteRateLimitOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<ProfileImageDeleteRateLimitOptions>()
            .Bind(configuration.GetSection(ProfileImageDeleteRateLimitOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<ProfileImageReadRateLimitOptions>()
            .Bind(configuration.GetSection(ProfileImageReadRateLimitOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddRateLimiter(options =>
        {
            // The partitioner runs per request, so the limits are resolved from options rather than
            // captured at startup — the value a test (or a deployment) overrides is the one that applies.
            options.AddPolicy(WritePolicy, context =>
            {
                var limits = context.RequestServices
                    .GetRequiredService<IOptions<ProfileImageWriteRateLimitOptions>>().Value;

                return FixedWindowPerCaller(WritePolicy, context, limits.PermitLimit, limits.WindowSeconds);
            });

            options.AddPolicy(DeletePolicy, context =>
            {
                var limits = context.RequestServices
                    .GetRequiredService<IOptions<ProfileImageDeleteRateLimitOptions>>().Value;

                return FixedWindowPerCaller(DeletePolicy, context, limits.PermitLimit, limits.WindowSeconds);
            });

            options.AddPolicy(ReadPolicy, context =>
            {
                var limits = context.RequestServices
                    .GetRequiredService<IOptions<ProfileImageReadRateLimitOptions>>().Value;

                return FixedWindowPerCaller(ReadPolicy, context, limits.PermitLimit, limits.WindowSeconds);
            });
        });

        return services;
    }

    private static RateLimitPartition<string> FixedWindowPerCaller(
        string policy, HttpContext context, int permitLimit, int windowSeconds) =>
        RateLimitPartition.GetFixedWindowLimiter(
            $"{policy}:{CallerPartitionKey(context)}",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permitLimit,
                Window = TimeSpan.FromSeconds(windowSeconds),
                // Reject immediately rather than parking the request behind a queue slot.
                QueueLimit = 0,
            });

    // UseRateLimiter runs after UseAuthentication/UseAuthorization, so an authenticated user id is
    // always present on a request that reaches any of these endpoints; the fallback is defensive only,
    // and errs toward one shared bucket rather than an unpartitioned pass.
    private static string CallerPartitionKey(HttpContext context) =>
        context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown";
}

/// <summary>
/// Bound from <c>RateLimiting:ProfileImageWrite</c> (issue #94 §10.12). Twelve uploads per five
/// minutes is far more than a person setting their own picture needs, and a hard bound on a script
/// spending the double container walk.
/// </summary>
public sealed class ProfileImageWriteRateLimitOptions
{
    public const string SectionName = "RateLimiting:ProfileImageWrite";

    /// <summary>Profile-picture uploads one caller may make per window.</summary>
    [Range(1, int.MaxValue)]
    public int PermitLimit { get; set; } = 12;

    [Range(1, 86400)]
    public int WindowSeconds { get; set; } = 300;
}

/// <summary>
/// Bound from <c>RateLimiting:ProfileImageDelete</c>. A <b>separate</b> budget from the write, so
/// exhausting the upload window never costs a user the ability to erase their own picture.
/// </summary>
public sealed class ProfileImageDeleteRateLimitOptions
{
    public const string SectionName = "RateLimiting:ProfileImageDelete";

    /// <summary>Profile-picture removals one caller may make per window.</summary>
    [Range(1, int.MaxValue)]
    public int PermitLimit { get; set; } = 12;

    [Range(1, 86400)]
    public int WindowSeconds { get; set; } = 300;
}

/// <summary>
/// Bound from <c>RateLimiting:ProfileImageRead</c>. Generous on purpose: a <c>/users</c> page at
/// maximum size on a cold cache issues one conditional request per renderable row, and a rejected read
/// is invisible to the user (it degrades to the monogram), so a tight limit would produce a silent,
/// hard-to-diagnose defect rather than an error anybody reports.
/// </summary>
public sealed class ProfileImageReadRateLimitOptions
{
    public const string SectionName = "RateLimiting:ProfileImageRead";

    /// <summary>Profile-picture reads one caller may make per window.</summary>
    [Range(1, int.MaxValue)]
    public int PermitLimit { get; set; } = 300;

    [Range(1, 86400)]
    public int WindowSeconds { get; set; } = 60;
}

using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Odyssey.Api;

/// <summary>
/// Per-<b>actor</b> fixed-window limits on the admin endpoints that need one — the two credential
/// endpoints of issue #406 §7, and the secret-settings writes of issue #444 §7.
/// </summary>
/// <remarks>
/// <para>
/// Registered via a second, separate <c>AddRateLimiter</c> call, following
/// <see cref="ImportExportRateLimiting"/> rather than <see cref="IdentityRateLimiting"/>:
/// <c>AddPolicy</c> registrations from multiple calls compose (each adds a dictionary entry to the same
/// <c>RateLimiterOptions</c>), but <c>OnRejected</c>/<c>GlobalLimiter</c> are plain property assignments
/// that would silently replace <see cref="IdentityRateLimiting"/>'s per-IP mail window. This class
/// therefore sets neither, and inherits that class's <c>OnRejectedAsync</c> — the RFC 7807 429 with
/// <c>Retry-After</c> — for free. Housing these policies in <see cref="IdentityRateLimiting"/> would also
/// be a cohesion mismatch: that class is scoped to the anonymous, root-mapped Identity endpoints, and
/// neither of these is anonymous or Identity-mapped.
/// </para>
/// <para>
/// Two pipeline facts this relies on, verified rather than assumed: <c>app.UseRateLimiter()</c> runs after
/// <c>UseAuthentication()</c>/<c>UseAuthorization()</c>, so the caller's <c>NameIdentifier</c> is populated
/// when a policy partitions; and <c>app.MapControllers()</c> carries no group-level rate-limit policy, so
/// the "a second policy on the same endpoint replaces the first" trap that affects <c>MapIdentityApi</c>'s
/// shared group does not apply here.
/// </para>
/// </remarks>
public static class AdminActionRateLimiting
{
    /// <summary>
    /// Bounds how many users one admin can force into the password gate. The per-recipient email throttle
    /// does not bound a sweep — the reset rotates the stamp and sets the flag for each <em>distinct</em>
    /// target regardless of another recipient's throttle state — so without this, one admin session (or a
    /// compromised admin credential) could loop the endpoint across every user id and force the entire
    /// user base to change their password in a single script. That is a system-wide availability event,
    /// and a quiet one.
    /// </summary>
    public const string PasswordResetPolicy = "admin-password-reset";

    /// <summary>
    /// Bounds guessing at the current password on <c>POST /api/account/password</c> — the one endpoint a
    /// password-gated session can write to, verifying the very password that may already be compromised.
    /// Identity's lockout accounting is the primary control (the endpoint wires it explicitly); this
    /// bounds the slow drip that repeatedly waits out a lockout window.
    /// </summary>
    public const string PasswordChangePolicy = "account-password-change";

    /// <summary>
    /// Bounds writes to the encrypted secret store (issue #444 §7). These endpoints carry no limit of
    /// their own otherwise: <c>app.MapControllers()</c> attaches no group-level rate-limit policy in
    /// this pipeline, so the "inherits the existing rate limiting" assumption a first draft made is
    /// simply false. Credential replacement is the highest-value write an admin session can make, and
    /// this bounds how many of them one compromised session can perform before anyone notices.
    /// </summary>
    public const string SecretWritePolicy = "system-settings-secret-write";

    public static IServiceCollection AddAdminActionRateLimiter(
        this IServiceCollection services, IConfiguration configuration)
    {
        // ValidateOnStart, not just ValidateDataAnnotations: an out-of-range limit is a misconfigured
        // security control, and surfacing it at startup beats discovering it on the first request that
        // needed limiting. Mirrors IdentityRateLimitOptions.
        services.AddOptions<AdminPasswordResetRateLimitOptions>()
            .Bind(configuration.GetSection(AdminPasswordResetRateLimitOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<PasswordChangeRateLimitOptions>()
            .Bind(configuration.GetSection(PasswordChangeRateLimitOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<SecretWriteRateLimitOptions>()
            .Bind(configuration.GetSection(SecretWriteRateLimitOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddRateLimiter(options =>
        {
            // ── Why RateLimiting:* is NOT admin-editable (issue #421 Non-Goal 5, recorded here by
            // issue #434 D4) ─────────────────────────────────────────────────────────────────────
            //
            // Read the two comments below together, because the difference between them is the whole
            // reason. The PARTITIONER runs per request, so it does re-read options every time. The
            // LIMITER FACTORY — the lambda handed to GetFixedWindowLimiter — runs only the first time
            // a given partition key is created, and the limiter it returns is then cached against that
            // key for the lifetime of the process.
            //
            // So a changed permit limit or window would reach a partition that has never been seen
            // before, and never reach a live one. In practice that means the attacker currently being
            // limited keeps the old limit while a fresh IP gets the new one — the exact "I changed the
            // limit and it did nothing" failure the settings feature exists to refuse, but worse,
            // because it is intermittent and looks like it worked.
            //
            // Making these editable therefore needs a limiter that can be reconfigured or replaced
            // per partition, not a settings row. Until that exists, they stay deploy-time config.
            // The partitioner runs per request, so the limits are resolved from options rather than
            // captured at startup — the value a test (or a deployment) overrides is the one that applies.
            options.AddPolicy(PasswordResetPolicy, context =>
            {
                var limits = context.RequestServices
                    .GetRequiredService<IOptions<AdminPasswordResetRateLimitOptions>>().Value;

                return FixedWindowPerActor(PasswordResetPolicy, context, limits.PermitLimit, limits.WindowSeconds);
            });

            options.AddPolicy(PasswordChangePolicy, context =>
            {
                var limits = context.RequestServices
                    .GetRequiredService<IOptions<PasswordChangeRateLimitOptions>>().Value;

                return FixedWindowPerActor(PasswordChangePolicy, context, limits.PermitLimit, limits.WindowSeconds);
            });

            options.AddPolicy(SecretWritePolicy, context =>
            {
                var limits = context.RequestServices
                    .GetRequiredService<IOptions<SecretWriteRateLimitOptions>>().Value;

                return FixedWindowPerActor(SecretWritePolicy, context, limits.PermitLimit, limits.WindowSeconds);
            });
        });

        return services;
    }

    private static RateLimitPartition<string> FixedWindowPerActor(
        string policy, HttpContext context, int permitLimit, int windowSeconds) =>
        RateLimitPartition.GetFixedWindowLimiter(
            $"{policy}:{ActorPartitionKey(context)}",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permitLimit,
                Window = TimeSpan.FromSeconds(windowSeconds),
                // Reject immediately rather than parking the request behind a queue slot.
                QueueLimit = 0,
            });

    // UseRateLimiter runs after UseAuthentication/UseAuthorization, so an authenticated user id is always
    // present on a request that reaches either of these endpoints; the fallback is defensive only, and
    // errs toward one shared bucket rather than an unpartitioned pass.
    private static string ActorPartitionKey(HttpContext context) =>
        context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown";
}

/// <summary>
/// Bound from the <c>RateLimiting:AdminPasswordReset</c> configuration section (issue #406). A cap of
/// 10/hour does not impede legitimate one-user-at-a-time admin work, but bounds the blast radius of one
/// bad session.
/// </summary>
public sealed class AdminPasswordResetRateLimitOptions
{
    public const string SectionName = "RateLimiting:AdminPasswordReset";

    /// <summary>Password resets one admin may trigger per window, across all targets.</summary>
    [Range(1, int.MaxValue)]
    public int PermitLimit { get; set; } = 10;

    [Range(1, 86400)]
    public int WindowSeconds { get; set; } = 3600;
}

/// <summary>Bound from the <c>RateLimiting:PasswordChange</c> configuration section (issue #406).</summary>
public sealed class PasswordChangeRateLimitOptions
{
    public const string SectionName = "RateLimiting:PasswordChange";

    /// <summary>Change-password attempts one caller may make per window.</summary>
    [Range(1, int.MaxValue)]
    public int PermitLimit { get; set; } = 10;

    [Range(1, 86400)]
    public int WindowSeconds { get; set; } = 3600;
}

/// <summary>
/// Bound from the <c>RateLimiting:SecretWrite</c> configuration section (issue #444 §7). A generous
/// window for a human rotating credentials one at a time, and a hard bound on a script.
/// </summary>
public sealed class SecretWriteRateLimitOptions
{
    public const string SectionName = "RateLimiting:SecretWrite";

    /// <summary>Secret writes (set or clear) one caller may make per window, across all keys.</summary>
    [Range(1, int.MaxValue)]
    public int PermitLimit { get; set; } = 30;

    [Range(1, 86400)]
    public int WindowSeconds { get; set; } = 3600;
}

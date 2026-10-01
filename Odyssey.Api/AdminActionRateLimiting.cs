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
        services
            .AddPerUserFixedWindowOptions<AdminPasswordResetRateLimitOptions>(
                configuration, AdminPasswordResetRateLimitOptions.SectionName)
            .AddPerUserFixedWindowOptions<PasswordChangeRateLimitOptions>(
                configuration, PasswordChangeRateLimitOptions.SectionName)
            .AddPerUserFixedWindowOptions<SecretWriteRateLimitOptions>(
                configuration, SecretWriteRateLimitOptions.SectionName);

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
            options
                .AddPerUserFixedWindowPolicy<AdminPasswordResetRateLimitOptions>(PasswordResetPolicy)
                .AddPerUserFixedWindowPolicy<PasswordChangeRateLimitOptions>(PasswordChangePolicy)
                .AddPerUserFixedWindowPolicy<SecretWriteRateLimitOptions>(SecretWritePolicy);
        });

        return services;
    }
}

/// <summary>
/// Bound from the <c>RateLimiting:AdminPasswordReset</c> configuration section (issue #406). A cap of
/// 10/hour does not impede legitimate one-user-at-a-time admin work, but bounds the blast radius of one
/// bad session. The permit counts resets one admin may trigger per window, across all targets.
/// </summary>
public sealed class AdminPasswordResetRateLimitOptions()
    : PerUserFixedWindowRateLimitOptions(permitLimit: 10, windowSeconds: 3600)
{
    public const string SectionName = "RateLimiting:AdminPasswordReset";
}

/// <summary>Bound from the <c>RateLimiting:PasswordChange</c> configuration section (issue #406).</summary>
public sealed class PasswordChangeRateLimitOptions()
    : PerUserFixedWindowRateLimitOptions(permitLimit: 10, windowSeconds: 3600)
{
    public const string SectionName = "RateLimiting:PasswordChange";
}

/// <summary>
/// Bound from the <c>RateLimiting:SecretWrite</c> configuration section (issue #444 §7). A generous
/// window for a human rotating credentials one at a time, and a hard bound on a script. The permit counts
/// writes (set or clear) one caller may make per window, across all keys.
/// </summary>
public sealed class SecretWriteRateLimitOptions()
    : PerUserFixedWindowRateLimitOptions(permitLimit: 30, windowSeconds: 3600)
{
    public const string SectionName = "RateLimiting:SecretWrite";
}

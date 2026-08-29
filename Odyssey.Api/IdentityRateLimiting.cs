using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace Odyssey.Api;

/// <summary>
/// Per-IP rate limiting for the anonymous, root-mapped Identity endpoints (issue #382). <c>/login</c>,
/// <c>/register</c>, <c>/forgotPassword</c> and <c>/resendConfirmationEmail</c> are the only
/// unauthenticated write surface in the app. Identity's per-account lockout stops password brute force
/// against a single account, but does nothing about the three abuses that vary the account instead:
/// enumerating which addresses are registered, registration spam, and driving
/// <c>SmtpEmailSender</c> through the two mail-sending endpoints.
/// <para>
/// A fixed window per client IP bounds all three. The limits are configuration-bound rather than
/// hardcoded because the right ceiling depends on the deployment — a stack behind a single office NAT
/// shares one partition key.
/// </para>
/// <para>
/// A group ceiling generous enough for that shared NAT still permits ~30 emails a minute, so the two
/// mail-sending routes carry a second, much tighter policy of their own (issue #393). That policy
/// bounds one IP; it cannot bound a rotating-IP source aimed at one mailbox, which is what the
/// per-recipient <c>IEmailSendThrottle</c> is for.
/// </para>
/// </summary>
public static class IdentityRateLimiting
{
    public const string PolicyName = "identity";

    /// <summary>
    /// Partition-key prefix for the tighter limiter layered over the two Identity endpoints that
    /// send mail (issue #393). Its own prefix, so a mail request never consumes — or is rejected by
    /// — the general Identity bucket for the same IP.
    /// </summary>
    public const string MailLimiterName = "identity-email";

    /// <summary>
    /// The <c>MapIdentityApi</c> routes that cause an outbound email on every call. Route text, not
    /// a handler reference, because the framework maps the group as a unit and exposes nothing else
    /// to match on — hence the startup check in <see cref="RequireMailEndpointRateLimiting"/>.
    /// </summary>
    public static readonly string[] MailEndpointRoutes = ["/forgotPassword", "/resendConfirmationEmail"];

    // Every non-mail request shares this single no-op partition, so the global limiter costs one
    // dictionary lookup on the rest of the API.
    private const string NoLimiterPartitionKey = "none";

    private const string RejectionMessage =
        "Too many requests. Please wait a moment before trying again.";

    private const string MailRejectionMessage =
        "Too many requests. Please wait a few minutes before requesting another email.";

    /// <summary>
    /// Distinct from the per-recipient throttle's message on purpose (issue #406 §3): an admin must be
    /// able to tell "too many reset emails to this address recently" from "too many resets from this
    /// account recently", because only one of the two is about the user they were trying to help.
    /// </summary>
    private const string AdminPasswordResetRejectionMessage =
        "Too many password resets from this account recently. Please wait before triggering another.";

    public static IServiceCollection AddIdentityRateLimiter(
        this IServiceCollection services, IConfiguration configuration)
    {
        // ValidateOnStart, not just ValidateDataAnnotations: an out-of-range limit is a
        // misconfigured security control, and surfacing it at startup beats discovering it on the
        // first request that needed limiting.
        services.AddOptions<IdentityRateLimitOptions>()
            .Bind(configuration.GetSection(IdentityRateLimitOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<IdentityEmailRateLimitOptions>()
            .Bind(configuration.GetSection(IdentityEmailRateLimitOptions.SectionName))
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
            // The limiter factory below runs only the first time a partition is created.
            options.AddPolicy(PolicyName, context =>
            {
                var limits = context.RequestServices
                    .GetRequiredService<IOptions<IdentityRateLimitOptions>>().Value;

                return RateLimitPartition.GetFixedWindowLimiter(
                    PartitionKey(context),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = limits.PermitLimit,
                        Window = TimeSpan.FromSeconds(limits.WindowSeconds),
                        // Reject immediately rather than parking the request: a queued attacker still
                        // occupies a connection, and a login that hangs is worse UX than a clear 429.
                        QueueLimit = 0,
                    });
            });

            // The mail limit rides on the GLOBAL limiter rather than a second named policy, because
            // the middleware reads exactly one EnableRateLimitingAttribute per endpoint (metadata
            // lookups return the last match) — a second policy would *replace* the group policy on
            // these routes instead of adding to it. The global limiter is acquired in addition to
            // the endpoint's policy, so the two mail endpoints end up subject to both and whichever
            // is tighter binds first. Every other request resolves to a no-op partition.
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                if (context.GetEndpoint()?.Metadata.GetMetadata<MailEndpointMetadata>() is null)
                {
                    return RateLimitPartition.GetNoLimiter(NoLimiterPartitionKey);
                }

                var limits = context.RequestServices
                    .GetRequiredService<IOptions<IdentityEmailRateLimitOptions>>().Value;

                return RateLimitPartition.GetFixedWindowLimiter(
                    $"{MailLimiterName}:{PartitionKey(context)}",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = limits.PermitLimit,
                        Window = TimeSpan.FromSeconds(limits.WindowSeconds),
                        QueueLimit = 0,
                    });
            });

            options.OnRejected = OnRejectedAsync;
        });

        return services;
    }

    /// <summary>
    /// Tags the <see cref="MailEndpointRoutes"/> members of an already-mapped Identity group with
    /// <see cref="MailEndpointMetadata"/> — the marker the global limiter reads to apply the tighter
    /// mail window — and logs an error if either route is missing.
    /// </summary>
    /// <remarks>
    /// <c>MapIdentityApi</c> maps the group as a unit, so the two endpoints can only be picked out by
    /// route text. A future ASP.NET version renaming either one would silently drop it back to the
    /// group policy alone: a degradation rather than a hole (the group limit and the per-recipient
    /// throttle both still apply), so this reports it to operators rather than refusing to serve
    /// traffic.
    /// </remarks>
    public static TBuilder RequireMailEndpointRateLimiting<TBuilder>(this TBuilder builder, ILogger logger)
        where TBuilder : IEndpointConventionBuilder
    {
        var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        builder.Add(endpointBuilder =>
        {
            if (endpointBuilder is not RouteEndpointBuilder route)
            {
                return;
            }

            var match = MailEndpointRoutes.FirstOrDefault(
                mailRoute => string.Equals(route.RoutePattern.RawText, mailRoute, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                return;
            }

            matched.Add(match);
            endpointBuilder.Metadata.Add(MailEndpointMetadata.Instance);
        });

        // Finally conventions run once every endpoint in the group has had its conventions applied,
        // so `matched` is complete by the first invocation; the flag keeps the check to one report.
        var reported = false;
        builder.Finally(_ =>
        {
            if (reported)
            {
                return;
            }

            reported = true;
            var missing = MailEndpointRoutes.Except(matched, StringComparer.OrdinalIgnoreCase).ToArray();
            if (missing.Length > 0)
            {
                logger.LogError(
                    "Identity mail endpoints {MissingRoutes} were not found, so the tighter '{MailLimiter}' rate "
                    + "limit is not applied to them. They fall back to the '{GroupPolicy}' group limit alone — "
                    + "check whether MapIdentityApi renamed these routes.",
                    string.Join(", ", missing), MailLimiterName, PolicyName);
            }
        });

        return builder;
    }

    // UseForwardedHeaders runs first, so RemoteIpAddress is the real client behind the reverse proxy.
    // A missing address (a test server, a unix socket) collapses to one shared bucket, which errs
    // toward limiting rather than waving requests through unpartitioned.
    private static string PartitionKey(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private static ValueTask OnRejectedAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        var response = context.HttpContext.Response;
        response.StatusCode = StatusCodes.Status429TooManyRequests;

        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            response.Headers.RetryAfter =
                ((int)retryAfter.TotalSeconds).ToString(NumberFormatInfo.InvariantInfo);
        }

        // The wording follows the endpoint, not the policy that happened to reject: on a mail
        // endpoint "wait a few minutes" is the actionable advice either way. It depends only on
        // which route was hit, never on whether the address is registered — see the non-enumeration
        // constraint in issue #393.
        var endpoint = context.HttpContext.GetEndpoint();
        var isMailEndpoint = endpoint?.Metadata.GetMetadata<MailEndpointMetadata>() is not null;
        var policy = endpoint?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName;
        var isAdminPasswordReset =
            string.Equals(policy, AdminActionRateLimiting.PasswordResetPolicy, StringComparison.Ordinal);

        // The admin-side anomaly signal (issue #406 §7): bulk abuse of the reset endpoint becomes visible
        // to operators before every recipient's inbox does the alerting. Warning level, with the actor's
        // id and never a target's.
        if (isAdminPasswordReset)
        {
            context.HttpContext.RequestServices
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger(typeof(AdminActionRateLimiting))
                .LogWarning(
                    "Admin-initiated password resets rate-limited for actor {ActorUserId}.",
                    context.HttpContext.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown");
        }

        // The same RFC 7807 application/problem+json shape as every other error path, so clients
        // parse a throttle the way they parse any other failure.
        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status429TooManyRequests,
            Title = ReasonPhrases.GetReasonPhrase(StatusCodes.Status429TooManyRequests),
            Detail = isMailEndpoint
                ? MailRejectionMessage
                : isAdminPasswordReset
                    ? AdminPasswordResetRejectionMessage
                    : RejectionMessage,
        };

        return new ValueTask(response.WriteAsJsonAsync(
            problem, options: null, contentType: "application/problem+json", cancellationToken));
    }
}

/// <summary>
/// Endpoint metadata marking a route as one that sends mail, so the global limiter can apply the
/// tighter <c>RateLimiting:IdentityEmail</c> window to it (issue #393). Metadata rather than a named
/// policy because a named policy would replace, not supplement, the Identity group's policy.
/// </summary>
public sealed class MailEndpointMetadata
{
    public static readonly MailEndpointMetadata Instance = new();

    private MailEndpointMetadata() { }
}

/// <summary>Bound from the <c>RateLimiting:Identity</c> configuration section.</summary>
public sealed class IdentityRateLimitOptions
{
    public const string SectionName = "RateLimiting:Identity";

    /// <summary>Requests allowed per client IP per window. Generous enough for an office NAT to sign in.</summary>
    [Range(1, int.MaxValue)]
    public int PermitLimit { get; set; } = 30;

    [Range(1, 3600)]
    public int WindowSeconds { get; set; } = 60;
}

/// <summary>
/// Bound from the <c>RateLimiting:IdentityEmail</c> configuration section (issue #393). Governs only
/// <see cref="IdentityRateLimiting.MailEndpointRoutes"/> — the endpoints that put a message on the
/// wire on every call, where the cost of abuse is SMTP quota and the sending domain's reputation
/// rather than CPU. Deliberately far tighter than the group limit; keep it that way (see
/// <see cref="IdentityRateLimiting.RequireMailEndpointRateLimiting"/>).
/// </summary>
public sealed class IdentityEmailRateLimitOptions
{
    public const string SectionName = "RateLimiting:IdentityEmail";

    /// <summary>Mail-triggering requests allowed per client IP per window.</summary>
    [Range(1, int.MaxValue)]
    public int PermitLimit { get; set; } = 5;

    /// <summary>Window length. Longer than the group's minute, so a slow drip is caught too.</summary>
    [Range(1, 86400)]
    public int WindowSeconds { get; set; } = 900;
}

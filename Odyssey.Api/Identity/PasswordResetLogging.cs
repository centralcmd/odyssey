using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing.Patterns;
using Odyssey.Context;

namespace Odyssey.Api.Identity;

/// <summary>
/// Emits one structured line when a password reset actually completes (issue #405). A reset is an
/// anonymous, account-takeover-adjacent action, so incident response needs to know it happened — but
/// an audit <em>table</em> is a non-goal, and the log must never carry the address or the token.
/// </summary>
/// <remarks>
/// <c>/resetPassword</c> is Microsoft's minimal-API handler, so the only way in is an endpoint filter
/// applied from outside. <c>MapIdentityApi&lt;TUser&gt;()</c> returns a single
/// <see cref="IEndpointConventionBuilder"/> covering all of its routes, so
/// <c>AddEndpointFilter</c> on that builder would log "password reset completed" on every successful
/// <c>/login</c>, <c>/register</c> and <c>/manage/info</c> as well. This walks the group's endpoints
/// and attaches the filter to the one matching route instead — the same shape
/// <see cref="IdentityRateLimiting.RequireMailEndpointRateLimiting"/> uses for the same reason.
/// </remarks>
public static class PasswordResetLogging
{
    /// <summary>The <c>MapIdentityApi</c> route this filter attaches to.</summary>
    public const string ResetRoute = "/resetPassword";

    /// <summary>
    /// Attaches the completion log to <see cref="ResetRoute"/> within an already-mapped Identity
    /// group, marking the endpoint it attached to with <see cref="PasswordResetLoggingMetadata"/> so
    /// <see cref="ValidatePasswordResetLogging"/> can confirm afterwards that it landed.
    /// </summary>
    public static TBuilder LogPasswordResetCompletion<TBuilder>(this TBuilder builder, ILogger logger)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.Add(endpointBuilder =>
        {
            if (endpointBuilder is not RouteEndpointBuilder route ||
                !string.Equals(route.RoutePattern.RawText, ResetRoute, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // A filter factory leaves nothing on the built endpoint to inspect — it is folded into the
            // request delegate — so the attachment is marked here. Without it the only observable
            // property would be "the route exists", which is not the property that matters.
            endpointBuilder.Metadata.Add(PasswordResetLoggingMetadata.Instance);

            // The same mechanism AddEndpointFilter() uses internally, applied to this one endpoint
            // rather than unconditionally to the whole group.
            route.FilterFactories.Add((_, next) => async context =>
            {
                var result = await next(context);
                if (IsSuccess(result))
                {
                    await LogCompletionAsync(context, logger);
                }

                return result;
            });
        });

        return builder;
    }

    /// <summary>
    /// Reports at startup if no built endpoint carries <see cref="PasswordResetLoggingMetadata"/>,
    /// i.e. if the filter was never attached and completed resets are going unlogged.
    /// </summary>
    /// <remarks>
    /// Read against the <em>built</em> endpoints, like
    /// <see cref="PasswordChangeExemptRoutes.ValidateExemptEndpoints"/>. This check previously lived in
    /// an <c>IEndpointConventionBuilder.Finally</c> convention on the assumption that <c>Finally</c>
    /// runs after the whole group has been walked. It does not: <c>RouteEndpointDataSource</c> applies
    /// group conventions and then group finally conventions <em>per endpoint</em>, so the report fired
    /// on the first endpoint built — before <c>/resetPassword</c> was reached — and logged this error
    /// on every boot even though the filter was attached correctly. See the same note on
    /// <see cref="IdentityRateLimiting.ValidateMailEndpointRateLimiting"/>.
    /// </remarks>
    public static void ValidatePasswordResetLogging(IEnumerable<Endpoint> endpoints, ILogger logger)
    {
        if (endpoints.Any(endpoint =>
                endpoint.Metadata.GetMetadata<PasswordResetLoggingMetadata>() is not null))
        {
            return;
        }

        logger.LogError(
            "Identity route {Route} was not found, so password-reset completions are not being logged — "
            + "check whether MapIdentityApi renamed it.", ResetRoute);
    }

    // The handler returns Results<Ok, ValidationProblem>, a struct wrapper around the real result,
    // and nothing has been written to the response yet when the filter regains control — so the
    // status has to come off the result object rather than off HttpContext.Response.
    private static bool IsSuccess(object? result)
    {
        var actual = result is INestedHttpResult nested ? nested.Result : result;
        return actual is IStatusCodeHttpResult { StatusCode: >= 200 and < 300 };
    }

    /// <summary>
    /// Resolves the user only <em>after</em> a 200. At that point the reset has already succeeded, so
    /// the account provably exists and the lookup adds no existence or timing oracle. The line carries
    /// the user id alone — never the address, never the token.
    /// </summary>
    private static async Task LogCompletionAsync(EndpointFilterInvocationContext context, ILogger logger)
    {
        try
        {
            // The already-bound parameter, never HttpContext.Request.Body: the minimal-API handler has
            // consumed the stream by the time next() returns, so re-reading it yields nothing.
            var request = context.Arguments.OfType<ResetPasswordRequest>().FirstOrDefault();
            if (request is null)
            {
                return;
            }

            var users = context.HttpContext.RequestServices.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await users.FindByEmailAsync(request.Email);
            if (user is not null)
            {
                logger.LogInformation("Password reset completed for user {UserId}.", user.Id);
            }
        }
        catch (Exception ex)
        {
            // Observability must never turn a completed reset into a 500 for the user.
            logger.LogWarning(ex, "Failed to log a completed password reset.");
        }
    }
}

/// <summary>
/// Endpoint metadata marking the endpoint the password-reset completion filter was attached to
/// (issue #405). A filter factory is folded into the request delegate and leaves no trace on the
/// built endpoint, so this is what makes the attachment observable — to
/// <see cref="PasswordResetLogging.ValidatePasswordResetLogging"/> at startup, and to a test running
/// against a real <c>MapIdentityApi</c> rather than a stand-in convention builder.
/// </summary>
public sealed class PasswordResetLoggingMetadata
{
    public static readonly PasswordResetLoggingMetadata Instance = new();

    private PasswordResetLoggingMetadata() { }
}

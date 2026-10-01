namespace Odyssey.Api.Identity;

/// <summary>
/// Keeps <c>MapIdentityApi</c> from issuing bearer tokens (issue #245). The application is cookie-only:
/// only the cookie handler is registered, and only the cookie re-checks the security stamp every minute,
/// so a bearer token would survive a password reset, a disable or a delete for its whole lifetime with
/// its permission claims frozen at issue time.
/// </summary>
/// <remarks>
/// Removing the bearer handler is the structural half — a token can no longer be minted or accepted.
/// These filters are the contract half: without them <c>/login?useCookies=false</c> would reach
/// <c>SignInAsync</c> against a scheme that is not registered and <c>/refresh</c> would dereference
/// bearer options that were never configured, and both would surface as a <c>500</c>. They are
/// attached per route for the same reason <see cref="PasswordResetLogging"/> is: a filter on the whole
/// <c>MapIdentityApi</c> group would run on every Identity endpoint.
/// </remarks>
public static class CookieOnlyIdentityEndpoints
{
    public const string LoginRoute = "/login";
    public const string RefreshRoute = "/refresh";

    /// <summary>
    /// Refuses a <c>/login</c> that would issue a bearer token and every call to <c>/refresh</c>,
    /// marking each endpoint it attached to with <see cref="CookieOnlyIdentityMetadata"/>.
    /// </summary>
    public static TBuilder RequireCookieOnlyIdentity<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.Add(endpointBuilder =>
        {
            if (endpointBuilder is not RouteEndpointBuilder route)
            {
                return;
            }

            var pattern = route.RoutePattern.RawText;
            if (string.Equals(pattern, LoginRoute, StringComparison.OrdinalIgnoreCase))
            {
                endpointBuilder.Metadata.Add(CookieOnlyIdentityMetadata.Instance);
                route.FilterFactories.Add((_, next) => context =>
                    RequestsCookie(context.HttpContext.Request.Query)
                        ? next(context)
                        : ValueTask.FromResult<object?>(Results.Problem(
                            statusCode: StatusCodes.Status400BadRequest,
                            title: "Bearer tokens are not supported.",
                            detail: "Sign in with useCookies=true or useSessionCookies=true.")));
            }
            else if (string.Equals(pattern, RefreshRoute, StringComparison.OrdinalIgnoreCase))
            {
                endpointBuilder.Metadata.Add(CookieOnlyIdentityMetadata.Instance);
                route.FilterFactories.Add((_, _) => _ =>
                    ValueTask.FromResult<object?>(Results.NotFound()));
            }
        });

        return builder;
    }

    /// <summary>
    /// Reports at startup unless both routes carry <see cref="CookieOnlyIdentityMetadata"/> — read
    /// against the built endpoints for the reason given on
    /// <see cref="PasswordResetLogging.ValidatePasswordResetLogging"/>.
    /// </summary>
    public static void ValidateCookieOnlyIdentity(IEnumerable<Endpoint> endpoints, ILogger logger)
    {
        var guarded = endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata.GetMetadata<CookieOnlyIdentityMetadata>() is not null)
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var route in new[] { LoginRoute, RefreshRoute }.Where(route => !guarded.Contains(route)))
        {
            logger.LogError(
                "Identity route {Route} was not found, so the bearer-token refusal is not attached to it — "
                + "check whether MapIdentityApi renamed it.", route);
        }
    }

    // Mirrors MapIdentityApi's own reading: the cookie scheme is chosen when either flag binds to true.
    private static bool RequestsCookie(IQueryCollection query) =>
        IsTrue(query["useCookies"]) || IsTrue(query["useSessionCookies"]);

    private static bool IsTrue(string? value) => bool.TryParse(value, out var parsed) && parsed;
}

/// <summary>
/// Endpoint metadata marking the Identity endpoints <see cref="CookieOnlyIdentityEndpoints"/> guards,
/// so the attachment is observable on the built endpoint.
/// </summary>
public sealed class CookieOnlyIdentityMetadata
{
    public static readonly CookieOnlyIdentityMetadata Instance = new();

    private CookieOnlyIdentityMetadata()
    {
    }
}

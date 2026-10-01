using Microsoft.AspNetCore.Http;

namespace Odyssey.Api.Identity;

/// <summary>
/// Closes <c>MapIdentityApi</c>'s <c>POST /manage/info</c> (issue #246) while leaving its
/// <c>GET</c> — the client's session probe — untouched.
/// </summary>
/// <remarks>
/// <para>
/// The stock endpoint did two things, both unsafely. With <c>newEmail</c> it mailed a change link to the
/// <em>new</em> address with no password check and no word to the old one, so a hijacked or unattended
/// session could move the account's sign-in identity to a mailbox the attacker controls and then use
/// <c>/forgotPassword</c> for a takeover that survives the victim signing out. With <c>newPassword</c> it
/// called <c>ChangePasswordAsync</c>, which does no lockout accounting, so <c>oldPassword</c> was an
/// unlocked guessing oracle. Both operations now live on <c>/api/account</c>, where they verify the
/// current password and count failures.
/// </para>
/// <para>
/// <c>MapIdentityApi</c> offers no way to omit a route, so the write is refused by an endpoint filter
/// that never calls the handler: <c>405</c> with <c>Allow: GET</c>, which is what the route now is. It
/// runs after authentication, authorization and the rate limiter, so an anonymous caller still gets
/// <c>401</c> and learns nothing new. Attached per endpoint rather than to the group for the reason
/// <see cref="PasswordResetLogging"/> gives, and matched on method as well as route because
/// <c>GET /manage/info</c> shares the pattern.
/// </para>
/// </remarks>
public static class ManageInfoWriteBlock
{
    /// <summary>The <c>MapIdentityApi</c> route whose write is blocked.</summary>
    public const string Route = "/manage/info";

    public const string Detail =
        "Changing the email address or password here is not supported. "
        + "Use POST /api/account/email or POST /api/account/password.";

    /// <summary>
    /// Refuses <c>POST</c> <see cref="Route"/> within an already-mapped Identity group, marking the
    /// endpoint with <see cref="ManageInfoWriteBlockMetadata"/> so <see cref="ValidateBlocked"/> can
    /// confirm it landed.
    /// </summary>
    public static TBuilder BlockManageInfoWrites<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.Add(endpointBuilder =>
        {
            if (endpointBuilder is not RouteEndpointBuilder route || !IsWrite(route))
            {
                return;
            }

            endpointBuilder.Metadata.Add(ManageInfoWriteBlockMetadata.Instance);
            route.FilterFactories.Add((_, _) => context =>
            {
                context.HttpContext.Response.Headers.Allow = HttpMethods.Get;
                return ValueTask.FromResult<object?>(Results.Problem(
                    statusCode: StatusCodes.Status405MethodNotAllowed,
                    detail: Detail));
            });
        });

        return builder;
    }

    /// <summary>
    /// Throws if any built endpoint answers <c>POST</c> <see cref="Route"/> without the block.
    /// </summary>
    /// <remarks>
    /// Fail-fast, like <see cref="PasswordChangeExemptRoutes.ValidateExemptEndpoints"/> and unlike the
    /// log-and-degrade checks beside it: a missing block is the vulnerability itself, not a degradation.
    /// It also throws when no such endpoint exists at all, because that means <c>MapIdentityApi</c>
    /// renamed the route and the new one is unblocked.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The write is reachable, or the route has moved.</exception>
    public static void ValidateBlocked(IEnumerable<Endpoint> endpoints)
    {
        var writes = endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => IsWrite(endpoint.RoutePattern.RawText, endpoint.Metadata.GetMetadata<HttpMethodMetadata>()))
            .ToArray();

        if (writes.Length > 0 &&
            writes.All(endpoint => endpoint.Metadata.GetMetadata<ManageInfoWriteBlockMetadata>() is not null))
        {
            return;
        }

        throw new InvalidOperationException(
            writes.Length == 0
                ? $"No POST {Route} endpoint was found to block — check whether MapIdentityApi renamed it, "
                  + "and block the new route (see ManageInfoWriteBlock)."
                : $"POST {Route} is reachable: it changes the sign-in email without re-authentication. "
                  + "Apply BlockManageInfoWrites() to the MapIdentityApi group (see ManageInfoWriteBlock).");
    }

    private static bool IsWrite(RouteEndpointBuilder route) =>
        IsWrite(route.RoutePattern.RawText, route.Metadata.OfType<HttpMethodMetadata>().LastOrDefault());

    private static bool IsWrite(string? rawText, HttpMethodMetadata? methods) =>
        string.Equals(rawText, Route, StringComparison.OrdinalIgnoreCase) &&
        methods?.HttpMethods.Contains(HttpMethods.Post, StringComparer.OrdinalIgnoreCase) == true;
}

/// <summary>
/// Endpoint metadata marking the endpoint <see cref="ManageInfoWriteBlock"/> refuses. A filter factory is
/// folded into the request delegate, so this is what makes the attachment observable.
/// </summary>
public sealed class ManageInfoWriteBlockMetadata
{
    public static readonly ManageInfoWriteBlockMetadata Instance = new();

    private ManageInfoWriteBlockMetadata() { }
}

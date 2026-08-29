namespace Odyssey.Api.Identity;

/// <summary>
/// The must-change-password exempt set, and the fail-fast startup check that every member of it is
/// actually present (issue #406 §5.6).
/// </summary>
public static class PasswordChangeExemptRoutes
{
    /// <summary>
    /// Exactly the endpoints <see cref="PasswordChangeRequiredMiddleware"/> lets through while the flag is
    /// set. Keyed by route <b>and</b> method, so a future action on an already-exempt controller cannot
    /// inherit an exemption nobody reviewed.
    /// </summary>
    public static readonly IReadOnlyList<PasswordChangeExemptEndpoint> Expected =
    [
        // The way out.
        new(HttpMethods.Post, "/api/account/password"),
        // The client gate reads MustChangePassword from here...
        new(HttpMethods.Get, "/api/profile"),
        // ...and cannot render an authenticated shell (or the gate page) without these two.
        new(HttpMethods.Get, "/auth/claims"),
        new(HttpMethods.Get, "/auth/permissions"),
        // A user who cannot change their password must still be able to leave.
        new(HttpMethods.Post, "/logout"),
    ];

    /// <summary>
    /// Resolves <see cref="Expected"/> against the application's built endpoints and throws if any of them
    /// is missing its exemption — whether because a <see cref="PasswordChangeExemptAttribute"/> was removed
    /// or because the endpoint it was on has been renamed or dropped.
    /// </summary>
    /// <remarks>
    /// Fail-fast rather than the log-and-degrade posture of
    /// <c>IdentityRateLimiting.RequireMailEndpointRateLimiting</c> and
    /// <c>PasswordResetLogging.LogPasswordResetCompletion</c>, because the failure modes differ in kind: a
    /// missed rate limit or a missed log line is a degradation, whereas a missing exemption is a
    /// <em>lockout</em> — a gated user with no reachable change-password endpoint, and no way to sign out,
    /// can never recover. Failing to boot is a louder and much cheaper failure than shipping that.
    /// </remarks>
    /// <exception cref="InvalidOperationException">One or more expected exemptions are absent.</exception>
    public static void ValidateExemptEndpoints(IEnumerable<Endpoint> endpoints)
    {
        var exempt = endpoints
            .Where(endpoint => endpoint.Metadata.GetMetadata<IPasswordChangeExemptMetadata>() is not null)
            .SelectMany(Describe)
            .ToHashSet();

        var missing = Expected.Where(expected => !exempt.Contains(expected)).ToArray();
        if (missing.Length == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            "The must-change-password exempt set is incomplete, which would leave a gated user unable to "
            + "recover. Missing: "
            + string.Join(", ", missing.Select(endpoint => $"{endpoint.Method} {endpoint.Route}"))
            + ". Check that [PasswordChangeExempt] is still on the corresponding action and that its route "
            + "is unchanged (see PasswordChangeExemptRoutes).");
    }

    /// <summary>Every (method, route) pair one endpoint answers, normalized for comparison.</summary>
    public static IEnumerable<PasswordChangeExemptEndpoint> Describe(Endpoint endpoint)
    {
        if (endpoint is not RouteEndpoint route)
        {
            return [];
        }

        var path = NormalizeRoute(route.RoutePattern.RawText);
        var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [];
        return methods.Select(method => new PasswordChangeExemptEndpoint(method, path));
    }

    /// <summary>
    /// Controller route patterns come through without a leading slash (<c>api/profile</c>) and
    /// root-mapped ones with it (<c>/logout</c>), so both are levelled here.
    /// </summary>
    internal static string NormalizeRoute(string? rawText)
    {
        var trimmed = (rawText ?? string.Empty).Trim().TrimEnd('/');
        return trimmed.StartsWith('/') ? trimmed : $"/{trimmed}";
    }
}

/// <summary>One exempt endpoint, identified by HTTP method and route rather than by controller.</summary>
public readonly record struct PasswordChangeExemptEndpoint(string Method, string Route)
{
    public bool Equals(PasswordChangeExemptEndpoint other) =>
        string.Equals(Method, other.Method, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Route, other.Route, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() =>
        HashCode.Combine(Method.ToUpperInvariant(), Route.ToUpperInvariant());

    public override string ToString() => $"{Method} {Route}";
}

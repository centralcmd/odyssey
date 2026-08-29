using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Odyssey.Dtos.Authorization;

namespace Odyssey.Api.Legal;

/// <summary>
/// The server-side half of the acceptance gate (issue #354 §5): any authenticated request from a
/// principal carrying a pending-acceptance claim is rejected with
/// <c>451 Unavailable For Legal Reasons</c> unless its (method, path) is on the allowlist below.
/// </summary>
/// <remarks>
/// <para>
/// <b>Middleware, not an authorization policy.</b> A policy attached to <c>MapControllers()</c> would
/// leave the separately-mapped Identity minimal-API group (<c>/login</c>, <c>/manage/*</c>, …) ungated,
/// so enforcement would depend on which of the app's two route groups an endpoint happened to be in.
/// Sitting in the pipeline after authentication covers both by construction.
/// </para>
/// <para>
/// <b>Allowlist, not a blocklist.</b> Every entry names a method as well as a path, so a read a gated
/// client genuinely needs (<c>GET /api/profile</c>, to bootstrap the app shell) can be permitted while
/// the write on the same path (<c>PUT /api/profile</c>) stays blocked. Anything not listed is gated —
/// including the admin ToS-management endpoints, which is what routes a non-compliant admin through the
/// interstitial before they can publish (AC 9).
/// </para>
/// <para>
/// The check reads only the claims already on the principal, so it costs no database round trip; the
/// claims themselves are recomputed at sign-in and on <c>SecurityStampValidator</c>'s existing interval
/// (see <see cref="LegalComplianceClaimsPrincipalFactory"/>).
/// </para>
/// </remarks>
public sealed class LegalComplianceMiddleware(RequestDelegate next)
{
    /// <summary>The machine-readable marker the client's <c>LegalComplianceHandler</c> keys off.</summary>
    public const string ProblemCode = "LEGAL_ACCEPTANCE_REQUIRED";

    private static readonly HashSet<(string Method, string Path)> Allowlist = new(AllowlistComparer.Instance)
    {
        // The feature's own endpoints — a gated user must be able to read what they owe and respond to it.
        (HttpMethods.Get, "/api/legal/license"),
        (HttpMethods.Get, "/api/legal/terms-of-service/current"),
        (HttpMethods.Get, "/api/legal/status"),
        (HttpMethods.Post, "/api/legal/respond"),

        // Session lifecycle: signing out (including the decline path) and re-authenticating must work
        // while gated, or a declining user would be stuck with a session they cannot end.
        (HttpMethods.Post, "/login"),
        (HttpMethods.Post, "/logout"),
        (HttpMethods.Post, "/register"),
        (HttpMethods.Post, "/refresh"),
        (HttpMethods.Get, "/confirmEmail"),
        (HttpMethods.Post, "/resendConfirmationEmail"),
        (HttpMethods.Post, "/forgotPassword"),
        (HttpMethods.Post, "/resetPassword"),

        // The must-change-password gate (issue #406) refuses every endpoint except the five that let a
        // user out of it, and this is the only write among them. Without it here, a user who owes an
        // acceptance AND has been reset would be bounced between the two gates: this one refuses the
        // password change, and that one refuses POST /api/legal/respond. It changes nothing else — the
        // endpoint requires the caller's current password and can do nothing but rotate it.
        (HttpMethods.Post, "/api/account/password"),

        // Read-only bootstrap the interstitial's own shell needs. The writes on these paths
        // (POST /manage/info, PUT /api/profile) are deliberately absent and stay gated.
        (HttpMethods.Get, "/manage/info"),
        (HttpMethods.Get, "/auth/claims"),
        (HttpMethods.Get, "/auth/permissions"),
        (HttpMethods.Get, "/api/profile"),

        // Infrastructure: the antiforgery token is a prerequisite for POST /api/legal/respond itself.
        (HttpMethods.Get, "/api/antiforgery/token"),
        (HttpMethods.Get, "/healthz"),
    };

    public async Task InvokeAsync(HttpContext context)
    {
        var user = context.User;

        if (user?.Identity?.IsAuthenticated is not true
            || !user.HasClaim(claim => claim.Type == LegalClaims.PendingAcceptanceType)
            || IsAllowed(context.Request))
        {
            await next(context);
            return;
        }

        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status451UnavailableForLegalReasons,
            Title = ReasonPhrases.GetReasonPhrase(StatusCodes.Status451UnavailableForLegalReasons),
            Detail = "The License and/or Terms of Service must be accepted before continuing.",
        };
        problem.Extensions["code"] = ProblemCode;
        problem.Extensions["pendingDocuments"] = user
            .FindAll(LegalClaims.PendingAcceptanceType)
            .Select(claim => claim.Value)
            .ToArray();

        context.Response.StatusCode = StatusCodes.Status451UnavailableForLegalReasons;
        await context.Response.WriteAsJsonAsync(problem, options: null, contentType: "application/problem+json");
    }

    private static bool IsAllowed(HttpRequest request)
    {
        var path = request.Path.HasValue ? request.Path.Value!.TrimEnd('/') : string.Empty;
        if (path.Length == 0)
        {
            path = "/";
        }

        return Allowlist.Contains((request.Method, path));
    }

    /// <summary>Methods and paths are both matched case-insensitively; paths are pre-trimmed of a trailing slash.</summary>
    private sealed class AllowlistComparer : IEqualityComparer<(string Method, string Path)>
    {
        public static readonly AllowlistComparer Instance = new();

        public bool Equals((string Method, string Path) x, (string Method, string Path) y) =>
            string.Equals(x.Method, y.Method, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Path, y.Path, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Method, string Path) value) =>
            HashCode.Combine(
                value.Method.ToUpperInvariant(),
                value.Path.ToUpperInvariant());
    }
}

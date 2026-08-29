using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Odyssey.Context;

namespace Odyssey.Api.Identity;

/// <summary>
/// The server-side enforcement of an admin-initiated password reset (issue #406 §5.6): while the caller's
/// <see cref="ApplicationUser.MustChangePassword"/> is set, every authenticated endpoint is refused with
/// <c>403</c> except the small, explicit set needed to escape the state
/// (<see cref="PasswordChangeExemptRoutes.Expected"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Deny-by-default.</b> Protection derives from an endpoint already carrying authorization metadata, so
/// a controller written next year is covered the day it is written; opening a hole takes an explicit,
/// greppable, method-level <c>[PasswordChangeExempt]</c>. The client-side gate is presentation only —
/// deleting it, or driving the API with <c>curl</c> and a stolen session cookie, changes nothing about
/// what the account can do.
/// </para>
/// <para>
/// <b>Middleware, not an authorization policy</b>, for the same reason as
/// <c>LegalComplianceMiddleware</c>: a policy on <c>MapControllers()</c> would leave the separately-mapped
/// Identity minimal-API group ungated, and <c>POST /manage/info</c> — which changes the password
/// <em>and</em> the email address — is precisely the endpoint that must stay blocked.
/// </para>
/// <para>
/// <b>Authoritative read, not a cookie claim.</b> Permission claims in this app are frozen into the auth
/// cookie until sign-out, so a claim would keep blocking a user who had already changed their password.
/// The flag is read from <c>OdysseyContext</c> instead — one indexed primary-key read of a single
/// <c>bool</c>, projected and untracked, and only on requests that reach step 5 below.
/// </para>
/// <para>
/// Evaluation order, and every step is a skip: unauthenticated → no endpoint → the endpoint is anonymous →
/// the endpoint is exempt → the flag is clear → block. It never touches the database for an
/// unauthenticated request, an anonymous endpoint, or an exempt one.
/// </para>
/// </remarks>
public sealed class PasswordChangeRequiredMiddleware(RequestDelegate next)
{
    /// <summary>The machine-readable marker the client keys off, rather than string-matching prose.</summary>
    public const string ProblemCode = "password_change_required";

    public const string ProblemType = "https://odyssey.local/errors/password-change-required";

    public const string ProblemDetail = "A password change is required before this account can be used.";

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.User?.Identity?.IsAuthenticated is not true)
        {
            await next(context);
            return;
        }

        var endpoint = context.GetEndpoint();

        // "No IAuthorizeData" means anonymous — /login, /forgotPassword, /resetPassword, /confirmEmail,
        // /healthz and /api/antiforgery/token all qualify, which is what keeps the emailed reset link
        // usable in a browser that is already signed in with the old password. IAllowAnonymous is checked
        // too because a controller action can opt out of the blanket MapControllers().RequireAuthorization()
        // while still carrying the group's inherited metadata (GET /api/legal/license does exactly that).
        if (endpoint is null
            || endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null
            || endpoint.Metadata.GetMetadata<IAuthorizeData>() is null
            || endpoint.Metadata.GetMetadata<IPasswordChangeExemptMetadata>() is not null)
        {
            await next(context);
            return;
        }

        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId) || !await MustChangePasswordAsync(context, userId))
        {
            await next(context);
            return;
        }

        // 403, not 401: the session is valid and re-authenticating would not help, so a 401 would send
        // well-behaved clients into a pointless sign-in loop.
        var problem = new ProblemDetails
        {
            Type = ProblemType,
            Status = StatusCodes.Status403Forbidden,
            Title = ReasonPhrases.GetReasonPhrase(StatusCodes.Status403Forbidden),
            Detail = ProblemDetail,
        };
        problem.Extensions["code"] = ProblemCode;

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(problem, options: null, contentType: "application/problem+json");
    }

    private static async Task<bool> MustChangePasswordAsync(HttpContext context, string userId)
    {
        var db = context.RequestServices.GetRequiredService<OdysseyContext>();

        return await db.Users
            .AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => user.MustChangePassword)
            .FirstOrDefaultAsync(context.RequestAborted);
    }
}

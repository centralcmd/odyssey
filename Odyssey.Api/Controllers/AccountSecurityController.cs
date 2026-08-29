using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Odyssey.Api.Identity;
using Odyssey.Context;
using Odyssey.Dtos.Application;
using Swashbuckle.AspNetCore.Annotations;

namespace Odyssey.Api.Controllers;

/// <summary>
/// The caller's own credential and session operations (issue #406 §5.7). Named for security rather than
/// for the account resource because "Account" is already load-bearing for the Finance bank-account domain
/// (<c>AccountController</c> → <c>api/accounts</c>); the route still mirrors the client's <c>/account</c>
/// page.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the password change could not simply reuse Identity's <c>POST /manage/info</c>:
/// that one endpoint changes the password <b>and</b> the email address. Exempting it from the
/// must-change-password block would let a gated session start an email change instead — and since a
/// pending email change is confirmed from the <em>new</em> address, an attacker holding the compromised
/// old password could move the account's sign-in identity to a mailbox they control while still blocked
/// from the app. No cheap middleware body-inspection reliably distinguishes the two operations, so the
/// password change gets an endpoint that can do nothing else. <c>/manage/info</c> stays mapped, unchanged
/// and <b>not</b> exempt.
/// </para>
/// </remarks>
[ApiController]
[Authorize]
[Route("api/account")]
public sealed class AccountSecurityController : ControllerBase
{
    private readonly UserManager<ApplicationUser> userManager;
    private readonly SignInManager<ApplicationUser> signInManager;

    public AccountSecurityController(
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager)
    {
        this.userManager = userManager;
        this.signInManager = signInManager;
    }

    /// <summary>
    /// Change the caller's own password. The only write a password-gated session can reach.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Lockout accounting is wired here explicitly, because the framework does not do it.</b>
    /// <c>UserManager.ChangePasswordAsync</c> verifies the old password and returns
    /// <c>PasswordMismatch</c> — it never calls <c>AccessFailedAsync</c>/<c>ResetAccessFailedCountAsync</c>;
    /// lockout accounting in Identity runs exclusively through <c>SignInManager</c>'s sign-in path. Since
    /// this is the one endpoint deliberately left reachable by a gated session — precisely because the old
    /// password may be compromised — leaving it unthrottled would hand an attacker with a stolen cookie
    /// unlimited guesses at that password.
    /// </para>
    /// <para>
    /// The <c>RefreshSignInAsync</c> at the end is not a nicety: with
    /// <c>SecurityStampValidatorOptions.ValidationInterval</c> at one minute, the stamp rotation a password
    /// change performs would otherwise sign the user out roughly a minute after they changed it.
    /// </para>
    /// </remarks>
    [HttpPost("password")]
    [PasswordChangeExempt]
    [EnableRateLimiting(AdminActionRateLimiting.PasswordChangePolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status423Locked, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Change the signed-in user's own password.")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request)
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
        {
            return Unauthorized();
        }

        // Identity does NOT reject a no-op change — ChangePasswordAsync happily rehashes the same password
        // and reports success — so the rule is enforced here. It matters because of the gate: a user an
        // admin reset could otherwise "change" straight back to the password that was reset in the first
        // place, clearing the flag with no real rotation. Compares two caller-supplied values, so it
        // discloses nothing about the stored one, and comes before the verification for that reason.
        if (string.Equals(request.CurrentPassword, request.NewPassword, StringComparison.Ordinal))
        {
            return this.BadRequestProblem("The new password must be different from the current one.");
        }

        if (await userManager.IsLockedOutAsync(user))
        {
            return LockedOut();
        }

        var result = await userManager.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
        if (result.Succeeded)
        {
            await userManager.ResetAccessFailedCountAsync(user);
            await signInManager.RefreshSignInAsync(user);
            return NoContent();
        }

        // The default IdentityErrorDescriber names its codes after its methods, so this is the code
        // ChangePasswordAsync produces when the supplied current password does not verify.
        if (result.Errors.Any(error =>
                string.Equals(error.Code, nameof(IdentityErrorDescriber.PasswordMismatch), StringComparison.Ordinal)))
        {
            await userManager.AccessFailedAsync(user);
            return await userManager.IsLockedOutAsync(user)
                ? LockedOut()
                : this.BadRequestProblem("The current password is incorrect.");
        }

        // A policy violation (or a no-op change, which Identity rejects) carries Identity's own messages,
        // so the client can tell it apart from the wrong-password case above.
        return this.BadRequestProblem(string.Join(" ", result.Errors.Select(error => error.Description)));
    }

    /// <summary>
    /// Ends the caller's session by clearing the Identity application cookie server-side.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The route is absolute (<c>/logout</c>, not <c>/api/account/logout</c>) because that is what
    /// <c>AuthApiClient.LogoutAsync</c> has always called and what <c>LegalComplianceMiddleware</c>'s
    /// allowlist has always named — <c>MapIdentityApi</c> maps <c>/login</c> but no matching sign-out, so
    /// until now that call answered 404 and the cookie outlived the sign-out. Added with issue #406
    /// because the must-change-password gate leans on it: a user who does not know their current password
    /// must be able to leave and use the emailed link instead, which is why it is also one of the five
    /// endpoints exempt from that gate.
    /// </para>
    /// <para>
    /// <c>[Authorize]</c> is inherited from the controller — signing out is only meaningful for a session
    /// that exists, and an anonymous caller has nothing to clear.
    /// </para>
    /// </remarks>
    [HttpPost("/logout")]
    [PasswordChangeExempt]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [SwaggerOperation(Summary = "Sign the current user out, clearing the authentication cookie.")]
    public async Task<IActionResult> Logout()
    {
        await signInManager.SignOutAsync();
        return NoContent();
    }

    private ObjectResult LockedOut() =>
        this.LockedProblem(
            "This account is temporarily locked after too many failed attempts. "
            + "Wait and try again, or use the password-reset link emailed to you.");
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using System.Text;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.WebUtilities;
using Odyssey.Api.Email;
using Odyssey.Api.Identity;
using Odyssey.Context;
using Odyssey.Dtos.Application;
using Swashbuckle.AspNetCore.Annotations;

namespace Odyssey.Api.Controllers;

/// <summary>
/// The caller's own credential and session operations (issue #406 §5.7). Named for security rather than
/// for the account resource because "Account" is already load-bearing for the Finance bank-account domain
/// (<c>AccountsController</c> → <c>api/accounts</c>); the route still mirrors the client's <c>/account</c>
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
/// password change gets an endpoint that can do nothing else.
/// </para>
/// <para>
/// Issue #246 then closed <c>POST /manage/info</c> outright (<see cref="ManageInfoWriteBlock"/>): its email
/// change needed no password and told nobody, and its password change skipped lockout accounting. The
/// email change moved here as <see cref="ChangeEmail"/>. <c>GET /manage/info</c> is still served.
/// </para>
/// </remarks>
[ApiController]
[Authorize]
[Route("api/account")]
public sealed class AccountSecurityController : ControllerBase
{
    private readonly UserManager<ApplicationUser> userManager;
    private readonly SignInManager<ApplicationUser> signInManager;
    private readonly IEmailChangeMailer emailChangeMailer;
    private readonly ILogger<AccountSecurityController> logger;

    public AccountSecurityController(
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        IEmailChangeMailer emailChangeMailer,
        ILogger<AccountSecurityController> logger)
    {
        this.userManager = userManager;
        this.signInManager = signInManager;
        this.emailChangeMailer = emailChangeMailer;
        this.logger = logger;
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
    /// Request a change of the caller's sign-in email (issue #246). Nothing changes until the link mailed
    /// to the new address is opened; the existing <c>/confirmEmail</c> endpoint applies it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The replacement for <c>POST /manage/info</c>'s <c>newEmail</c>, and stricter in four ways:
    /// <list type="bullet">
    /// <item><b>The current password is required</b>, so a hijacked or unattended session cannot move the
    /// account's identity — and with it the password-reset channel — to a mailbox the attacker controls.</item>
    /// <item><b>Failures are counted</b> (<c>AccessFailedAsync</c>, <c>423</c> on lockout), exactly as
    /// <see cref="ChangePassword"/> does, and the two share one rate-limit budget, so this cannot be used
    /// to double the guesses that endpoint allows.</item>
    /// <item><b>The security stamp rotates before the token is issued.</b> That signs out every other
    /// session and voids any earlier change or reset token, so only the newest request can be confirmed.
    /// The caller's own cookie is refreshed against the new stamp.</item>
    /// <item><b>The current address is told</b>, so an owner whose session was misused finds out while the
    /// change is still pending — and changing the password then cancels it, by rotating the stamp again.</item>
    /// </list>
    /// </para>
    /// <para>
    /// The status is <c>202</c> whether or not the new address is already another account's. That case
    /// mails nothing to the new address — the change could never be confirmed, and Identity's
    /// <c>/confirmEmail</c> would apply the email and then fail on the user name, leaving the two apart —
    /// and a distinct status would make this an existence oracle for any address. The notice still goes
    /// out. The uniformity is of the <em>status</em> only: the skipped send makes that path measurably
    /// faster. That residual is accepted rather than engineered away, because every probe costs the
    /// account's own password, counts toward its lockout and spends the shared per-actor rate limit —
    /// and <c>/register</c> already answers the same question more cheaply.
    /// </para>
    /// <para>
    /// Deliberately <b>not</b> <see cref="PasswordChangeExemptAttribute"/>: a session gated on a forced
    /// password change is one whose password may be compromised, which is exactly when moving the sign-in
    /// email must be impossible.
    /// </para>
    /// </remarks>
    [HttpPost("email")]
    [EnableRateLimiting(AdminActionRateLimiting.PasswordChangePolicy)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status423Locked, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Request a change of the signed-in user's email address.")]
    public async Task<IActionResult> ChangeEmail([FromBody] ChangeEmailRequest request)
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
        {
            return Unauthorized();
        }

        var newEmail = request.NewEmail.Trim();

        // Two caller-supplied values against the caller's own address, so it discloses nothing and comes
        // before the verification, like ChangePassword's no-op rule.
        if (string.Equals(newEmail, user.Email, StringComparison.OrdinalIgnoreCase))
        {
            return this.BadRequestProblem("This is already your email address.");
        }

        if (await userManager.IsLockedOutAsync(user))
        {
            return LockedOut();
        }

        if (!await userManager.CheckPasswordAsync(user, request.CurrentPassword))
        {
            await userManager.AccessFailedAsync(user);
            return await userManager.IsLockedOutAsync(user)
                ? LockedOut()
                : this.BadRequestProblem("The current password is incorrect.");
        }

        await userManager.ResetAccessFailedCountAsync(user);

        // Before the token: change-email tokens are bound to the stamp, so this is what voids any earlier
        // pending change. RefreshSignInAsync re-issues this session's cookie against the new stamp.
        await userManager.UpdateSecurityStampAsync(user);
        await signInManager.RefreshSignInAsync(user);

        var currentEmail = user.Email;
        if (await IsTakenAsync(newEmail, user.Id))
        {
            logger.LogInformation(
                "Email change for user {UserId} requested to an address already in use; no link sent.", user.Id);
        }
        else
        {
            var token = await userManager.GenerateChangeEmailTokenAsync(user, newEmail);
            await emailChangeMailer.SendChangeConfirmationAsync(
                newEmail, ConfirmationLink(user.Id, token, newEmail));
            logger.LogInformation("Email change requested for user {UserId}.", user.Id);
        }

        if (!string.IsNullOrEmpty(currentEmail))
        {
            await emailChangeMailer.SendChangeNoticeAsync(currentEmail, newEmail);
        }

        return Accepted();
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

    private async Task<bool> IsTakenAsync(string email, string userId)
    {
        // Both lookups: the user name IS the email in this app, and /confirmEmail sets both, so a clash on
        // either one makes the change unconfirmable.
        var byEmail = await userManager.FindByEmailAsync(email);
        var byName = await userManager.FindByNameAsync(email);
        return (byEmail is not null && byEmail.Id != userId) || (byName is not null && byName.Id != userId);
    }

    /// <summary>
    /// The link <c>MapIdentityApi</c>'s own <c>/manage/info</c> used to build: its <c>/confirmEmail</c>
    /// with <c>userId</c>, the Base64Url-encoded token and <c>changedEmail</c>. The mailer rewrites the
    /// origin onto the client's <c>confirm-email</c> page and keeps this query verbatim.
    /// </summary>
    private string ConfirmationLink(string userId, string token, string newEmail)
    {
        var code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
        var query = QueryString.Create(new Dictionary<string, string?>
        {
            ["userId"] = userId,
            ["code"] = code,
            ["changedEmail"] = newEmail,
        });

        return UriHelper.BuildAbsolute(
            Request.Scheme, Request.Host, Request.PathBase, "/confirmEmail", query);
    }

    private ObjectResult LockedOut() =>
        this.LockedProblem(
            "This account is temporarily locked after too many failed attempts. "
            + "Wait and try again, or use the password-reset link emailed to you.");
}

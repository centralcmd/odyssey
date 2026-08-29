using Odyssey.Dtos.Application;
using System.Security.Claims;
using Odyssey.Api.Email;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Odyssey.Api.UserAdministration;

[ApiController]
[Route("api/users")]
public sealed class UserAdministrationController : ControllerBase
{
    private readonly UserAdministrationService userAdministrationService;
    private readonly ILogger<UserAdministrationController> logger;

    public UserAdministrationController(
        UserAdministrationService userAdministrationService,
        ILogger<UserAdministrationController> logger)
    {
        this.userAdministrationService = userAdministrationService;
        this.logger = logger;
    }

    [HttpGet]
    [Authorize(Policy = PermissionClaims.UsersRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(PagedResult<ExistingUser>))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    public async Task<IActionResult> GetUsers(
        [FromQuery] UsersQueryParams query,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var users = await userAdministrationService.SearchAsync(User, query, cancellationToken);
            return Ok(users);
        }
        catch (UserAdministrationValidationException exception)
        {
            return this.BadRequestProblem(exception.Message);
        }
    }

    [HttpGet("roles")]
    [Authorize(Policy = PermissionClaims.UsersRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(IReadOnlyList<ExistingRole>))]
    public async Task<ActionResult<IReadOnlyList<ExistingRole>>> GetRoles()
    {
        return Ok(await userAdministrationService.GetRolesAsync());
    }

    [HttpGet("permissions")]
    [Authorize(Policy = PermissionClaims.UsersRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(IReadOnlyList<ExistingPermission>))]
    public ActionResult<IReadOnlyList<ExistingPermission>> GetPermissions()
    {
        return Ok(userAdministrationService.GetPermissions());
    }

    [HttpGet("{id}")]
    [Authorize(Policy = PermissionClaims.UsersRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingUser))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    public async Task<IActionResult> GetUser([FromRoute] string id)
    {
        var user = await userAdministrationService.GetAsync(User, id);
        if (user is null)
        {
            return this.NotFoundProblem($"User ID {id} was not found.");
        }

        return Ok(user);
    }

    [HttpPatch("{id}")]
    [Authorize(Policy = PermissionClaims.UsersUpdate)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingUser))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    public async Task<IActionResult> UpdateUser([FromRoute] string id, [FromBody] UpdatedUser request)
    {
        try
        {
            var updatedUser = await userAdministrationService.UpdateAsync(User, GetActorUserId(), id, request);
            return Ok(updatedUser);
        }
        catch (UserAdministrationValidationException exception)
        {
            return this.BadRequestProblem(exception.Message);
        }
        catch (UserAdministrationNotFoundException exception)
        {
            return this.NotFoundProblem(exception.Message);
        }
        catch (UserAdministrationConflictException exception)
        {
            return this.ConflictProblem(exception.Message);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unexpected error updating user {TargetUserId}.", id);
            throw;
        }
    }

    [HttpPut("{id}/role")]
    [Authorize(Policy = PermissionClaims.UsersUpdate)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingUser))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    public async Task<IActionResult> AssignRole([FromRoute] string id, [FromBody] UpdatedUserRole request)
    {
        try
        {
            var updatedUser = await userAdministrationService.AssignRoleAsync(User, GetActorUserId(), id, request);
            return Ok(updatedUser);
        }
        catch (UserAdministrationValidationException exception)
        {
            return this.BadRequestProblem(exception.Message);
        }
        catch (UserAdministrationNotFoundException exception)
        {
            return this.NotFoundProblem(exception.Message);
        }
        catch (UserAdministrationConflictException exception)
        {
            return this.ConflictProblem(exception.Message);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unexpected error assigning role for user {TargetUserId}.", id);
            throw;
        }
    }

    /// <summary>
    /// Trigger a password reset for one user (issue #406): mail them the same link a self-service reset
    /// produces, revoke their live sessions, and require a password change before the account can be used
    /// again. No temporary password exists at any point — the admin triggers, the user chooses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Empty body by design: there is nothing for the caller to supply, which also means there is no
    /// mass-assignment surface. The success response is <c>200</c> rather than <c>204</c> because the one
    /// outcome the admin can act on that a status code does not already carry is "the reset was applied but
    /// the mail did not go out".
    /// </para>
    /// <para>
    /// Gated on the existing <c>users.update</c> claim rather than a new one: any principal who can already
    /// flip <c>EmailConfirmed</c>/<c>Enabled</c> and reassign roles can already deny or escalate access to
    /// an account, so credential-reset authority does not meaningfully widen what the claim confers. It is
    /// audit-logged with the actor's id, notified (the target is emailed every time), and per-actor rate
    /// limited.
    /// </para>
    /// <para>
    /// Self-targeting is allowed — it signs the acting admin out of their other sessions and gates their
    /// current one. Blocking it would add a special case with no security benefit. The endpoint is
    /// <b>not</b> exempt from the must-change-password block, so a gated admin must fix their own password
    /// before they can reset anyone else's.
    /// </para>
    /// </remarks>
    [HttpPost("{id}/password-reset")]
    [Authorize(Policy = PermissionClaims.UsersUpdate)]
    [EnableRateLimiting(AdminActionRateLimiting.PasswordResetPolicy)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(PasswordResetDispatch))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests, Type = typeof(ProblemDetails))]
    public async Task<IActionResult> SendPasswordReset(
        [FromRoute] string id,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var delivery = await userAdministrationService.SendPasswordResetAsync(
                GetActorUserId(), id, cancellationToken);

            // NotConfigured maps to delivered: with no SMTP host the link is logged instead, and in that
            // (Development/Testing-only) environment logging *is* the delivery mechanism.
            return Ok(new PasswordResetDispatch
            {
                EmailDelivered = delivery is not PasswordResetLinkDelivery.Failed,
            });
        }
        catch (UserAdministrationValidationException exception)
        {
            return this.BadRequestProblem(exception.Message);
        }
        catch (UserAdministrationNotFoundException exception)
        {
            return this.NotFoundProblem(exception.Message);
        }
        catch (UserAdministrationUnprocessableException exception)
        {
            return this.UnprocessableEntityProblem(exception.Message);
        }
        catch (UserAdministrationThrottledException exception)
        {
            return this.TooManyRequestsProblem(exception.Message);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unexpected error sending a password reset for user {TargetUserId}.", id);
            throw;
        }
    }

    [HttpDelete("{id}")]
    [Authorize(Policy = PermissionClaims.UsersDelete)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    public async Task<IActionResult> DeleteUser([FromRoute] string id)
    {
        try
        {
            await userAdministrationService.DeleteAsync(GetActorUserId(), id);
            return NoContent();
        }
        catch (UserAdministrationValidationException exception)
        {
            return this.BadRequestProblem(exception.Message);
        }
        catch (UserAdministrationNotFoundException exception)
        {
            return this.NotFoundProblem(exception.Message);
        }
        catch (UserAdministrationConflictException exception)
        {
            return this.ConflictProblem(exception.Message);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unexpected error deleting user {TargetUserId}.", id);
            throw;
        }
    }

    private string GetActorUserId()
    {
        return User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown";
    }
}

using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Odyssey.Api.Legal;
using Odyssey.Context;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Authorization;
using Swashbuckle.AspNetCore.Annotations;

namespace Odyssey.Api.Controllers;

/// <summary>
/// License and Terms of Service acceptance (issue #354 §7).
/// </summary>
/// <remarks>
/// Three tiers of access, deliberately: the two document reads are anonymous (the registration page
/// shows both documents before any session exists); status/respond are authenticated and resolve the
/// target user <em>only</em> from the caller's own identity, so there is no id in either path and no
/// cross-user access; the three version-management endpoints require the existing <c>users.manage</c>
/// claim.
/// </remarks>
[ApiController]
[Authorize]
[Route("api/legal")]
public sealed class LegalController : ControllerBase
{
    private readonly LegalComplianceService service;
    private readonly SignInManager<ApplicationUser> signInManager;
    private readonly UserManager<ApplicationUser> userManager;

    public LegalController(
        LegalComplianceService service,
        SignInManager<ApplicationUser> signInManager,
        UserManager<ApplicationUser> userManager)
    {
        this.service = service;
        this.signInManager = signInManager;
        this.userManager = userManager;
    }

    [HttpGet("license")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(LicenseDocument))]
    [SwaggerOperation(Summary = "The repository LICENSE text and the SHA-256 digest acceptance is recorded against.")]
    public ActionResult<LicenseDocument> GetLicense() => Ok(service.GetLicense());

    [HttpGet("terms-of-service/current")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(TermsOfServiceDocument))]
    [SwaggerOperation(Summary = "The current published Terms of Service version, or null if none has been published.")]
    public async Task<IActionResult> GetCurrentTermsOfService(CancellationToken cancellationToken)
    {
        var document = await service.GetCurrentTermsOfServiceAsync(cancellationToken);

        // JsonResult rather than Ok(): MVC's HttpNoContentOutputFormatter turns a null ObjectResult
        // value into a 204, but "no version published yet" is a normal state the client must be able to
        // distinguish from an empty response, and §7 pins the contract as 200 with a null body.
        return new JsonResult(document);
    }

    [HttpGet("status")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(LegalComplianceStatus))]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [SwaggerOperation(Summary = "The calling user's own License/ToS compliance state.")]
    public async Task<IActionResult> GetStatus(CancellationToken cancellationToken)
    {
        if (CallerUserId is not { } userId)
        {
            return Unauthorized();
        }

        return Ok(await service.GetStatusAsync(userId, cancellationToken));
    }

    /// <summary>
    /// Record the caller's accept/decline for one document.
    /// </summary>
    /// <remarks>
    /// An acceptance is followed by <c>RefreshSignInAsync</c> so the recomputed claims take effect
    /// immediately rather than at the next 30-minute revalidation. A decline signs the session out
    /// server-side; it does not lock the account, so the user can log in again and respond differently
    /// (§2 non-goal 7).
    /// </remarks>
    [HttpPost("respond")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Accept or decline the License or the current Terms of Service.")]
    public async Task<IActionResult> Respond(
        [FromBody] LegalDocumentResponse request,
        CancellationToken cancellationToken)
    {
        if (CallerUserId is not { } userId)
        {
            return Unauthorized();
        }

        try
        {
            await service.RespondAsync(userId, request, cancellationToken);
        }
        catch (LegalValidationException exception)
        {
            return this.BadRequestProblem(exception.Message);
        }
        catch (LegalVersionConflictException exception)
        {
            return this.ConflictProblem(exception.Message);
        }

        if (request.Accepted == true)
        {
            // Null only if the principal outlives its user row (never in a real session; possible in
            // tests whose synthetic actor id matches no user). Nothing to refresh in that case.
            var user = await userManager.GetUserAsync(User);
            if (user is not null)
            {
                await signInManager.RefreshSignInAsync(user);
            }
        }
        else
        {
            await signInManager.SignOutAsync();
        }

        return NoContent();
    }

    [HttpGet("terms-of-service/versions")]
    [Authorize(Policy = PermissionClaims.UsersManage)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(IReadOnlyList<ExistingTermsOfServiceVersion>))]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [SwaggerOperation(Summary = "Terms of Service version history (metadata only — never the content).")]
    public async Task<IActionResult> GetVersions(CancellationToken cancellationToken) =>
        Ok(await service.GetVersionsAsync(User, cancellationToken));

    [HttpGet("terms-of-service/versions/{id:int}", Name = "GetTermsOfServiceVersion")]
    [Authorize(Policy = PermissionClaims.UsersManage)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(TermsOfServiceVersionDetail))]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "One Terms of Service version including its full text.")]
    public async Task<IActionResult> GetVersion(int id, CancellationToken cancellationToken)
    {
        var version = await service.GetVersionAsync(User, id, cancellationToken);

        return version is null
            ? this.NotFoundProblem($"Terms of Service version {id} was not found.")
            : Ok(version);
    }

    [HttpPost("terms-of-service/versions")]
    [Authorize(Policy = PermissionClaims.UsersManage)]
    [ProducesResponseType(StatusCodes.Status201Created, Type = typeof(TermsOfServiceVersionDetail))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [SwaggerOperation(Summary = "Publish a new current Terms of Service version; prior versions are retained untouched.")]
    public async Task<IActionResult> PublishVersion(
        [FromBody] NewTermsOfServiceVersion request,
        CancellationToken cancellationToken)
    {
        try
        {
            var version = await service.PublishAsync(User, CallerUserId ?? string.Empty, request, cancellationToken);
            return CreatedAtRoute("GetTermsOfServiceVersion", new { id = version.Id }, version);
        }
        catch (LegalValidationException exception)
        {
            return this.BadRequestProblem(exception.Message);
        }
    }

    private string? CallerUserId
    {
        get
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            return string.IsNullOrWhiteSpace(userId) ? null : userId;
        }
    }
}

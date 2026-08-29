using Odyssey.Api.Identity;
using Odyssey.Dtos.Application;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Swashbuckle.AspNetCore.Annotations;

namespace Odyssey.Api.Profiles;

/// <summary>
/// Self-service profile endpoints (issue #316). Both operate strictly on the authenticated caller's own
/// row — there is no id in the path, so no IDOR and no cross-user access. Any authenticated user may
/// read/write their own profile; no permission claim is required (per-user owned data, not an
/// admin-gated resource).
/// </summary>
[ApiController]
[Authorize]
[Route("api/profile")]
public sealed class ProfileController : ControllerBase
{
    private readonly ProfileService service;

    public ProfileController(ProfileService service)
    {
        this.service = service;
    }

    // Exempt from the must-change-password block (issue #406): this is where the client reads the flag,
    // so a gated user must be able to fetch it or the gate page could not render. The write below is
    // deliberately not exempt.
    [HttpGet(Name = "GetProfile")]
    [PasswordChangeExempt]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ProfileDto))]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [SwaggerOperation(Summary = "Read the current user's own profile and its completeness flag.")]
    public async Task<IActionResult> Get(CancellationToken cancellationToken = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized();
        }

        return Ok(await service.GetAsync(userId, cancellationToken));
    }

    [HttpPut(Name = "PutProfile")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ProfileDto))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [SwaggerOperation(Summary = "Set the current user's own profile (required fields must be present & valid).")]
    public async Task<IActionResult> Put(
        [FromBody] ProfileDto request,
        CancellationToken cancellationToken = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized();
        }

        try
        {
            return Ok(await service.SaveAsync(userId, request, cancellationToken));
        }
        catch (ProfileValidationException exception)
        {
            return this.BadRequestProblem(exception.Message);
        }
    }
}

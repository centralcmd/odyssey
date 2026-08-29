using Odyssey.Dtos.Application;
using Odyssey.Dtos.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Swashbuckle.AspNetCore.Annotations;
using System.Security.Claims;

namespace Odyssey.Api.Preferences;

[ApiController]
[Authorize]
[Route("api/user-preferences")]
public class UserPreferencesController : ControllerBase
{
    // Matches UserPreference.Key's [Length(1, 256)] column, so an over-long key is a 400 rather than
    // a write failure (architect finding F-14).
    private const int MaxPageKeyLength = 256;

    private readonly UserPreferencesService service;

    public UserPreferencesController(UserPreferencesService service)
    {
        this.service = service;
    }

    [HttpGet("{pageKey}", Name = "GetPreference")]
    [Authorize(Policy = PermissionClaims.UserPreferencesRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(UserPreferenceResponse))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "Get user preference for page key.",
        Description = @"Get user preference for page key.")]
    public async Task<IActionResult> GetPreference(
        [FromRoute(Name = "pageKey")] [SwaggerParameter("ID", Required = true,
            Description = @"The page key for the preference to get.")]string pageKey,
        CancellationToken cancellationToken)
    {
        if (!TryValidatePageKey(pageKey, out var problem))
        {
            return problem;
        }

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized();
        }

        var preference = await service.GetAsync(userId, pageKey, cancellationToken);

        return preference is null ? NotFound() : Ok(preference);
    }

    [HttpPut("{pageKey}", Name = "UpsertPreference")]
    [Authorize(Policy = PermissionClaims.UserPreferencesUpdate)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(UserPreferenceResponse))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "Update user preference for page key.",
        Description = @"Update user preference for page key.")]
    public async Task<IActionResult> UpsertPreference(
        [FromRoute(Name = "pageKey")] [SwaggerParameter("ID", Required = true,
            Description = @"The page key for the user preference to update.")] string pageKey,
        [FromBody] UserPreferenceRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryValidatePageKey(pageKey, out var problem))
        {
            return problem;
        }

        if (string.IsNullOrWhiteSpace(request.PreferencesJson))
        {
            return this.BadRequestProblem("Preferences JSON is required.");
        }

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized();
        }

        return Ok(await service.UpsertAsync(userId, pageKey, request.PreferencesJson, cancellationToken));
    }

    private bool TryValidatePageKey(string pageKey, out IActionResult problem)
    {
        if (string.IsNullOrWhiteSpace(pageKey))
        {
            problem = this.BadRequestProblem("Page key is required.");
            return false;
        }

        if (pageKey.Length > MaxPageKeyLength)
        {
            problem = this.BadRequestProblem($"Page key must not exceed {MaxPageKeyLength} characters.");
            return false;
        }

        problem = null!;
        return true;
    }
}

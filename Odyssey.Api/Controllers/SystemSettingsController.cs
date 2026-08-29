using Odyssey.Dtos.Application;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Odyssey.Api.SystemSettings;
using Odyssey.Dtos.Authorization;

namespace Odyssey.Api.Controllers;

/// <summary>
/// Admin-configurable runtime settings (issue #349). Both actions require <c>system-settings.read</c>
/// — including PUT, so a write-only caller can't use which fields 403 versus which don't as a
/// value-oracle for current state without ever successfully reading the resource. PUT's per-field
/// write claims (<c>system-settings.update</c>/<c>system-settings.security.update</c>) are enforced
/// inside <see cref="SystemSettingsService"/>, not via <c>[Authorize]</c>, because which claim a given
/// request needs depends on which fields are non-null.
/// </summary>
[ApiController]
[Route("api/system-settings")]
[Authorize(Policy = PermissionClaims.SystemSettingsRead)]
public sealed class SystemSettingsController : ControllerBase
{
    private readonly SystemSettingsService service;

    public SystemSettingsController(SystemSettingsService service)
    {
        this.service = service;
    }

    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(SystemSettingsDto))]
    public async Task<ActionResult<SystemSettingsDto>> Get(CancellationToken cancellationToken)
    {
        var dto = await service.GetAsync(User, cancellationToken);
        return Ok(dto);
    }

    [HttpPut]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(SystemSettingsDto))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    public async Task<ActionResult<SystemSettingsDto>> Put(
        [FromBody] SystemSettingsUpdate request, CancellationToken cancellationToken)
    {
        try
        {
            var dto = await service.UpdateAsync(User, ActorUserId, request, cancellationToken);
            return Ok(dto);
        }
        catch (SystemSettingsForbiddenException exception)
        {
            return this.ForbiddenProblem(exception.Message);
        }
    }

    private string ActorUserId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown";
}

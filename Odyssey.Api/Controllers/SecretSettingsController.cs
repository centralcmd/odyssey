using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Odyssey.Api.SystemSettings;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Authorization;

namespace Odyssey.Api.Controllers;

/// <summary>
/// Encrypted secret settings (issue #444) — list status, set one, clear one.
///
/// <para>
/// <c>system-settings.read</c> sits on the <strong>class</strong>, writes included, mirroring
/// <see cref="SystemSettingsController"/>'s rule that a write-only caller must not be able to probe the
/// resource. <c>system-settings.security.update</c> sits on the <strong>actions</strong>, so the claim
/// is evaluated before any key lookup: an unknown key must be a <c>403</c> for a caller without the
/// claim and a <c>404</c> for one holding it, and <c>RequiredClaim</c> is a per-descriptor field and
/// therefore unknowable until after resolution. The descriptor's claim is re-checked inside the
/// service as defence in depth for non-HTTP callers, and a guard test asserts the two agree.
/// </para>
///
/// <para>
/// <strong>No response body ever carries a value.</strong> Both writes return <c>204</c> with no body,
/// so there is no round-trip of the submitted value and no updated-resource representation that could
/// be mistaken for one. Writes are per-key rather than bulk, deliberately: a bulk body would carry
/// several plaintext credentials in one payload, so one unhandled exception, one proxy access log with
/// bodies enabled, or one debugging <c>ToString()</c> would spill all of them at once.
/// </para>
///
/// <para>
/// The writes carry an explicit per-actor rate-limit policy. <c>MapControllers()</c> attaches no
/// group-level policy in this pipeline, so without it these endpoints would have none at all.
/// </para>
/// </summary>
[ApiController]
[Route("api/system-settings/secrets")]
[Authorize(Policy = PermissionClaims.SystemSettingsRead)]
public sealed class SecretSettingsController : ControllerBase
{
    private readonly SecretSettingsService service;

    public SecretSettingsController(SecretSettingsService service)
    {
        this.service = service;
    }

    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(IReadOnlyList<SecretSettingStatusDto>))]
    public async Task<ActionResult<IReadOnlyList<SecretSettingStatusDto>>> Get(CancellationToken cancellationToken)
    {
        var statuses = await service.GetStatusesAsync(User, cancellationToken);
        return Ok(statuses);
    }

    [HttpPut("{key}")]
    [Authorize(Policy = PermissionClaims.SystemSettingsSecurityUpdate)]
    [EnableRateLimiting(AdminActionRateLimiting.SecretWritePolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable, Type = typeof(ProblemDetails))]
    public async Task<IActionResult> Put(
        string key, [FromBody] SecretSettingUpdate request, CancellationToken cancellationToken)
    {
        try
        {
            await service.SetAsync(User, ActorUserId, key, request.Value, cancellationToken);
            return NoContent();
        }
        catch (SystemSettingsForbiddenException exception)
        {
            return this.ForbiddenProblem(exception.Message);
        }
        catch (KeyRingNotDurableException exception)
        {
            return this.ServiceUnavailableProblem(exception.Message);
        }
    }

    [HttpDelete("{key}")]
    [Authorize(Policy = PermissionClaims.SystemSettingsSecurityUpdate)]
    [EnableRateLimiting(AdminActionRateLimiting.SecretWritePolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    public async Task<IActionResult> Delete(string key, CancellationToken cancellationToken)
    {
        try
        {
            await service.ClearAsync(User, ActorUserId, key, cancellationToken);
            return NoContent();
        }
        catch (SystemSettingsForbiddenException exception)
        {
            return this.ForbiddenProblem(exception.Message);
        }
    }

    private string ActorUserId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown";
}

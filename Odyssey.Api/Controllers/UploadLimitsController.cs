using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Odyssey.Dtos.Application;
using Odyssey.Core.Finance;
using Swashbuckle.AspNetCore.Annotations;

namespace Odyssey.Api.Controllers;

/// <summary>
/// The effective upload cap (issue #421 Wave 4), for any authenticated caller — no permission claim,
/// mirroring <see cref="ImportLimitsController"/>. The upload dialogs need the real number to
/// pre-validate a selection and to name the limit in their error text, and they are used by roles that
/// hold no system-settings claim at all.
/// </summary>
[ApiController]
[Route("api/upload-limits")]
[Authorize]
public sealed class UploadLimitsController : ControllerBase
{
    private readonly IUploadLimitsLookup lookup;

    public UploadLimitsController(IUploadLimitsLookup lookup)
    {
        this.lookup = lookup;
    }

    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(UploadLimitsDto))]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "The effective maximum upload size for file-upload surfaces.")]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var limits = await lookup.GetAsync(cancellationToken);

        // A degraded read must never be presented as configuration. The upload PATH still enforces the
        // degraded number — it is the more conservative one, so enforcement keeps working — but this
        // read-only display surface fails closed rather than telling the client a cap the
        // administrator never set. The client renders its compiled fallback instead.
        if (limits.IsDegraded)
        {
            return this.ServiceUnavailableProblem(
                "The upload limit is temporarily unavailable while the server recovers a configuration problem.");
        }

        return Ok(new UploadLimitsDto
        {
            MaxUploadBytes = limits.MaxUploadBytes,
            MaxUploadMegabytes = limits.MaxUploadMegabytes,
        });
    }
}

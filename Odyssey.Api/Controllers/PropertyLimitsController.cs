using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Odyssey.Dtos.Application;
using Odyssey.Core.Finance;
using Swashbuckle.AspNetCore.Annotations;

namespace Odyssey.Api.Controllers;

/// <summary>
/// The effective per-property limits (issue #167), for any authenticated caller — no permission claim,
/// mirroring <see cref="AccountLimitsController"/>, <see cref="ContractLimitsController"/>, <see cref="UploadLimitsController"/> and
/// <see cref="ImportLimitsController"/> exactly. A property's smart-tag section needs the real number
/// both to pre-check an add and to name the limit in its message, and it is used by roles that hold no
/// system-settings claim at all.
/// </summary>
/// <remarks>
/// <para>
/// A sibling endpoint rather than a field on <c>/api/account-limits</c> or <c>/api/contract-limits</c>,
/// for the reasons <see cref="ContractLimitsController"/> records: each has its own cache key, eviction
/// trigger and degraded posture.
/// </para>
/// <para>
/// <strong>Read exposure.</strong> Exactly one integer, and an instance-wide policy one: not personal
/// data, not per-user, and revealing nothing about any property, tag or transaction — which is why it
/// can be claim-free without eroding <c>properties.read</c>.
/// </para>
/// </remarks>
[ApiController]
[Route("api/property-limits")]
[Authorize]
public sealed class PropertyLimitsController : ControllerBase
{
    private readonly IPropertyLimitsLookup lookup;

    public PropertyLimitsController(IPropertyLimitsLookup lookup)
    {
        this.lookup = lookup;
    }

    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(PropertyLimitsDto))]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "The effective per-property limits for the Properties page.")]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var limits = await lookup.GetAsync(cancellationToken);

        // A degraded read must never be presented as configuration. The add PATH still enforces the
        // degraded number — it is the more conservative one, so enforcement keeps working — but this
        // read-only display surface fails closed rather than telling the client a cap the administrator
        // never set.
        //
        // An ABSENT row is not degraded: it resolves to the compiled default and returns 200. Treating
        // absent as degraded 503s every database whose settings rows have not been seeded.
        if (limits.IsDegraded)
        {
            return this.ServiceUnavailableProblem(
                "The property limits are temporarily unavailable while the server recovers a configuration problem.");
        }

        return Ok(new PropertyLimitsDto { MaxSmartTagsPerProperty = limits.MaxSmartTagsPerProperty });
    }
}

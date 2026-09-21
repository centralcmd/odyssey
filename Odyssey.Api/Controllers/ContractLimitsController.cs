using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Odyssey.Dtos.Application;
using Odyssey.Core.Finance;
using Swashbuckle.AspNetCore.Annotations;

namespace Odyssey.Api.Controllers;

/// <summary>
/// The effective per-contract limits (issue #166), for any authenticated caller — no permission claim,
/// mirroring <see cref="AccountLimitsController"/>, <see cref="UploadLimitsController"/> and
/// <see cref="ImportLimitsController"/> exactly. A contract's smart-tag section needs the real number
/// both to pre-check an add and to name the limit in its message, and it is used by roles that hold no
/// system-settings claim at all.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why a fourth near-identical endpoint rather than a field on <c>/api/account-limits</c> or a
/// new aggregate <c>/api/limits</c>.</strong> Considered and rejected. Each of these has its own cache
/// key, its own eviction trigger and its own degraded posture; collapsing them means any settings save
/// evicts all of them, and one concern's degraded read would <c>503</c> an endpoint the other callers
/// need. A fourth instance is cheaper to review than an aggregation with four failure modes behind one
/// status code.
/// </para>
/// <para>
/// <strong>Read exposure.</strong> Exactly one integer, and an instance-wide policy one: not personal
/// data, not per-user, and revealing nothing about any contract, tag or transaction — which is why it
/// can be claim-free without eroding <c>contracts.read</c>.
/// </para>
/// </remarks>
[ApiController]
[Route("api/contract-limits")]
[Authorize]
public sealed class ContractLimitsController : ControllerBase
{
    private readonly IContractLimitsLookup lookup;

    public ContractLimitsController(IContractLimitsLookup lookup)
    {
        this.lookup = lookup;
    }

    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ContractLimitsDto))]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "The effective per-contract limits for the Contracts page.")]
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
                "The contract limits are temporarily unavailable while the server recovers a configuration problem.");
        }

        return Ok(new ContractLimitsDto { MaxSmartTagsPerContract = limits.MaxSmartTagsPerContract });
    }
}

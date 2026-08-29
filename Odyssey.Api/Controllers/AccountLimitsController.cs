using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Odyssey.Dtos.Application;
using Odyssey.Core.Finance;
using Swashbuckle.AspNetCore.Annotations;

namespace Odyssey.Api.Controllers;

/// <summary>
/// The effective per-account limits (issue #434 key 15), for any authenticated caller — no permission
/// claim, mirroring <see cref="UploadLimitsController"/> and <see cref="ImportLimitsController"/>
/// exactly. The Accounts page's smart-tag section needs the real number both to pre-check an add and to
/// name the limit in its message, and it is used by roles that hold no system-settings claim at all.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why a third near-identical endpoint rather than one aggregate <c>/api/limits</c>.</strong>
/// Considered and rejected. Each of these has its own cache key, its own eviction trigger and its own
/// degraded posture; collapsing them means any settings save evicts all of them, and one concern's
/// degraded read would <c>503</c> an endpoint the other two callers need. The per-concern shape is the
/// established pattern and a third instance is cheaper to review than a new aggregation with three
/// failure modes behind one status code.
/// </para>
/// <para>
/// <strong>Read exposure.</strong> Exactly one integer, and an instance-wide policy one: not personal
/// data, not per-user, and revealing nothing about any account, tag or transaction — which is why it
/// can be claim-free without eroding <c>accounts.read</c>. It is the <em>same</em> number already
/// visible to every user today as a hardcoded literal in the page's own source, so this endpoint
/// strictly reduces what is baked into the shipped client rather than exposing anything new.
/// </para>
/// </remarks>
[ApiController]
[Route("api/account-limits")]
[Authorize]
public sealed class AccountLimitsController : ControllerBase
{
    private readonly IAccountLimitsLookup lookup;

    public AccountLimitsController(IAccountLimitsLookup lookup)
    {
        this.lookup = lookup;
    }

    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(AccountLimitsDto))]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "The effective per-account limits for the Accounts page.")]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var limits = await lookup.GetAsync(cancellationToken);

        // A degraded read must never be presented as configuration. The add PATH still enforces the
        // degraded number — it is the more conservative one, so enforcement keeps working — but this
        // read-only display surface fails closed rather than telling the client a cap the administrator
        // never set. The client renders its compiled fallback instead.
        //
        // An ABSENT row is not degraded: it resolves to the compiled default and returns 200. Treating
        // absent as degraded 503s every database whose settings rows have not been seeded.
        if (limits.IsDegraded)
        {
            return this.ServiceUnavailableProblem(
                "The account limits are temporarily unavailable while the server recovers a configuration problem.");
        }

        return Ok(new AccountLimitsDto { MaxSmartTagsPerAccount = limits.MaxSmartTagsPerAccount });
    }
}
